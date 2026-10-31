using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace PamisWang.ClaudeAgentSdk.Sessions;

// 改名、標籤、刪除、分支。對應 Python 的 _internal/session_mutations.py。
// 改名與標籤跟 CLI 一樣在 JSONL 後面附加一行 metadata。目標 session 正開在 CLI 裡時，CLI 重新附加 metadata 前會先讀結尾，
// 所以 SDK 寫的值會被 CLI 吸收、不會被舊值蓋掉。
public static partial class ClaudeSessions
{
    /// <summary>
    /// 幫 session 改名：附加一行 <c>custom-title</c>。<see cref="ListSessions"/> 讀結尾最後一個標題，所以重複呼叫沒問題，最後的為準。
    /// 對應 Python 的 <c>rename_session()</c>。
    /// </summary>
    /// <param name="sessionId">session 的 UUID。</param>
    /// <param name="title">新標題；前後空白會去掉，去掉後不能是空的。</param>
    /// <param name="directory">專案資料夾（意思同 <see cref="ListSessions"/>）；沒給時找所有專案。</param>
    /// <param name="claudeConfigDir">設定資料夾，等同設定 <c>CLAUDE_CONFIG_DIR</c>。</param>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> 不是 UUID，或 <paramref name="title"/> 是空的（Python 的 <c>ValueError</c>）。</exception>
    /// <exception cref="FileNotFoundException">找不到 session 檔。</exception>
    public static void RenameSession(string sessionId, string title, string? directory = null, string? claudeConfigDir = null)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(title);
        if (SessionPaths.ValidateUuid(sessionId) is null)
        {
            throw new ArgumentException($"Invalid session_id: {sessionId}", nameof(sessionId));
        }

        // 跟 CLI 一樣拒絕空白標題，不拿它當「清除標題」
        var stripped = PyText.Strip(title);
        if (stripped.Length == 0)
        {
            throw new ArgumentException("title must be non-empty", nameof(title));
        }

        var data = PyJson.Dumps(new JsonObject { ["type"] = "custom-title", ["customTitle"] = stripped, ["sessionId"] = sessionId }) + "\n";
        AppendToSession(SessionPaths.ProjectsDir(claudeConfigDir), sessionId, data, directory);
    }

    /// <summary>
    /// 幫 session 加標籤；<see langword="null"/> 清除標籤。附加一行 <c>{"type":"tag","tag":…,"sessionId":…}</c>，
    /// <see cref="ListSessions"/> 讀最後一個；清除時寫空字串，讀出來是 <see langword="null"/>。
    /// 標籤先去掉危險的 Unicode 字元（零寬字元、方向標記、私用區等），跟 CLI 的篩選相容。對應 Python 的 <c>tag_session()</c>。
    /// </summary>
    /// <param name="sessionId">session 的 UUID。</param>
    /// <param name="tag">標籤；前後空白會去掉，處理後不能是空的。<see langword="null"/> 表示清除。</param>
    /// <param name="directory">專案資料夾；沒給時找所有專案。</param>
    /// <param name="claudeConfigDir">設定資料夾，等同設定 <c>CLAUDE_CONFIG_DIR</c>。</param>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> 不是 UUID，或 <paramref name="tag"/> 處理後是空的。</exception>
    /// <exception cref="FileNotFoundException">找不到 session 檔。</exception>
    public static void TagSession(string sessionId, string? tag, string? directory = null, string? claudeConfigDir = null)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        if (SessionPaths.ValidateUuid(sessionId) is null)
        {
            throw new ArgumentException($"Invalid session_id: {sessionId}", nameof(sessionId));
        }

        if (tag is not null)
        {
            var sanitized = PyText.Strip(SanitizeUnicode(tag));
            if (sanitized.Length == 0)
            {
                throw new ArgumentException("tag must be non-empty (use None to clear)", nameof(tag));
            }

            tag = sanitized;
        }

        var data = PyJson.Dumps(new JsonObject { ["type"] = "tag", ["tag"] = tag ?? "", ["sessionId"] = sessionId }) + "\n";
        AppendToSession(SessionPaths.ProjectsDir(claudeConfigDir), sessionId, data, directory);
    }

    /// <summary>
    /// 刪除 session：永久刪掉 <c>{sessionId}.jsonl</c> 與旁邊放 subagent 紀錄的 <c>{sessionId}/</c> 資料夾（有的話）。
    /// 要「軟刪除」可以改用 <c>TagSession(id, "__hidden")</c> 再在列出時過濾。對應 Python 的 <c>delete_session()</c>。
    /// </summary>
    /// <param name="sessionId">session 的 UUID。</param>
    /// <param name="directory">專案資料夾；沒給時找所有專案。</param>
    /// <param name="claudeConfigDir">設定資料夾，等同設定 <c>CLAUDE_CONFIG_DIR</c>。</param>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> 不是 UUID。</exception>
    /// <exception cref="FileNotFoundException">找不到 session 檔。</exception>
    public static void DeleteSession(string sessionId, string? directory = null, string? claudeConfigDir = null)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        if (SessionPaths.ValidateUuid(sessionId) is null)
        {
            throw new ArgumentException($"Invalid session_id: {sessionId}", nameof(sessionId));
        }

        var found = FindSessionFileWithDir(SessionPaths.ProjectsDir(claudeConfigDir), sessionId, directory);
        if (found is not { } source)
        {
            throw new FileNotFoundException(
                $"Session {sessionId} not found" + (string.IsNullOrEmpty(directory) ? "" : $" in project directory for {directory}"));
        }

        var path = source.FilePath;
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            throw new FileNotFoundException($"Session {sessionId} not found", path);
        }

        File.Delete(path);

        // subagent 紀錄在旁邊的 {sessionId}/ 資料夾，常常不存在；跟 shutil.rmtree(ignore_errors=True) 一樣不管錯誤，也不刪符號連結
        var subagentDir = Path.Combine(Path.GetDirectoryName(path) ?? "", sessionId);
        try
        {
            var info = new DirectoryInfo(subagentDir);
            if (info.Exists && info.LinkTarget is null)
            {
                info.Delete(recursive: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // 忽略
        }
    }

    /// <summary>
    /// 把 session 分支成新的 session：複製紀錄裡的訊息到新檔案，每個 UUID 換新、保留 <c>parentUuid</c> 的鏈結。
    /// 可以用 <paramref name="upToMessageId"/> 從對話的某一點分出去。新的 session 沒有復原紀錄（不複製 file-history snapshot）。
    /// 對應 Python 的 <c>fork_session()</c>。
    /// </summary>
    /// <param name="sessionId">來源 session 的 UUID。</param>
    /// <param name="directory">專案資料夾；沒給時找所有專案。新檔案寫在來源檔案旁邊。</param>
    /// <param name="upToMessageId">複製到這則訊息為止（含）；沒給時複製全部。</param>
    /// <param name="title">新 session 的標題；沒給時用原本的標題加上「 (fork)」。</param>
    /// <param name="claudeConfigDir">設定資料夾，等同設定 <c>CLAUDE_CONFIG_DIR</c>。</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="sessionId"/> 或 <paramref name="upToMessageId"/> 不是 UUID、session 沒有可以分支的訊息，或紀錄裡找不到 <paramref name="upToMessageId"/>。
    /// </exception>
    /// <exception cref="FileNotFoundException">找不到來源 session 檔。</exception>
    public static ForkSessionResult ForkSession(
        string sessionId, string? directory = null, string? upToMessageId = null, string? title = null, string? claudeConfigDir = null)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        if (SessionPaths.ValidateUuid(sessionId) is null)
        {
            throw new ArgumentException($"Invalid session_id: {sessionId}", nameof(sessionId));
        }

        if (!string.IsNullOrEmpty(upToMessageId) && SessionPaths.ValidateUuid(upToMessageId) is null)
        {
            throw new ArgumentException($"Invalid up_to_message_id: {upToMessageId}", nameof(upToMessageId));
        }

        var found = FindSessionFileWithDir(SessionPaths.ProjectsDir(claudeConfigDir), sessionId, directory);
        if (found is not { } source)
        {
            throw new FileNotFoundException(
                $"Session {sessionId} not found" + (string.IsNullOrEmpty(directory) ? "" : $" in project directory for {directory}"));
        }

        var content = File.ReadAllBytes(source.FilePath);
        if (content.Length == 0)
        {
            throw new ArgumentException($"Session {sessionId} has no messages to fork", nameof(sessionId));
        }

        var (transcript, contentReplacements) = ParseForkTranscript(content, sessionId);

        string? DeriveTitle()
        {
            var head = PyText.DecodeReplace(content.AsSpan(0, Math.Min(content.Length, SessionLite.ReadBufferSize)));
            var tail = PyText.DecodeReplace(content.AsSpan(Math.Max(0, content.Length - SessionLite.ReadBufferSize)));
            return SessionLite.NullIfEmpty(SessionLite.ExtractLastJsonStringField(tail, "customTitle"))
                ?? SessionLite.NullIfEmpty(SessionLite.ExtractLastJsonStringField(head, "customTitle"))
                ?? SessionLite.NullIfEmpty(SessionLite.ExtractLastJsonStringField(tail, "aiTitle"))
                ?? SessionLite.NullIfEmpty(SessionLite.ExtractLastJsonStringField(head, "aiTitle"))
                ?? SessionLite.NullIfEmpty(SessionLite.ExtractFirstPromptFromHead(head));
        }

        var (forkedSessionId, lines) = BuildForkLines(transcript, contentReplacements, sessionId, upToMessageId, title, DeriveTitle);

        var forkPath = Path.Combine(source.ProjectDir, $"{forkedSessionId}.jsonl");
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (var stream = new FileStream(forkPath, options))
        {
            stream.Write(Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n"));
        }

        return new ForkSessionResult(forkedSessionId);
    }

    /// <summary>
    /// 分支的核心：換掉 UUID 並產生每一行 JSON（不含換行）。回傳 (新的 session ID, 各行)。
    /// <paramref name="deriveTitle"/> 只在沒給 <paramref name="title"/> 時才呼叫。對應 <c>_build_fork_lines</c>。
    /// </summary>
    internal static (string ForkedSessionId, List<string> Lines) BuildForkLines(
        List<JsonObject> transcript,
        List<JsonNode?> contentReplacements,
        string sessionId,
        string? upToMessageId,
        string? title,
        Func<string?> deriveTitle)
    {
        // 去掉 sidechain（subagent 有自己的 parentUuid 圖）；isMeta 留著，它們穿插在主線裡
        transcript = transcript.Where(e => !PyJson.Truthy(e["isSidechain"])).ToList();
        if (transcript.Count == 0)
        {
            throw new ArgumentException($"Session {sessionId} has no messages to fork", nameof(sessionId));
        }

        if (!string.IsNullOrEmpty(upToMessageId))
        {
            var cutoff = transcript.FindIndex(e => PyJson.Str(e, "uuid") == upToMessageId);
            if (cutoff == -1)
            {
                throw new ArgumentException($"Message {upToMessageId} not found in session {sessionId}", nameof(upToMessageId));
            }

            transcript = transcript.GetRange(0, cutoff + 1);
        }

        // progress 也要對應新 UUID，走 parentUuid 鏈時需要
        var uuidMapping = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in transcript)
        {
            uuidMapping[PyJson.Str(entry, "uuid")!] = NewUuid();
        }

        // progress 只是 UI 用的鏈結，新的分支不需要
        var writable = transcript.Where(e => PyJson.Str(e, "type") != "progress").ToList();
        if (writable.Count == 0)
        {
            throw new ArgumentException($"Session {sessionId} has no messages to fork", nameof(sessionId));
        }

        var byUuid = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var entry in transcript)
        {
            byUuid[PyJson.Str(entry, "uuid")!] = entry;
        }

        var forkedSessionId = NewUuid();
        var now = IsoNow();
        var lines = new List<string>();

        for (var i = 0; i < writable.Count; i++)
        {
            var original = writable[i];
            var originalUuid = PyJson.Str(original, "uuid")!;
            var newUuid = uuidMapping[originalUuid];

            // 找父紀錄，跳過 progress；Python 遇到 progress 的迴圈會停不下來，這裡走過的就不再走
            string? newParentUuid = null;
            var parentId = original["parentUuid"];
            var visited = new HashSet<string>(StringComparer.Ordinal);
            while (PyJson.Truthy(parentId))
            {
                if (PyJson.Str(parentId) is not { } id || !byUuid.TryGetValue(id, out var parent) || !visited.Add(id))
                {
                    break;
                }

                if (PyJson.Str(parent, "type") != "progress")
                {
                    newParentUuid = uuidMapping.GetValueOrDefault(id);
                    break;
                }

                parentId = parent["parentUuid"];
            }

            // 只更新最後一則的時間（resume 時用來找葉節點）
            JsonNode? timestamp = i == writable.Count - 1
                ? JsonValue.Create(now)
                : original.TryGetPropertyValue("timestamp", out var originalTimestamp) ? originalTimestamp?.DeepClone() : JsonValue.Create(now);

            // compact boundary 的 logicalParentUuid 也換掉
            var logicalParent = original["logicalParentUuid"];
            JsonNode? newLogicalParent = PyJson.Truthy(logicalParent)
                ? (PyJson.Str(logicalParent) is { } logical && uuidMapping.TryGetValue(logical, out var mapped) ? JsonValue.Create(mapped) : null)
                : logicalParent?.DeepClone();

            var overrides = new List<(string Key, JsonNode? Value)>
            {
                ("uuid", JsonValue.Create(newUuid)),
                ("parentUuid", newParentUuid is null ? null : JsonValue.Create(newParentUuid)),
                ("logicalParentUuid", newLogicalParent),
                ("sessionId", JsonValue.Create(forkedSessionId)),
                ("timestamp", timestamp),
                // 清掉從來源帶過來的 session 專屬欄位
                ("isSidechain", JsonValue.Create(false)),
                ("forkedFrom", new JsonObject { ["sessionId"] = sessionId, ["messageUuid"] = originalUuid }),
            };

            // Python 的 {**original, ...}：已有的鍵留在原位置換值，新的鍵依序加在後面
            var forked = new JsonObject();
            foreach (var (key, value) in original)
            {
                forked[key] = value?.DeepClone();
            }

            foreach (var (key, value) in overrides)
            {
                forked[key] = value;
            }

            // 會把來源 session 狀態帶過來的欄位拿掉
            foreach (var key in (ReadOnlySpan<string>)["teamName", "agentName", "slug", "sourceToolAssistantUUID"])
            {
                forked.Remove(key);
            }

            lines.Add(PyJson.Dumps(forked));
        }

        // content-replacement（有的話）改成新的 sessionId 附在後面
        if (contentReplacements.Count > 0)
        {
            var replacements = new JsonArray();
            foreach (var replacement in contentReplacements)
            {
                replacements.Add(replacement?.DeepClone());
            }

            lines.Add(PyJson.Dumps(new JsonObject
            {
                ["type"] = "content-replacement",
                ["sessionId"] = forkedSessionId,
                ["replacements"] = replacements,
                ["uuid"] = NewUuid(),
                ["timestamp"] = now,
            }));
        }

        // 標題：明確給的 > 原本的 customTitle > aiTitle > 第一個提示；推導出來的加上「 (fork)」。ListSessions 讀結尾最後一個 custom-title
        var forkTitle = string.IsNullOrEmpty(title) ? null : PyText.Strip(title);
        if (string.IsNullOrEmpty(forkTitle))
        {
            forkTitle = $"{deriveTitle() ?? "Forked session"} (fork)";
        }

        lines.Add(PyJson.Dumps(new JsonObject
        {
            ["type"] = "custom-title",
            ["sessionId"] = forkedSessionId,
            ["customTitle"] = forkTitle,
            ["uuid"] = NewUuid(),
            ["timestamp"] = now,
        }));

        return (forkedSessionId, lines);
    }

    /// <summary>Python 的 <c>str(uuid.uuid4())</c>。</summary>
    private static string NewUuid() => Guid.NewGuid().ToString("D");

    /// <summary>Python 的 <c>datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")</c>：微秒是 0 時不寫小數。</summary>
    internal static string IsoNow(DateTime? utcNow = null)
    {
        var now = utcNow ?? DateTime.UtcNow;
        var microsecond = now.Ticks % TimeSpan.TicksPerSecond / 10;
        var text = now.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        if (microsecond != 0)
        {
            text += "." + microsecond.ToString("000000", CultureInfo.InvariantCulture);
        }

        return text + "Z";
    }

    /// <summary>分支要用的紀錄類型。對應 <c>_TRANSCRIPT_TYPES</c>。</summary>
    private static readonly HashSet<string> ForkTranscriptTypes = new(StringComparer.Ordinal) { "user", "assistant", "attachment", "system", "progress" };

    /// <summary>
    /// 把 JSONL 解析成紀錄與 content-replacement：紀錄只留有字串 uuid 的對話類型；屬於這個 session 的 content-replacement 收集起來給分支重寫。
    /// 對應 <c>_parse_fork_transcript</c>（Python 用 <c>splitlines()</c> 切行）。
    /// </summary>
    private static (List<JsonObject> Transcript, List<JsonNode?> ContentReplacements) ParseForkTranscript(byte[] content, string sessionId)
    {
        var transcript = new List<JsonObject>();
        var contentReplacements = new List<JsonNode?>();
        foreach (var rawLine in PyText.SplitLines(PyText.DecodeReplace(content)))
        {
            var line = PyText.Strip(rawLine);
            if (line.Length == 0 || PyJson.LoadsObject(line) is not { } entry)
            {
                continue;
            }

            var entryType = PyJson.Str(entry, "type");
            if (entryType is not null && ForkTranscriptTypes.Contains(entryType) && PyJson.Str(entry, "uuid") is not null)
            {
                transcript.Add(entry);
            }
            else if (entryType == "content-replacement" && PyJson.Str(entry, "sessionId") == sessionId && entry["replacements"] is JsonArray replacements)
            {
                contentReplacements.AddRange(replacements);
            }
        }

        return (transcript, contentReplacements);
    }

    /// <summary>
    /// 附加到現有的 session 檔：依序試每個可能的位置，不先檢查存在（沒有 TOCTOU）。對應 <c>_append_to_session</c>。
    /// </summary>
    private static void AppendToSession(string projectsDir, string sessionId, string data, string? directory)
    {
        var fileName = $"{sessionId}.jsonl";
        if (!string.IsNullOrEmpty(directory))
        {
            var canonical = SessionPaths.CanonicalizePath(directory);
            if (SessionPaths.FindProjectDir(projectsDir, canonical) is { } projectDir && TryAppend(Path.Combine(projectDir, fileName), data))
            {
                return;
            }

            // session 可能在別的 worktree 底下
            foreach (var worktree in TryGetWorktreePaths(canonical))
            {
                if (worktree == canonical)
                {
                    continue;
                }

                if (SessionPaths.FindProjectDir(projectsDir, worktree) is { } worktreeProjectDir && TryAppend(Path.Combine(worktreeProjectDir, fileName), data))
                {
                    return;
                }
            }

            throw new FileNotFoundException($"Session {sessionId} not found in project directory for {directory}");
        }

        var dirents = SessionPaths.ListDirectory(projectsDir)
            ?? throw new FileNotFoundException($"Session {sessionId} not found (no projects directory)");
        foreach (var entry in dirents)
        {
            if (TryAppend(Path.Combine(entry, fileName), data))
            {
                return;
            }
        }

        throw new FileNotFoundException($"Session {sessionId} not found in any project directory");
    }

    /// <summary>
    /// 附加到一個路徑。不建立檔案：不存在（或路徑中間不是資料夾）時回傳 <see langword="false"/>；0 位元組的檔案也回傳 <see langword="false"/>
    /// （「session 不在這裡，繼續找」，讀取那邊也這樣看）。其他錯誤（磁碟滿、沒有權限、路徑是資料夾）照樣丟出。對應 <c>_try_append</c>。
    /// </summary>
    /// <remarks>
    /// Python 用 <c>O_APPEND</c>，由核心在每次寫入時移到檔尾。.NET 沒有對應的開檔方式，這裡開檔後寫在當下的檔尾；
    /// CLI 剛好同時寫同一個檔案時，有極短的時間差可能交錯。
    /// </remarks>
    internal static bool TryAppend(string path, string data)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        catch (IOException) when (!Directory.Exists(Path.GetDirectoryName(Path.GetFullPath(path))))
        {
            // 路徑中間不是資料夾（ENOTDIR）：.NET 在 Unix 上丟的是一般的 IOException
            return false;
        }

        using (stream)
        {
            var length = RandomAccess.GetLength(stream.SafeFileHandle);
            if (length == 0)
            {
                return false;
            }

            RandomAccess.Write(stream.SafeFileHandle, Encoding.UTF8.GetBytes(data), length);
            return true;
        }
    }

    // 危險的 Unicode 字元的明確範圍（跟分類檢查重複，但跟 TypeScript 一樣）。對應 _UNICODE_STRIP_RE
    private static bool IsExplicitlyStripped(int codePoint) =>
        codePoint is (>= 0x200b and <= 0x200f) or (>= 0x202a and <= 0x202e) or (>= 0x2066 and <= 0x2069) or 0xfeff or (>= 0xe000 and <= 0xf8ff);

    /// <summary>
    /// 去掉危險的 Unicode 字元：反覆做 NFKC 正規化並去掉格式字元（Cf）、私用區（Co）、未指派（Cn），直到不再變化（最多 10 次）。
    /// 對應 <c>_sanitize_unicode</c>。
    /// </summary>
    internal static string SanitizeUnicode(string value)
    {
        var current = value;
        for (var iteration = 0; iteration < 10; iteration++)
        {
            var previous = current;
            current = current.Normalize(NormalizationForm.FormKC);
            var builder = new StringBuilder(current.Length);
            for (var i = 0; i < current.Length; i++)
            {
                var c = current[i];
                int codePoint = c;
                var width = 1;
                if (char.IsHighSurrogate(c) && i + 1 < current.Length && char.IsLowSurrogate(current[i + 1]))
                {
                    codePoint = char.ConvertToUtf32(c, current[i + 1]);
                    width = 2;
                }

                // 不成對的 surrogate 是 Cs，留著（Python 也是）
                var category = width == 2 || !char.IsSurrogate(c)
                    ? CharUnicodeInfo.GetUnicodeCategory(codePoint)
                    : UnicodeCategory.Surrogate;
                if (category is not (UnicodeCategory.Format or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned)
                    && !IsExplicitlyStripped(codePoint))
                {
                    builder.Append(current, i, width);
                }

                i += width - 1;
            }

            current = builder.ToString();
            if (current == previous)
            {
                break;
            }
        }

        return current;
    }
}
