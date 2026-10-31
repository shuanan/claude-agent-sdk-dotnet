using System.Text.Json.Nodes;
using PamisWang.ClaudeAgentSdk.Sessions;
using static PamisWang.ClaudeAgentSdk.Tests.Sessions.SessionTestKit;

namespace PamisWang.ClaudeAgentSdk.Tests.Sessions;

/// <summary>移植 tests/test_sessions.py 的 <c>TestGetSessionMessages</c> 與 <c>TestSessionMessageType</c>。</summary>
public sealed class SessionMessagesTests : IDisposable
{
    private readonly SessionTestKit _kit = new();

    public void Dispose() => _kit.Dispose();

    private IReadOnlyList<SessionMessage> Get(string sessionId, string? directory = null, int? limit = null, int offset = 0) =>
        ClaudeSessions.GetSessionMessages(sessionId, directory, limit, offset, _kit.ConfigDir);

    private (string ProjectPath, string ProjectDir) Project()
    {
        var projectPath = _kit.MakeProjectPath("proj");
        return (projectPath, _kit.MakeProjectDir(RealPath(projectPath)));
    }

    private static JsonObject Entry(string type, string uuid, string? parent, string sid, JsonNode? content = null, params (string, JsonNode?)[] extras) =>
        MakeTranscriptEntry(type, uuid, parent, sid, content, extras);

    // Python: test_sessions.py::TestGetSessionMessages::test_invalid_session_id
    [Fact]
    public void InvalidSessionId()
    {
        Assert.Empty(Get("not-a-uuid"));
        Assert.Empty(Get(""));
    }

    // Python: test_sessions.py::TestGetSessionMessages::test_nonexistent_session
    [Fact]
    public void NonexistentSession()
    {
        Assert.Empty(Get(NewUuid()));
    }

    // Python: test_sessions.py::TestGetSessionMessages::test_no_config_dir
    [Fact]
    public void NoConfigDir()
    {
        Assert.Empty(ClaudeSessions.GetSessionMessages(NewUuid(), claudeConfigDir: Path.Combine(_kit.TmpPath, "nonexistent")));
    }

    // Python: test_sessions.py::TestGetSessionMessages::test_simple_chain
    [Fact]
    public void SimpleChain()
    {
        var (projectPath, projectDir) = Project();
        var sid = NewUuid();
        string u1 = NewUuid(), a1 = NewUuid(), u2 = NewUuid(), a2 = NewUuid();
        WriteTranscript(projectDir, sid,
        [
            Entry("user", u1, null, sid, "hello"),
            Entry("assistant", a1, u1, sid, "hi!"),
            Entry("user", u2, a1, sid, "thanks"),
            Entry("assistant", a2, u2, sid, "welcome"),
        ]);

        var messages = Get(sid, projectPath);
        Assert.Equal(4, messages.Count);

        // 由舊到新：根 → 葉
        Assert.Equal("user", messages[0].Type);
        Assert.Equal(u1, messages[0].Uuid);
        Assert.Equal(sid, messages[0].SessionId);
        Assert.True(JsonEquals(messages[0].Message, """{"role": "user", "content": "hello"}"""));
        Assert.Null(messages[0].ParentToolUseId);

        Assert.Equal("assistant", messages[1].Type);
        Assert.Equal(a1, messages[1].Uuid);
        Assert.True(JsonEquals(messages[1].Message, """{"role": "assistant", "content": "hi!"}"""));

        Assert.Equal("user", messages[2].Type);
        Assert.Equal(u2, messages[2].Uuid);

        Assert.Equal("assistant", messages[3].Type);
        Assert.Equal(a2, messages[3].Uuid);
    }

    // Python: test_sessions.py::TestGetSessionMessages::test_filters_meta_messages
    [Fact]
    public void FiltersMetaMessages()
    {
        var (projectPath, projectDir) = Project();
        var sid = NewUuid();
        string u1 = NewUuid(), meta = NewUuid(), a1 = NewUuid();
        WriteTranscript(projectDir, sid,
        [
            Entry("user", u1, null, sid, "hello"),
            // 鏈上的 meta 使用者訊息：走得過去，但不回傳
            Entry("user", meta, u1, sid, "meta", ("isMeta", true)),
            Entry("assistant", a1, meta, sid, "hi"),
        ]);

        var messages = Get(sid, projectPath);
        // 只有 u1 與 a1（meta 濾掉）
        Assert.Equal([u1, a1], messages.Select(m => m.Uuid));
    }

    // Python: test_sessions.py::TestGetSessionMessages::test_filters_non_user_assistant_from_chain
    [Fact]
    public void FiltersNonUserAssistantFromChain()
    {
        var (projectPath, projectDir) = Project();
        var sid = NewUuid();
        string u1 = NewUuid(), prog = NewUuid(), a1 = NewUuid();
        WriteTranscript(projectDir, sid,
        [
            Entry("user", u1, null, sid, "hello"),
            // 鏈上的 progress
            Entry("progress", prog, u1, sid),
            Entry("assistant", a1, prog, sid, "hi"),
        ]);

        // progress 走得過去，但不回傳
        Assert.Equal([u1, a1], Get(sid, projectPath).Select(m => m.Uuid));
    }

    // Python: test_sessions.py::TestGetSessionMessages::test_keeps_compact_summary
    [Fact]
    public void KeepsCompactSummary()
    {
        var (projectPath, projectDir) = Project();
        var sid = NewUuid();
        string u1 = NewUuid(), a1 = NewUuid();
        WriteTranscript(projectDir, sid,
        [
            Entry("user", u1, null, sid, "compact summary", ("isCompactSummary", true)),
            Entry("assistant", a1, u1, sid, "hi"),
        ]);

        var messages = Get(sid, projectPath);
        Assert.Equal(2, messages.Count);
        Assert.Equal(u1, messages[0].Uuid); // compact 摘要留著
    }

    // Python: test_sessions.py::TestGetSessionMessages::test_limit_and_offset
    [Fact]
    public void LimitAndOffset()
    {
        var (projectPath, projectDir) = Project();
        var sid = NewUuid();

        // 6 則訊息的鏈：u→a→u→a→u→a
        var uuids = Enumerable.Range(0, 6).Select(_ => NewUuid()).ToArray();
        WriteTranscript(projectDir, sid,
            uuids.Select((uid, i) => Entry(i % 2 == 0 ? "user" : "assistant", uid, i > 0 ? uuids[i - 1] : null, sid, $"m{i}")));

        // 沒有 limit/offset
        Assert.Equal(6, Get(sid, projectPath).Count);

        // limit=2
        Assert.Equal(uuids[..2], Get(sid, projectPath, limit: 2).Select(m => m.Uuid));

        // offset=2, limit=2
        Assert.Equal(uuids[2..4], Get(sid, projectPath, limit: 2, offset: 2).Select(m => m.Uuid));

        // 只有 offset
        Assert.Equal(uuids[4..], Get(sid, projectPath, offset: 4).Select(m => m.Uuid));

        // limit=0 回傳全部
        Assert.Equal(6, Get(sid, projectPath, limit: 0).Count);

        // offset 超過結尾
        Assert.Empty(Get(sid, projectPath, offset: 100));
    }

    // Python: test_sessions.py::TestGetSessionMessages::test_picks_main_chain_over_sidechain
    [Fact]
    public void PicksMainChainOverSidechain()
    {
        var (projectPath, projectDir) = Project();
        var sid = NewUuid();
        string root = NewUuid(), mainLeaf = NewUuid(), sideLeaf = NewUuid();
        WriteTranscript(projectDir, sid,
        [
            Entry("user", root, null, sid, "root"),
            // 主線接下去
            Entry("assistant", mainLeaf, root, sid, "main"),
            // sidechain 的分支（也從 root 分出），不能當葉節點
            Entry("assistant", sideLeaf, root, sid, "side", ("isSidechain", true)),
        ]);

        Assert.Equal([root, mainLeaf], Get(sid, projectPath).Select(m => m.Uuid)); // 選主線，不選 sidechain
    }

    // Python: test_sessions.py::TestGetSessionMessages::test_picks_latest_leaf_by_file_position
    [Fact]
    public void PicksLatestLeafByFilePosition()
    {
        var (projectPath, projectDir) = Project();
        var sid = NewUuid();
        string root = NewUuid(), oldLeaf = NewUuid(), newLeaf = NewUuid();
        // 兩個葉節點都從 root 分出；newLeaf 在檔案後面
        WriteTranscript(projectDir, sid,
        [
            Entry("user", root, null, sid, "root"),
            Entry("assistant", oldLeaf, root, sid, "old"),
            Entry("assistant", newLeaf, root, sid, "new"),
        ]);

        // newLeaf 的檔案位置比較後面 → 選它
        Assert.Equal([root, newLeaf], Get(sid, projectPath).Select(m => m.Uuid));
    }

    // Python: test_sessions.py::TestGetSessionMessages::test_terminal_non_message_walked_back
    [Fact]
    public void TerminalNonMessageWalkedBack()
    {
        var (projectPath, projectDir) = Project();
        var sid = NewUuid();
        string u1 = NewUuid(), a1 = NewUuid(), prog = NewUuid();
        WriteTranscript(projectDir, sid,
        [
            Entry("user", u1, null, sid, "hi"),
            Entry("assistant", a1, u1, sid, "hello"),
            // 最後是 progress：要往回走到 a1
            Entry("progress", prog, a1, sid),
        ]);

        Assert.Equal([u1, a1], Get(sid, projectPath).Select(m => m.Uuid));
    }

    // Python: test_sessions.py::TestGetSessionMessages::test_corrupt_lines_skipped
    [Fact]
    public void CorruptLinesSkipped()
    {
        var (projectPath, projectDir) = Project();
        var sid = NewUuid();
        string u1 = NewUuid(), a1 = NewUuid();
        string[] lines =
        [
            Dumps(Entry("user", u1, null, sid, "hi")),
            "not valid json {{{",
            "",
            Dumps(Entry("assistant", a1, u1, sid, "hello")),
        ];
        WriteText(Path.Combine(projectDir, $"{sid}.jsonl"), string.Join('\n', lines) + "\n");

        Assert.Equal(2, Get(sid, projectPath).Count);
    }

    // Python: test_sessions.py::TestGetSessionMessages::test_search_all_projects_when_no_dir
    [Fact]
    public void SearchAllProjectsWhenNoDir()
    {
        _ = _kit.MakeProjectDir("/path/one");
        var proj2 = _kit.MakeProjectDir("/path/two");
        var sid = NewUuid();
        string u1 = NewUuid(), a1 = NewUuid();

        // session 只在 proj2；proj1 存在但沒有這個 session
        WriteTranscript(proj2, sid, [Entry("user", u1, null, sid, "hi"), Entry("assistant", a1, u1, sid, "hello")]);

        var messages = Get(sid); // 沒給 directory
        Assert.Equal(2, messages.Count);
        Assert.Equal(u1, messages[0].Uuid);
    }

    // Python: test_sessions.py::TestGetSessionMessages::test_cycle_detection
    [Fact]
    public void CycleDetection()
    {
        var (projectPath, projectDir) = Project();
        var sid = NewUuid();
        string u1 = NewUuid(), a1 = NewUuid();

        // a1 → u1 → a1（迴圈）
        WriteTranscript(projectDir, sid, [Entry("user", u1, a1, sid, "hi"), Entry("assistant", a1, u1, sid, "hello")]);

        // 要能結束。兩筆互為父紀錄 → 沒有終端 → 空的
        Assert.Empty(Get(sid, projectPath));
    }

    // Python: test_sessions.py::TestGetSessionMessages::test_empty_transcript_file
    [Fact]
    public void EmptyTranscriptFile()
    {
        var (projectPath, projectDir) = Project();
        var sid = NewUuid();
        WriteText(Path.Combine(projectDir, $"{sid}.jsonl"), "");
        Assert.Empty(Get(sid, projectPath));
    }

    // Python: test_sessions.py::TestGetSessionMessages::test_ignores_non_transcript_types
    [Fact]
    public void IgnoresNonTranscriptTypes()
    {
        var (projectPath, projectDir) = Project();
        var sid = NewUuid();
        string u1 = NewUuid(), a1 = NewUuid();
        string[] lines =
        [
            Dumps(Entry("user", u1, null, sid, "hi")),
            Dumps(Obj("""{"type": "summary", "summary": "A nice chat"}""")),
            Dumps(Entry("assistant", a1, u1, sid, "hello")),
        ];
        WriteText(Path.Combine(projectDir, $"{sid}.jsonl"), string.Join('\n', lines) + "\n");

        Assert.Equal(2, Get(sid, projectPath).Count);
    }

    // Python: test_sessions.py::TestGetSessionMessages::test_parallel_tool_calls_keep_every_call_and_result
    [Theory]
    [InlineData(new[] { 0, 1, 2 })]
    [InlineData(new[] { 2, 1, 0 })]
    [InlineData(new[] { 1, 2, 0 })]
    [InlineData(new[] { 0, 2, 1 })]
    public void ParallelToolCallsKeepEveryCallAndResult(int[] resultOrder)
    {
        // 每個結果掛在自己那個呼叫的助理紀錄下，紀錄因此分岔，走 parentUuid 只會走到下一輪接續的那一支。
        // 不管最後寫的是哪個結果，其他分支上的呼叫與結果都要回傳
        var (projectPath, projectDir) = Project();
        var sid = NewUuid();
        var (calls, results) = ParallelToolTurn(sid, "prompt", 3, resultOrder);
        WriteTranscript(projectDir, sid,
        [
            Entry("user", "prompt", null, sid, "read them"),
            .. calls,
            .. results,
            // 下一個回應接在最後寫的結果後面
            Entry("assistant", "answer", (string)results[^1]["uuid"]!, sid, "done"),
        ]);

        var messages = Get(sid, projectPath);

        var callIds = BlockIds(messages, "tool_use", "id");
        var resultIds = BlockIds(messages, "tool_result", "tool_use_id");
        Assert.Equal(["toolu_0", "toolu_1", "toolu_2"], callIds);
        Assert.Equal(["toolu_0", "toolu_1", "toolu_2"], resultIds.Order(StringComparer.Ordinal));
        Assert.Equal(8, messages.Count);
        Assert.Equal(8, messages.Select(m => m.Uuid).Distinct().Count());
        // 這一輪保持完整：提示、呼叫、結果、回答
        Assert.Equal("prompt", messages[0].Uuid);
        Assert.Equal(["assistant", "assistant", "assistant", "user", "user", "user"], messages.Skip(1).Take(6).Select(m => m.Type));
        Assert.Equal("answer", messages[^1].Uuid);
    }

    // Python: test_sessions.py::TestGetSessionMessages::test_parallel_tool_calls_as_last_turn
    [Fact]
    public void ParallelToolCallsAsLastTurn()
    {
        // 平行的那一輪在紀錄最後時也補得回來
        var (projectPath, projectDir) = Project();
        var sid = NewUuid();
        var (calls, results) = ParallelToolTurn(sid, "prompt", 2, [1, 0]);
        WriteTranscript(projectDir, sid, [Entry("user", "prompt", null, sid, "read them"), .. calls, .. results]);

        Assert.Equal(["prompt", "call-0", "call-1", "result-1", "result-0"], Get(sid, projectPath).Select(m => m.Uuid));
    }

    // Python: test_sessions.py::TestGetSessionMessages::test_parallel_tool_calls_limit_offset
    [Fact]
    public void ParallelToolCallsLimitOffset()
    {
        // 分頁也算補回來的呼叫與結果
        var (projectPath, projectDir) = Project();
        var sid = NewUuid();
        var (calls, results) = ParallelToolTurn(sid, "prompt", 3, [0, 1, 2]);
        WriteTranscript(projectDir, sid,
        [
            Entry("user", "prompt", null, sid, "read them"),
            .. calls,
            .. results,
            Entry("assistant", "answer", "result-2", sid, "k"),
        ]);

        Assert.Equal(["result-0", "result-1", "result-2"], Get(sid, projectPath, limit: 3, offset: 4).Select(m => m.Uuid));
    }

    // Python: test_sessions.py::TestSessionMessageType::test_creation
    [Fact]
    public void SessionMessageCreation()
    {
        var msg = new SessionMessage
        {
            Type = "user",
            Uuid = "abc",
            SessionId = "sess",
            Message = PyJson.ToElement(Obj("""{"role": "user", "content": "hi"}""")),
        };
        Assert.Equal("user", msg.Type);
        Assert.Equal("abc", msg.Uuid);
        Assert.Equal("sess", msg.SessionId);
        Assert.True(JsonEquals(msg.Message, """{"role": "user", "content": "hi"}"""));
        Assert.Null(msg.ParentToolUseId);
        Assert.Null(msg.ParentAgentId);
    }

    // Python: test_sessions.py::TestSessionMessageType::test_parent_ids
    [Fact]
    public void SessionMessageParentIds()
    {
        var msg = new SessionMessage
        {
            Type = "assistant",
            Uuid = "abc",
            SessionId = "sess",
            Message = null,
            ParentToolUseId = "toolu_1",
            ParentAgentId = "agent-1",
        };
        Assert.Equal("toolu_1", msg.ParentToolUseId);
        Assert.Equal("agent-1", msg.ParentAgentId);
    }
}

/// <summary>移植 tests/test_sessions.py 的 <c>TestBuildConversationChain</c>（<c>_build_conversation_chain</c> 的單元測試）。</summary>
public sealed class BuildConversationChainTests
{
    private static List<string> Uuids(IEnumerable<JsonObject> entries) => entries.Select(e => (string)e["uuid"]!).ToList();

    private static List<JsonObject> Build(params IEnumerable<JsonObject> entries) => TranscriptChain.BuildConversationChain([.. entries]);

    // Python: test_sessions.py::TestBuildConversationChain::test_empty_input
    [Fact]
    public void EmptyInput()
    {
        Assert.Empty(Build());
    }

    // Python: test_sessions.py::TestBuildConversationChain::test_single_entry
    [Fact]
    public void SingleEntry()
    {
        var entry = Obj("""{"type": "user", "uuid": "a", "parentUuid": null}""");
        Assert.Same(entry, Assert.Single(Build(entry)));
    }

    // Python: test_sessions.py::TestBuildConversationChain::test_linear_chain
    [Fact]
    public void LinearChain()
    {
        var result = Build(
            Obj("""{"type": "user", "uuid": "a", "parentUuid": null}"""),
            Obj("""{"type": "assistant", "uuid": "b", "parentUuid": "a"}"""),
            Obj("""{"type": "user", "uuid": "c", "parentUuid": "b"}"""));
        Assert.Equal(["a", "b", "c"], Uuids(result));
    }

    // Python: test_sessions.py::TestBuildConversationChain::test_only_progress_entries_returns_empty
    [Fact]
    public void OnlyProgressEntriesReturnsEmpty()
    {
        // 沒有 user/assistant → 沒有葉節點 → 空的
        Assert.Empty(Build(
            Obj("""{"type": "progress", "uuid": "a", "parentUuid": null}"""),
            Obj("""{"type": "progress", "uuid": "b", "parentUuid": "a"}""")));
    }

    // Python: test_sessions.py::TestBuildConversationChain::test_recovers_sibling_calls_dropped_by_the_walk
    [Fact]
    public void RecoversSiblingCallsDroppedByTheWalk()
    {
        // 從第一個呼叫的結果往回走會跳過後面的呼叫
        var (calls, results) = ParallelToolTurn("s", "prompt", 3, [1, 2, 0]);
        var result = Build(
        [
            Obj("""{"type": "user", "uuid": "prompt", "parentUuid": null}"""),
            .. calls,
            .. results,
            Obj("""{"type": "assistant", "uuid": "answer", "parentUuid": "result-0"}"""),
        ]);
        // 分支外的呼叫與結果放在分支上最後一個呼叫之後、該分支自己的結果之前
        Assert.Equal(["prompt", "call-0", "call-1", "call-2", "result-1", "result-2", "result-0", "answer"], Uuids(result));
    }

    // Python: test_sessions.py::TestBuildConversationChain::test_recovered_results_are_in_completion_order
    [Fact]
    public void RecoveredResultsAreInCompletionOrder()
    {
        // 補回的結果依時間排，不是依呼叫順序。第二個呼叫先完成
        var (calls, results) = ParallelToolTurn("s", "prompt", 3, [1, 0, 2]);
        var result = Build(
        [
            Obj("""{"type": "user", "uuid": "prompt", "parentUuid": null}"""),
            .. calls,
            .. results,
            Obj("""{"type": "assistant", "uuid": "answer", "parentUuid": "result-2"}"""),
        ]);
        Assert.Equal(["prompt", "call-0", "call-1", "call-2", "result-1", "result-0", "result-2", "answer"], Uuids(result));
    }

    // Python: test_sessions.py::TestBuildConversationChain::test_recovered_calls_are_in_timestamp_order
    [Fact]
    public void RecoveredCallsAreInTimestampOrder()
    {
        // 補回的呼叫依時間排，不是依檔案順序。call-2 比 call-1 先寫進檔案
        var (calls, results) = ParallelToolTurn("s", "prompt", 3, [1, 2, 0]);
        var result = Build(
        [
            Obj("""{"type": "user", "uuid": "prompt", "parentUuid": null}"""),
            calls[0],
            calls[2],
            calls[1],
            .. results,
            Obj("""{"type": "assistant", "uuid": "answer", "parentUuid": "result-0"}"""),
        ]);
        Assert.Equal(["call-0", "call-1", "call-2"], Uuids(result)[1..4]);
    }

    // Python: test_sessions.py::TestBuildConversationChain::test_sequential_tool_calls_are_unchanged
    [Fact]
    public void SequentialToolCallsAreUnchanged()
    {
        // 一次一個呼叫的回應沒有東西要補
        List<JsonObject> entries =
        [
            Obj("""{"type": "user", "uuid": "prompt", "parentUuid": null}"""),
            Obj("""{"type": "assistant", "uuid": "call-0", "parentUuid": "prompt", "message": {"id": "msg_1", "content": [{"type": "tool_use", "id": "toolu_0"}]}}"""),
            Obj("""{"type": "user", "uuid": "result-0", "parentUuid": "call-0", "message": {"content": [{"type": "tool_result", "tool_use_id": "toolu_0"}]}}"""),
            Obj("""{"type": "assistant", "uuid": "call-1", "parentUuid": "result-0", "message": {"id": "msg_1", "content": [{"type": "tool_use", "id": "toolu_1"}]}}"""),
            Obj("""{"type": "user", "uuid": "result-1", "parentUuid": "call-1", "message": {"content": [{"type": "tool_result", "tool_use_id": "toolu_1"}]}}"""),
        ];
        Assert.Equal(entries, Build(entries));
    }

    // Python: test_sessions.py::TestBuildConversationChain::test_abandoned_branch_is_not_recovered
    [Fact]
    public void AbandonedBranchIsNotRecovered()
    {
        // 重試的那一輪的舊回應留在主線外：只補跟主線同一個回應 id 的紀錄，之前的嘗試是不同的回應
        var (calls, results) = ParallelToolTurn("s", "prompt", 2, [0, 1]);
        var old = Obj("""{"type": "assistant", "uuid": "old-call", "parentUuid": "prompt", "message": {"id": "msg_abandoned", "content": [{"type": "tool_use", "id": "toolu_old"}]}}""");
        var oldResult = Obj("""{"type": "user", "uuid": "old-result", "parentUuid": "old-call", "message": {"content": [{"type": "tool_result", "tool_use_id": "toolu_old"}]}}""");
        var result = Build([Obj("""{"type": "user", "uuid": "prompt", "parentUuid": null}"""), old, oldResult, .. calls, .. results]);
        Assert.Equal(["prompt", "call-0", "call-1", "result-0", "result-1"], Uuids(result));
    }

    // Python: test_sessions.py::TestBuildConversationChain::test_recovers_result_whose_parent_is_outside_the_turn
    [Fact]
    public void RecoversResultWhoseParentIsOutsideTheTurn()
    {
        // parentUuid 不是這個回應的紀錄時，用 tool_use_id 對到呼叫
        var (calls, results) = ParallelToolTurn("s", "prompt", 2, [0, 1]);
        results[0]["parentUuid"] = "missing";
        results[0].Remove("sourceToolAssistantUUID");
        var result = Build(
        [
            Obj("""{"type": "user", "uuid": "prompt", "parentUuid": null}"""),
            .. calls,
            .. results,
            Obj("""{"type": "assistant", "uuid": "answer", "parentUuid": "result-1"}"""),
        ]);
        Assert.Equal(["prompt", "call-0", "call-1", "result-0", "result-1", "answer"], Uuids(result));
    }

    // Python: test_sessions.py::TestBuildConversationChain::test_does_not_duplicate_an_answered_call
    [Fact]
    public void DoesNotDuplicateAnAnsweredCall()
    {
        // 主線已經回答的呼叫，另一個多出來的結果不放進來
        var (calls, results) = ParallelToolTurn("s", "prompt", 2, [0, 1]);
        var duplicate = (JsonObject)results[1].DeepClone();
        duplicate["uuid"] = "result-1-copy";
        duplicate["parentUuid"] = "elsewhere";
        duplicate.Remove("sourceToolAssistantUUID");
        var result = Build(
        [
            Obj("""{"type": "user", "uuid": "prompt", "parentUuid": null}"""),
            .. calls,
            .. results,
            duplicate,
            Obj("""{"type": "assistant", "uuid": "answer", "parentUuid": "result-1"}"""),
        ]);
        Assert.Equal(["prompt", "call-0", "call-1", "result-0", "result-1", "answer"], Uuids(result));
    }

    // Python: test_sessions.py::TestBuildConversationChain::test_does_not_pull_in_another_agents_result
    [Fact]
    public void DoesNotPullInAnotherAgentsResult()
    {
        // sidechain 的結果用了主線的 tool_use_id，不能對到主線的呼叫
        var (calls, results) = ParallelToolTurn("s", "prompt", 2, [0, 1]);
        results[0]["parentUuid"] = "sidechain-call";
        results[0]["isSidechain"] = true;
        results[0]["agentId"] = "agent-1";
        results[0].Remove("sourceToolAssistantUUID");
        var result = Build(
        [
            Obj("""{"type": "user", "uuid": "prompt", "parentUuid": null}"""),
            .. calls,
            .. results,
            Obj("""{"type": "assistant", "uuid": "answer", "parentUuid": "result-1"}"""),
        ]);
        Assert.Equal(["prompt", "call-0", "call-1", "result-1", "answer"], Uuids(result));
    }

    // Python: test_sessions.py::TestBuildConversationChain::test_recovery_tolerates_malformed_entries
    [Fact]
    public void RecoveryToleratesMalformedEntries()
    {
        // 不相關欄位的怪形狀不會讓建鏈失敗
        var (calls, results) = ParallelToolTurn("s", "prompt", 2, [0, 1]);
        results[0]["timestamp"] = 12345;
        var result = Build(
        [
            Obj("""{"type": "user", "uuid": "prompt", "parentUuid": null}"""),
            .. calls,
            .. results,
            Obj("""{"type": "assistant", "uuid": "no-message", "parentUuid": "result-1"}"""),
            Obj("""{"type": "assistant", "uuid": "odd", "parentUuid": "no-message", "message": {"id": 7, "content": ["text", null, {"type": "tool_use"}]}}"""),
            Obj("""{"type": "user", "uuid": "odd-result", "parentUuid": "odd", "message": {"content": [{"type": "tool_result", "tool_use_id": 3}]}}"""),
        ]);
        Assert.Equal(["prompt", "call-0", "call-1", "result-0", "result-1", "no-message", "odd", "odd-result"], Uuids(result));
    }
}
