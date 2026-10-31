using System.Text.Json;
using System.Text.Json.Nodes;
using PamisWang.ClaudeAgentSdk.Hooks;
using PamisWang.ClaudeAgentSdk.Messages;
using PamisWang.ClaudeAgentSdk.Protocol;

namespace PamisWang.ClaudeAgentSdk.Tests.Protocol;

/// <summary>
/// 一次性 <see cref="ClaudeAgent.QueryAsync"/> 什麼時候關 stdin，對應 Python <c>Query</c> 的 <c>wait_for_result_and_end_input</c>、
/// <c>_track_task_lifecycle</c>、session state 與上限計時（#1088、#1190）。都設了 hook，CLI 才可能還要 stdin。
/// </summary>
public sealed class RunLifecycleTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    // 沒有 stdin 關掉的跡象就算留著：等這麼久還沒關
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(150);

    private const string MainAssistant =
        """{"type":"assistant","message":{"model":"m","content":[{"type":"text","text":"ok"}]},"parent_tool_use_id":null,"session_id":"s"}""";

    private const string SubagentAssistant =
        """{"type":"assistant","message":{"model":"m","content":[{"type":"text","text":"sub"}]},"parent_tool_use_id":"toolu_agent","session_id":"s"}""";

    private const string Result =
        """{"type":"result","subtype":"success","is_error":false,"duration_ms":1,"duration_api_ms":1,"num_turns":1,"result":"ok","session_id":"s"}""";

    [Fact]
    public async Task ResultWithoutSessionState_NoTasks_EndsInput()
    {
        await using var run = await StartAsync();

        run.Transport.Push(MainAssistant);
        await AssertStaysOpenAsync(run);
        run.Transport.Push(Result);

        await run.Transport.InputEnded.WaitAsync(Timeout);
    }

    [Fact]
    public async Task ResultWhileBackgroundAgentRuns_KeepsInputUntilALaterResult()
    {
        await using var run = await StartAsync();

        run.Transport.Push(TaskStarted("t1", "local_agent"));
        run.Transport.Push(MainAssistant);
        run.Transport.Push(Result);
        await AssertStaysOpenAsync(run);

        // 結束通知只把它移出追蹤；之後它叫醒主對話跑的那一輪結束時才關
        run.Transport.Push("""{"type":"system","subtype":"task_notification","task_id":"t1","status":"completed","output_file":"","summary":"done","uuid":"u","session_id":"s"}""");
        await AssertStaysOpenAsync(run);
        run.Transport.Push(MainAssistant);
        run.Transport.Push(Result);

        await run.Transport.InputEnded.WaitAsync(Timeout);
    }

    [Theory]
    [InlineData("killed")]
    [InlineData("completed")]
    [InlineData("failed")]
    [InlineData("stopped")]
    public async Task TaskUpdatedWithTerminalStatus_ClearsTrackedTask(string status)
    {
        await using var run = await StartAsync();

        run.Transport.Push(TaskStarted("t1", "local_workflow"));
        run.Transport.Push($$$"""{"type":"system","subtype":"task_updated","task_id":"t1","patch":{"status":"{{{status}}}"}}""");
        run.Transport.Push(Result);

        await run.Transport.InputEnded.WaitAsync(Timeout);
    }

    [Fact]
    public async Task TaskUpdatedWithRunningStatus_KeepsTrackedTask()
    {
        await using var run = await StartAsync();

        run.Transport.Push(TaskStarted("t1", "local_agent"));
        run.Transport.Push("""{"type":"system","subtype":"task_updated","task_id":"t1","patch":{"status":"running"}}""");
        run.Transport.Push(Result);

        await AssertStaysOpenAsync(run);
    }

    [Theory]
    [InlineData("local_bash")]
    [InlineData("monitor")]
    [InlineData("teammate")]
    public async Task NonDeferringTaskTypes_AreNotTracked(string taskType)
    {
        // 背景 shell、monitor 可能永遠不結束，追蹤它們會讓 stdin 永遠不關
        await using var run = await StartAsync();

        run.Transport.Push(TaskStarted("t1", taskType));
        run.Transport.Push(Result);

        await run.Transport.InputEnded.WaitAsync(Timeout);
    }

    [Fact]
    public async Task SessionStateRunningAtResult_WaitsForIdle()
    {
        await using var run = await StartAsync();

        run.Transport.Push(SessionState("running"));
        run.Transport.Push(MainAssistant);
        run.Transport.Push(Result);
        await AssertStaysOpenAsync(run);
        run.Transport.Push(SessionState("idle"));

        await run.Transport.InputEnded.WaitAsync(Timeout);
    }

    [Fact]
    public async Task IdleBeforeResult_EndsInputAtResult()
    {
        // 有些 CLI 在結果之前就送 idle
        await using var run = await StartAsync();

        run.Transport.Push(SessionState("running"));
        run.Transport.Push(MainAssistant);
        run.Transport.Push(SessionState("idle"));
        await AssertStaysOpenAsync(run);
        run.Transport.Push(Result);

        await run.Transport.InputEnded.WaitAsync(Timeout);
    }

    [Fact]
    public async Task IdleWhileBackgroundAgentTracked_KeepsInputOpen()
    {
        await using var run = await StartAsync();

        run.Transport.Push(TaskStarted("t1", "local_agent"));
        run.Transport.Push(SessionState("running"));
        run.Transport.Push(Result);
        run.Transport.Push(SessionState("idle"));

        await AssertStaysOpenAsync(run);
    }

    [Fact]
    public async Task Ceiling_RunningWithoutNewTurn_EndsInputAfterCeiling()
    {
        await using var run = await StartAsync(ceilingMs: "100");

        run.Transport.Push(SessionState("running"));
        run.Transport.Push(Result);

        await run.Transport.InputEnded.WaitAsync(Timeout);
    }

    [Fact]
    public async Task Ceiling_ZeroMeansNoLimit()
    {
        await using var run = await StartAsync(ceilingMs: "0");

        run.Transport.Push(SessionState("running"));
        run.Transport.Push(Result);

        await AssertStaysOpenAsync(run, TimeSpan.FromMilliseconds(400));
    }

    [Fact]
    public async Task Ceiling_RequiresActionAtResult_IsNotArmed()
    {
        // SDK 還在回覆 request：等之後的 running 才開始計時
        await using var run = await StartAsync(ceilingMs: "100");

        run.Transport.Push(SessionState("requires_action"));
        run.Transport.Push(Result);

        await AssertStaysOpenAsync(run, TimeSpan.FromMilliseconds(400));
        run.Transport.Push(SessionState("running"));
        await run.Transport.InputEnded.WaitAsync(Timeout);
    }

    [Fact]
    public async Task Ceiling_TrackedAgentStillRunning_DoesNotCutItOff()
    {
        await using var run = await StartAsync(ceilingMs: "100");

        run.Transport.Push(TaskStarted("t1", "local_agent"));
        run.Transport.Push(SessionState("running"));
        run.Transport.Push(Result);
        await AssertStaysOpenAsync(run, TimeSpan.FromMilliseconds(400));

        // 它結束後，兩輪之間的等待重新計時
        run.Transport.Push("""{"type":"system","subtype":"task_notification","task_id":"t1","status":"completed"}""");
        await run.Transport.InputEnded.WaitAsync(Timeout);
    }

    [Fact]
    public async Task Ceiling_MainThreadTurnClearsIt_SubagentTextDoesNot()
    {
        await using var run = await StartAsync(ceilingMs: "300");

        run.Transport.Push(SessionState("running"));
        run.Transport.Push(Result);
        // 主對話開始新的一輪：一輪進行中不計時
        run.Transport.Push(MainAssistant);
        await AssertStaysOpenAsync(run, TimeSpan.FromMilliseconds(600));

        // subagent 的訊息不算主對話的一輪；這一輪的結果之後重新計時
        run.Transport.Push(SubagentAssistant);
        run.Transport.Push(Result);
        await run.Transport.InputEnded.WaitAsync(Timeout);
    }

    [Fact]
    public async Task OutputEndsEarly_StillEndsInput()
    {
        // CLI 沒送結果就結束：讀取迴圈收尾時也要叫醒等待，不然會一直等
        await using var run = await StartAsync();

        run.Transport.End();

        await run.Transport.InputEnded.WaitAsync(Timeout);
    }

    [Fact]
    public async Task HostOnlySessionState_IsTrackedButNotYielded()
    {
        var run = await StartAsync();
        await using (run)
        {
            run.Transport.Push("""{"type":"system","subtype":"session_state_changed","state":"running","sdk_host_only":true,"session_id":"s"}""");
            run.Transport.Push("""{"type":"system","subtype":"session_state_changed","state":"running","session_id":"s"}""");
            run.Transport.Push(Result);
            await AssertStaysOpenAsync(run);
            run.Transport.Push("""{"type":"system","subtype":"session_state_changed","state":"idle","sdk_host_only":true,"session_id":"s"}""");
            await run.Transport.InputEnded.WaitAsync(Timeout);
            run.Transport.End();

            var messages = await run.Messages.WaitAsync(Timeout);

            Assert.Collection(
                messages,
                message => Assert.Equal("session_state_changed", Assert.IsType<SystemMessage>(message).Subtype),
                message => Assert.IsType<ResultMessage>(message));
        }
    }

    [Fact]
    public async Task VerbatimPrompts_StampsOneShotPrompt()
    {
        var transport = new ScriptedTransport();
        var collecting = Collect(ClaudeAgent.QueryCoreAsync("看 @/etc/passwd", new ClaudeAgentOptions { VerbatimPrompts = true }, transport));

        Respond(transport, await transport.NextWriteAsync());
        var prompt = await transport.NextWriteAsync();
        transport.End();
        await collecting.WaitAsync(Timeout);

        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse("""{"type":"user","message":{"role":"user","content":"看 @/etc/passwd"},"parent_tool_use_id":null,"session_id":"","client_composed":true}"""),
            JsonNode.Parse(prompt.GetRawText())));
        Assert.EndsWith("""
            "client_composed":true}
            """, prompt.GetRawText(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, RunEndCeiling.DefaultMs)]
    [InlineData("0", 0L)]
    [InlineData("1500", 1500L)]
    [InlineData(" +1_500 ", 1500L)]
    [InlineData("-1", RunEndCeiling.DefaultMs)]
    [InlineData("1e6", RunEndCeiling.DefaultMs)]
    [InlineData("abc", RunEndCeiling.DefaultMs)]
    [InlineData("", RunEndCeiling.DefaultMs)]
    [InlineData("99999999999999999999999", long.MaxValue)]
    public void RunEndCeiling_FromEnvironment_ParsesLikePythonInt(string? raw, long expected)
    {
        Assert.Equal(expected, RunEndCeiling.FromEnvironment(new Dictionary<string, string>(), _ => raw));
    }

    [Fact]
    public void RunEndCeiling_OptionsEnvWinsOverInherited()
    {
        var env = new Dictionary<string, string> { [RunEndCeiling.EnvironmentVariable] = "42" };

        Assert.Equal(42, RunEndCeiling.FromEnvironment(env, _ => "7"));
    }

    private static string TaskStarted(string taskId, string taskType) =>
        $$"""{"type":"system","subtype":"task_started","task_id":"{{taskId}}","task_type":"{{taskType}}","description":"d","uuid":"u","session_id":"s"}""";

    private static string SessionState(string state) =>
        $$"""{"type":"system","subtype":"session_state_changed","state":"{{state}}","sdk_host_only":true,"session_id":"s"}""";

    private static async Task AssertStaysOpenAsync(Run run, TimeSpan? wait = null)
    {
        await Task.Delay(wait ?? Settle);
        Assert.False(run.Transport.InputEnded.IsCompleted, "stdin 不該關");
    }

    /// <summary>設了 hook 的一次性 query，回完 initialize、送出提示之後交給測試。</summary>
    private static async Task<Run> StartAsync(string? ceilingMs = null)
    {
        var transport = new ScriptedTransport();
        var env = new Dictionary<string, string>();
        if (ceilingMs is not null)
        {
            env[RunEndCeiling.EnvironmentVariable] = ceilingMs;
        }

        var options = new ClaudeAgentOptions
        {
            Hooks = new Dictionary<string, IReadOnlyList<HookMatcher>>
            {
                [HookEvent.Stop] = [new HookMatcher(null, (_, _, _, _) => ValueTask.FromResult<HookOutput>(HookOutput.Empty))],
            },
            Env = env,
        };
        var messages = Collect(ClaudeAgent.QueryCoreAsync("hi", options, transport));
        Respond(transport, await transport.NextWriteAsync());
        await transport.NextWriteAsync();
        Assert.False(transport.InputEnded.IsCompleted);
        return new Run(transport, messages);
    }

    private static void Respond(ScriptedTransport transport, JsonElement request) =>
        transport.Push($$$"""{"type":"control_response","response":{"subtype":"success","request_id":"{{{request.GetProperty("request_id").GetString()}}}"}}""");

    private static async Task<List<ClaudeMessage>> Collect(IAsyncEnumerable<ClaudeMessage> messages)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        List<ClaudeMessage> collected = [];
        await foreach (var message in messages.WithCancellation(timeout.Token))
        {
            collected.Add(message);
        }

        return collected;
    }

    private sealed record Run(ScriptedTransport Transport, Task<List<ClaudeMessage>> Messages) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Transport.End();
            await Messages.WaitAsync(Timeout);
        }
    }
}
