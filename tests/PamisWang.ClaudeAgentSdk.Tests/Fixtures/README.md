# 協定樣本

Claude Code CLI 的 stream-json 收發紀錄，給單元測試用假的 transport 重播，不用真的啟動 Claude Code。由 [tools/record_fixtures.py](../../../tools/record_fixtures.py) 用官方 Python SDK 實際跑出來，不要手改。

## 格式

每行一個 JSON 物件，`dir` 表示這一行是什麼：

| `dir` | 內容 |
|---|---|
| `meta` | 只在第一行：情境名稱與說明、Python SDK 與 CLI 版本、模型、CLI 參數（`argv`）、錄製日期 |
| `send` | SDK 寫進 CLI stdin 的一則訊息，放在 `data` |
| `recv` | 從 CLI stdout 讀到的一則訊息，放在 `data` |
| `note` | 錄製腳本的註記，例如什麼時候送出 interrupt；重播時略過 |

行的順序就是錄製時看到的收發順序。CLI 寫到 stdout 的每一行都是一則 JSON，`data` 就是那一行解析後的內容。

## 情境

| 檔案 | 情境 |
|---|---|
| `basic.jsonl` | 一般問答：一輪，不用工具 |
| `multi_turn.jsonl` | 多輪對話：同一個連線送兩句話 |
| `resume.jsonl` | 接續舊 session（`--resume`）；被接續的 session 由另一個不錄的 client 先開好 |
| `partial_messages.jsonl` | 部分訊息串流（`--include-partial-messages`） |
| `permission_allow.jsonl` | 權限詢問：允許 |
| `permission_deny.jsonl` | 權限詢問：拒絕 |
| `permission_always_allow.jsonl` | 權限詢問：把 CLI 給的建議權限帶回去（一律允許），下一輪同一工具不再問 |
| `interrupt.jsonl` | 模型輸出到一半時中斷，再接著說一句 |
| `interrupt_during_permission.jsonl` | 等權限回覆時中斷 |

## 錄製條件

- 模型用 haiku，對話都很短。
- 跟 chibi-office 的 `chat.ts` 一樣：一律掛權限回呼（`--permission-prompt-tool stdio`）、停用 `AskUserQuestion`。
- 不載入使用者的設定與 claude.ai 連接器（`--setting-sources=`、`--strict-mcp-config`），所以 `init` 訊息裡的工具、外掛、指令都只有內建的。
- 去識別化：

  | 原本 | 換成 |
  |---|---|
  | 工作目錄 | `<CWD>` |
  | 家目錄 | `<HOME>` |
  | 這兩者在 `~/.claude/projects` 底下的資料夾名稱 | `<CWD-PROJECT-DIR>`、`<HOME-PROJECT-DIR>` |
  | email | `user@example.com` |
  | 帳號所屬組織 | `Example Org` |

## 重錄

Claude Code 升級後重錄一次，再用 `git diff` 看協定改了什麼。uuid、session id、耗時和模型的回答每次都不一樣，看 diff 時注意的是訊息型別與欄位有沒有增減。

```bash
D:/SideProject/claude-agent-sdk-python/.venv/Scripts/python -I tools/record_fixtures.py            # 全部
D:/SideProject/claude-agent-sdk-python/.venv/Scripts/python -I tools/record_fixtures.py resume     # 只錄指定的
```
