using System.Text.Json.Nodes;

namespace PamisWang.ClaudeAgentSdk.Sessions;

/// <summary>
/// 讀寫 Claude Code 存在本機的 session 紀錄（<c>~/.claude/projects/&lt;專案&gt;/&lt;sessionId&gt;.jsonl</c>）：
/// 列出 session、讀訊息與 subagent、改名、加標籤、刪除、分支。全部是同步的，跟 Python SDK 一樣。
/// 對應 Python 的 <c>list_sessions</c>、<c>get_session_info</c>、<c>get_session_messages</c>、<c>list_subagents</c>、
/// <c>get_subagent_messages</c>、<c>project_key_for_directory</c>、<c>rename_session</c>、<c>tag_session</c>、
/// <c>delete_session</c>、<c>fork_session</c>、<c>fold_session_summary</c>。
/// </summary>
/// <remarks>
/// 設定資料夾依序是 <c>claudeConfigDir</c> 參數、環境變數 <c>CLAUDE_CONFIG_DIR</c>、<c>~/.claude</c>。
/// <c>claudeConfigDir</c> 是 .NET 版多的參數（Python 只讀環境變數），效果跟設了 <c>CLAUDE_CONFIG_DIR</c> 一樣，
/// 例如用 <see cref="ClaudeAgentOptions.Env"/> 指定給 CLI 的設定資料夾時，傳同一個值就會讀到 CLI 寫的地方。
/// <c>directory</c> 是專案路徑（不是存放的資料夾）；有給時也會找同一個 git repo 的其他 worktree，沒給時找所有專案。
/// session store 的版本（<c>*_from_store</c>、<c>*_via_store</c>）沒有移植。
/// </remarks>
public static partial class ClaudeSessions
{
    /// <summary>
    /// 列出 session，資訊只從檔案大小、修改時間與開頭結尾取得。依 <see cref="SdkSessionInfo.LastModified"/> 由新到舊排序。
    /// 對應 Python 的 <c>list_sessions()</c>。
    /// </summary>
    /// <param name="directory">要列出的專案資料夾；有給時也包含它的 git worktree（見 <paramref name="includeWorktrees"/>）。沒給時列出所有專案。</param>
    /// <param name="limit">最多回傳幾個；<see langword="null"/>、0 或負數表示全部。</param>
    /// <param name="offset">排序後跳過幾個，搭配 <paramref name="limit"/> 分頁；0 或負數不跳過。</param>
    /// <param name="includeWorktrees">有給 <paramref name="directory"/> 且它在 git repo 裡時，一併列出所有 worktree 的 session。</param>
    /// <param name="claudeConfigDir">設定資料夾，等同設定 <c>CLAUDE_CONFIG_DIR</c>；<see langword="null"/> 或空字串時照環境變數。</param>
    /// <example>
    /// <code>
    /// var page1 = ClaudeSessions.ListSessions(limit: 50);
    /// var page2 = ClaudeSessions.ListSessions(limit: 50, offset: 50);
    /// var mine = ClaudeSessions.ListSessions(directory: "/path/to/project", includeWorktrees: false);
    /// </code>
    /// </example>
    public static IReadOnlyList<SdkSessionInfo> ListSessions(
        string? directory = null, int? limit = null, int offset = 0, bool includeWorktrees = true, string? claudeConfigDir = null)
    {
        var projectsDir = SessionPaths.ProjectsDir(claudeConfigDir);
        return string.IsNullOrEmpty(directory)
            ? ListAllSessions(projectsDir, limit, offset)
            : ListSessionsForProject(projectsDir, directory, limit, offset, includeWorktrees);
    }

    /// <summary>一個專案資料夾裡的 session；略過 sidechain 與只有 metadata 的 session。對應 <c>_read_sessions_from_dir</c>。</summary>
    private static List<SdkSessionInfo> ReadSessionsFromDir(string projectDir, string? projectPath = null)
    {
        var results = new List<SdkSessionInfo>();
        foreach (var entry in SessionPaths.ListDirectory(projectDir) ?? [])
        {
            var name = Path.GetFileName(entry);
            if (!name.EndsWith(".jsonl", StringComparison.Ordinal) || SessionPaths.ValidateUuid(name[..^6]) is not { } sessionId)
            {
                continue;
            }

            if (SessionLite.ReadSessionLite(entry) is { } lite && SessionLite.ParseSessionInfo(sessionId, lite, projectPath) is { } info)
            {
                results.Add(info);
            }
        }

        return results;
    }

    /// <summary>同一個 session ID 只留修改時間最新的，順序是第一次出現的位置。對應 <c>_deduplicate_by_session_id</c>。</summary>
    private static List<SdkSessionInfo> DeduplicateBySessionId(List<SdkSessionInfo> sessions)
    {
        var byId = new OrderedDictionary<string, SdkSessionInfo>(StringComparer.Ordinal);
        foreach (var session in sessions)
        {
            if (!byId.TryGetValue(session.SessionId, out var existing) || session.LastModified > existing.LastModified)
            {
                byId[session.SessionId] = session;
            }
        }

        return [.. byId.Values];
    }

    /// <summary>依修改時間由新到舊（穩定排序），再套用 offset 與 limit。對應 <c>_apply_sort_limit_offset</c>。</summary>
    private static List<SdkSessionInfo> ApplySortLimitOffset(List<SdkSessionInfo> sessions, int? limit, int offset)
    {
        IEnumerable<SdkSessionInfo> sorted = sessions.OrderByDescending(s => s.LastModified);
        if (offset > 0)
        {
            sorted = sorted.Skip(offset);
        }

        if (limit is > 0)
        {
            sorted = sorted.Take(limit.Value);
        }

        return [.. sorted];
    }

    private static List<string> TryGetWorktreePaths(string canonicalDir) => SessionPaths.GetWorktreePaths(canonicalDir);

    /// <summary>一個專案（與它的 worktree）的 session。對應 <c>_list_sessions_for_project</c>。</summary>
    private static List<SdkSessionInfo> ListSessionsForProject(string projectsDir, string directory, int? limit, int offset, bool includeWorktrees)
    {
        var canonicalDir = SessionPaths.CanonicalizePath(directory);
        var worktreePaths = includeWorktrees ? TryGetWorktreePaths(canonicalDir) : [];

        // 沒有 worktree（或沒有 git、不掃）：只看這一個專案資料夾
        if (worktreePaths.Count <= 1)
        {
            var projectDir = SessionPaths.FindProjectDir(projectsDir, canonicalDir);
            return projectDir is null ? [] : ApplySortLimitOffset(ReadSessionsFromDir(projectDir, canonicalDir), limit, offset);
        }

        var caseInsensitive = OperatingSystem.IsWindows();

        // 依 sanitize 後的長度由長到短，較具體的先比對
        var indexed = worktreePaths
            .Select(wt =>
            {
                var sanitized = SessionPaths.SanitizePath(wt);
                return (Path: wt, Prefix: caseInsensitive ? sanitized.ToLowerInvariant() : sanitized);
            })
            .OrderByDescending(x => x.Prefix.Length)
            .ToList();

        var allDirents = SessionPaths.ListSubdirectories(projectsDir);
        if (allDirents is null)
        {
            var projectDir = SessionPaths.FindProjectDir(projectsDir, canonicalDir);
            return projectDir is null ? ApplySortLimitOffset([], limit, offset) : ApplySortLimitOffset(ReadSessionsFromDir(projectDir, canonicalDir), limit, offset);
        }

        var allSessions = new List<SdkSessionInfo>();
        var seenDirs = new HashSet<string>(StringComparer.Ordinal);

        // 使用者給的資料夾一定要看（例如 /repo/packages/my-app 這種不會符合 worktree 根目錄的子資料夾）
        if (SessionPaths.FindProjectDir(projectsDir, canonicalDir) is { } canonicalProjectDir)
        {
            var dirBase = Path.GetFileName(canonicalProjectDir);
            seenDirs.Add(caseInsensitive ? dirBase.ToLowerInvariant() : dirBase);
            allSessions.AddRange(ReadSessionsFromDir(canonicalProjectDir, canonicalDir));
        }

        foreach (var entry in allDirents)
        {
            var name = Path.GetFileName(entry);
            var dirName = caseInsensitive ? name.ToLowerInvariant() : name;
            if (seenDirs.Contains(dirName))
            {
                continue;
            }

            foreach (var (worktreePath, prefix) in indexed)
            {
                // 只有截斷過（後面接雜湊）的長路徑才用前綴比對；短路徑要完全相同，免得 /root/project 配到 /root/project-foo
                var isMatch = dirName == prefix
                    || (prefix.Length >= SessionPaths.MaxSanitizedLength && dirName.StartsWith(prefix + "-", StringComparison.Ordinal));
                if (isMatch)
                {
                    seenDirs.Add(dirName);
                    allSessions.AddRange(ReadSessionsFromDir(entry, worktreePath));
                    break;
                }
            }
        }

        return ApplySortLimitOffset(DeduplicateBySessionId(allSessions), limit, offset);
    }

    /// <summary>所有專案的 session。對應 <c>_list_all_sessions</c>。</summary>
    private static List<SdkSessionInfo> ListAllSessions(string projectsDir, int? limit, int offset)
    {
        var projectDirs = SessionPaths.ListSubdirectories(projectsDir);
        if (projectDirs is null)
        {
            return [];
        }

        var allSessions = new List<SdkSessionInfo>();
        foreach (var projectDir in projectDirs)
        {
            allSessions.AddRange(ReadSessionsFromDir(projectDir));
        }

        return ApplySortLimitOffset(DeduplicateBySessionId(allSessions), limit, offset);
    }

    /// <summary>
    /// 讀一個 session 的資訊，只讀那一個檔案。找不到、是 sidechain session 或沒有任何摘要時回傳 <see langword="null"/>。
    /// 對應 Python 的 <c>get_session_info()</c>。
    /// </summary>
    /// <param name="sessionId">session 的 UUID；不是 UUID 時回傳 <see langword="null"/>。</param>
    /// <param name="directory">專案資料夾（意思同 <see cref="ListSessions"/>）；沒給時找所有專案。</param>
    /// <param name="claudeConfigDir">設定資料夾，等同設定 <c>CLAUDE_CONFIG_DIR</c>。</param>
    public static SdkSessionInfo? GetSessionInfo(string sessionId, string? directory = null, string? claudeConfigDir = null)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        if (SessionPaths.ValidateUuid(sessionId) is not { } uuid)
        {
            return null;
        }

        var projectsDir = SessionPaths.ProjectsDir(claudeConfigDir);
        var fileName = $"{uuid}.jsonl";

        if (!string.IsNullOrEmpty(directory))
        {
            var canonical = SessionPaths.CanonicalizePath(directory);
            if (SessionPaths.FindProjectDir(projectsDir, canonical) is { } projectDir
                && SessionLite.ReadSessionLite(Path.Combine(projectDir, fileName)) is { } lite)
            {
                return SessionLite.ParseSessionInfo(uuid, lite, canonical);
            }

            // session 可能在別的 worktree 底下，跟 GetSessionMessages 一樣
            foreach (var worktree in TryGetWorktreePaths(canonical))
            {
                if (worktree == canonical)
                {
                    continue;
                }

                if (SessionPaths.FindProjectDir(projectsDir, worktree) is { } worktreeProjectDir
                    && SessionLite.ReadSessionLite(Path.Combine(worktreeProjectDir, fileName)) is { } worktreeLite)
                {
                    return SessionLite.ParseSessionInfo(uuid, worktreeLite, worktree);
                }
            }

            return null;
        }

        foreach (var entry in SessionPaths.ListSubdirectories(projectsDir) ?? [])
        {
            if (SessionLite.ReadSessionLite(Path.Combine(entry, fileName)) is { } lite)
            {
                return SessionLite.ParseSessionInfo(uuid, lite);
            }
        }

        return null;
    }

    /// <summary>讀專案資料夾裡的 session 檔；讀不到時回傳 <see langword="null"/>。對應 <c>_try_read_session_file</c>。</summary>
    private static string? TryReadSessionFile(string projectDir, string fileName)
    {
        try
        {
            return PyText.ReadTextFile(Path.Combine(projectDir, fileName));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // 跟 Python 一樣只吃 OSError；不是有效 UTF-8 的 DecoderFallbackException 往外丟
            return null;
        }
    }

    /// <summary>找到並讀出 session 的 JSONL（第一個非空的）。對應 <c>_read_session_file</c>。</summary>
    private static string? ReadSessionFile(string projectsDir, string sessionId, string? directory)
    {
        var fileName = $"{sessionId}.jsonl";
        if (!string.IsNullOrEmpty(directory))
        {
            var canonicalDir = SessionPaths.CanonicalizePath(directory);
            if (SessionPaths.FindProjectDir(projectsDir, canonicalDir) is { } projectDir
                && TryReadSessionFile(projectDir, fileName) is { Length: > 0 } content)
            {
                return content;
            }

            foreach (var worktree in TryGetWorktreePaths(canonicalDir))
            {
                if (worktree == canonicalDir)
                {
                    continue;
                }

                if (SessionPaths.FindProjectDir(projectsDir, worktree) is { } worktreeProjectDir
                    && TryReadSessionFile(worktreeProjectDir, fileName) is { Length: > 0 } worktreeContent)
                {
                    return worktreeContent;
                }
            }

            return null;
        }

        foreach (var entry in SessionPaths.ListDirectory(projectsDir) ?? [])
        {
            if (TryReadSessionFile(entry, fileName) is { Length: > 0 } content)
            {
                return content;
            }
        }

        return null;
    }

    /// <summary>
    /// 讀一個 session 的對話：完整解析 JSONL，依 <c>parentUuid</c> 串出主線，由舊到新回傳使用者與助理訊息。
    /// 平行執行工具的那一輪會完整回傳（每個 <c>tool_use</c> 與 <c>tool_result</c>），不只是對話接下去的那一支。
    /// 找不到、<paramref name="sessionId"/> 不是 UUID 或沒有看得見的訊息時回傳空的清單。對應 Python 的 <c>get_session_messages()</c>。
    /// </summary>
    /// <param name="sessionId">session 的 UUID。</param>
    /// <param name="directory">專案資料夾；沒給時找所有專案。</param>
    /// <param name="limit">最多回傳幾則；<see langword="null"/>、0 或負數表示全部。</param>
    /// <param name="offset">從頭跳過幾則（負數照 Python 的切片從尾端算）。</param>
    /// <param name="claudeConfigDir">設定資料夾，等同設定 <c>CLAUDE_CONFIG_DIR</c>。</param>
    /// <exception cref="System.Text.DecoderFallbackException">session 檔不是有效的 UTF-8（Python 的 <c>UnicodeDecodeError</c>）。</exception>
    public static IReadOnlyList<SessionMessage> GetSessionMessages(
        string sessionId, string? directory = null, int? limit = null, int offset = 0, string? claudeConfigDir = null)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        if (SessionPaths.ValidateUuid(sessionId) is null)
        {
            return [];
        }

        var content = ReadSessionFile(SessionPaths.ProjectsDir(claudeConfigDir), sessionId, directory);
        if (string.IsNullOrEmpty(content))
        {
            return [];
        }

        return TranscriptChain.EntriesToSessionMessages(TranscriptChain.ParseTranscriptEntries(content), limit, offset);
    }

    /// <summary>
    /// session 檔的路徑（第一個大小 &gt; 0 的）；找法跟 <see cref="ReadSessionFile"/> 一樣。對應 <c>_resolve_session_file_path</c>。
    /// </summary>
    private static string? ResolveSessionFilePath(string projectsDir, string sessionId, string? directory) =>
        FindSessionFileWithDir(projectsDir, sessionId, directory, skipFilesInProjectsDir: true)?.FilePath;

    /// <summary>
    /// <c>path.stat().st_size &gt; 0</c>：檔案看大小；資料夾在 Python 也 stat 得到、大小不是 0，所以也算有。
    /// </summary>
    private static bool HasContent(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length > 0 : Directory.Exists(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// 找 session 檔與它所在的專案資料夾：有 <paramref name="directory"/> 時看它與它的 worktree，否則看所有專案。
    /// 對應 <c>_find_session_file_with_dir</c>（<paramref name="skipFilesInProjectsDir"/> 是 <c>_resolve_session_file_path</c> 多的 <c>is_dir()</c> 檢查）。
    /// </summary>
    private static (string FilePath, string ProjectDir)? FindSessionFileWithDir(
        string projectsDir, string sessionId, string? directory, bool skipFilesInProjectsDir = false)
    {
        var fileName = $"{sessionId}.jsonl";
        (string, string)? TryDir(string projectDir)
        {
            var path = Path.Combine(projectDir, fileName);
            return HasContent(path) ? (path, projectDir) : null;
        }

        if (!string.IsNullOrEmpty(directory))
        {
            var canonical = SessionPaths.CanonicalizePath(directory);
            if (SessionPaths.FindProjectDir(projectsDir, canonical) is { } projectDir && TryDir(projectDir) is { } found)
            {
                return found;
            }

            foreach (var worktree in TryGetWorktreePaths(canonical))
            {
                if (worktree == canonical)
                {
                    continue;
                }

                if (SessionPaths.FindProjectDir(projectsDir, worktree) is { } worktreeProjectDir && TryDir(worktreeProjectDir) is { } worktreeFound)
                {
                    return worktreeFound;
                }
            }

            return null;
        }

        foreach (var entry in SessionPaths.ListDirectory(projectsDir) ?? [])
        {
            if (skipFilesInProjectsDir && !Directory.Exists(entry))
            {
                continue;
            }

            if (TryDir(entry) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// session 的 subagent 資料夾：session 檔在 <c>&lt;專案&gt;/&lt;sessionId&gt;.jsonl</c>，subagent 在 <c>&lt;專案&gt;/&lt;sessionId&gt;/subagents/</c>。
    /// 對應 <c>_resolve_subagents_dir</c>。
    /// </summary>
    private static string? ResolveSubagentsDir(string projectsDir, string sessionId, string? directory)
    {
        var resolved = ResolveSessionFilePath(projectsDir, sessionId, directory);
        return resolved is null ? null : Path.Combine(Path.ChangeExtension(resolved, null), "subagents");
    }

    /// <summary>
    /// 遞迴收集 <c>agent-*.jsonl</c>（可能直接在 <c>subagents/</c>，也可能在 <c>subagents/workflows/&lt;runId&gt;/</c> 這種子資料夾），
    /// 每一層依名稱排序。回傳 (agent ID, 路徑)。對應 <c>_collect_agent_files</c>。
    /// </summary>
    private static List<(string AgentId, string FilePath)> CollectAgentFiles(string baseDir)
    {
        var results = new List<(string, string)>();
        void Walk(string currentDir)
        {
            if (SessionPaths.ListDirectory(currentDir) is not { } entries)
            {
                return;
            }

            foreach (var entry in entries.OrderBy(Path.GetFileName, PyText.CodePointComparer))
            {
                var name = Path.GetFileName(entry);
                if (File.Exists(entry) && name.StartsWith("agent-", StringComparison.Ordinal) && name.EndsWith(".jsonl", StringComparison.Ordinal)
                    && name.Length >= "agent-".Length + ".jsonl".Length)
                {
                    results.Add((name["agent-".Length..^".jsonl".Length], entry));
                }
                else if (Directory.Exists(entry))
                {
                    Walk(entry);
                }
            }
        }

        Walk(baseDir);
        return results;
    }

    /// <summary>
    /// 列出 session 的 subagent ID（掃 <c>&lt;sessionId&gt;/subagents/</c> 底下的 <c>agent-&lt;agentId&gt;.jsonl</c>，含子資料夾）。
    /// 找不到 session、<paramref name="sessionId"/> 不是 UUID 或沒有 subagent 時回傳空的清單。對應 Python 的 <c>list_subagents()</c>。
    /// </summary>
    /// <param name="sessionId">上層 session 的 UUID。</param>
    /// <param name="directory">專案資料夾；沒給時找所有專案。</param>
    /// <param name="claudeConfigDir">設定資料夾，等同設定 <c>CLAUDE_CONFIG_DIR</c>。</param>
    public static IReadOnlyList<string> ListSubagents(string sessionId, string? directory = null, string? claudeConfigDir = null)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        if (SessionPaths.ValidateUuid(sessionId) is null)
        {
            return [];
        }

        var subagentsDir = ResolveSubagentsDir(SessionPaths.ProjectsDir(claudeConfigDir), sessionId, directory);
        return subagentsDir is null ? [] : [.. CollectAgentFiles(subagentsDir).Select(x => x.AgentId)];
    }

    /// <summary>
    /// 讀一個 subagent 的對話，由舊到新回傳使用者與助理訊息。每則訊息的 <see cref="SessionMessage.ParentToolUseId"/> 與
    /// <see cref="SessionMessage.ParentAgentId"/> 取自紀錄旁邊的 <c>agent-&lt;agentId&gt;.meta.json</c>；沒有或讀不了時是 <see langword="null"/>。
    /// 找不到時回傳空的清單。對應 Python 的 <c>get_subagent_messages()</c>。
    /// </summary>
    /// <param name="sessionId">上層 session 的 UUID。</param>
    /// <param name="agentId">subagent 的 ID（<see cref="ListSubagents"/> 回傳的）。</param>
    /// <param name="directory">專案資料夾；沒給時找所有專案。</param>
    /// <param name="limit">最多回傳幾則；<see langword="null"/>、0 或負數表示全部。</param>
    /// <param name="offset">從頭跳過幾則。</param>
    /// <param name="claudeConfigDir">設定資料夾，等同設定 <c>CLAUDE_CONFIG_DIR</c>。</param>
    /// <exception cref="System.Text.DecoderFallbackException">紀錄不是有效的 UTF-8（Python 的 <c>UnicodeDecodeError</c>）。</exception>
    public static IReadOnlyList<SessionMessage> GetSubagentMessages(
        string sessionId, string agentId, string? directory = null, int? limit = null, int offset = 0, string? claudeConfigDir = null)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        if (SessionPaths.ValidateUuid(sessionId) is null || string.IsNullOrEmpty(agentId))
        {
            return [];
        }

        var subagentsDir = ResolveSubagentsDir(SessionPaths.ProjectsDir(claudeConfigDir), sessionId, directory);
        if (subagentsDir is null)
        {
            return [];
        }

        string? match = null;
        foreach (var (foundId, filePath) in CollectAgentFiles(subagentsDir))
        {
            if (foundId == agentId)
            {
                match = filePath;
                break;
            }
        }

        if (match is null)
        {
            return [];
        }

        string content;
        try
        {
            content = PyText.ReadTextFile(match);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        if (content.Length == 0)
        {
            return [];
        }

        // .meta.json 記錄是哪個 Agent tool_use 產生這個 subagent；讀不到（不存在、讀不了、壞掉）就當作沒有
        var (parentToolUseId, parentAgentId) = ParentIdsFromAgentMetadata(ReadAgentMetadataSidecar(match));
        return TranscriptChain.EntriesToSubagentMessages(TranscriptChain.ParseTranscriptEntries(content), limit, offset, parentToolUseId, parentAgentId);
    }

    /// <summary><c>agent-&lt;id&gt;.jsonl</c> → 同資料夾的 <c>agent-&lt;id&gt;.meta.json</c>。對應 <c>_agent_metadata_sidecar_path</c>。</summary>
    private static string AgentMetadataSidecarPath(string transcriptPath) =>
        Path.Combine(Path.GetDirectoryName(transcriptPath) ?? "", Path.GetFileName(transcriptPath)[..^".jsonl".Length] + ".meta.json");

    /// <summary>
    /// 讀 subagent 紀錄旁邊的 <c>.meta.json</c>；不存在、讀不了、不是 JSON 或不是物件都回傳 <see langword="null"/>。
    /// 對應 <c>_read_agent_metadata_sidecar</c> 加上 <c>get_subagent_messages</c> 對 <c>OSError</c> 的處理。
    /// </summary>
    private static JsonObject? ReadAgentMetadataSidecar(string transcriptPath)
    {
        try
        {
            return PyJson.LoadsObject(PyText.ReadTextFile(AgentMetadataSidecarPath(transcriptPath)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException)
        {
            return null;
        }
    }

    /// <summary>metadata 裡的 (<c>toolUseId</c>, <c>parentAgentId</c>)，不是字串的當作沒有。對應 <c>_parent_ids_from_agent_metadata</c>。</summary>
    private static (string? ToolUseId, string? ParentAgentId) ParentIdsFromAgentMetadata(JsonObject? meta) =>
        meta is null || meta.Count == 0 ? (null, null) : (PyJson.Str(meta, "toolUseId"), PyJson.Str(meta, "parentAgentId"));

    /// <summary>
    /// 資料夾對應的專案 key：realpath + NFC + 跟 CLI 一樣的 sanitize（長路徑加雜湊），也就是 <c>projects</c> 底下的資料夾名稱。
    /// 沒給時用目前的工作目錄。對應 Python 的 <c>project_key_for_directory()</c>。
    /// </summary>
    public static string ProjectKeyForDirectory(string? directory = null) =>
        SessionPaths.SanitizePath(SessionPaths.CanonicalizePath(directory ?? "."));
}
