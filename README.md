# PamisWang.ClaudeAgentSdk

Claude Code 的 .NET 用戶端：啟動本機的 `claude` CLI，用 stream-json 協定對話、批准工具、中斷、接續 session。

A .NET client for Claude Code: it launches the local `claude` CLI and talks to it over the stream-json protocol to chat, approve tool use, interrupt, and resume sessions.

> **非官方**。移植自 [claude-agent-sdk-python](https://github.com/anthropics/claude-agent-sdk-python)，與 Anthropic 無關。官方的 Agent SDK 只有 Python 與 TypeScript。
>
> **Unofficial.** Ported from [claude-agent-sdk-python](https://github.com/anthropics/claude-agent-sdk-python) and not affiliated with Anthropic. The official Agent SDK is available only for Python and TypeScript.

## 狀態 / Status

開發中，還不能用。

Work in progress; not usable yet.

| 對照 / Tracks | 版本 / Version |
|---|---|
| Python SDK | v0.2.174 |
| Claude Code CLI | 2.1.312（Python SDK v0.2.174 打包的版本 / bundled with Python SDK v0.2.174） |
| 錄協定樣本用的 CLI / CLI used to record protocol fixtures | 2.1.309 |

## 第一版的範圍 / Scope of the first release

要做 / Planned:

- 多輪對話（stream-json 輸入） / Multi-turn conversations (stream-json input)
- 接續 session（`--resume`） / Resuming sessions (`--resume`)
- 工具權限回呼，含「一律允許」（回傳 CLI 給的建議權限） / Tool permission callback, including "always allow" (returning the CLI's suggested permissions)
- 部分訊息串流（`--include-partial-messages`） / Partial message streaming (`--include-partial-messages`)
- 停用特定工具（`--disallowedTools`） / Disallowing specific tools (`--disallowedTools`)
- 中斷這一輪 / Interrupting the current turn

不做 / Not planned: hook 回呼、SDK 內的 MCP server、session store、transcript mirror / hook callbacks, in-SDK MCP servers, session stores, transcript mirroring.

## 需求 / Requirements

- .NET 10
- 已安裝 Claude Code CLI。Windows 請用原生安裝的 `claude.exe`，不支援 npm 的 `claude.cmd`（批次檔會讓 cmd.exe 重新解析參數，有指令注入風險）。
- Claude Code CLI installed. On Windows, use the native `claude.exe`; the npm `claude.cmd` wrapper is not supported, because cmd.exe re-parses the arguments of batch files, which allows command injection.

## 授權 / License

MIT，見 [LICENSE](LICENSE)；移植來源的授權聲明見 [NOTICE](NOTICE)。

MIT, see [LICENSE](LICENSE). The license notice of the ported source is in [NOTICE](NOTICE).
