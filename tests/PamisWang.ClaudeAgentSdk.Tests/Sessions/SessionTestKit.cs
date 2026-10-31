using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PamisWang.ClaudeAgentSdk.Sessions;

namespace PamisWang.ClaudeAgentSdk.Tests.Sessions;

/// <summary>
/// Python 測試的 fixture：<c>tmp_path</c> 與 <c>claude_config_dir</c>（<c>tmp_path/.claude/projects</c>）。
/// Python 用 monkeypatch 設 <c>CLAUDE_CONFIG_DIR</c>；這裡把 <see cref="ConfigDir"/> 當 <c>claudeConfigDir</c> 傳進去，不改程序的環境變數。
/// </summary>
internal sealed class SessionTestKit : IDisposable
{
    private readonly TempDirectory _temp = new();

    public SessionTestKit()
    {
        ConfigDir = Path.Combine(TmpPath, ".claude");
        Directory.CreateDirectory(Path.Combine(ConfigDir, "projects"));
    }

    /// <summary>pytest 的 <c>tmp_path</c>。</summary>
    public string TmpPath => _temp.Path;

    /// <summary><c>claude_config_dir</c> fixture。</summary>
    public string ConfigDir { get; }

    /// <summary>Python 的 <c>str(tmp_path / name)</c>，並建立資料夾（<c>Path(project_path).mkdir(parents=True)</c>）。</summary>
    public string MakeProjectPath(string name)
    {
        var path = Path.Combine(TmpPath, name);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary><c>os.path.realpath</c>。</summary>
    public static string RealPath(string path) => SessionPaths.RealPath(path);

    /// <summary><c>_make_project_dir</c>：建立 sanitize 過的專案資料夾。</summary>
    public string MakeProjectDir(string projectPath)
    {
        var projectDir = Path.Combine(ConfigDir, "projects", SessionPaths.SanitizePath(projectPath));
        Directory.CreateDirectory(projectDir);
        return projectDir;
    }

    /// <summary>Python 的 <c>str(uuid.uuid4())</c>。</summary>
    public static string NewUuid() => Guid.NewGuid().ToString();

    /// <summary>Python 的 <c>json.dumps(obj)</c>（預設的 <c>", "</c>、<c>": "</c> 分隔）。</summary>
    public static string Dumps(JsonNode? node) => PyJson.Dumps(node, compact: false);

    /// <summary>Python 的 <c>json.dumps(obj, separators=(",", ":"))</c>。</summary>
    public static string DumpsCompact(JsonNode? node) => PyJson.Dumps(node, compact: true);

    /// <summary>把 JSON 文字變成 dict（測試裡寫 dict 字面值用）。</summary>
    public static JsonObject Obj(string json) => (JsonObject)JsonNode.Parse(json)!;

    /// <summary><c>Path.write_text(text)</c>（UTF-8、不加 BOM）。</summary>
    public static void WriteText(string path, string text) => File.WriteAllText(path, text, new UTF8Encoding(false));

    /// <summary><c>os.utime(path, (mtime, mtime))</c>，秒。</summary>
    public static void SetMtime(string path, double seconds) =>
        File.SetLastWriteTimeUtc(path, DateTime.UnixEpoch.AddTicks((long)(seconds * TimeSpan.TicksPerSecond)));

    /// <summary>tests/test_sessions.py 的 <c>_make_session_file</c>。回傳 (session_id, file_path)。</summary>
    public static (string SessionId, string FilePath) MakeSessionFile(
        string projectDir,
        string? sessionId = null,
        string firstPrompt = "Hello Claude",
        string? summary = null,
        string? customTitle = null,
        string? gitBranch = null,
        string? cwd = null,
        bool isSidechain = false,
        bool isMetaOnly = false,
        double? mtime = null)
    {
        var sid = sessionId ?? NewUuid();
        var filePath = Path.Combine(projectDir, $"{sid}.jsonl");

        var firstEntry = new JsonObject { ["type"] = "user", ["message"] = new JsonObject { ["role"] = "user", ["content"] = firstPrompt } };
        if (cwd is not null)
        {
            firstEntry["cwd"] = cwd;
        }

        if (gitBranch is not null)
        {
            firstEntry["gitBranch"] = gitBranch;
        }

        if (isSidechain)
        {
            firstEntry["isSidechain"] = true;
        }

        if (isMetaOnly)
        {
            firstEntry["isMeta"] = true;
        }

        var lines = new List<string>
        {
            Dumps(firstEntry),
            Dumps(new JsonObject { ["type"] = "assistant", ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = "Hi there!" } }),
        };

        var tailEntry = new JsonObject { ["type"] = "summary" };
        if (summary is not null)
        {
            tailEntry["summary"] = summary;
        }

        if (customTitle is not null)
        {
            tailEntry["customTitle"] = customTitle;
        }

        if (gitBranch is not null)
        {
            tailEntry["gitBranch"] = gitBranch;
        }

        lines.Add(Dumps(tailEntry));
        WriteText(filePath, string.Join('\n', lines) + "\n");

        if (mtime is { } seconds)
        {
            SetMtime(filePath, seconds);
        }

        return (sid, filePath);
    }

    /// <summary>tests/test_sessions.py 的 <c>_make_transcript_entry</c>。<paramref name="content"/> 是字串或 list。</summary>
    public static JsonObject MakeTranscriptEntry(
        string entryType, string entryUuid, string? parentUuid, string sessionId, JsonNode? content = null, params (string Key, JsonNode? Value)[] extras)
    {
        var entry = new JsonObject
        {
            ["type"] = entryType,
            ["uuid"] = entryUuid,
            ["parentUuid"] = parentUuid,
            ["sessionId"] = sessionId,
        };
        if (content is not null)
        {
            var role = entryType is "user" or "assistant" ? entryType : "user";
            entry["message"] = new JsonObject { ["role"] = role, ["content"] = content };
        }

        foreach (var (key, value) in extras)
        {
            entry[key] = value;
        }

        return entry;
    }

    /// <summary>tests/test_sessions.py 的 <c>_write_transcript</c>。</summary>
    public static string WriteTranscript(string projectDir, string sessionId, IEnumerable<JsonObject> entries)
    {
        var filePath = Path.Combine(projectDir, $"{sessionId}.jsonl");
        WriteText(filePath, string.Join('\n', entries.Select(Dumps)) + "\n");
        return filePath;
    }

    /// <summary>
    /// tests/test_sessions.py 的 <c>_parallel_tool_turn</c>：一輪 <paramref name="tools"/> 個平行工具呼叫。
    /// 回傳 (calls, results)，都是檔案順序。
    /// </summary>
    public static (List<JsonObject> Calls, List<JsonObject> Results) ParallelToolTurn(string sid, string parentUuid, int tools, int[] resultOrder)
    {
        var calls = new List<JsonObject>();
        for (var i = 0; i < tools; i++)
        {
            var call = MakeTranscriptEntry(
                "assistant",
                $"call-{i}",
                calls.Count > 0 ? (string)calls[^1]["uuid"]! : parentUuid,
                sid,
                new JsonArray(new JsonObject { ["type"] = "tool_use", ["id"] = $"toolu_{i}", ["name"] = "Read", ["input"] = new JsonObject() }),
                ("timestamp", $"2026-01-01T00:00:01.{i:000}Z"));
            call["message"]!["id"] = "msg_parallel";
            calls.Add(call);
        }

        var results = resultOrder
            .Select((i, position) => MakeTranscriptEntry(
                "user",
                $"result-{i}",
                $"call-{i}",
                sid,
                new JsonArray(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = $"toolu_{i}", ["content"] = "ok" }),
                ("sourceToolAssistantUUID", $"call-{i}"),
                ("timestamp", $"2026-01-01T00:00:02.{position:000}Z")))
            .ToList();
        return (calls, results);
    }

    /// <summary>tests/test_sessions.py 的 <c>_block_ids</c>：依序取出每個 <paramref name="blockType"/> 區塊的 <paramref name="field"/>。</summary>
    public static List<string> BlockIds(IEnumerable<SessionMessage> messages, string blockType, string field) =>
        messages
            .Where(m => m.Message is { } message && message.GetProperty("content").ValueKind == JsonValueKind.Array)
            .SelectMany(m => m.Message!.Value.GetProperty("content").EnumerateArray())
            .Where(block => block.GetProperty("type").GetString() == blockType)
            .Select(block => block.GetProperty(field).GetString()!)
            .ToList();

    /// <summary>Python 的 <c>message == {...}</c>：兩邊都轉成 JSON 再比（鍵的順序不算）。</summary>
    public static bool JsonEquals(JsonElement? actual, string expectedJson) =>
        actual is { } element && JsonNode.DeepEquals(JsonNode.Parse(element.GetRawText()), JsonNode.Parse(expectedJson));

    /// <summary>Python 的 <c>path.read_text()</c>。</summary>
    public static string ReadText(string path) => File.ReadAllText(path);

    public void Dispose() => _temp.Dispose();
}
