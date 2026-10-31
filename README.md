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

不做 / Not planned: hook 回呼、SDK 內的 MCP server、session store、transcript mirror / hook callbacks, in-SDK MCP servers, session stores, transcript mirroring.

## 需求 / Requirements

- .NET 10
- 已安裝 Claude Code CLI。Windows 請用原生安裝的 `claude.exe`，不支援 npm 的 `claude.cmd`（批次檔會讓 cmd.exe 重新解析參數，有指令注入風險）。
- Claude Code CLI installed. On Windows, use the native `claude.exe`; the npm `claude.cmd` wrapper is not supported, because cmd.exe re-parses the arguments of batch files, which allows command injection.

## 授權 / License

MIT，見 [LICENSE](https://github.com/pamis-wang/claude-agent-sdk-dotnet/blob/main/LICENSE)；移植來源的授權聲明見 [NOTICE](https://github.com/pamis-wang/claude-agent-sdk-dotnet/blob/main/NOTICE)。

MIT, see [LICENSE](https://github.com/pamis-wang/claude-agent-sdk-dotnet/blob/main/LICENSE). The license notice of the ported source is in [NOTICE](https://github.com/pamis-wang/claude-agent-sdk-dotnet/blob/main/NOTICE).
