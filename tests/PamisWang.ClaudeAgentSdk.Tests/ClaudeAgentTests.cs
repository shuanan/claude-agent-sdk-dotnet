using System.Text.Json;
using PamisWang.ClaudeAgentSdk.Hooks;
using PamisWang.ClaudeAgentSdk.Mcp;
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
    public async Task QueryAsync_WithHooks_RegistersThemAndKeepsInputOpenUntilResult()
    {
        var transport = new ScriptedTransport();
        var options = new ClaudeAgentOptions
        {
            Hooks = new Dictionary<string, IReadOnlyList<HookMatcher>>
            {
                [HookEvent.PreToolUse] = [new HookMatcher("Bash", (input, _, _, _) => ValueTask.FromResult<HookOutput>(new SyncHookOutput
                {
                    HookSpecificOutput = new PreToolUseHookSpecificOutput
                    {
                        PermissionDecision = "deny",
                        PermissionDecisionReason = $"不准 {((PreToolUseHookInput)input).ToolInput.GetProperty("command").GetString()}",
                    },
                }))],
            },
        };
        var collecting = CollectAsync(ClaudeAgent.QueryCoreAsync("刪掉", options, transport));

        var initialize = await transport.NextWriteAsync();
        RespondSuccess(transport, initialize);
        await transport.NextWriteAsync();
        // 送出問題後 stdin 還開著，CLI 才能在回合中呼叫 hook
        Assert.False(transport.InputEnded.IsCompleted);
        transport.Push("""{"type":"control_request","request_id":"hook-1","request":{"subtype":"hook_callback","callback_id":"hook_0","input":{"hook_event_name":"PreToolUse","tool_name":"Bash","tool_input":{"command":"rm -rf /"}},"tool_use_id":"tu1"}}""");
        var hookResponse = await transport.NextWriteAsync();
        transport.Push(Assistant);

        Assert.False(transport.InputEnded.IsCompleted);
        transport.Push(Result);
        await transport.InputEnded.WaitAsync(Timeout);
        transport.End();
        var messages = await collecting;

        Assert.Equal(
            """{"PreToolUse":[{"matcher":"Bash","hookCallbackIds":["hook_0"]}]}""",
            initialize.GetProperty("request").GetProperty("hooks").GetRawText());
        var specific = hookResponse.GetProperty("response").GetProperty("response").GetProperty("hookSpecificOutput");
        Assert.Equal("deny", specific.GetProperty("permissionDecision").GetString());
        Assert.Equal("不准 rm -rf /", specific.GetProperty("permissionDecisionReason").GetString());
        Assert.IsType<ResultMessage>(messages[^1]);
    }

    [Fact]
    public async Task QueryAsync_WithSdkMcpServer_KeepsInputOpenForToolCallsUntilResultThenEndsIt()
    {
        var transport = new ScriptedTransport();
        var add = SdkMcpTool.Create(
            "add",
            "Add two numbers",
            McpSchema.Parameters(("a", typeof(double)), ("b", typeof(double))),
            args => ValueTask.FromResult(McpToolResult.Text($"{args.GetProperty("a").GetDouble() + args.GetProperty("b").GetDouble()}")));
        var options = new ClaudeAgentOptions
        {
            McpServers = new Dictionary<string, McpServerConfig> { ["calc"] = SdkMcpServer.Create("calculator", add) },
            AllowedTools = ["mcp__calc__add"],
        };
        var collecting = CollectAsync(ClaudeAgent.QueryCoreAsync("1+2?", options, transport));

        RespondSuccess(transport, await transport.NextWriteAsync());
        await transport.NextWriteAsync();
        // 送出問題後 stdin 還開著，CLI 才能在回合中呼叫工具
        Assert.False(transport.InputEnded.IsCompleted);
        // 跟 CLI 一樣先握手，再呼叫工具
        transport.Push("""{"type":"control_request","request_id":"mcp-0","request":{"subtype":"mcp_message","server_name":"calc","message":{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"claude-code","version":"2.1.312"}}}}}""");
        await transport.NextWriteAsync();
        transport.Push("""{"type":"control_request","request_id":"mcp-1","request":{"subtype":"mcp_message","server_name":"calc","message":{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"add","arguments":{"a":1,"b":2}}}}}""");
        var toolResponse = await transport.NextWriteAsync();
        transport.Push(Assistant);

        Assert.False(transport.InputEnded.IsCompleted);
        transport.Push(Result);
        await transport.InputEnded.WaitAsync(Timeout);
        transport.End();
        var messages = await collecting;

        Assert.Equal(
            """{"type":"control_response","response":{"subtype":"success","request_id":"mcp-1","response":{"mcp_response":{"jsonrpc":"2.0","id":1,"result":{"content":[{"type":"text","text":"3"}],"isError":false}}}}}""",
            toolResponse.GetRawText());
        Assert.IsType<ResultMessage>(messages[^1]);
    }

    [Fact]
    public async Task QueryAsync_WithOnlyExternalMcpServer_EndsInputRightAfterPrompt()
    {
        var transport = new ScriptedTransport();
        var options = new ClaudeAgentOptions
        {
            McpServers = new Dictionary<string, McpServerConfig> { ["fs"] = new McpStdioServerConfig("npx") },
        };
        var collecting = CollectAsync(ClaudeAgent.QueryCoreAsync("hi", options, transport));

        RespondSuccess(transport, await transport.NextWriteAsync());
        await transport.NextWriteAsync();
        await transport.InputEnded.WaitAsync(Timeout);
        transport.Push(Result);
        transport.End();

        Assert.IsType<ResultMessage>(Assert.Single(await collecting));
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
