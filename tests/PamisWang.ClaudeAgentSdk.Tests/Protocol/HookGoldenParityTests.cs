using System.Text.Json;
using System.Text.Json.Nodes;
using PamisWang.ClaudeAgentSdk.Hooks;
using PamisWang.ClaudeAgentSdk.Protocol;

namespace PamisWang.ClaudeAgentSdk.Tests.Protocol;

/// <summary>
/// 跟真的 Python SDK 對照：Python 的 <c>Query</c> 用同樣的 hook 設定（golden.py）送出的 initialize <c>hooks</c>，
/// 以及對每個 <c>hook_callback</c> 寫出的 control response。JSON 要完全相同（鍵的順序與跳脫可以不同）。
/// Python 那邊的設定：PreToolUse 有 <c>Bash</c>（deny_bash、modify，timeout 5）與不限（empty）兩組、
/// PostToolUse 有 <c>Read</c>（post）、Stop 有 stop_it 與 async_hook、UserPromptSubmit 有 boom（丟出 RuntimeError("hook failed")），
/// 另外 Notification 是空的清單，不會送出。
/// </summary>
public sealed class HookGoldenParityTests : IAsyncLifetime, IAsyncDisposable
{
    private const string GoldenInitialize =
        """{"subtype": "initialize", "hooks": {"PreToolUse": [{"matcher": "Bash", "hookCallbackIds": ["hook_0", "hook_1"], "timeout": 5}, {"matcher": null, "hookCallbackIds": ["hook_2"]}], "PostToolUse": [{"matcher": "Read", "hookCallbackIds": ["hook_3"]}], "Stop": [{"matcher": null, "hookCallbackIds": ["hook_4", "hook_5"]}], "UserPromptSubmit": [{"matcher": null, "hookCallbackIds": ["hook_6"]}]}}""";

    // 依錄製的順序：(Python 處理的 control request, Python 寫出的回應)
    private static readonly (string Request, string Response)[] Golden =
    [
        (
            """{"type": "control_request", "request_id": "r0", "request": {"subtype": "hook_callback", "callback_id": "hook_0", "input": {"session_id": "s", "transcript_path": "/t", "cwd": "/c", "hook_event_name": "PreToolUse", "tool_name": "Bash", "tool_input": {"command": "rm -rf /"}, "tool_use_id": "tu1"}, "tool_use_id": "tu1"}}""",
            """{"type": "control_response", "response": {"subtype": "success", "request_id": "r0", "response": {"hookSpecificOutput": {"hookEventName": "PreToolUse", "permissionDecision": "deny", "permissionDecisionReason": "no bash"}}}}"""
        ),
        (
            """{"type": "control_request", "request_id": "r1", "request": {"subtype": "hook_callback", "callback_id": "hook_1", "input": {"hook_event_name": "PreToolUse", "tool_name": "Bash", "tool_input": {"command": "x"}}, "tool_use_id": "tu2"}}""",
            """{"type": "control_response", "response": {"subtype": "success", "request_id": "r1", "response": {"continue": true, "suppressOutput": false, "systemMessage": "hi", "hookSpecificOutput": {"hookEventName": "PreToolUse", "permissionDecision": "allow", "updatedInput": {"command": "ls"}}}}}"""
        ),
        (
            """{"type": "control_request", "request_id": "r2", "request": {"subtype": "hook_callback", "callback_id": "hook_2", "input": {"hook_event_name": "PreToolUse", "tool_name": "Read", "tool_input": {}}, "tool_use_id": null}}""",
            """{"type": "control_response", "response": {"subtype": "success", "request_id": "r2", "response": {}}}"""
        ),
        (
            """{"type": "control_request", "request_id": "r3", "request": {"subtype": "hook_callback", "callback_id": "hook_3", "input": {"hook_event_name": "PostToolUse", "tool_name": "Read", "tool_input": {"file_path": "/a"}, "tool_response": "content"}, "tool_use_id": "tu3"}}""",
            """{"type": "control_response", "response": {"subtype": "success", "request_id": "r3", "response": {"hookSpecificOutput": {"hookEventName": "PostToolUse", "additionalContext": "observed"}}}}"""
        ),
        (
            """{"type": "control_request", "request_id": "r4", "request": {"subtype": "hook_callback", "callback_id": "hook_4", "input": {"hook_event_name": "Stop", "stop_hook_active": false}, "tool_use_id": null}}""",
            """{"type": "control_response", "response": {"subtype": "success", "request_id": "r4", "response": {"continue": false, "stopReason": "enough", "decision": "block", "reason": "r"}}}"""
        ),
        (
            """{"type": "control_request", "request_id": "r5", "request": {"subtype": "hook_callback", "callback_id": "hook_5", "input": {"hook_event_name": "Stop", "stop_hook_active": true}, "tool_use_id": null}}""",
            """{"type": "control_response", "response": {"subtype": "success", "request_id": "r5", "response": {"async": true, "asyncTimeout": 5000}}}"""
        ),
        (
            """{"type": "control_request", "request_id": "r6", "request": {"subtype": "hook_callback", "callback_id": "hook_6", "input": {"hook_event_name": "UserPromptSubmit", "prompt": "p"}, "tool_use_id": null}}""",
            """{"type": "control_response", "response": {"subtype": "error", "request_id": "r6", "error": "hook failed"}}"""
        ),
        (
            """{"type": "control_request", "request_id": "r7", "request": {"subtype": "hook_callback", "callback_id": "hook_99", "input": {"hook_event_name": "Stop"}, "tool_use_id": null}}""",
            """{"type": "control_response", "response": {"subtype": "error", "request_id": "r7", "error": "No hook callback found for ID: hook_99"}}"""
        ),
    ];

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly ScriptedTransport _transport = new();
    private readonly List<(HookInput Input, string? ToolUseId)> _denyBashCalls = [];
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
    public async Task Initialize_HooksPayloadMatchesPython()
    {
        var protocol = Start();

        var initializing = protocol.InitializeAsync(Timeout);
        var request = await _transport.NextWriteAsync();
        _transport.Push($$$"""{"type":"control_response","response":{"subtype":"success","request_id":"{{{request.GetProperty("request_id").GetString()}}}","response":{} } }""");
        await initializing;

        AssertJsonEqual(GoldenInitialize, request.GetProperty("request").GetRawText(), "initialize");
    }

    [Fact]
    public async Task ReplayGolden_EveryResponseMatchesPython()
    {
        Start();

        for (var i = 0; i < Golden.Length; i++)
        {
            var (request, expected) = Golden[i];
            _transport.Push(request);
            var actual = await _transport.NextWriteAsync();

            AssertJsonEqual(expected, actual.GetRawText(), $"golden #{i}: {request}");
        }

        // Python 的 deny_bash 收到的是 (input, "tu1", {"signal": None})
        var (input, toolUseId) = Assert.Single(_denyBashCalls);
        var preToolUse = Assert.IsType<PreToolUseHookInput>(input);
        Assert.Equal("tu1", toolUseId);
        Assert.Equal("s", preToolUse.SessionId);
        Assert.Equal("/t", preToolUse.TranscriptPath);
        Assert.Equal("/c", preToolUse.Cwd);
        Assert.Equal("Bash", preToolUse.ToolName);
        Assert.Equal("rm -rf /", preToolUse.ToolInput.GetProperty("command").GetString());
        Assert.Equal("tu1", preToolUse.ToolUseId);
        AssertJsonEqual(
            """{"session_id": "s", "transcript_path": "/t", "cwd": "/c", "hook_event_name": "PreToolUse", "tool_name": "Bash", "tool_input": {"command": "rm -rf /"}, "tool_use_id": "tu1"}""",
            preToolUse.Raw.GetRawText(),
            "deny_bash input");
    }

    private static void AssertJsonEqual(string expected, string actual, string step)
    {
        var expectedNode = JsonNode.Parse(expected);
        var actualNode = JsonNode.Parse(actual);
        Assert.True(
            JsonNode.DeepEquals(expectedNode, actualNode),
            $"{step}\nexpected: {expectedNode?.ToJsonString()}\nactual:   {actualNode?.ToJsonString()}");
    }

    /// <summary>跟 golden.py 一樣的 hook 設定。</summary>
    private ControlProtocol Start()
    {
        HookCallback denyBash = (input, toolUseId, _, _) =>
        {
            _denyBashCalls.Add((input, toolUseId));
            return ValueTask.FromResult<HookOutput>(new SyncHookOutput
            {
                HookSpecificOutput = new PreToolUseHookSpecificOutput { PermissionDecision = "deny", PermissionDecisionReason = "no bash" },
            });
        };
        HookCallback modify = (_, _, _, _) => ValueTask.FromResult<HookOutput>(new SyncHookOutput
        {
            Continue = true,
            SuppressOutput = false,
            SystemMessage = "hi",
            HookSpecificOutput = new PreToolUseHookSpecificOutput
            {
                PermissionDecision = "allow",
                UpdatedInput = JsonSerializer.Deserialize<JsonElement>("""{"command":"ls"}"""),
            },
        });
        HookCallback post = (_, _, _, _) => ValueTask.FromResult<HookOutput>(new SyncHookOutput
        {
            HookSpecificOutput = new PostToolUseHookSpecificOutput { AdditionalContext = "observed" },
        });
        HookCallback stopIt = (_, _, _, _) => ValueTask.FromResult<HookOutput>(new SyncHookOutput
        {
            Continue = false,
            StopReason = "enough",
            Decision = "block",
            Reason = "r",
        });
        HookCallback asyncHook = (_, _, _, _) => ValueTask.FromResult<HookOutput>(new AsyncHookOutput(AsyncTimeout: 5000));
        HookCallback empty = (_, _, _, _) => ValueTask.FromResult(HookOutput.Empty);
        HookCallback boom = (_, _, _, _) => throw new InvalidOperationException("hook failed");

        var hooks = new Dictionary<string, IReadOnlyList<HookMatcher>>
        {
            [HookEvent.PreToolUse] = [new HookMatcher("Bash", [denyBash, modify], Timeout: 5), new HookMatcher(null, empty)],
            [HookEvent.PostToolUse] = [new HookMatcher("Read", post)],
            [HookEvent.Stop] = [new HookMatcher(null, [stopIt, asyncHook])],
            [HookEvent.UserPromptSubmit] = [new HookMatcher(null, boom)],
            [HookEvent.Notification] = [],
        };

        _protocol = new ControlProtocol(_transport, canUseTool: null, mcpServers: null, hooks)
        {
            HandlerShutdownTimeout = TimeSpan.FromSeconds(1),
        };
        _protocol.Start();
        return _protocol;
    }
}
