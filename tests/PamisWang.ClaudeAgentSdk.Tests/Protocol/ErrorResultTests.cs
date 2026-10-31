using System.Text.Json;
using PamisWang.ClaudeAgentSdk.Protocol;

namespace PamisWang.ClaudeAgentSdk.Tests.Protocol;

/// <summary>
/// CLI 送出出錯的結果後以非零結束：跟 Python 的 <c>ResultError</c> 一樣換成 <see cref="CliResultException"/>，
/// 只在最近一則訊息就是那個結果時才換。
/// </summary>
public sealed class ErrorResultTests : IAsyncLifetime, IAsyncDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private const string BudgetResult =
        """{"type":"result","subtype":"error_max_budget_usd","is_error":true,"duration_ms":1,"duration_api_ms":1,"num_turns":2"""
        + ""","errors":["  Reached maximum budget ($0.01)  ",""," ",42],"terminal_reason":"max_budget","api_error_status":null,"session_id":"s-1"}""";

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
    public async Task ErrorResultThenExit_ThrowsCliResultExceptionWithResultPayload()
    {
        var protocol = Start();
        var processError = new CliProcessException("Claude Code 異常結束", 1);

        _transport.Push(BudgetResult);
        _transport.Fail(processError);

        var (messages, error) = await ReadUntilErrorAsync(protocol);
        var resultError = Assert.IsType<CliResultException>(error);
        // 出錯的結果本身照樣先送到讀取端
        Assert.Equal("result", Assert.Single(messages).GetProperty("type").GetString());
        Assert.Equal("error_max_budget_usd", resultError.Subtype);
        Assert.Equal(["Reached maximum budget ($0.01)"], resultError.Errors);
        Assert.Null(resultError.Result);
        Assert.Null(resultError.ApiErrorStatus);
        Assert.Equal("max_budget", resultError.TerminalReason);
        Assert.Equal("s-1", resultError.SessionId);
        Assert.Equal(1, resultError.ExitCode);
        Assert.Equal("error_max_budget_usd", resultError.RawResult?.GetProperty("subtype").GetString());
        Assert.Same(processError, resultError.InnerException);
        Assert.Equal("Claude Code 回報了出錯的結果：Reached maximum budget ($0.01)（exit code 1）", resultError.Message);
    }

    [Fact]
    public async Task ErrorResultThenExit_FailsPendingControlRequestWithSameException()
    {
        var protocol = Start();
        var initializing = protocol.InitializeAsync(Timeout);
        await _transport.NextWriteAsync();

        _transport.Push(BudgetResult);
        _transport.Fail(new CliProcessException("Claude Code 異常結束", 1));

        var (_, readError) = await ReadUntilErrorAsync(protocol);
        var requestError = await Assert.ThrowsAsync<CliResultException>(() => initializing);
        Assert.Same(readError, requestError);
    }

    [Theory]
    [InlineData("""{"type":"system","subtype":"session_state_changed","state":"idle"}""")]
    [InlineData("""{"type":"system","subtype":"session_state_changed","state":"idle","sdk_host_only":true}""")]
    [InlineData("""{"type":"control_cancel_request","request_id":"unknown"}""")]
    [InlineData("""{"type":"control_response","response":{"subtype":"success","request_id":"unknown"}}""")]
    [InlineData("""{"type":"transcript_mirror","filePath":"x","entries":[]}""")]
    public async Task ErrorResultFollowedByStateOrControlFrame_StillReplaced(string frame)
    {
        var protocol = Start();

        _transport.Push(BudgetResult);
        _transport.Push(frame);
        _transport.Fail(new CliProcessException("Claude Code 異常結束", 1));

        var (_, error) = await ReadUntilErrorAsync(protocol);
        Assert.IsType<CliResultException>(error);
    }

    [Theory]
    [InlineData("""{"type":"assistant","message":{"model":"m","content":[]},"parent_tool_use_id":null}""")]
    [InlineData("""{"type":"system","subtype":"init"}""")]
    [InlineData("""{"type":"stream_event","event":{}}""")]
    [InlineData("""{"type":"result","subtype":"success","is_error":false,"duration_ms":1,"duration_api_ms":1,"num_turns":1}""")]
    public async Task ConversationMovedOnAfterErrorResult_KeepsOriginalException(string frame)
    {
        var protocol = Start();
        var processError = new CliProcessException("Claude Code 異常結束", 1);

        _transport.Push(BudgetResult);
        _transport.Push(frame);
        _transport.Fail(processError);

        var (_, error) = await ReadUntilErrorAsync(protocol);
        Assert.Same(processError, error);
    }

    [Fact]
    public async Task ErrorResultThenNonProcessFailure_KeepsOriginalException()
    {
        var protocol = Start();
        var decodeError = new CliJsonDecodeException("壞掉的一行", new JsonException());

        _transport.Push(BudgetResult);
        _transport.Fail(decodeError);

        var (_, error) = await ReadUntilErrorAsync(protocol);
        Assert.Same(decodeError, error);
    }

    [Fact]
    public async Task ErrorResultThenNormalEnd_DoesNotThrow()
    {
        var protocol = Start();

        _transport.Push(BudgetResult);
        _transport.End();

        var (messages, error) = await ReadUntilErrorAsync(protocol);
        Assert.Null(error);
        Assert.Single(messages);
    }

    [Theory]
    [InlineData("""{"is_error":true,"subtype":"error_during_execution","errors":"  單一字串  "}""", "單一字串")]
    [InlineData("""{"is_error":true,"subtype":"error_max_turns","errors":["a","b"]}""", "a; b")]
    [InlineData("""{"is_error":true,"subtype":"success","errors":[],"result":"  API Error: 529 overloaded "}""", "API Error: 529 overloaded")]
    [InlineData("""{"is_error":true,"subtype":"error_max_turns","result":"   "}""", "error_max_turns")]
    [InlineData("""{"is_error":true,"subtype":"success","api_error_status":529}""", "API error (HTTP 529)")]
    [InlineData("""{"is_error":true,"subtype":"success"}""", "unknown error")]
    public void Message_PicksMostInformativeTextLikePython(string result, string expectedText)
    {
        var error = new CliResultException(JsonSerializer.Deserialize<JsonElement>(result), new CliProcessException("x", 2));

        Assert.Equal($"Claude Code 回報了出錯的結果：{expectedText}（exit code 2）", error.Message);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("\"yes\"")]
    public async Task TruthyIsError_CountsAsErrorResult(string isError)
    {
        var protocol = Start();

        _transport.Push($$"""{"type":"result","subtype":"error_during_execution","is_error":{{isError}}}""");
        _transport.Fail(new CliProcessException("Claude Code 異常結束", 1));

        var (_, error) = await ReadUntilErrorAsync(protocol);
        var resultError = Assert.IsType<CliResultException>(error);
        Assert.Equal(529, new CliResultException(
            JsonSerializer.Deserialize<JsonElement>("""{"api_error_status":529}"""), new CliProcessException("x", 1)).ApiErrorStatus);
        Assert.Empty(resultError.Errors);
    }

    private ControlProtocol Start()
    {
        _protocol = new ControlProtocol(_transport, canUseTool: null) { HandlerShutdownTimeout = TimeSpan.FromSeconds(1) };
        _protocol.Start();
        return _protocol;
    }

    private static async Task<(List<JsonElement> Messages, Exception? Error)> ReadUntilErrorAsync(ControlProtocol protocol)
    {
        using var timeout = new CancellationTokenSource(Timeout);
        List<JsonElement> messages = [];
        try
        {
            await foreach (var message in protocol.ReceiveMessagesAsync(timeout.Token))
            {
                messages.Add(message);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return (messages, e);
        }

        return (messages, null);
    }
}
