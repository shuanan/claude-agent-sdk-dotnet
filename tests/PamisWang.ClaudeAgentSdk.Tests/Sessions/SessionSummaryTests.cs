using System.Text.Json;
using System.Text.Json.Nodes;
using PamisWang.ClaudeAgentSdk.Sessions;
using static PamisWang.ClaudeAgentSdk.Tests.Sessions.SessionTestKit;

namespace PamisWang.ClaudeAgentSdk.Tests.Sessions;

/// <summary>
/// 移植 tests/test_session_summary.py 的 <c>TestFoldSessionSummary</c>、<c>TestSummaryEntryToSdkInfo</c>、<c>TestParityWithLiteParse</c>。
/// 其他類別都是 session store（InMemorySessionStore、list_sessions_from_store），不在這次的範圍。
/// </summary>
public sealed class SessionSummaryTests
{
    private const string Dir = "/workspace/project";
    private static readonly string ProjectKey = ClaudeSessions.ProjectKeyForDirectory(Dir);
    private static readonly SessionKey Key = new(ProjectKey, "11111111-1111-4111-8111-111111111111");

    /// <summary>tests/test_session_summary.py 的 <c>_user</c>。</summary>
    private static JsonObject User(JsonNode text, string ts = "2024-01-01T00:00:00.000Z", params (string Key, JsonNode? Value)[] extra)
    {
        var entry = new JsonObject
        {
            ["type"] = "user",
            ["timestamp"] = ts,
            ["message"] = new JsonObject { ["role"] = "user", ["content"] = text },
        };
        foreach (var (key, value) in extra)
        {
            entry[key] = value;
        }

        return entry;
    }

    private static JsonElement E(JsonObject entry) => PyJson.ToElement(entry);

    private static JsonElement E(string json) => PyJson.ToElement(Obj(json));

    private static SessionSummaryEntry Fold(SessionSummaryEntry? prev, SessionKey key, params JsonElement[] entries) =>
        ClaudeSessions.FoldSessionSummary(prev, key, entries);

    // Python: test_session_summary.py::TestFoldSessionSummary::test_init_from_none
    [Fact]
    public void FoldInitFromNone()
    {
        var s = Fold(null, Key);
        Assert.Equal(Key.SessionId, s.SessionId);
        Assert.Equal(0, s.Mtime);
        Assert.Empty(s.Data);
    }

    // Python: test_session_summary.py::TestFoldSessionSummary::test_set_once_fields_freeze
    [Fact]
    public void FoldSetOnceFieldsFreeze()
    {
        var s = Fold(null, Key,
            E("""{"type": "x", "timestamp": "2024-01-01T00:00:00.000Z", "cwd": "/a", "isSidechain": false}"""),
            E("""{"type": "x", "timestamp": "2024-01-01T00:00:05.000Z", "cwd": "/b"}"""));
        Assert.Equal(1704067200000, (long)s.Data["created_at"]!);
        Assert.Equal("/a", (string?)s.Data["cwd"]);
        Assert.False((bool)s.Data["is_sidechain"]!);

        // 第二次附加不能蓋掉只設一次的欄位
        var s2 = Fold(s, Key, E("""{"type": "x", "timestamp": "2024-01-02T00:00:00.000Z", "cwd": "/c", "isSidechain": true}"""));
        Assert.Equal(1704067200000, (long)s2.Data["created_at"]!);
        Assert.Equal("/a", (string?)s2.Data["cwd"]);
        Assert.False((bool)s2.Data["is_sidechain"]!);
    }

    // Python: test_session_summary.py::TestFoldSessionSummary::test_last_wins_overwrite
    [Fact]
    public void FoldLastWinsOverwrite()
    {
        var s = Fold(null, Key,
            E("""{"type": "x", "timestamp": "2024-01-01T00:00:00Z", "customTitle": "t1", "gitBranch": "main"}"""),
            E("""{"type": "x", "timestamp": "2024-01-01T00:00:01Z", "customTitle": "t2"}"""));
        Assert.Equal("t2", (string?)s.Data["custom_title"]);
        Assert.Equal("main", (string?)s.Data["git_branch"]);

        var s2 = Fold(s, Key, E("""{"type": "x", "aiTitle": "ai", "lastPrompt": "lp", "summary": "sm", "gitBranch": "dev"}"""));
        Assert.Equal("t2", (string?)s2.Data["custom_title"]);
        Assert.Equal("ai", (string?)s2.Data["ai_title"]);
        Assert.Equal("lp", (string?)s2.Data["last_prompt"]);
        Assert.Equal("sm", (string?)s2.Data["summary_hint"]);
        Assert.Equal("dev", (string?)s2.Data["git_branch"]);
    }

    // Python: test_session_summary.py::TestFoldSessionSummary::test_mtime_not_derived_from_entries
    [Fact]
    public void FoldMtimeNotDerivedFromEntries()
    {
        // fold 不動 mtime：它是 store 存檔的時間，跟 list_sessions().mtime 同一個時鐘
        var s = Fold(null, Key,
            E("""{"type": "x", "timestamp": "2024-01-01T00:00:05.000Z"}"""),
            E("""{"type": "x", "timestamp": "2024-01-01T00:00:01.000Z"}"""));
        // 新的 session：mtime 是 0，由 store 蓋上
        Assert.Equal(0, s.Mtime);

        // 之前的 mtime 原樣保留，不管這批紀錄的時間
        var prev = new SessionSummaryEntry(Key.SessionId, 42, new JsonObject());
        var s2 = Fold(prev, Key, E("""{"type": "x", "timestamp": "2024-01-01T00:00:10.000Z"}"""));
        Assert.Equal(42, s2.Mtime);
    }

    // Python: test_session_summary.py::TestFoldSessionSummary::test_tag_set_and_clear
    [Fact]
    public void FoldTagSetAndClear()
    {
        var s = Fold(null, Key, E("""{"type": "tag", "tag": "wip"}"""));
        Assert.Equal("wip", (string?)s.Data["tag"]);
        var s2 = Fold(s, Key, E("""{"type": "tag", "tag": ""}"""));
        Assert.False(s2.Data.ContainsKey("tag"));
        // 不是 tag 的紀錄裡的 "tag"（例如 tool_use 的輸入）不管
        var s3 = Fold(s, Key, E("""{"type": "user", "tag": "ignored"}"""));
        Assert.Equal("wip", (string?)s3.Data["tag"]);
    }

    // Python: test_session_summary.py::TestFoldSessionSummary::test_sidechain_from_first_entry
    [Fact]
    public void FoldSidechainFromFirstEntry()
    {
        var s = Fold(null, Key, E("""{"type": "x", "timestamp": "2024-01-01T00:00:00Z", "isSidechain": true}"""));
        Assert.True((bool)s.Data["is_sidechain"]!);
    }

    // Python: test_session_summary.py::TestFoldSessionSummary::test_sidechain_latched_when_first_entry_lacks_timestamp
    [Fact]
    public void FoldSidechainLatchedWhenFirstEntryLacksTimestamp()
    {
        // 回歸測試：第一筆沒有（或解析不了）時間時，is_sidechain 也要在第一筆就定下來，第二筆不能把它改成 false
        var s = Fold(null, Key,
            E("""{"type": "user", "isSidechain": true}"""),
            E("""{"type": "x", "timestamp": "2024-01-01T00:00:00Z"}"""));
        Assert.True((bool)s.Data["is_sidechain"]!);
        // created_at 還是取第一個解析得到的時間
        Assert.Equal(1704067200000, (long)s.Data["created_at"]!);
    }

    // Python: test_session_summary.py::TestFoldSessionSummary::test_first_prompt_skips_meta_tool_result_and_compact
    [Fact]
    public void FoldFirstPromptSkipsMetaToolResultAndCompact()
    {
        var s = Fold(null, Key,
            E(User("ignored meta", "2024-01-01T00:00:00.000Z", ("isMeta", true))),
            E(User("ignored compact", "2024-01-01T00:00:00.000Z", ("isCompactSummary", true))),
            E(User(JsonNode.Parse("""[{"type": "tool_result", "tool_use_id": "x", "content": "res"}]""")!)),
            E(User("real first")),
            E(User("not me")));
        Assert.Equal("real first", (string?)s.Data["first_prompt"]);
        Assert.True((bool)s.Data["first_prompt_locked"]!);
    }

    // Python: test_session_summary.py::TestFoldSessionSummary::test_first_prompt_command_fallback
    [Fact]
    public void FoldFirstPromptCommandFallback()
    {
        var s = Fold(null, Key, E(User("<command-name>/init</command-name> stuff")), E(User("<command-name>/second</command-name>")));
        Assert.NotEqual(true, (bool?)s.Data["first_prompt_locked"]);
        Assert.Equal("/init", (string?)s.Data["command_fallback"]);
        // 之後真的提示會定下來
        var s2 = Fold(s, Key, E(User("now real")));
        Assert.Equal("now real", (string?)s2.Data["first_prompt"]);
        Assert.True((bool)s2.Data["first_prompt_locked"]!);
    }

    // Python: test_session_summary.py::TestFoldSessionSummary::test_first_prompt_skip_pattern
    [Fact]
    public void FoldFirstPromptSkipPattern()
    {
        var s = Fold(null, Key, E(User("<local-command-stdout> some output")), E(User("hello")));
        Assert.Equal("hello", (string?)s.Data["first_prompt"]);
    }

    // Python: test_session_summary.py::TestFoldSessionSummary::test_first_prompt_truncated
    [Fact]
    public void FoldFirstPromptTruncated()
    {
        var s = Fold(null, Key, E(User(new string('x', 300))));
        var firstPrompt = (string)s.Data["first_prompt"]!;
        Assert.True(firstPrompt.Length <= 201);
        Assert.EndsWith("\x2026", firstPrompt, StringComparison.Ordinal);
    }

    // Python: test_session_summary.py::TestFoldSessionSummary::test_prev_is_not_mutated
    [Fact]
    public void FoldPrevIsNotMutated()
    {
        var prev = new SessionSummaryEntry("a", 5, new JsonObject());
        Fold(prev, Key, E("""{"type": "x", "customTitle": "t"}"""));
        Assert.Equal("a", prev.SessionId);
        Assert.Equal(5, prev.Mtime);
        Assert.Empty(prev.Data);
    }

    private static SdkSessionInfo? ToInfo(string sessionId, long mtime, string data, string? projectPath) =>
        ClaudeSessions.SummaryEntryToSdkInfo(new SessionSummaryEntry(sessionId, mtime, Obj(data)), projectPath);

    // Python: test_session_summary.py::TestSummaryEntryToSdkInfo::test_sidechain_returns_none
    [Fact]
    public void ToInfoSidechainReturnsNone()
    {
        Assert.Null(ToInfo("s", 1, """{"is_sidechain": true, "custom_title": "t"}""", null));
    }

    // Python: test_session_summary.py::TestSummaryEntryToSdkInfo::test_empty_summary_returns_none
    [Fact]
    public void ToInfoEmptySummaryReturnsNone()
    {
        Assert.Null(ToInfo("s", 1, "{}", null));
    }

    // Python: test_session_summary.py::TestSummaryEntryToSdkInfo::test_precedence_chain
    [Fact]
    public void ToInfoPrecedenceChain()
    {
        var data = Obj("""
            {"first_prompt": "fp", "first_prompt_locked": true, "command_fallback": "/cmd", "summary_hint": "sh",
             "last_prompt": "lp", "ai_title": "ai", "custom_title": "ct"}
            """);
        var baseEntry = new SessionSummaryEntry("s", 1, data);

        var info = ClaudeSessions.SummaryEntryToSdkInfo(baseEntry, null);
        Assert.True(info is { Summary: "ct", CustomTitle: "ct" });

        data.Remove("custom_title");
        info = ClaudeSessions.SummaryEntryToSdkInfo(baseEntry, null);
        Assert.True(info is { Summary: "ai", CustomTitle: "ai" });

        data.Remove("ai_title");
        info = ClaudeSessions.SummaryEntryToSdkInfo(baseEntry, null);
        Assert.True(info is { Summary: "lp", CustomTitle: null });

        data.Remove("last_prompt");
        info = ClaudeSessions.SummaryEntryToSdkInfo(baseEntry, null);
        Assert.True(info is { Summary: "sh" });

        data.Remove("summary_hint");
        info = ClaudeSessions.SummaryEntryToSdkInfo(baseEntry, null);
        Assert.True(info is { Summary: "fp", FirstPrompt: "fp" });

        data["first_prompt_locked"] = false;
        info = ClaudeSessions.SummaryEntryToSdkInfo(baseEntry, null);
        Assert.True(info is { Summary: "/cmd", FirstPrompt: "/cmd" });
    }

    // Python: test_session_summary.py::TestSummaryEntryToSdkInfo::test_cwd_fallback_to_project_path
    [Fact]
    public void ToInfoCwdFallbackToProjectPath()
    {
        Assert.True(ToInfo("s", 1, """{"custom_title": "t"}""", "/proj") is { Cwd: "/proj" });
        Assert.True(ToInfo("s", 1, """{"custom_title": "t", "cwd": "/own"}""", "/proj") is { Cwd: "/own" });
    }

    // Python: test_session_summary.py::TestSummaryEntryToSdkInfo::test_field_passthrough
    [Fact]
    public void ToInfoFieldPassthrough()
    {
        var info = ToInfo("s", 99, """{"custom_title": "t", "git_branch": "main", "tag": "wip", "created_at": 50}""", null);
        Assert.NotNull(info);
        Assert.Equal("s", info.SessionId);
        Assert.Equal(99, info.LastModified);
        Assert.Equal("main", info.GitBranch);
        Assert.Equal("wip", info.Tag);
        Assert.Equal(50, info.CreatedAt);
        // FileSize 只有本機的 JSONL 有；store 的摘要一律是 null
        Assert.Null(info.FileSize);
    }

    // Python: test_session_summary.py::TestParityWithLiteParse::test_incremental_equals_batch
    [Fact]
    public void ParityIncrementalEqualsBatch()
    {
        // SummaryEntryToSdkInfo(fold(...)) 要跟同一串紀錄的 ParseSessionInfo 一樣
        const string Sid = "22222222-2222-4222-8222-222222222222";
        var key = new SessionKey(ProjectKey, Sid);
        List<JsonObject> entries =
        [
            User("<command-name>/clear</command-name>", "2024-01-01T00:00:00.000Z", ("cwd", "/work"), ("gitBranch", "main")),
            User("ignored", "2024-01-01T00:00:01.000Z", ("isMeta", true)),
            User("real prompt here", "2024-01-01T00:00:02.000Z"),
            Obj("""{"type": "assistant", "timestamp": "2024-01-01T00:00:03.000Z", "message": {"content": [{"type": "text", "text": "ok"}]}}"""),
            Obj("""{"type": "x", "timestamp": "2024-01-01T00:00:04.000Z", "aiTitle": "AI Named"}"""),
            Obj("""{"type": "tag", "timestamp": "2024-01-01T00:00:05.000Z", "tag": "wip"}"""),
            Obj("""{"type": "x", "timestamp": "2024-01-01T00:00:06.000Z", "customTitle": "User Named", "gitBranch": "feature"}"""),
        ];

        // 逐步：分兩批附加，測試延續
        var folded = Fold(null, key, [.. entries.Take(3).Select(E)]);
        folded = Fold(folded, key, [.. entries.Skip(3).Select(E)]);
        var incremental = ClaudeSessions.SummaryEntryToSdkInfo(folded, "/work");

        // 一次全部：list_sessions_from_store 的後備路徑
        var jsonl = ClaudeSessions.EntriesToJsonl(entries);
        var batch = SessionLite.ParseSessionInfo(Sid, ClaudeSessions.JsonlToLite(jsonl, folded.Mtime), "/work");

        Assert.NotNull(incremental);
        Assert.NotNull(batch);
        // FileSize 是只對 JSONL 有意義的位元組數
        Assert.Equal(incremental, batch with { FileSize = null });
    }

    // Python: test_session_summary.py::TestParityWithLiteParse::test_parity_first_prompt_only
    [Fact]
    public void ParityFirstPromptOnly()
    {
        const string Sid = "33333333-3333-4333-8333-333333333333";
        var key = new SessionKey(ProjectKey, Sid);
        List<JsonObject> entries = [User("just a prompt", "2024-02-01T00:00:00.000Z", ("cwd", "/w"))];

        var folded = Fold(null, key, [.. entries.Select(E)]);
        var incremental = ClaudeSessions.SummaryEntryToSdkInfo(folded, "/w");
        var batch = SessionLite.ParseSessionInfo(Sid, ClaudeSessions.JsonlToLite(ClaudeSessions.EntriesToJsonl(entries), folded.Mtime), "/w");

        Assert.NotNull(incremental);
        Assert.NotNull(batch);
        Assert.Equal(incremental, batch with { FileSize = null });
    }
}
