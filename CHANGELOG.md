# Changelog

## 0.1.0（未發佈）

- 專案骨架：函式庫、測試專案、打包設定。
- 對照 Python SDK v0.2.174、Claude Code CLI 2.1.312。
- 協定樣本：`tools/record_fixtures.py` 用 Python SDK 錄下九種情境的 stream-json 收發，CLI 為 2.1.309。
- Transport（內部）：找 CLI（Windows 只執行原生 `claude.exe`，拒絕批次檔）、組參數與環境變數、
  以子程序收發 stream-json（UTF-8、一行一則、單則大小上限）、stderr callback、收尾時逾時強制結束整個程序樹。
- 公開型別：`ClaudeAgentOptions`（`CliPath`、`Cwd`、`Model`、`Resume`、`IncludePartialMessages`、`DisallowedTools`、
  `Env`、`Stderr`、`MaxBufferSize`）與例外階層（`ClaudeAgentException`、`CliConnectionException`、`CliNotFoundException`、
  `CliProcessException`、`CliJsonDecodeException`）。
- 協定層（內部）：initialize 握手；讀取迴圈把 control 協定與一般訊息分開，濾掉 `sdk_host_only` 的 session state；
  送出 control request 並等回應（逾時、錯誤回應、輸出結束時立刻失敗）；回覆權限詢問時另開工作，不擋讀取迴圈，
  CLI 撤回詢問時取消回呼且不回覆；中斷；送出使用者訊息。
- 權限：`ClaudeAgentOptions.CanUseTool`，以及 `Permissions` 命名空間的 `ToolPermissionRequest`、
  `PermissionResult`（`PermissionResultAllow`、`PermissionResultDeny`）、`PermissionUpdate`、`PermissionRuleValue`。
- 訊息：`Messages` 命名空間的訊息型別（`AssistantMessage`、`UserMessage`、`SystemMessage`、`ResultMessage`、
  `StreamEvent`、`RateLimitEvent`、`ConversationResetMessage`、`UnknownMessage`）與內容區塊（`TextBlock`、
  `ThinkingBlock`、`ToolUseBlock`、`ToolResultBlock`、`ServerToolUseBlock`、`ServerToolResultBlock`、`UnknownContentBlock`），
  每個都保留原始 JSON（`Raw`）。parser 的必要欄位跟 Python SDK 一樣，缺了丟 `MessageParseException`；
  不認得的訊息型別與內容區塊原樣保留，不丟掉。
- `ClaudeAgentClient`：`ConnectAsync`（啟動 CLI 並完成初始化握手）、`SendAsync`、`ReceiveMessagesAsync`、
  `ReceiveResponseAsync`、`InterruptAsync`、`ServerInfo`、`DisposeAsync`。一個 client 只連線一次。
