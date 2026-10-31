using System.Text.Json;
using System.Text.Json.Nodes;
using PamisWang.ClaudeAgentSdk.Hooks;
using PamisWang.ClaudeAgentSdk.Protocol;

namespace PamisWang.ClaudeAgentSdk.Tests.Protocol;

/// <summary>hook 在 control 協定上的行為：initialize 的登記、hook_callback 的分派、同時執行、撤回與關閉。</summary>
public sealed class HookProtocolTests : IAsyncLifetime, IAsyncDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly ScriptedTransport _transport = new();
    private ControlProtocol? _protocol;

    public Task InitializeAsync() => Task.CompletedTask;

    Task IAsyncLifetime.DisposeAsync() => DisposeAsync().AsTask();

    public async ValueTask DisposeAsync()
    {
        if (_protocol is not null)
        {
            await _protocol.DisposeAsync();
        }

        await _transport.DisposeAsync();
    }

    [Fact]
    public async Task Initialize_WithoutHooks_SendsNullAndNeedsNoInput()
    {
        var protocol = Start(hooks: null);

        var request = await InitializeAsync(protocol);

        Assert.Equal(JsonValueKind.Null, request.GetProperty("hooks").ValueKind);
        Assert.False(protocol.HasBidirectionalNeeds);
    }

    [Fact]
    public async Task Initialize_OnlyEmptyMatcherLists_SendsNullButStillKeepsInput()
    {
        // Python：hooks_config 是空的就送 None；但 self.hooks 不是空的，_has_bidirectional_needs 仍是 True
        var protocol = Start(new Dictionary<string, IReadOnlyList<HookMatcher>> { [HookEvent.Stop] = [] });

        var request = await InitializeAsync(protocol);

        Assert.Equal(JsonValueKind.Null, request.GetProperty("hooks").ValueKind);
        Assert.True(protocol.HasBidirectionalNeeds);
    }

    [Fact]
    public async Task Initialize_NumbersCallbacksAcrossEventsAndWritesFractionalTimeout()
    {
        var protocol = Start(new Dictionary<string, IReadOnlyList<HookMatcher>>
        {
            [HookEvent.SubagentStart] = [new HookMatcher(null, Empty)],
            ["SessionStart"] = [new HookMatcher("startup", [Empty, Empty], Timeout: 2.5), new HookMatcher("resume", [])],
        });

        var request = await InitializeAsync(protocol);

        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse("""{"SubagentStart":[{"matcher":null,"hookCallbackIds":["hook_0"]}],"SessionStart":[{"matcher":"startup","hookCallbackIds":["hook_1","hook_2"],"timeout":2.5},{"matcher":"resume","hookCallbackIds":[]}]}"""),
            JsonNode.Parse(request.GetProperty("hooks").GetRawText())));
        Assert.True(protocol.HasBidirectionalNeeds);
    }

    [Fact]
    public async Task HookCallback_PassesTypedInputToolUseIdAndContext()
    {
        HookInput? received = null;
        string? receivedToolUseId = "unset";
        Start(Hooks(HookEvent.PostToolUse, (input, toolUseId, context, cancellationToken) =>
        {
            received = input;
            receivedToolUseId = toolUseId;
            Assert.Equal(cancellationToken, context.Signal);
            return ValueTask.FromResult(HookOutput.Empty);
        }));

        _transport.Push(HookRequest("r1", "hook_0", """{"hook_event_name":"PostToolUse","tool_name":"Read","tool_response":"x"}""", toolUseId: null));
        var response = (await _transport.NextWriteAsync()).GetProperty("response");

        Assert.Equal("success", response.GetProperty("subtype").GetString());
        Assert.Equal("{}", response.GetProperty("response").GetRawText());
        Assert.Equal("x", Assert.IsType<PostToolUseHookInput>(received).ToolResponse.GetString());
        Assert.Null(receivedToolUseId);
    }

    [Fact]
    public async Task HookCallback_WithoutInput_GetsUnknownInput()
    {
        HookInput? received = null;
        Start(Hooks(HookEvent.Stop, (input, _, _, _) =>
        {
            received = input;
            return ValueTask.FromResult(HookOutput.Empty);
        }));

        _transport.Push("""{"type":"control_request","request_id":"r1","request":{"subtype":"hook_callback","callback_id":"hook_0"}}""");
        await _transport.NextWriteAsync();

        Assert.IsType<UnknownHookInput>(received);
    }

    [Fact]
    public async Task HookCallback_RawOutput_IsSentAsIs()
    {
        Start(Hooks(HookEvent.PreToolUse, (_, _, _, _) => ValueTask.FromResult<HookOutput>(
            new RawHookOutput(new JsonObject { ["continue_"] = true, ["futureField"] = new JsonArray(1, 2) }))));

        _transport.Push(HookRequest("r1", "hook_0", """{"hook_event_name":"PreToolUse"}"""));
        var response = (await _transport.NextWriteAsync()).GetProperty("response");

        Assert.Equal("""{"continue":true,"futureField":[1,2]}""", response.GetProperty("response").GetRawText());
    }

    [Fact]
    public async Task HookCallback_ReturnsNull_RespondsWithError()
    {
        Start(Hooks(HookEvent.Stop, (_, _, _, _) => ValueTask.FromResult<HookOutput>(null!)));

        _transport.Push(HookRequest("r1", "hook_0", """{"hook_event_name":"Stop"}"""));
        var response = (await _transport.NextWriteAsync()).GetProperty("response");

        Assert.Equal("error", response.GetProperty("subtype").GetString());
        Assert.Contains("HookOutput.Empty", response.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task HookCallback_MissingCallbackId_RespondsNotFound()
    {
        Start(Hooks(HookEvent.Stop, Empty));

        _transport.Push("""{"type":"control_request","request_id":"r1","request":{"subtype":"hook_callback","input":{}}}""");
        var response = (await _transport.NextWriteAsync()).GetProperty("response");

        Assert.Equal("error", response.GetProperty("subtype").GetString());
        Assert.StartsWith("No hook callback found for ID:", response.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task HookCallback_SlowHookDoesNotBlockOthersOrMessages()
    {
        // CLI 會同時送出同一個事件的多個 hook_callback
        var slowRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        HookCallback slow = async (_, _, _, cancellationToken) =>
        {
            slowStarted.SetResult();
            await slowRelease.Task.WaitAsync(cancellationToken);
            return new SyncHookOutput { Reason = "slow" };
        };
        HookCallback fast = (_, _, _, _) => ValueTask.FromResult<HookOutput>(new SyncHookOutput { Reason = "fast" });
        var protocol = Start(new Dictionary<string, IReadOnlyList<HookMatcher>>
        {
            [HookEvent.PreToolUse] = [new HookMatcher(null, slow), new HookMatcher(null, fast)],
        });
        await using var messages = protocol.ReceiveMessagesAsync().GetAsyncEnumerator();

        _transport.Push(HookRequest("slow", "hook_0", """{"hook_event_name":"PreToolUse"}"""));
        await slowStarted.Task.WaitAsync(Timeout);
        _transport.Push(HookRequest("fast", "hook_1", """{"hook_event_name":"PreToolUse"}"""));
        var first = (await _transport.NextWriteAsync()).GetProperty("response");
        _transport.Push("""{"type":"assistant","message":{"content":[]}}""");
        Assert.True(await messages.MoveNextAsync().AsTask().WaitAsync(Timeout));

        slowRelease.SetResult();
        var second = (await _transport.NextWriteAsync()).GetProperty("response");

        Assert.Equal("fast", first.GetProperty("request_id").GetString());
        Assert.Equal("fast", first.GetProperty("response").GetProperty("reason").GetString());
        Assert.Equal("assistant", messages.Current.GetProperty("type").GetString());
        Assert.Equal("slow", second.GetProperty("request_id").GetString());
        Assert.Equal("slow", second.GetProperty("response").GetProperty("reason").GetString());
    }

    [Fact]
    public async Task HookCallback_ControlCancelRequest_CancelsTokenAndSendsNoResponse()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var signalCancelled = false;
        Start(Hooks(HookEvent.PreToolUse, async (input, _, context, cancellationToken) =>
        {
            if (input is not PreToolUseHookInput { ToolName: "slow" })
            {
                return new SyncHookOutput { Reason = "next" };
            }

            using var registration = cancellationToken.Register(() =>
            {
                signalCancelled = context.Signal.IsCancellationRequested;
                cancelled.SetResult();
            });
            started.SetResult();
            await Task.Delay(System.Threading.Timeout.Infinite, cancellationToken);
            return HookOutput.Empty;
        }));

        _transport.Push(HookRequest("r1", "hook_0", """{"hook_event_name":"PreToolUse","tool_name":"slow"}"""));
        await started.Task.WaitAsync(Timeout);
        _transport.Push("""{"type":"control_cancel_request","request_id":"r1"}""");
        await cancelled.Task.WaitAsync(Timeout);

        // 撤回之後的請求照常回覆，而且是第一個寫出的：被撤回的沒有回應
        _transport.Push(HookRequest("r2", "hook_0", """{"hook_event_name":"PreToolUse","tool_name":"fast"}"""));
        var response = (await _transport.NextWriteAsync()).GetProperty("response");

        Assert.True(signalCancelled);
        Assert.Equal("r2", response.GetProperty("request_id").GetString());
        Assert.Equal("next", response.GetProperty("response").GetProperty("reason").GetString());
        Assert.True(_transport.HasNoPendingWrites);
    }

    [Fact]
    public async Task DisposeAsync_CancelsRunningHook()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var protocol = Start(Hooks(HookEvent.Stop, async (_, _, _, cancellationToken) =>
        {
            using var registration = cancellationToken.Register(cancelled.SetResult);
            started.SetResult();
            await Task.Delay(System.Threading.Timeout.Infinite, cancellationToken);
            return HookOutput.Empty;
        }));
        _transport.Push(HookRequest("r1", "hook_0", """{"hook_event_name":"Stop"}"""));
        await started.Task.WaitAsync(Timeout);

        await protocol.DisposeAsync();

        await cancelled.Task.WaitAsync(Timeout);
        Assert.True(_transport.HasNoPendingWrites);
    }

    private static ValueTask<HookOutput> Empty(HookInput input, string? toolUseId, HookContext context, CancellationToken cancellationToken) =>
        ValueTask.FromResult(HookOutput.Empty);

    private static Dictionary<string, IReadOnlyList<HookMatcher>> Hooks(string hookEvent, HookCallback callback) =>
        new() { [hookEvent] = [new HookMatcher(null, callback)] };

    private static string HookRequest(string requestId, string callbackId, string input, string? toolUseId = "tu") =>
        $$$"""{"type":"control_request","request_id":"{{{requestId}}}","request":{"subtype":"hook_callback","callback_id":"{{{callbackId}}}","input":{{{input}}},"tool_use_id":{{{(toolUseId is null ? "null" : $"\"{toolUseId}\"")}}}}}""";

    private ControlProtocol Start(IReadOnlyDictionary<string, IReadOnlyList<HookMatcher>>? hooks)
    {
        _protocol = new ControlProtocol(_transport, canUseTool: null, mcpServers: null, hooks)
        {
            HandlerShutdownTimeout = TimeSpan.FromSeconds(1),
        };
        _protocol.Start();
        return _protocol;
    }

    private async Task<JsonElement> InitializeAsync(ControlProtocol protocol)
    {
        var initializing = protocol.InitializeAsync(Timeout);
        var request = await _transport.NextWriteAsync();
        _transport.Push($$$"""{"type":"control_response","response":{"subtype":"success","request_id":"{{{request.GetProperty("request_id").GetString()}}}","response":{} } }""");
        await initializing;
        return request.GetProperty("request");
    }
}
