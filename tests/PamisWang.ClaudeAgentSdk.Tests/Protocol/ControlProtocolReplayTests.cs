using System.Diagnostics;
using System.Text.Json;
using PamisWang.ClaudeAgentSdk.Permissions;

namespace PamisWang.ClaudeAgentSdk.Tests.Protocol;

/// <summary>
/// 拿錄下的協定樣本重播：SDK 送出的每一則都要跟 Python SDK 當時送的一樣（ReplayTransport 逐則比對），
/// 上層收到的訊息也要跟樣本一致。
/// </summary>
public sealed class ControlProtocolReplayTests
{
    private const string DenyMessage = "使用者在辦公室拒絕了這個動作";

    [Theory]
    [InlineData("basic")]
    [InlineData("resume")]
    [InlineData("partial_messages")]
    public async Task OneTurn_DeliversEveryMessageExceptControlFrames(string fixture)
    {
        await using var session = await ReplaySession.StartAsync(fixture);

        await session.SendNextUserMessageAsync();
        var result = await session.ReceiveUntilResultAsync();

        Assert.Equal("success", result.GetProperty("subtype").GetString());
        AssertSameMessages(session.ExpectedReceived, session.Received);
    }

    [Fact]
    public async Task Initialize_ReturnsCliResponse()
    {
        await using var session = await ReplaySession.StartAsync("basic");

        Assert.Equal(
            session.Fixture.Meta.GetProperty("cli_version").GetString(),
            session.InitializeResponse.GetProperty("claude_code_version").GetString());
    }

    [Fact]
    public async Task MultiTurn_SecondMessageContinuesConversation()
    {
        await using var session = await ReplaySession.StartAsync("multi_turn");

        await session.SendNextUserMessageAsync();
        await session.ReceiveUntilResultAsync();
        await session.SendNextUserMessageAsync();
        var result = await session.ReceiveUntilResultAsync();

        Assert.Equal("鳳梨酥", result.GetProperty("result").GetString());
        AssertSameMessages(session.ExpectedReceived, session.Received);
    }

    [Fact]
    public async Task PermissionAllow_PassesRequestToCallbackAndAnswersWithOriginalInput()
    {
        ToolPermissionRequest? asked = null;
        await using var session = await ReplaySession.StartAsync("permission_allow", (request, _) =>
        {
            asked = request;
            return Task.FromResult<PermissionResult>(new PermissionResultAllow());
        });

        await session.SendNextUserMessageAsync();
        var result = await session.ReceiveUntilResultAsync();

        Assert.Equal("success", result.GetProperty("subtype").GetString());
        Assert.NotNull(asked);
        Assert.Equal("Write", asked.ToolName);
        Assert.Equal("你好", asked.Input.GetProperty("content").GetString());
        Assert.StartsWith("toolu_", asked.ToolUseId, StringComparison.Ordinal);
        Assert.Equal(new PermissionUpdate("setMode") { Mode = "acceptEdits", Destination = "session" }, Assert.Single(asked.Suggestions));
    }

    [Fact]
    public async Task PermissionDeny_SendsDenyMessage()
    {
        await using var session = await ReplaySession.StartAsync(
            "permission_deny",
            (_, _) => Task.FromResult<PermissionResult>(new PermissionResultDeny(DenyMessage)));

        await session.SendNextUserMessageAsync();
        var result = await session.ReceiveUntilResultAsync();

        Assert.Equal("Write", result.GetProperty("permission_denials")[0].GetProperty("tool_name").GetString());
    }

    [Fact]
    public async Task PermissionAlwaysAllow_ReturningSuggestionsStopsLaterPrompts()
    {
        var calls = 0;
        await using var session = await ReplaySession.StartAsync("permission_always_allow", (request, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult<PermissionResult>(new PermissionResultAllow(UpdatedPermissions: request.Suggestions));
        });

        await session.SendNextUserMessageAsync();
        await session.ReceiveUntilResultAsync();
        await session.SendNextUserMessageAsync();
        await session.ReceiveUntilResultAsync();

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Interrupt_StopsTurnAndConversationContinues()
    {
        await using var session = await ReplaySession.StartAsync("interrupt");
        var interrupted = false;

        await session.SendNextUserMessageAsync();
        var interruptedResult = await session.ReceiveUntilResultAsync(async message =>
        {
            if (!interrupted && message.GetProperty("type").GetString() == "stream_event"
                && message.GetProperty("event").GetProperty("type").GetString() == "content_block_delta")
            {
                interrupted = true;
                await session.Protocol.InterruptAsync();
            }
        });
        await session.SendNextUserMessageAsync();
        var result = await session.ReceiveUntilResultAsync();

        Assert.Equal("error_during_execution", interruptedResult.GetProperty("subtype").GetString());
        Assert.Equal("好", result.GetProperty("result").GetString());
    }

    [Fact]
    public async Task InterruptDuringPermission_CancelsCallbackWithoutAnswering()
    {
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = await ReplaySession.StartAsync("interrupt_during_permission", async (_, cancellationToken) =>
        {
            asked.SetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                cancelled.SetResult();
                throw;
            }

            throw new UnreachableException();
        });

        await session.SendNextUserMessageAsync();
        var interrupting = asked.Task.ContinueWith(_ => session.Protocol.InterruptAsync(), TaskScheduler.Default).Unwrap();
        var result = await session.ReceiveUntilResultAsync();
        await interrupting;
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await session.Protocol.DisposeAsync();

        Assert.Equal("aborted_tools", result.GetProperty("terminal_reason").GetString());
        // 被撤回的詢問沒有回覆：只送了 initialize、使用者訊息與 interrupt
        Assert.Equal(
            ["initialize", "user", "interrupt"],
            session.Transport.Writes.Select(message => message.GetProperty("type").GetString() == "user"
                ? "user"
                : message.GetProperty("request").GetProperty("subtype").GetString()));
    }

    private static void AssertSameMessages(IReadOnlyList<JsonElement> expected, List<JsonElement> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        Assert.All(expected.Zip(actual), pair => Assert.True(
            JsonElement.DeepEquals(pair.First, pair.Second),
            $"預期 {pair.First.GetRawText()}\n實際 {pair.Second.GetRawText()}"));
    }
}
