using System.Text.Json;
using PamisWang.ClaudeAgentSdk.Messages;
using PamisWang.ClaudeAgentSdk.Permissions;
using PamisWang.ClaudeAgentSdk.Tests.Protocol;

namespace PamisWang.ClaudeAgentSdk.Tests;

/// <summary>一次性的 <see cref="ClaudeAgent.QueryAsync"/>：送出、何時關 stdin、讀到 CLI 結束、收尾。</summary>
public sealed class ClaudeAgentTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private const string Assistant =
        """{"type":"assistant","message":{"model":"fake","content":[{"type":"text","text":"Paris"}]},"parent_tool_use_id":null,"session_id":"s"}""";

    private const string Result =
        """{"type":"result","subtype":"success","is_error":false,"duration_ms":1,"duration_api_ms":1,"num_turns":1,"result":"Paris","session_id":"s"}""";

    private readonly TempDirectory _cwd = new();

    public void Dispose() => _cwd.Dispose();

    [Fact]
    public async Task QueryAsync_WithoutCanUseTool_EndsInputRightAfterPromptAndYieldsUntilOutputEnds()
    {
        var transport = new ScriptedTransport();
        var collecting = CollectAsync(ClaudeAgent.QueryCoreAsync("Capital?", new ClaudeAgentOptions(), transport));

        var initialize = await transport.NextWriteAsync();
        RespondSuccess(transport, initialize);
        var prompt = await transport.NextWriteAsync();
        // 還沒收到任何回覆，stdin 就關了
        await transport.InputEnded.WaitAsync(Timeout);
        transport.Push(Assistant);
        transport.Push(Result);
        transport.Push("""{"type":"system","subtype":"after_result"}""");
        transport.End();
        var messages = await collecting;

        Assert.Equal("initialize", initialize.GetProperty("request").GetProperty("subtype").GetString());
        Assert.Equal(
            """{"type":"user","message":{"role":"user","content":"Capital?"},"parent_tool_use_id":null,"session_id":""}""",
            JsonSerializer.Serialize(prompt));
        Assert.Collection(
            messages,
            message => Assert.IsType<AssistantMessage>(message),
            message => Assert.Equal("Paris", Assert.IsType<ResultMessage>(message).Result),
            message => Assert.IsType<SystemMessage>(message));
        Assert.True(transport.Disposed);
    }

    [Fact]
    public async Task QueryAsync_WithCanUseTool_KeepsInputOpenUntilResult()
    {
        var transport = new ScriptedTransport();
        var options = new ClaudeAgentOptions
        {
            CanUseTool = (_, _) => Task.FromResult<PermissionResult>(new PermissionResultAllow()),
        };
        var collecting = CollectAsync(ClaudeAgent.QueryCoreAsync("寫檔", options, transport));

        RespondSuccess(transport, await transport.NextWriteAsync());
        await transport.NextWriteAsync();
        transport.Push("""{"type":"control_request","request_id":"perm-1","request":{"subtype":"can_use_tool","tool_name":"Write","input":{}}}""");
        var permission = await transport.NextWriteAsync();
        transport.Push(Assistant);

        Assert.False(transport.InputEnded.IsCompleted);
        transport.Push(Result);
        await transport.InputEnded.WaitAsync(Timeout);
        transport.End();
        var messages = await collecting;

        Assert.Equal("allow", permission.GetProperty("response").GetProperty("response").GetProperty("behavior").GetString());
        Assert.IsType<ResultMessage>(messages[^1]);
    }

    [Fact]
    public async Task QueryAsync_ConflictingOptions_ThrowsWhenEnumerated()
    {
        var options = new ClaudeAgentOptions
        {
            PermissionPromptToolName = "mcp__perm__ask",
            CanUseTool = (_, _) => Task.FromResult<PermissionResult>(new PermissionResultAllow()),
        };

        await Assert.ThrowsAsync<ArgumentException>(() => CollectAsync(ClaudeAgent.QueryAsync("你好", options)));
    }

    [Fact]
    public async Task QueryAsync_FakeCliProcess_AnswersAndExits()
    {
        var options = new ClaudeAgentOptions
        {
            CliPath = FakeClaude.Path,
            Cwd = _cwd.Path,
            Env = new Dictionary<string, string> { ["FAKE_CLAUDE_MODE"] = "chat" },
        };

        var messages = await CollectAsync(ClaudeAgent.QueryAsync("你好 🐱", options));

        Assert.Equal("收到：你好 🐱", messages.OfType<AssistantMessage>().Single().Content.OfType<TextBlock>().Single().Text);
        Assert.IsType<ResultMessage>(messages[^1]);
    }

    [Fact]
    public async Task QueryAsync_FakeCliErrorResultThenExit_ThrowsCliResultException()
    {
        var options = new ClaudeAgentOptions
        {
            CliPath = FakeClaude.Path,
            Cwd = _cwd.Path,
            Env = new Dictionary<string, string> { ["FAKE_CLAUDE_MODE"] = "error_result" },
            MaxBudgetUsd = 0.0001m,
        };
        List<ClaudeMessage> received = [];

        var error = await Assert.ThrowsAsync<CliResultException>(async () =>
        {
            await foreach (var message in ClaudeAgent.QueryAsync("你好", options))
            {
                received.Add(message);
            }
        });

        // 出錯的結果先照樣收到，CLI 結束後才丟出
        Assert.True(Assert.IsType<ResultMessage>(Assert.Single(received)).IsError);
        Assert.Equal("error_max_budget_usd", error.Subtype);
        Assert.Equal(["Reached maximum budget ($0.0001)"], error.Errors);
        Assert.Equal(1, error.ExitCode);
        Assert.IsType<CliProcessException>(error.InnerException);
    }

    private static void RespondSuccess(ScriptedTransport transport, JsonElement request) =>
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
