using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PamisWang.ClaudeAgentSdk.Sessions;

// 逐步算出 session 摘要，對應 Python 的 _internal/session_summary.py。
// 每個欄位不是「只設一次」就是「最後的為準」，store 不必重讀以前附加的紀錄。
public static partial class ClaudeSessions
{
    // 紀錄的鍵 → 摘要的鍵，最後的為準。對應 _LAST_WINS_FIELDS
    private static readonly (string Source, string Target)[] LastWinsFields =
    [
        ("customTitle", "custom_title"),
        ("aiTitle", "ai_title"),
        ("lastPrompt", "last_prompt"),
        ("summary", "summary_hint"),
        ("gitBranch", "git_branch"),
    ];

    /// <summary>
    /// 把一批附加的紀錄併進 <paramref name="key"/> 的摘要。store 在附加紀錄時呼叫，不必重讀整份紀錄；
    /// <paramref name="prev"/> 是同一個 key 之前的摘要（第一次附加時是 <see langword="null"/>），不會被修改。
    /// 有 <see cref="SessionKey.Subpath"/> 的 key（subagent 紀錄）不要呼叫，它們不算進主 session 的摘要。
    /// <see cref="SessionSummaryEntry.Mtime"/> 不動：新的 session 是 0，由 store 存檔後蓋上。
    /// <c>created_at</c> 記第一個解析得到的時間。對應 Python 的 <c>fold_session_summary()</c>。
    /// </summary>
    /// <param name="prev">之前的摘要。</param>
    /// <param name="key">session 的 key。</param>
    /// <param name="entries">這次附加的紀錄（每筆是 JSONL 的一行，JSON 物件）。</param>
    /// <exception cref="ArgumentException">有一筆不是 JSON 物件。</exception>
    public static SessionSummaryEntry FoldSessionSummary(SessionSummaryEntry? prev, SessionKey key, IEnumerable<JsonElement> entries)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(entries);

        var data = new JsonObject();
        if (prev is not null)
        {
            foreach (var (name, value) in prev.Data)
            {
                data[name] = value?.DeepClone();
            }
        }

        foreach (var raw in entries)
        {
            if (PyJson.FromElement(raw) is not JsonObject entry)
            {
                throw new ArgumentException("每筆紀錄都要是 JSON 物件。", nameof(entries));
            }

            FoldEntry(data, entry);
        }

        return prev is not null ? new SessionSummaryEntry(prev.SessionId, prev.Mtime, data) : new SessionSummaryEntry(key.SessionId, 0, data);
    }

    private static void FoldEntry(JsonObject data, JsonObject entry)
    {
        // session_summary._iso_to_epoch_ms：跟 created_at 一樣的算法
        var ms = PyJson.Str(entry, "timestamp") is { } timestamp ? PyDateTime.IsoToEpochMs(timestamp) : null;

        if (!data.ContainsKey("is_sidechain"))
        {
            data["is_sidechain"] = PyJson.IsTrue(entry["isSidechain"]);
        }

        if (!data.ContainsKey("created_at") && ms is { } createdAt)
        {
            data["created_at"] = createdAt;
        }

        if (!data.ContainsKey("cwd") && PyJson.Str(entry, "cwd") is { Length: > 0 } cwd)
        {
            data["cwd"] = cwd;
        }

        FoldFirstPrompt(data, entry);

        foreach (var (source, target) in LastWinsFields)
        {
            if (PyJson.Str(entry, source) is { } value)
            {
                data[target] = value;
            }
        }

        if (PyJson.Str(entry, "type") == "tag")
        {
            if (PyJson.Str(entry, "tag") is { Length: > 0 } tag)
            {
                data["tag"] = tag;
            }
            else
            {
                // 空字串或沒有 tag：清除
                data.Remove("tag");
            }
        }
    }

    /// <summary>
    /// 對單一筆紀錄做 <see cref="SessionLite.ExtractFirstPromptFromHead"/> 的事：找到時設 <c>first_prompt</c> 與 <c>first_prompt_locked</c>，
    /// slash command 記在 <c>command_fallback</c>。對應 <c>_fold_first_prompt</c>。
    /// </summary>
    private static void FoldFirstPrompt(JsonObject data, JsonObject entry)
    {
        if (PyJson.Truthy(data["first_prompt_locked"]) || PyJson.Str(entry, "type") != "user")
        {
            return;
        }

        if (PyJson.IsTrue(entry["isMeta"]) || PyJson.IsTrue(entry["isCompactSummary"]))
        {
            return;
        }

        if (entry["message"] is not JsonObject message)
        {
            return;
        }

        if (message["content"] is JsonArray content && content.Any(b => b is JsonObject block && PyJson.Str(block, "type") == "tool_result"))
        {
            return;
        }

        foreach (var raw in SessionLite.TextBlocks(message))
        {
            var result = PyText.Strip(raw.Replace('\n', ' '));
            if (result.Length == 0)
            {
                continue;
            }

            var command = SessionLite.CommandNameRegex().Match(result);
            if (command.Success)
            {
                if (!PyJson.Truthy(data["command_fallback"]))
                {
                    data["command_fallback"] = command.Groups[1].Value;
                }

                continue;
            }

            if (SessionLite.SkipFirstPromptRegex().IsMatch(result))
            {
                continue;
            }

            data["first_prompt"] = SessionLite.TruncatePrompt(result);
            data["first_prompt_locked"] = true;
            return;
        }
    }

    /// <summary>
    /// 摘要轉成 <see cref="SdkSessionInfo"/>；sidechain 或沒有任何摘要時回傳 <see langword="null"/>，跟 <see cref="SessionLite.ParseSessionInfo"/> 一樣篩選。
    /// 對應 <c>summary_entry_to_sdk_info</c>。
    /// </summary>
    internal static SdkSessionInfo? SummaryEntryToSdkInfo(SessionSummaryEntry entry, string? projectPath)
    {
        var data = entry.Data;
        if (PyJson.Truthy(data["is_sidechain"]))
        {
            return null;
        }

        var firstPrompt = SessionLite.NullIfEmpty(PyJson.Truthy(data["first_prompt_locked"]) ? PyJson.Str(data, "first_prompt") : PyJson.Str(data, "command_fallback"));
        var customTitle = SessionLite.NullIfEmpty(PyJson.Str(data, "custom_title")) ?? SessionLite.NullIfEmpty(PyJson.Str(data, "ai_title"));
        var summary = customTitle
            ?? SessionLite.NullIfEmpty(PyJson.Str(data, "last_prompt"))
            ?? SessionLite.NullIfEmpty(PyJson.Str(data, "summary_hint"))
            ?? firstPrompt;
        if (summary is null)
        {
            return null;
        }

        return new SdkSessionInfo
        {
            SessionId = entry.SessionId,
            Summary = summary,
            LastModified = entry.Mtime,
            // FileSize 是 JSONL 的位元組數，只有本機的檔案有
            FileSize = null,
            CustomTitle = customTitle,
            FirstPrompt = firstPrompt,
            GitBranch = SessionLite.NullIfEmpty(PyJson.Str(data, "git_branch")),
            Cwd = SessionLite.NullIfEmpty(PyJson.Str(data, "cwd")) ?? SessionLite.NullIfEmpty(projectPath),
            Tag = SessionLite.NullIfEmpty(PyJson.Str(data, "tag")),
            CreatedAt = PyJson.Integer(data["created_at"]),
        };
    }

    /// <summary>
    /// 紀錄寫成 JSONL（每筆一行緊湊的 JSON，<c>type</c> 移到最前面，跟磁碟上的位元組形狀一樣）。對應 <c>_entries_to_jsonl</c>。
    /// </summary>
    internal static string EntriesToJsonl(IEnumerable<JsonObject> entries)
    {
        var builder = new StringBuilder();
        var first = true;
        foreach (var entry in entries)
        {
            if (!first)
            {
                builder.Append('\n');
            }

            first = false;
            JsonObject typeFirst;
            if (entry.ContainsKey("type"))
            {
                typeFirst = new JsonObject { ["type"] = entry["type"]?.DeepClone() };
                foreach (var (name, value) in entry)
                {
                    typeFirst[name] = value?.DeepClone();
                }
            }
            else
            {
                typeFirst = entry;
            }

            builder.Append(PyJson.Dumps(typeFirst));
        }

        return builder.Append('\n').ToString();
    }

    /// <summary>記憶體裡的 JSONL 做成跟 <see cref="SessionLite.ReadSessionLite"/> 一樣的開頭與結尾。對應 <c>_jsonl_to_lite</c>。</summary>
    internal static LiteSessionFile JsonlToLite(string jsonl, long mtime)
    {
        var buffer = Encoding.UTF8.GetBytes(jsonl);
        var size = buffer.Length;
        var head = PyText.DecodeReplace(buffer.AsSpan(0, Math.Min(size, SessionLite.ReadBufferSize)));
        var tail = size > SessionLite.ReadBufferSize ? PyText.DecodeReplace(buffer.AsSpan(size - SessionLite.ReadBufferSize)) : head;
        return new LiteSessionFile(mtime, size, head, tail);
    }
}
