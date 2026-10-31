# Changelog

## 未發佈 / Unreleased

- SDK MCP server（在這個程序裡跑的 MCP 工具），對應 Python 的 `@tool`、`create_sdk_mcp_server()`：
  `SdkMcpTool.Create`、`McpSchema.Parameters`（名稱對型別的 schema 簡寫）、`McpToolResult`（`Text`／`Error`）與內容型別、
  `McpToolAnnotations`（含 `MaxResultSizeChars` → `_meta`）、`SdkMcpServer.Create` → `McpSdkServerConfig`。
  放進 `McpServers` 時 CLI 只拿到 `{"type":"sdk","name":…}`；control 協定的 `mcp_message` 回答
  `initialize`、`ping`、`tools/list`、`tools/call`、通知與 `notifications/cancelled`，回應包在 `{"mcp_response": …}`。
  回應以 Python SDK 搭配 mcp 2.3.0 實際錄下的為準（`SdkMcpGoldenParityTests` 逐則比對）：capabilities 只有 `tools`、
  沒處理的方法回 -32601 並在 `data` 帶方法名稱、握手前的 `tools/*` 回 -32602。
  跟 Python 不同的地方：參數驗證只實作常用的 JSON Schema 關鍵字（沒有 `$ref`、`format`）；
  不能像 Python 那樣放自己組的 MCP `Server`，也沒有 server 停掉後重新握手的流程。
- `ClaudeAgent.QueryAsync`：有 SDK MCP server 時也留著 stdin 到第一個結果（對應 Python 的 `_has_bidirectional_needs`）。
- Hook，對應 Python 的 `hooks`：`ClaudeAgentOptions.Hooks`（事件 → `HookMatcher` 清單）、`HookEvent`（Python `HookEvent` 的 10 個事件）、
  `HookCallback`（`input`、`toolUseId`、`HookContext`、`CancellationToken`）。
  輸入依 `hook_event_name` 轉成 `PreToolUseHookInput`、`PostToolUseHookInput`、`PostToolUseFailureHookInput`、`UserPromptSubmitHookInput`、
  `StopHookInput`、`SubagentStopHookInput`、`PreCompactHookInput`、`NotificationHookInput`、`SubagentStartHookInput`、`PermissionRequestHookInput`，
  不認得的是 `UnknownHookInput`；都帶 `Raw`。結果是 `SyncHookOutput`／`AsyncHookOutput`／`RawHookOutput`，
  `HookSpecificOutput` 有 Python 的全部 21 種（含 SessionStart、PostToolBatch、PermissionDenied、Elicitation、WorktreeCreate 等）。
  initialize 跟 Python 一樣送 `hooks`（`hookCallbackIds` 依登記順序是 `hook_<n>`、有逾時才送 `timeout`、空的事件不送、沒有就是 `null`）；
  `hook_callback` 回覆與錯誤以 Python SDK 實際的輸出為準（`HookGoldenParityTests`）。回呼各自在背景跑，慢的不會擋住其他請求；
  `control_cancel_request` 會取消它、不送回應。有 hook 時 `ClaudeAgent.QueryAsync` 留著 stdin 到第一個結果。
  跟 Python 不同的地方：`HookContext.Signal` 是真的會取消的 `CancellationToken`（Python 一律是 `None`）；回呼回傳 `null` 時回錯誤。
- `tools/local-ci.sh`：在本機跑跟 CI 一樣的建置、測試與打包。

## 0.1.0-preview.3（2026-10-31）

- 函式庫沒有變更。git 歷史改寫過（協定樣本去識別化），重新發佈讓套件記錄的 commit 對應到現在的歷史；
  preview.1、preview.2 記錄的是改寫前的 commit。
- 協定樣本的去識別化加上帳號的訂閱方案。

## 0.1.0-preview.2（2026-10-31）

- `ClaudeAgentOptions.SystemPrompt`：`SystemPromptPreset`（Claude Code 預設的系統提示詞，可以在後面附加一段）、
  `SystemPromptCustom`（自訂全文）、`SystemPromptFile`（從檔案讀）。沒指定時照舊傳空的系統提示詞，跟 Python SDK 一樣。
- `ClaudeAgentOptions.Effort`：思考深度（`--effort`）。
- `ClaudeAgentClient.SetModelAsync`：對話中途換模型（`set_model`），對應 Python 的 `set_model()`；
  另外回傳 CLI 換過去的完整模型 ID（Python 與 TypeScript SDK 不回傳）。
- `ClaudeAgentClient.ApplyFlagSettingsAsync`：對話中途套用設定（`apply_flag_settings`），例如換思考深度。
  Python SDK 沒有這個功能，照 TypeScript SDK v0.3.316 的 `applyFlagSettings()` 送出的協定格式寫。
- 協定樣本加上 `set_model`、`apply_flag_settings` 兩個情境（CLI 2.1.309）。

## 0.1.0-preview.1（2026-10-30）

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
