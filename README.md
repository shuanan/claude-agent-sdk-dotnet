# PamisWang.ClaudeAgentSdk

Claude Code 的 .NET 用戶端：啟動本機的 `claude` CLI，用 stream-json 協定對話、批准工具、中斷、接續 session。

A .NET client for Claude Code: it launches the local `claude` CLI and talks to it over the stream-json protocol to chat, approve tool use, interrupt, and resume sessions.

> **非官方**。移植自 [claude-agent-sdk-python](https://github.com/anthropics/claude-agent-sdk-python)，與 Anthropic 無關。官方的 Agent SDK 只有 Python 與 TypeScript。
> Python SDK 沒有的少數功能，照 TypeScript SDK 送給 CLI 的協定格式另外寫；TypeScript SDK 不是開源授權，沒有移植它的程式碼。
>
> **Unofficial.** Ported from [claude-agent-sdk-python](https://github.com/anthropics/claude-agent-sdk-python) and not affiliated with Anthropic. The official Agent SDK is available only for Python and TypeScript.
> The few features missing from the Python SDK are written against the protocol messages the TypeScript SDK sends to the CLI; the TypeScript SDK is not open-source licensed, and none of its code is ported.

## 狀態 / Status

預覽版已經發佈到 NuGet；1.0 之前 API 可能還會變。

A preview is available on NuGet; the API may still change before 1.0.

```bash
dotnet add package PamisWang.ClaudeAgentSdk --prerelease
```

| 對照 / Tracks | 版本 / Version |
|---|---|
| Python SDK | v0.2.174 |
| TypeScript SDK | v0.3.316（只參考 Python 沒有的 `apply_flag_settings` / only for `apply_flag_settings`, which Python lacks） |
| Claude Code CLI | 2.1.312（Python SDK v0.2.174 打包的版本 / bundled with Python SDK v0.2.174） |
| 錄協定樣本用的 CLI / CLI used to record protocol fixtures | 2.1.309 |

## 使用方式 / Usage

```csharp
using System.Text.Json.Nodes;
using PamisWang.ClaudeAgentSdk;
using PamisWang.ClaudeAgentSdk.Messages;
using PamisWang.ClaudeAgentSdk.Permissions;

var options = new ClaudeAgentOptions
{
    Cwd = @"D:\work\my-project",
    SystemPrompt = new SystemPromptPreset(), // Claude Code 預設的系統提示詞；不給是空的 / Claude Code's default system prompt; empty if omitted
    Model = "sonnet",
    Effort = "high",
    Resume = null,                        // 接續舊 session 時填 session ID / a session ID to resume
    IncludePartialMessages = true,        // 逐段收到輸出 / receive output as it streams
    DisallowedTools = ["AskUserQuestion"],
    // 工具要執行前詢問；中斷時 cancellationToken 會被取消 / asked before a tool runs; cancelled on interrupt
    CanUseTool = async (request, cancellationToken) =>
        await AskUserAsync(request.ToolName, request.Input, cancellationToken)
            ? new PermissionResultAllow(UpdatedPermissions: request.Suggestions) // 一律允許 / always allow
            : new PermissionResultDeny("使用者拒絕了 / denied by the user"),
};

await using var client = new ClaudeAgentClient(options);
await client.ConnectAsync();
await client.SendAsync("這個專案在做什麼？");

await foreach (var message in client.ReceiveResponseAsync())
{
    switch (message)
    {
        case StreamEvent { TextDelta: { } delta }:
            Console.Write(delta);
            break;
        case AssistantMessage assistant:
            foreach (var tool in assistant.Content.OfType<ToolUseBlock>())
            {
                Console.WriteLine($"\n[{tool.Name}]");
            }

            break;
        case ResultMessage result:
            Console.WriteLine($"\n{result.Subtype}，{result.DurationMs} ms");
            break;
    }
}

// 接著說下一句，或中斷這一輪 / keep talking, or interrupt the current turn
await client.SendAsync("那測試怎麼跑？");
await client.InterruptAsync();

// 對話中途換模型、換思考深度 / switch the model or effort mid-conversation
await client.SetModelAsync("opus");
await client.ApplyFlagSettingsAsync(new JsonObject { ["effortLevel"] = "max" });
```

- 沒指定 `SystemPrompt` 時跟 Python SDK 一樣傳空的系統提示詞，Claude 不會拿到 Claude Code 預設的那份（寫程式的準則、工作目錄與 git 狀態等）；要跟在終端機用時一樣，指定 `SystemPromptPreset`。
  Without `SystemPrompt`, an empty system prompt is sent, as in the Python SDK, so Claude does not get Claude Code's default one (coding guidelines, working directory, git status, and so on); use `SystemPromptPreset` to match the terminal experience.
- 可以選的模型與各自支援的思考深度，在 `ServerInfo` 的 `models` 裡。
  The available models and the effort levels each supports are listed under `models` in `ServerInfo`.

- `ReceiveResponseAsync` 讀到這一輪的 `ResultMessage` 為止；`ReceiveMessagesAsync` 跨越多輪一直讀，適合在背景持續接收。
  `ReceiveResponseAsync` stops after the turn's `ResultMessage`; `ReceiveMessagesAsync` keeps reading across turns, which suits a background receive loop.
- 每則訊息都有 `Raw`（原始 JSON），沒有對應屬性的欄位從這裡讀；不認得的訊息型別是 `UnknownMessage`，不會被丟掉。
  Every message carries `Raw` (the original JSON) for fields without a property; unrecognized message types arrive as `UnknownMessage` instead of being dropped.
- 一個 client 只連線一次；要重新開始就建立新的 client，用 `Resume` 接續原本的 session。
  A client connects once; to start over, create a new client and use `Resume` to continue the session.

### SDK MCP 工具 / In-process MCP tools

工具在你的程式裡執行，不另外開程序；對應 Python 的 `@tool` 與 `create_sdk_mcp_server()`。
Tools run inside your process, with no extra server process; this mirrors Python's `@tool` and `create_sdk_mcp_server()`.

```csharp
using PamisWang.ClaudeAgentSdk;
using PamisWang.ClaudeAgentSdk.Mcp;
using PamisWang.ClaudeAgentSdk.Messages;

var add = SdkMcpTool.Create(
    "add",
    "把兩個數字相加 / Add two numbers",
    McpSchema.Parameters(("a", typeof(double)), ("b", typeof(double))), // {"type":"object",…,"required":["a","b"]}
    args => ValueTask.FromResult(McpToolResult.Text(
        $"{args.GetProperty("a").GetDouble() + args.GetProperty("b").GetDouble()}")));

var options = new ClaudeAgentOptions
{
    McpServers = new Dictionary<string, McpServerConfig>
    {
        ["calc"] = SdkMcpServer.Create("calculator", add),            // 可以跟 stdio/SSE/HTTP 混用 / mixes with stdio/SSE/HTTP
    },
    AllowedTools = ["mcp__calc__add"],                                // mcp__<鍵/key>__<工具/tool>
};

await foreach (var message in ClaudeAgent.QueryAsync("1.5 加 2 是多少？", options))
{
    if (message is ResultMessage result)
    {
        Console.WriteLine(result.Result);
    }
}
```

- 工具的完整名稱是 `mcp__<McpServers 的鍵>__<工具名稱>`，上例是 `mcp__calc__add`；放進 `AllowedTools` 就不必詢問。
  A tool's full name is `mcp__<McpServers key>__<tool name>` (`mcp__calc__add` above); list it in `AllowedTools` to skip the permission prompt.
- 參數先用 `InputSchema` 驗證；參數不合格、找不到工具、處理函式丟出例外，都跟 Python 一樣回給 Claude 一個出錯的結果（`isError`），不讓請求失敗。要自己回報錯誤就用 `McpToolResult.Error("…")`。
  Arguments are validated against `InputSchema` first; invalid arguments, unknown tools and exceptions thrown by a handler all come back to Claude as error results (`isError`), as in Python. Return `McpToolResult.Error("…")` to report a failure yourself.
- 給 CLI 的 JSON-RPC 回應跟 Python SDK（搭配 mcp 2.3.0）逐則比對過；參數驗證只實作常用的 JSON Schema 關鍵字（`$ref`、`format` 不檢查）。
  The JSON-RPC responses are checked against recordings of the Python SDK (with mcp 2.3.0); argument validation covers the common JSON Schema keywords only (`$ref` and `format` are not checked).
- 有 SDK MCP server 時，`ClaudeAgent.QueryAsync` 會留著 stdin 到第一個 `ResultMessage` 才關，讓 CLI 在回合中呼叫工具。
  With an SDK MCP server, `ClaudeAgent.QueryAsync` keeps stdin open until the first `ResultMessage` so the CLI can call the tools during the turn.

### Hook

在 Claude Code 的事件上跑你的程式，例如擋下工具、記錄工具的結果；對應 Python 的 `hooks` 與 `HookMatcher`。
Run your own code on Claude Code events, such as blocking a tool or observing its result; this mirrors Python's `hooks` and `HookMatcher`.

```csharp
using PamisWang.ClaudeAgentSdk;
using PamisWang.ClaudeAgentSdk.Hooks;

var options = new ClaudeAgentOptions
{
    Hooks = new Dictionary<string, IReadOnlyList<HookMatcher>>
    {
        // 擋下所有 Bash / Deny every Bash call
        [HookEvent.PreToolUse] = [new HookMatcher("Bash", (input, toolUseId, context, ct) =>
            ValueTask.FromResult<HookOutput>(new SyncHookOutput
            {
                HookSpecificOutput = new PreToolUseHookSpecificOutput
                {
                    PermissionDecision = "deny",
                    PermissionDecisionReason = "這裡不能用 Bash / Bash is not allowed here",
                },
            }))],
        // 看 Read 讀了什麼 / Observe what Read returned
        [HookEvent.PostToolUse] = [new HookMatcher("Read", (input, toolUseId, context, ct) =>
        {
            var read = (PostToolUseHookInput)input;
            Console.WriteLine($"Read {read.ToolInput.GetProperty("file_path")}: {read.ToolResponse}");
            return ValueTask.FromResult(HookOutput.Empty);
        })],
    },
};
```

- 輸入依事件是對應的型別（`PreToolUseHookInput`、`StopHookInput`…），不認得的事件是 `UnknownHookInput`；原始 JSON 都在 `Raw`。
  Inputs are typed per event (`PreToolUseHookInput`, `StopHookInput`, …); unknown events arrive as `UnknownHookInput`, and the original JSON is always in `Raw`.
- 結果：`SyncHookOutput`（`Continue`、`StopReason`、`Decision`、`SystemMessage`、`Reason`、`HookSpecificOutput`）、
  `AsyncHookOutput`（`{"async": true}`），或自己組 JSON 的 `RawHookOutput`。回呼丟出例外時 CLI 收到的是錯誤回應。
  Results: `SyncHookOutput`, `AsyncHookOutput` (`{"async": true}`), or `RawHookOutput` for hand-built JSON. An exception thrown by a callback becomes an error response to the CLI.
- 同一個事件的多個 matcher 會被 CLI 同時觸發，回呼可能同時在跑；CLI 撤回時 `CancellationToken` 會被取消，不送回應。
  The CLI fires all matchers of one event concurrently, so callbacks may run in parallel; when the CLI withdraws a call the `CancellationToken` is cancelled and no response is sent.
- initialize 送出的設定與每種回應都跟 Python SDK 實際的輸出比對過（`HookGoldenParityTests`）。有 hook 時 `ClaudeAgent.QueryAsync` 一樣留著 stdin 到第一個結果。
  The initialize payload and every response are checked against the real Python SDK (`HookGoldenParityTests`). With hooks, `ClaudeAgent.QueryAsync` keeps stdin open until the first result.

## 範圍 / Scope

支援 / Supported:

- 多輪對話（stream-json 輸入） / Multi-turn conversations (stream-json input)
- 接續 session（`--resume`） / Resuming sessions (`--resume`)
- 工具權限回呼，含「一律允許」（回傳 CLI 給的建議權限） / Tool permission callback, including "always allow" (returning the CLI's suggested permissions)
- 部分訊息串流（`--include-partial-messages`） / Partial message streaming (`--include-partial-messages`)
- 停用特定工具（`--disallowedTools`） / Disallowing specific tools (`--disallowedTools`)
- 中斷這一輪 / Interrupting the current turn
- 系統提示詞：Claude Code 預設的、在預設後面附加、自訂全文、從檔案讀 / System prompts: Claude Code's default, appended to the default, fully custom, or read from a file
- 指定模型與思考深度（`--model`、`--effort`），對話中途換模型（`set_model`） / Choosing the model and effort (`--model`, `--effort`), and switching models mid-conversation (`set_model`)
- 對話中途套用設定，例如換思考深度（`apply_flag_settings`；Python SDK 沒有，照 TypeScript SDK 的協定） / Applying settings mid-conversation, such as the effort level (`apply_flag_settings`; missing from the Python SDK, follows the TypeScript SDK's protocol)
- 對話中途換權限模式（`SetPermissionModeAsync`）；一次性問答 `ClaudeAgent.QueryAsync`（對應 Python 的 `query()`） / Switching the permission mode mid-conversation (`SetPermissionModeAsync`); one-shot `ClaudeAgent.QueryAsync` (Python's `query()`)
- 跟 Python 一樣的其他選項 / Further options matching Python: `Tools`, `AllowedTools`, `MaxTurns`, `MaxBudgetUsd`, `FallbackModel`, `Betas`, `PermissionPromptToolName`, `PermissionMode`, `ContinueConversation`, `SessionId`, `Settings` + `Sandbox`, `AddDirs`, `McpServers`（外部的 stdio/SSE/HTTP，以及 SDK MCP server / external stdio/SSE/HTTP, plus in-process SDK MCP servers）/ `McpConfig`, `StrictMcpConfig`, `ForkSession`, `SettingSources`, `Plugins`, `ExtraArgs`, `Thinking` / `MaxThinkingTokens`, `OutputFormat`, `Agents`
- Hook 回呼（`Hooks`），所有 Python 有的事件輸入與事件專屬結果 / Hook callbacks (`Hooks`) with every hook input and hook-specific output type Python has

不做 / Not planned: session store、transcript mirror / session stores, transcript mirroring.

## 需求 / Requirements

- .NET 10
- 已安裝 Claude Code CLI。Windows 請用原生安裝的 `claude.exe`，不支援 npm 的 `claude.cmd`（批次檔會讓 cmd.exe 重新解析參數，有指令注入風險）。
- Claude Code CLI installed. On Windows, use the native `claude.exe`; the npm `claude.cmd` wrapper is not supported, because cmd.exe re-parses the arguments of batch files, which allows command injection.

## 授權 / License

MIT，見 [LICENSE](https://github.com/pamis-wang/claude-agent-sdk-dotnet/blob/main/LICENSE)；移植來源的授權聲明見 [NOTICE](https://github.com/pamis-wang/claude-agent-sdk-dotnet/blob/main/NOTICE)。

MIT, see [LICENSE](https://github.com/pamis-wang/claude-agent-sdk-dotnet/blob/main/LICENSE). The license notice of the ported source is in [NOTICE](https://github.com/pamis-wang/claude-agent-sdk-dotnet/blob/main/NOTICE).
