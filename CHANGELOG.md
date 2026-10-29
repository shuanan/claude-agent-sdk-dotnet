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
