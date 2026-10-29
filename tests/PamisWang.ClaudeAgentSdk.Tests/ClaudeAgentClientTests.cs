using PamisWang.ClaudeAgentSdk.Messages;
using PamisWang.ClaudeAgentSdk.Permissions;
using PamisWang.ClaudeAgentSdk.Tests.Protocol;

namespace PamisWang.ClaudeAgentSdk.Tests;

public sealed class ClaudeAgentClientTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly TempDirectory _cwd = new();

    public void Dispose() => _cwd.Dispose();

    [Fact]
    public async Task ReceiveResponseAsync_RecordedTurn_YieldsTypedMessagesEndingWithResult()
    {
        var fixture = ProtocolFixture.Load("basic");
        await using var client = new ClaudeAgentClient(new ClaudeAgentOptions(), new ReplayTransport(fixture));

        await client.ConnectAsync();
        await client.SendAsync(fixture.UserPrompts[0]);
        var messages = await CollectAsync(client.ReceiveResponseAsync());

        Assert.True(client.IsConnected);
        Assert.Equal(fixture.Meta.GetProperty("cli_version").GetString(), client.ServerInfo?.GetProperty("claude_code_version").GetString());
        Assert.Equal("init", Assert.IsType<SystemMessage>(messages[0]).Subtype);
        Assert.Contains(messages, message => message is AssistantMessage);
        Assert.IsType<ResultMessage>(messages[^1]);
    }

    [Fact]
    public async Task ReceiveResponseAsync_TwoTurns_EachEndsAtItsResult()
    {
        var fixture = ProtocolFixture.Load("multi_turn");
        await using var client = new ClaudeAgentClient(new ClaudeAgentOptions(), new ReplayTransport(fixture));
        await client.ConnectAsync();

        await client.SendAsync(fixture.UserPrompts[0]);
        var first = (await CollectAsync(client.ReceiveResponseAsync())).OfType<ResultMessage>().Single();
        await client.SendAsync(fixture.UserPrompts[1]);
        var second = (await CollectAsync(client.ReceiveResponseAsync())).OfType<ResultMessage>().Single();

        Assert.Equal("好", first.Result);
        Assert.Equal("鳳梨酥", second.Result);
        Assert.Equal(first.SessionId, second.SessionId);
    }

    [Fact]
    public async Task CanUseTool_RecordedPermission_IsAnsweredThroughOptions()
    {
        var fixture = ProtocolFixture.Load("permission_allow");
        var options = new ClaudeAgentOptions
        {
            CanUseTool = (_, _) => Task.FromResult<PermissionResult>(new PermissionResultAllow()),
        };
        await using var client = new ClaudeAgentClient(options, new ReplayTransport(fixture));
        await client.ConnectAsync();

        await client.SendAsync(fixture.UserPrompts[0]);
        var messages = await CollectAsync(client.ReceiveResponseAsync());

        var toolResult = messages.OfType<UserMessage>().SelectMany(m => m.Content).OfType<ToolResultBlock>().Single();
        Assert.NotEqual(true, toolResult.IsError);
        Assert.Equal("success", messages.OfType<ResultMessage>().Single().Subtype);
    }

    [Fact]
    public async Task InterruptAsync_RecordedInterrupt_EndsTurnWithErrorResult()
    {
        var fixture = ProtocolFixture.Load("interrupt");
        await using var client = new ClaudeAgentClient(new ClaudeAgentOptions(), new ReplayTransport(fixture));
        await client.ConnectAsync();
        await client.SendAsync(fixture.UserPrompts[0]);
        ResultMessage? result = null;

        var interrupted = false;
        await foreach (var message in client.ReceiveResponseAsync())
        {
            if (!interrupted && message is StreamEvent { EventType: "content_block_delta" })
            {
                interrupted = true;
                await client.InterruptAsync();
            }

            result = message as ResultMessage;
        }

        Assert.True(result?.IsError);
        Assert.Equal("aborted_streaming", result?.TerminalReason);
    }

    [Fact]
    public async Task EndToEnd_FakeCliProcess_ChatsAndAnswersPermission()
    {
        ToolPermissionRequest? asked = null;
        var options = new ClaudeAgentOptions
        {
            CliPath = FakeClaude.Path,
            Cwd = _cwd.Path,
            Env = new Dictionary<string, string> { ["FAKE_CLAUDE_MODE"] = "chat" },
            CanUseTool = (request, _) =>
            {
                asked = request;
                return Task.FromResult<PermissionResult>(new PermissionResultDeny("不行"));
            },
        };
        await using var client = new ClaudeAgentClient(options);
        await client.ConnectAsync().WaitAsync(Timeout);

        await client.SendAsync("你好 🐱");
        var reply = await CollectAsync(client.ReceiveResponseAsync());
        await client.SendAsync("請求權限");
        var permission = await CollectAsync(client.ReceiveResponseAsync());
        await client.InterruptAsync().WaitAsync(Timeout);

        Assert.Equal("fake", client.ServerInfo?.GetProperty("claude_code_version").GetString());
        Assert.Equal("收到：你好 🐱", reply.OfType<AssistantMessage>().Single().Content.OfType<TextBlock>().Single().Text);
        Assert.Equal("Write", asked?.ToolName);
        Assert.Equal("deny", permission.OfType<ResultMessage>().Single().Result);
    }

    [Fact]
    public async Task SendAsync_BeforeConnect_ThrowsCliConnectionException()
    {
        await using var client = new ClaudeAgentClient(new ClaudeAgentOptions(), new ScriptedTransport());

        await Assert.ThrowsAsync<CliConnectionException>(() => client.SendAsync("你好"));
        Assert.False(client.IsConnected);
        Assert.Null(client.ServerInfo);
    }

    [Fact]
    public async Task Methods_AfterDispose_ThrowObjectDisposed()
    {
        var client = new ClaudeAgentClient(new ClaudeAgentOptions(), new ReplayTransport(ProtocolFixture.Load("basic")));
        await client.ConnectAsync();

        await client.DisposeAsync();
        await client.DisposeAsync();

        Assert.False(client.IsConnected);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.SendAsync("你好"));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.InterruptAsync());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.ConnectAsync());
    }

    [Fact]
    public async Task ConnectAsync_Twice_Throws()
    {
        await using var client = new ClaudeAgentClient(new ClaudeAgentOptions(), new ReplayTransport(ProtocolFixture.Load("basic")));
        await client.ConnectAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ConnectAsync());
    }

    [Fact]
    public async Task ConnectAsync_CliMissing_ThrowsAndClientIsClosed()
    {
        var client = new ClaudeAgentClient(new ClaudeAgentOptions { CliPath = Path.Combine(_cwd.Path, "claude.exe"), Cwd = _cwd.Path });

        await Assert.ThrowsAsync<CliNotFoundException>(() => client.ConnectAsync());

        Assert.False(client.IsConnected);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.SendAsync("你好"));
        await client.DisposeAsync();
    }

    [Theory]
    [InlineData(null, 60)]
    [InlineData("", 60)]
    [InlineData("不是數字", 60)]
    [InlineData("1000", 60)]
    [InlineData("120000", 120)]
    public void InitializeTimeout_AtLeastSixtySeconds(string? environmentValue, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), ClaudeAgentClient.InitializeTimeout(environmentValue));
    }

    private static async Task<List<ClaudeMessage>> CollectAsync(IAsyncEnumerable<ClaudeMessage> messages)
    {
        using var timeout = new CancellationTokenSource(Timeout);
        List<ClaudeMessage> collected = [];
        await foreach (var message in messages.WithCancellation(timeout.Token))
        {
            collected.Add(message);
        }

        return collected;
    }
}
