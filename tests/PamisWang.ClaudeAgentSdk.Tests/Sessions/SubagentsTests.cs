using System.Text.Json.Nodes;
using PamisWang.ClaudeAgentSdk.Sessions;
using static PamisWang.ClaudeAgentSdk.Tests.Sessions.SessionTestKit;

namespace PamisWang.ClaudeAgentSdk.Tests.Sessions;

/// <summary>移植 tests/test_sessions.py 的 <c>TestListSubagents</c> 與 <c>TestGetSubagentMessages</c>。</summary>
public sealed class SubagentsTests : IDisposable
{
    private readonly SessionTestKit _kit = new();

    public void Dispose() => _kit.Dispose();

    private IReadOnlyList<string> List(string sessionId, string? directory = null) =>
        ClaudeSessions.ListSubagents(sessionId, directory, _kit.ConfigDir);

    private IReadOnlyList<SessionMessage> Get(string sessionId, string agentId, string? directory = null, int? limit = null, int offset = 0) =>
        ClaudeSessions.GetSubagentMessages(sessionId, agentId, directory, limit, offset, _kit.ConfigDir);

    /// <summary>tests/test_sessions.py 的 <c>_make_session_with_subagents</c>。回傳 (session_id, subagents_dir)。</summary>
    private (string SessionId, string SubagentsDir) MakeSessionWithSubagents(string projectPath, params string[] agentIds)
    {
        var projectDir = _kit.MakeProjectDir(RealPath(projectPath));
        var (sid, _) = MakeSessionFile(projectDir);
        var subagentsDir = Path.Combine(projectDir, sid, "subagents");
        Directory.CreateDirectory(subagentsDir);
        foreach (var agentId in agentIds)
        {
            WriteText(Path.Combine(subagentsDir, $"agent-{agentId}.jsonl"), Dumps(Obj("""{"type": "user", "uuid": "u", "parentUuid": null}""")) + "\n");
        }

        return (sid, subagentsDir);
    }

    private static JsonObject Entry(string type, string uuid, string? parent, string sid, JsonNode? content = null) =>
        MakeTranscriptEntry(type, uuid, parent, sid, content);

    // Python: test_sessions.py::TestListSubagents::test_invalid_session_id
    [Fact]
    public void ListInvalidSessionId()
    {
        Assert.Empty(List("not-a-uuid"));
        Assert.Empty(List(""));
    }

    // Python: test_sessions.py::TestListSubagents::test_nonexistent_session
    [Fact]
    public void ListNonexistentSession()
    {
        Assert.Empty(List(NewUuid()));
    }

    // Python: test_sessions.py::TestListSubagents::test_session_exists_no_subagents_dir
    [Fact]
    public void ListSessionExistsNoSubagentsDir()
    {
        var projectPath = _kit.MakeProjectPath("proj");
        var (sid, _) = MakeSessionFile(_kit.MakeProjectDir(RealPath(projectPath)));
        Assert.Empty(List(sid, projectPath));
    }

    // Python: test_sessions.py::TestListSubagents::test_empty_subagents_dir
    [Fact]
    public void ListEmptySubagentsDir()
    {
        var projectPath = _kit.MakeProjectPath("proj");
        var (sid, _) = MakeSessionWithSubagents(projectPath);
        Assert.Empty(List(sid, projectPath));
    }

    // Python: test_sessions.py::TestListSubagents::test_happy_path
    [Fact]
    public void ListHappyPath()
    {
        var projectPath = _kit.MakeProjectPath("proj");
        var (sid, _) = MakeSessionWithSubagents(projectPath, "abc123", "def456");
        Assert.Equal(["abc123", "def456"], List(sid, projectPath).Order(StringComparer.Ordinal));
    }

    // Python: test_sessions.py::TestListSubagents::test_ignores_non_agent_files
    [Fact]
    public void ListIgnoresNonAgentFiles()
    {
        var projectPath = _kit.MakeProjectPath("proj");
        var (sid, subagentsDir) = MakeSessionWithSubagents(projectPath, "keep");
        WriteText(Path.Combine(subagentsDir, "agent-keep.meta.json"), "{}");
        WriteText(Path.Combine(subagentsDir, "other.jsonl"), "{}\n");
        WriteText(Path.Combine(subagentsDir, "agent-noext"), "{}");

        Assert.Equal(["keep"], List(sid, projectPath));
    }

    // Python: test_sessions.py::TestListSubagents::test_recurses_into_subdirectories
    [Fact]
    public void ListRecursesIntoSubdirectories()
    {
        var projectPath = _kit.MakeProjectPath("proj");
        var (sid, subagentsDir) = MakeSessionWithSubagents(projectPath, "top");
        var nested = Directory.CreateDirectory(Path.Combine(subagentsDir, "workflows", "run-1")).FullName;
        WriteText(Path.Combine(nested, "agent-nested.jsonl"), "{}\n");

        Assert.Equal(["nested", "top"], List(sid, projectPath).Order(StringComparer.Ordinal));
    }

    // Python: test_sessions.py::TestListSubagents::test_searches_all_projects_without_directory
    [Fact]
    public void ListSearchesAllProjectsWithoutDirectory()
    {
        var projectDir = _kit.MakeProjectDir("/some/project");
        var (sid, _) = MakeSessionFile(projectDir);
        var subagentsDir = Directory.CreateDirectory(Path.Combine(projectDir, sid, "subagents")).FullName;
        WriteText(Path.Combine(subagentsDir, "agent-x.jsonl"), "{}\n");

        Assert.Equal(["x"], List(sid));
    }

    // Python: test_sessions.py::TestGetSubagentMessages::test_invalid_session_id
    [Fact]
    public void GetInvalidSessionId()
    {
        Assert.Empty(Get("not-a-uuid", "abc"));
        Assert.Empty(Get("", "abc"));
    }

    // Python: test_sessions.py::TestGetSubagentMessages::test_empty_agent_id
    [Fact]
    public void GetEmptyAgentId()
    {
        Assert.Empty(Get(NewUuid(), ""));
    }

    // Python: test_sessions.py::TestGetSubagentMessages::test_nonexistent_session
    [Fact]
    public void GetNonexistentSession()
    {
        Assert.Empty(Get(NewUuid(), "abc"));
    }

    // Python: test_sessions.py::TestGetSubagentMessages::test_nonexistent_agent
    [Fact]
    public void GetNonexistentAgent()
    {
        var projectPath = _kit.MakeProjectPath("proj");
        var (sid, _) = MakeSessionWithSubagents(projectPath, "other");
        Assert.Empty(Get(sid, "missing", projectPath));
    }

    // Python: test_sessions.py::TestGetSubagentMessages::test_simple_chain
    [Fact]
    public void GetSimpleChain()
    {
        var projectPath = _kit.MakeProjectPath("proj");
        var (sid, subagentsDir) = MakeSessionWithSubagents(projectPath);
        string u1 = NewUuid(), a1 = NewUuid(), u2 = NewUuid(), a2 = NewUuid();
        JsonObject[] entries =
        [
            Entry("user", u1, null, sid, "task"),
            Entry("assistant", a1, u1, sid, "working"),
            Entry("user", u2, a1, sid, "continue"),
            Entry("assistant", a2, u2, sid, "done"),
        ];
        WriteText(Path.Combine(subagentsDir, "agent-abc.jsonl"), string.Join('\n', entries.Select(Dumps)) + "\n");

        var messages = Get(sid, "abc", projectPath);
        Assert.Equal([u1, a1, u2, a2], messages.Select(m => m.Uuid));
        Assert.Equal("user", messages[0].Type);
        Assert.Equal(sid, messages[0].SessionId);
        Assert.True(JsonEquals(messages[0].Message, """{"role": "user", "content": "task"}"""));
        Assert.Null(messages[0].ParentToolUseId);
        Assert.Equal("assistant", messages[3].Type);
    }

    // Python: test_sessions.py::TestGetSubagentMessages::test_finds_agent_in_nested_subdirectory
    [Fact]
    public void GetFindsAgentInNestedSubdirectory()
    {
        var projectPath = _kit.MakeProjectPath("proj");
        var (sid, subagentsDir) = MakeSessionWithSubagents(projectPath);
        var nested = Directory.CreateDirectory(Path.Combine(subagentsDir, "workflows", "run-1")).FullName;
        string u1 = NewUuid(), a1 = NewUuid();
        WriteText(Path.Combine(nested, "agent-deep.jsonl"),
            string.Join('\n', new[] { Entry("user", u1, null, sid, "hi"), Entry("assistant", a1, u1, sid, "hello") }.Select(Dumps)) + "\n");

        Assert.Equal([u1, a1], Get(sid, "deep", projectPath).Select(m => m.Uuid));
    }

    /// <summary>tests/test_sessions.py 的 <c>TestGetSubagentMessages._write_agent</c>；<paramref name="meta"/> 是 dict、字串或 null。</summary>
    private static void WriteAgent(string subagentsDir, string agentId, string sid, object? meta)
    {
        string u1 = NewUuid(), a1 = NewUuid();
        WriteText(Path.Combine(subagentsDir, $"agent-{agentId}.jsonl"),
            string.Join('\n', new[] { Entry("user", u1, null, sid, "hi"), Entry("assistant", a1, u1, sid, "hello") }.Select(Dumps)) + "\n");
        if (meta is not null)
        {
            WriteText(Path.Combine(subagentsDir, $"agent-{agentId}.meta.json"), meta as string ?? Dumps((JsonObject)meta));
        }
    }

    // Python: test_sessions.py::TestGetSubagentMessages::test_parent_ids_recovered_from_meta_sidecar
    [Fact]
    public void GetParentIdsRecoveredFromMetaSidecar()
    {
        // agent-<id>.meta.json 的 toolUseId / parentAgentId 蓋在每則訊息的 ParentToolUseId / ParentAgentId
        var projectPath = _kit.MakeProjectPath("proj");
        var (sid, subagentsDir) = MakeSessionWithSubagents(projectPath);
        WriteAgent(subagentsDir, "abc", sid,
            Obj("""{"agentType": "general-purpose", "toolUseId": "toolu_01ABC", "parentAgentId": "a-parent", "spawnDepth": 2}"""));

        var messages = Get(sid, "abc", projectPath);
        Assert.Equal(2, messages.Count);
        Assert.All(messages, m => Assert.Equal("toolu_01ABC", m.ParentToolUseId));
        Assert.All(messages, m => Assert.Equal("a-parent", m.ParentAgentId));
    }

    // Python: test_sessions.py::TestGetSubagentMessages::test_parent_ids_from_nested_meta_sidecar
    [Fact]
    public void GetParentIdsFromNestedMetaSidecar()
    {
        // 紀錄在子資料夾裡時，也是讀它旁邊的 sidecar
        var projectPath = _kit.MakeProjectPath("proj");
        var (sid, subagentsDir) = MakeSessionWithSubagents(projectPath);
        var nested = Directory.CreateDirectory(Path.Combine(subagentsDir, "workflows", "run-1")).FullName;
        WriteAgent(nested, "deep", sid, Obj("""{"toolUseId": "toolu_nested"}"""));

        var messages = Get(sid, "deep", projectPath);
        Assert.Equal(["toolu_nested", "toolu_nested"], messages.Select(m => m.ParentToolUseId));
        Assert.All(messages, m => Assert.Null(m.ParentAgentId));
    }

    public static TheoryData<string?> UnusableSidecars => new()
    {
        null, // 沒有 sidecar
        "not json {", // 讀不了的 sidecar
        """{"agentType": "general-purpose"}""", // 沒有 id
        """{"toolUseId": 42, "parentAgentId": ["x"]}""", // 型別不對
    };

    // Python: test_sessions.py::TestGetSubagentMessages::test_parent_ids_none_when_sidecar_missing_or_unusable
    [Theory]
    [MemberData(nameof(UnusableSidecars))]
    public void GetParentIdsNoneWhenSidecarMissingOrUnusable(string? meta)
    {
        var projectPath = _kit.MakeProjectPath("proj");
        var (sid, subagentsDir) = MakeSessionWithSubagents(projectPath);
        // Python 的 dict 參數寫成 json.dumps(meta) 的結果；這裡直接給 JSON 文字（字串參數也是原樣寫入）
        object? sidecar = meta is null ? null : meta.StartsWith('{') ? Obj(meta) : meta;
        WriteAgent(subagentsDir, "x", sid, sidecar);

        var messages = Get(sid, "x", projectPath);
        Assert.Equal(2, messages.Count);
        Assert.All(messages, m => Assert.Null(m.ParentToolUseId));
        Assert.All(messages, m => Assert.Null(m.ParentAgentId));
    }

    // Python: test_sessions.py::TestGetSubagentMessages::test_unreadable_sidecar_degrades_to_none
    [Fact]
    public void GetUnreadableSidecarDegradesToNone()
    {
        // sidecar 存在但讀不了（這裡是資料夾）時，這個盡力而為的讀取不能丟例外
        var projectPath = _kit.MakeProjectPath("proj");
        var (sid, subagentsDir) = MakeSessionWithSubagents(projectPath);
        WriteAgent(subagentsDir, "x", sid, null);
        Directory.CreateDirectory(Path.Combine(subagentsDir, "agent-x.meta.json"));

        var messages = Get(sid, "x", projectPath);
        Assert.Equal(2, messages.Count);
        Assert.All(messages, m => Assert.Null(m.ParentToolUseId));
        Assert.All(messages, m => Assert.Null(m.ParentAgentId));
    }

    // Python: test_sessions.py::TestGetSubagentMessages::test_skips_corrupt_lines
    [Fact]
    public void GetSkipsCorruptLines()
    {
        var projectPath = _kit.MakeProjectPath("proj");
        var (sid, subagentsDir) = MakeSessionWithSubagents(projectPath);
        string u1 = NewUuid(), a1 = NewUuid();
        string[] lines = [Dumps(Entry("user", u1, null, sid, "hi")), "not valid json {", "", Dumps(Entry("assistant", a1, u1, sid, "ok"))];
        WriteText(Path.Combine(subagentsDir, "agent-x.jsonl"), string.Join('\n', lines) + "\n");

        Assert.Equal([u1, a1], Get(sid, "x", projectPath).Select(m => m.Uuid));
    }

    // Python: test_sessions.py::TestGetSubagentMessages::test_limit_and_offset
    [Fact]
    public void GetLimitAndOffset()
    {
        var projectPath = _kit.MakeProjectPath("proj");
        var (sid, subagentsDir) = MakeSessionWithSubagents(projectPath);
        var uuids = Enumerable.Range(0, 6).Select(_ => NewUuid()).ToArray();
        var entries = uuids.Select((uid, i) => Entry(i % 2 == 0 ? "user" : "assistant", uid, i > 0 ? uuids[i - 1] : null, sid, $"m{i}"));
        WriteText(Path.Combine(subagentsDir, "agent-p.jsonl"), string.Join('\n', entries.Select(Dumps)) + "\n");

        Assert.Equal(6, Get(sid, "p", projectPath).Count);
        Assert.Equal(uuids[..2], Get(sid, "p", projectPath, limit: 2).Select(m => m.Uuid));
        Assert.Equal(uuids[2..4], Get(sid, "p", projectPath, limit: 2, offset: 2).Select(m => m.Uuid));
        Assert.Equal(uuids[4..], Get(sid, "p", projectPath, offset: 4).Select(m => m.Uuid));
        Assert.Equal(6, Get(sid, "p", projectPath, limit: 0).Count);
    }

    // Python: test_sessions.py::TestGetSubagentMessages::test_empty_agent_file
    [Fact]
    public void GetEmptyAgentFile()
    {
        var projectPath = _kit.MakeProjectPath("proj");
        var (sid, subagentsDir) = MakeSessionWithSubagents(projectPath);
        WriteText(Path.Combine(subagentsDir, "agent-empty.jsonl"), "");

        Assert.Empty(Get(sid, "empty", projectPath));
    }
}
