using System.Text.Json.Nodes;
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
    public async Task SetModelAsync_FakeCliProcess_LaterRepliesUseNewModel()
    {
        var options = new ClaudeAgentOptions
        {
            CliPath = FakeClaude.Path,
            Cwd = _cwd.Path,
            Env = new Dictionary<string, string> { ["FAKE_CLAUDE_MODE"] = "chat" },
        };
        await using var client = new ClaudeAgentClient(options);
        await client.ConnectAsync().WaitAsync(Timeout);

        var switchedTo = await client.SetModelAsync("sonnet").WaitAsync(Timeout);
        await client.SendAsync("你好");
        var switched = await CollectAsync(client.ReceiveResponseAsync());
        var resetTo = await client.SetModelAsync().WaitAsync(Timeout);
        await client.SendAsync("你好");
        var reset = await CollectAsync(client.ReceiveResponseAsync());

        Assert.Equal("sonnet", switchedTo);
        Assert.Equal("sonnet", switched.OfType<AssistantMessage>().Single().Model);
        Assert.Equal("fake", resetTo);
        Assert.Equal("fake", reset.OfType<AssistantMessage>().Single().Model);
    }

    [Fact]
    public async Task SetModelAsync_RecordedSwitch_ReturnsResolvedModelAndNextTurnUsesIt()
    {
        var fixture = ProtocolFixture.Load("set_model");
        await using var client = new ClaudeAgentClient(new ClaudeAgentOptions(), new ReplayTransport(fixture));
        await client.ConnectAsync();

        await client.SendAsync(fixture.UserPrompts[0]);
        await CollectAsync(client.ReceiveResponseAsync());
        var model = await client.SetModelAsync("sonnet");
        await client.SendAsync(fixture.UserPrompts[1]);
        var messages = await CollectAsync(client.ReceiveResponseAsync());

        Assert.Equal("claude-sonnet-5-5", model);
        // CLI 把換模型的結果當成一則重播的使用者訊息送來
        var notice = messages.OfType<UserMessage>().Single();
        Assert.True(notice.Raw.GetProperty("isReplay").GetBoolean());
        Assert.Contains("Set model to", notice.Content.OfType<TextBlock>().Single().Text, StringComparison.Ordinal);
        Assert.Equal("claude-sonnet-5-5", messages.OfType<AssistantMessage>().Single().Model);
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
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.SetModelAsync("sonnet"));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.SetPermissionModeAsync("plan"));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.ApplyFlagSettingsAsync(new JsonObject { ["effortLevel"] = "low" }));
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

    [Fact]
    public async Task ConnectAsync_CanUseToolWithPermissionPromptToolName_ThrowsBeforeStartingCli()
    {
        var transport = new ScriptedTransport();
        var options = new ClaudeAgentOptions
        {
            PermissionPromptToolName = "mcp__perm__ask",
            CanUseTool = (_, _) => Task.FromResult<PermissionResult>(new PermissionResultAllow()),
        };
        await using var client = new ClaudeAgentClient(options, transport);

        await Assert.ThrowsAsync<ArgumentException>(() => client.ConnectAsync());

        Assert.False(client.IsConnected);
        Assert.True(transport.HasNoPendingWrites);
    }

    [Fact]
    public async Task ConnectAsync_WithAgents_SendsThemInInitialize()
    {
        var transport = new ScriptedTransport();
        var options = new ClaudeAgentOptions
        {
            Agents = new Dictionary<string, AgentDefinition> { ["helper"] = new("Helps out", "You help.") { Model = "haiku" } },
        };
        await using var client = new ClaudeAgentClient(options, transport);

        var connecting = client.ConnectAsync();
        var initialize = await transport.NextWriteAsync();
        RespondSuccess(transport, initialize);
        await connecting.WaitAsync(Timeout);

        Assert.Equal(
            """{"subtype":"initialize","hooks":null,"agents":{"helper":{"description":"Helps out","prompt":"You help.","model":"haiku"}}}""",
            initialize.GetProperty("request").GetRawText());
    }

    [Fact]
    public async Task SetPermissionModeAsync_SendsControlRequest()
    {
        var transport = new ScriptedTransport();
        await using var client = new ClaudeAgentClient(new ClaudeAgentOptions(), transport);
        var connecting = client.ConnectAsync();
        RespondSuccess(transport, await transport.NextWriteAsync());
        await connecting.WaitAsync(Timeout);

        var setting = client.SetPermissionModeAsync("bypassPermissions");
        var request = await transport.NextWriteAsync();
        RespondSuccess(transport, request);
        await setting.WaitAsync(Timeout);

        Assert.Equal("""{"subtype":"set_permission_mode","mode":"bypassPermissions"}""", request.GetProperty("request").GetRawText());
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

    private static void RespondSuccess(ScriptedTransport transport, System.Text.Json.JsonElement request) =>
        transport.Push($$$"""{"type":"control_response","response":{"subtype":"success","request_id":"{{{request.GetProperty("request_id").GetString()}}}"}}""");

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
