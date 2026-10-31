# 第三方鏡像（Third-party mirror）

> ⚠️ **這是第三方程式碼的鏡像，不是我們寫的，也不是 Anthropic 官方產品。**

## 來源

- 上游 repo：https://github.com/pamis-wang/claude-agent-sdk-dotnet
- NuGet 套件：https://www.nuget.org/packages/PamisWang.ClaudeAgentSdk
- 作者：Pamis Wang
- 鏡像版本：**0.1.0-preview.3（預覽版）**，取自上游 commit `d260184`，鏡像日期 2026-10-31

## 這是什麼

非官方的 Claude Agent SDK .NET 移植版，讓 C# 程式可以啟動並操作 Claude Code。

- 主要照 Anthropic 官方 **Python** Claude Agent SDK 移植。
- 部分只有 TypeScript SDK 才有的功能，是作者依照 CLI 協定自行重寫的。作者表示沒有複製 TypeScript SDK 的程式碼；這點**我們沒有驗證**。
- 預覽版：API 可能變動，不建議直接用在正式環境。
- 需要 **.NET 10**（目標框架 `net10.0`）。

## 授權

- MIT 授權，見 [LICENSE](LICENSE)。
- [NOTICE](NOTICE) 保留了 Anthropic 對 Python SDK 的 MIT 授權聲明（本專案是從它移植的）。
- LICENSE、NOTICE、README 和所有原始碼都與上游 `d260184` 完全相同，本鏡像只新增了這個檔案。

## 我們的擴充

之後我們可能會在這個鏡像上加自己的擴充（例如 `ExtraArgs`，讓呼叫端能把額外參數直接傳給 CLI）。這類改動會用獨立的 commit 或 PR，並在此檔記錄，方便和上游區分。

## 從上游同步

```bash
git fetch upstream
git merge upstream/main
```

（`upstream` 指向 pamis-wang/claude-agent-sdk-dotnet。用 merge，不要 rebase 已推送的分支。）

---

## English

**Third-party mirror.** This repository mirrors
[pamis-wang/claude-agent-sdk-dotnet](https://github.com/pamis-wang/claude-agent-sdk-dotnet)
([NuGet: PamisWang.ClaudeAgentSdk](https://www.nuget.org/packages/PamisWang.ClaudeAgentSdk)) by Pamis Wang.
It is an **unofficial** .NET port of Anthropic's Python Claude Agent SDK; some TypeScript-only features were
reimplemented from the CLI protocol (the author states no TypeScript SDK code was copied; not verified by us).
**Preview** release 0.1.0-preview.3, mirrored from upstream commit `d260184` on 2026-10-31. Requires .NET 10.
Licensed under MIT (see `LICENSE`); `NOTICE` retains Anthropic's MIT notice for the Python SDK it was ported from.
All upstream files are unchanged; only this file was added. We may add our own extensions (e.g. `ExtraArgs`) here later.
Sync from upstream with `git fetch upstream && git merge upstream/main`.
