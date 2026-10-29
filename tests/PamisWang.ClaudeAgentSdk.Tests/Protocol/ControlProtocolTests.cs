using System.Text.Json;
using PamisWang.ClaudeAgentSdk.Permissions;
using PamisWang.ClaudeAgentSdk.Protocol;

namespace PamisWang.ClaudeAgentSdk.Tests.Protocol;

/// <summary>樣本裡沒有的情況：錯誤回應、逾時、讀取失敗、不支援的請求、回呼出錯與關閉。</summary>
public sealed class ControlProtocolTests : IAsyncLifetime, IAsyncDisposable
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
    public async Task InitializeAsync_SendsInitializeAndReturnsResponse()
    {
        var protocol = Start();

        var initializing = protocol.InitializeAsync(Timeout);
        var request = await _transport.NextWriteAsync();
        var requestId = request.GetProperty("request_id").GetString()!;
        _transport.Push("""{"type":"control_response","response":{"subtype":"success","request_id":"ID","response":{"commands":[]}}}"""
            .Replace("ID", requestId, StringComparison.Ordinal));
        var response = await initializing;

        Assert.Matches("^req_1_[0-9a-f]{8}$", requestId);
        Assert.Equal(
            $$$"""{"type":"control_request","request_id":"{{{requestId}}}","request":{"subtype":"initialize","hooks":null}}""",
            request.GetRawText());
        Assert.Equal(JsonValueKind.Array, response.GetProperty("commands").ValueKind);
    }

    [Fact]
    public async Task SendControlRequestAsync_RequestIdsCountUp()
    {
        var protocol = Start();

        _ = protocol.InterruptAsync();
        _ = protocol.InterruptAsync();
        var ids = new[] { await _transport.NextWriteAsync(), await _transport.NextWriteAsync() }
            .Select(message => message.GetProperty("request_id").GetString()!.Split('_')[1])
            .Order();

        Assert.Equal(["1", "2"], ids);
    }

    [Fact]
    public async Task SendControlRequestAsync_ErrorResponse_ThrowsWithCliMessage()
    {
        var protocol = Start();

        var interrupting = protocol.InterruptAsync();
        var requestId = (await _transport.NextWriteAsync()).GetProperty("request_id").GetString();
        _transport.Push($$$"""{"type":"control_response","response":{"subtype":"error","request_id":"{{{requestId}}}","error":"沒有在跑的回合"}}""");

        var error = await Assert.ThrowsAsync<ClaudeAgentException>(() => interrupting);
        Assert.Equal("沒有在跑的回合", error.Message);
    }

    [Fact]
    public async Task SendControlRequestAsync_NoResponse_TimesOut()
    {
        var protocol = Start();

        var error = await Assert.ThrowsAsync<TimeoutException>(
            () => protocol.SendControlRequestAsync("interrupt", null, TimeSpan.FromMilliseconds(100)));
        Assert.Contains("interrupt", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendControlRequestAsync_OutputEndsWhileWaiting_FailsWithoutWaitingForTimeout()
    {
        var protocol = Start();

        var initializing = protocol.InitializeAsync(TimeSpan.FromMinutes(5));
        await _transport.NextWriteAsync();
        _transport.End();

        await Assert.ThrowsAsync<CliConnectionException>(() => initializing.WaitAsync(Timeout));
    }

    [Fact]
    public async Task SendControlRequestAsync_BeforeStart_Throws()
    {
        _protocol = new ControlProtocol(_transport, canUseTool: null);

        await Assert.ThrowsAsync<CliConnectionException>(() => _protocol.InterruptAsync());
    }

    [Fact]
    public async Task ReceiveMessagesAsync_TransportFails_RethrowsOriginalExceptionToReaderAndPendingRequest()
    {
        var protocol = Start();
        var initializing = protocol.InitializeAsync(Timeout);
        await _transport.NextWriteAsync();

        _transport.Fail(new CliProcessException("Claude Code 異常結束", 1));

        var readError = await Assert.ThrowsAsync<CliProcessException>(() => ReadAllAsync(protocol));
        var requestError = await Assert.ThrowsAsync<CliProcessException>(() => initializing);
        Assert.Equal(1, readError.ExitCode);
        Assert.Same(readError, requestError);
    }

    [Fact]
    public async Task ReceiveMessagesAsync_PassesOnlyNonControlMessages()
    {
        var protocol = Start();

        _transport.Push("""{"type":"system","subtype":"session_state_changed","state":"running","sdk_host_only":true}""");
        _transport.Push("""{"type":"system","subtype":"session_state_changed","state":"idle"}""");
        _transport.Push("""{"type":"transcript_mirror","filePath":"x","entries":[]}""");
        _transport.Push("""{"type":"control_response","response":{"subtype":"success","request_id":"req_9_unknown"}}""");
        _transport.Push("""{"type":"control_cancel_request","request_id":"unknown"}""");
        _transport.Push("""{"type":"assistant","message":{"content":[]}}""");
        _transport.Push("""{"type":"rate_limit_event"}""");
        _transport.End();
        var messages = await ReadAllAsync(protocol);

        Assert.Equal(
            ["system", "assistant", "rate_limit_event"],
            messages.Select(message => message.GetProperty("type").GetString()));
        Assert.Equal("idle", messages[0].GetProperty("state").GetString());
    }

    [Fact]
    public async Task ControlRequest_UnsupportedSubtype_RespondsWithError()
    {
        Start();

        _transport.Push("""{"type":"control_request","request_id":"cli_1","request":{"subtype":"hook_callback","callback_id":"hook_0"}}""");
        var response = (await _transport.NextWriteAsync()).GetProperty("response");

        Assert.Equal("error", response.GetProperty("subtype").GetString());
        Assert.Equal("cli_1", response.GetProperty("request_id").GetString());
        Assert.Contains("hook_callback", response.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CanUseTool_NotConfigured_RespondsWithError()
    {
        Start();

        _transport.Push(PermissionRequest("cli_1"));
        var response = (await _transport.NextWriteAsync()).GetProperty("response");

        Assert.Equal("error", response.GetProperty("subtype").GetString());
        Assert.Contains("CanUseTool", response.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CanUseTool_CallbackThrows_RespondsWithItsMessage()
    {
        Start((_, _) => throw new InvalidOperationException("審核服務掛了"));

        _transport.Push(PermissionRequest("cli_1"));
        var response = (await _transport.NextWriteAsync()).GetProperty("response");

        Assert.Equal("error", response.GetProperty("subtype").GetString());
        Assert.Equal("審核服務掛了", response.GetProperty("error").GetString());
    }

    [Fact]
    public async Task CanUseTool_AllowWithUpdatedInput_SendsUpdatedInput()
    {
        var updated = JsonSerializer.Deserialize<JsonElement>("""{"command":"npm test -- --ci"}""");
        Start((_, _) => Task.FromResult<PermissionResult>(new PermissionResultAllow(UpdatedInput: updated)));

        _transport.Push(PermissionRequest("cli_1"));
        var response = (await _transport.NextWriteAsync()).GetProperty("response").GetProperty("response");

        Assert.Equal("""{"behavior":"allow","updatedInput":{"command":"npm test -- --ci"}}""", response.GetRawText());
    }

    [Fact]
    public async Task CanUseTool_DenyWithInterrupt_SendsInterruptFlag()
    {
        Start((_, _) => Task.FromResult<PermissionResult>(new PermissionResultDeny("不行", Interrupt: true)));

        _transport.Push(PermissionRequest("cli_1"));
        var response = (await _transport.NextWriteAsync()).GetProperty("response").GetProperty("response");

        Assert.Equal("deny", response.GetProperty("behavior").GetString());
        Assert.Equal("不行", response.GetProperty("message").GetString());
        Assert.True(response.GetProperty("interrupt").GetBoolean());
    }

    [Fact]
    public async Task CanUseTool_WaitingForUser_DoesNotBlockOtherMessages()
    {
        var answer = new TaskCompletionSource<PermissionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var protocol = Start((_, _) => answer.Task);
        await using var messages = protocol.ReceiveMessagesAsync().GetAsyncEnumerator();

        _transport.Push(PermissionRequest("cli_1"));
        _transport.Push("""{"type":"assistant","message":{"content":[]}}""");

        Assert.True(await messages.MoveNextAsync().AsTask().WaitAsync(Timeout));
        Assert.Equal("assistant", messages.Current.GetProperty("type").GetString());
        Assert.True(_transport.HasNoPendingWrites);

        answer.SetResult(new PermissionResultAllow());
        var response = (await _transport.NextWriteAsync()).GetProperty("response");
        Assert.Equal("cli_1", response.GetProperty("request_id").GetString());
    }

    [Fact]
    public async Task DisposeAsync_CancelsWaitingCallbackAndClosesTransport()
    {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var protocol = Start(async (_, cancellationToken) =>
        {
            asked.SetResult();
            using var registration = cancellationToken.Register(cancelled.SetResult);
            await Task.Delay(System.Threading.Timeout.Infinite, cancellationToken);
            return new PermissionResultAllow();
        });
        _transport.Push(PermissionRequest("cli_1"));
        await asked.Task.WaitAsync(Timeout);

        await protocol.DisposeAsync();

        await cancelled.Task.WaitAsync(Timeout);
        Assert.True(_transport.Disposed);
        Assert.True(_transport.HasNoPendingWrites);
    }

    private ControlProtocol Start(Func<ToolPermissionRequest, CancellationToken, Task<PermissionResult>>? canUseTool = null)
    {
        _protocol = new ControlProtocol(_transport, canUseTool) { HandlerShutdownTimeout = TimeSpan.FromSeconds(1) };
        _protocol.Start();
        return _protocol;
    }

    private static string PermissionRequest(string requestId) =>
        $$$"""{"type":"control_request","request_id":"{{{requestId}}}","request":{"subtype":"can_use_tool","tool_name":"Bash","input":{"command":"npm test"},"permission_suggestions":[],"tool_use_id":"toolu_1"}}""";

    private static async Task<List<JsonElement>> ReadAllAsync(ControlProtocol protocol)
    {
        using var timeout = new CancellationTokenSource(Timeout);
        List<JsonElement> messages = [];
        await foreach (var message in protocol.ReceiveMessagesAsync(timeout.Token))
        {
            messages.Add(message);
        }

        return messages;
    }
}
