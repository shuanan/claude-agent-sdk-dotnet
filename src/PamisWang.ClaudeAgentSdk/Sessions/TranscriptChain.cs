using System.Text.Json.Nodes;

namespace PamisWang.ClaudeAgentSdk.Sessions;

/// <summary>
/// 從完整的 JSONL 紀錄重建對話：解析紀錄、依 <c>parentUuid</c> 找出主線、補回平行工具呼叫被走訪漏掉的呼叫與結果、篩出看得見的訊息。
/// 每筆紀錄是 <see cref="JsonObject"/>（Python 的 dict）。對應 Python <c>_internal/sessions.py</c> 的
/// <c>_parse_transcript_entries</c>、<c>_build_conversation_chain</c>、<c>_recover_orphaned_parallel_tool_results</c> 等。
/// </summary>
internal static class TranscriptChain
{
    /// <summary>帶 uuid 與 parentUuid 鏈結的紀錄類型。對應 <c>_TRANSCRIPT_ENTRY_TYPES</c>。</summary>
    private static readonly HashSet<string> TranscriptEntryTypes = new(StringComparer.Ordinal) { "user", "assistant", "progress", "system", "attachment" };

    private static string Uuid(JsonObject entry) => PyJson.Str(entry, "uuid")!;

    private static JsonNode? Get(JsonObject entry, string name) => entry.TryGetPropertyValue(name, out var value) ? value : null;

    private static bool IsUserOrAssistant(JsonObject entry) => PyJson.Str(entry, "type") is "user" or "assistant";

    /// <summary>
    /// <c>parentUuid</c> 為真值時找父紀錄。Python 用任何可雜湊的值查 dict；不是字串的值不可能等於 uuid，所以直接當作找不到。
    /// </summary>
    private static JsonObject? Parent(JsonObject entry, IReadOnlyDictionary<string, JsonObject> byUuid)
    {
        var parent = Get(entry, "parentUuid");
        return PyJson.Truthy(parent) && PyJson.Str(parent) is { } id && byUuid.TryGetValue(id, out var found) ? found : null;
    }

    /// <summary>
    /// 把 JSONL 解析成紀錄：只留有字串 uuid 的 user/assistant/progress/system/attachment，壞掉的行跳過。
    /// 對應 <c>_parse_transcript_entries</c>。
    /// </summary>
    public static List<JsonObject> ParseTranscriptEntries(string content)
    {
        var entries = new List<JsonObject>();
        var start = 0;
        var length = content.Length;
        while (start < length)
        {
            var end = content.IndexOf('\n', start);
            if (end == -1)
            {
                end = length;
            }

            var line = PyText.Strip(content[start..end]);
            start = end + 1;
            if (line.Length == 0)
            {
                continue;
            }

            if (PyJson.LoadsObject(line) is { } entry && IsTranscriptEntry(entry))
            {
                entries.Add(entry);
            }
        }

        return entries;
    }

    /// <summary>是帶字串 uuid 的對話紀錄類型。</summary>
    public static bool IsTranscriptEntry(JsonObject entry) =>
        PyJson.Str(entry, "type") is { } type && TranscriptEntryTypes.Contains(type) && PyJson.Str(entry, "uuid") is not null;

    /// <summary>
    /// 從最後的葉節點沿 <c>parentUuid</c> 走回根，回傳由根到葉的順序；平行工具呼叫被漏掉的部分由
    /// <see cref="RecoverOrphanedParallelToolResults"/> 補回。不跟 <c>logicalParentUuid</c>（compact 之後的摘要已經取代前面的訊息）。
    /// 對應 <c>_build_conversation_chain</c>。
    /// </summary>
    public static List<JsonObject> BuildConversationChain(IReadOnlyList<JsonObject> entries)
    {
        if (entries.Count == 0)
        {
            return [];
        }

        // Python dict：位置是第一次出現的，值是最後一個
        var byUuid = new OrderedDictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            byUuid[Uuid(entry)] = entry;
        }

        var entryIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < entries.Count; i++)
        {
            entryIndex[Uuid(entries[i])] = i;
        }

        var parentUuids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var parent = Get(entry, "parentUuid");
            if (PyJson.Truthy(parent) && PyJson.Str(parent) is { } id)
            {
                parentUuids.Add(id);
            }
        }

        // 從每個終端往回找最近的 user/assistant 當葉節點
        var leaves = new List<JsonObject>();
        foreach (var terminal in entries.Where(e => !parentUuids.Contains(Uuid(e))))
        {
            var walkSeen = new HashSet<string>(StringComparer.Ordinal);
            var current = terminal;
            while (current is not null)
            {
                if (!walkSeen.Add(Uuid(current)))
                {
                    break;
                }

                if (IsUserOrAssistant(current))
                {
                    leaves.Add(current);
                    break;
                }

                current = Parent(current, byUuid);
            }
        }

        if (leaves.Count == 0)
        {
            return [];
        }

        // 優先選主線（不是 sidechain、team、meta）上檔案位置最後的葉節點
        var mainLeaves = leaves
            .Where(leaf => !PyJson.Truthy(Get(leaf, "isSidechain")) && !PyJson.Truthy(Get(leaf, "teamName")) && !PyJson.Truthy(Get(leaf, "isMeta")))
            .ToList();

        JsonObject PickBest(List<JsonObject> candidates)
        {
            var best = candidates[0];
            var bestIndex = entryIndex.GetValueOrDefault(Uuid(best), -1);
            foreach (var candidate in candidates.Skip(1))
            {
                var index = entryIndex.GetValueOrDefault(Uuid(candidate), -1);
                if (index > bestIndex)
                {
                    best = candidate;
                    bestIndex = index;
                }
            }

            return best;
        }

        var leaf = mainLeaves.Count > 0 ? PickBest(mainLeaves) : PickBest(leaves);

        var chain = new List<JsonObject>();
        var chainSeen = new HashSet<string>(StringComparer.Ordinal);
        JsonObject? cursor = leaf;
        while (cursor is not null)
        {
            if (!chainSeen.Add(Uuid(cursor)))
            {
                break;
            }

            chain.Add(cursor);
            cursor = Parent(cursor, byUuid);
        }

        chain.Reverse();
        return RecoverOrphanedParallelToolResults(byUuid, chain, chainSeen);
    }

    /// <summary>助理紀錄的 API <c>message.id</c>（非空字串）。對應 <c>_assistant_message_id</c>。</summary>
    private static string? AssistantMessageId(JsonObject entry)
    {
        if (PyJson.Str(entry, "type") != "assistant" || Get(entry, "message") is not JsonObject message)
        {
            return null;
        }

        return PyJson.Str(message, "id") is { Length: > 0 } id ? id : null;
    }

    /// <summary>每個 <paramref name="blockType"/> 內容區塊的 <paramref name="idField"/>（字串）。對應 <c>_content_block_ids</c>。</summary>
    private static List<string> ContentBlockIds(JsonObject entry, string blockType, string idField)
    {
        var ids = new List<string>();
        if (Get(entry, "message") is not JsonObject message || Get(message, "content") is not JsonArray content)
        {
            return ids;
        }

        foreach (var block in content)
        {
            if (block is JsonObject obj && PyJson.Str(obj, "type") == blockType && PyJson.Str(obj, idField) is { } id)
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    /// <summary>有父紀錄、內容帶 tool_result 的使用者紀錄。對應 <c>_is_tool_result_entry</c>。</summary>
    private static bool IsToolResultEntry(JsonObject entry)
    {
        if (PyJson.Str(entry, "type") != "user" || PyJson.Str(entry, "parentUuid") is not { Length: > 0 })
        {
            return false;
        }

        return Get(entry, "message") is JsonObject message && Get(message, "content") is JsonArray content
            && content.Any(block => block is JsonObject obj && PyJson.Str(obj, "type") == "tool_result");
    }

    /// <summary>兩筆紀錄是同一個（sub）agent 寫的。對應 <c>_same_writer</c>。</summary>
    private static bool SameWriter(JsonObject a, JsonObject b) =>
        PyJson.Truthy(Get(a, "isSidechain")) == PyJson.Truthy(Get(b, "isSidechain")) && PyJson.PyEquals(Get(a, "agentId"), Get(b, "agentId"));

    private static string TimestampSortKey(JsonObject entry) => PyJson.Str(entry, "timestamp") ?? "";

    /// <summary>
    /// 補回平行工具呼叫被 <c>parentUuid</c> 走訪漏掉的助理紀錄與工具結果，插在該回應在主線上的最後一筆之後。
    /// <paramref name="seen"/> 是主線上的 uuid，補回的也會加進去。對應 <c>_recover_orphaned_parallel_tool_results</c>。
    /// </summary>
    private static List<JsonObject> RecoverOrphanedParallelToolResults(
        OrderedDictionary<string, JsonObject> byUuid, List<JsonObject> chain, HashSet<string> seen)
    {
        var chainAssistants = chain.Where(e => PyJson.Str(e, "type") == "assistant").ToList();
        if (chainAssistants.Count == 0)
        {
            return chain;
        }

        // 每個回應在主線上的最後一筆
        var anchorByMessageId = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var entry in chainAssistants)
        {
            if (AssistantMessageId(entry) is { } messageId)
            {
                anchorByMessageId[messageId] = entry;
            }
        }

        if (anchorByMessageId.Count == 0)
        {
            return chain;
        }

        // 依回應分組，記下每個 tool_use 是哪筆紀錄發出的；同一個 id 出現在不同回應時不明確（null），不用來放結果
        var siblingsByMessageId = new Dictionary<string, List<JsonObject>>(StringComparer.Ordinal);
        var callOwner = new Dictionary<string, JsonObject?>(StringComparer.Ordinal);
        var toolResultEntries = new List<JsonObject>();
        foreach (var entry in byUuid.Values)
        {
            if (AssistantMessageId(entry) is { } messageId)
            {
                if (!siblingsByMessageId.TryGetValue(messageId, out var siblings))
                {
                    siblingsByMessageId[messageId] = siblings = [];
                }

                siblings.Add(entry);
                foreach (var callId in ContentBlockIds(entry, "tool_use", "id"))
                {
                    if (!callOwner.TryGetValue(callId, out var owner))
                    {
                        callOwner[callId] = entry;
                        continue;
                    }

                    var sameResponse = owner is not null && AssistantMessageId(owner) == messageId;
                    callOwner[callId] = sameResponse ? entry : null;
                }
            }
            else if (IsToolResultEntry(entry))
            {
                toolResultEntries.Add(entry);
            }
        }

        // 每個結果掛在可能的助理紀錄下：它的父紀錄、sourceToolAssistantUUID、回答的呼叫的發出者（後兩者要同一個 agent 寫的）
        var resultsByAssistant = new Dictionary<string, List<JsonObject>>(StringComparer.Ordinal);
        void AddResult(string assistantUuid, JsonObject result)
        {
            if (!resultsByAssistant.TryGetValue(assistantUuid, out var list))
            {
                resultsByAssistant[assistantUuid] = list = [];
            }

            list.Add(result);
        }

        foreach (var result in toolResultEntries)
        {
            AddResult(PyJson.Str(result, "parentUuid")!, result);
            if (PyJson.Str(result, "sourceToolAssistantUUID") is { } source
                && byUuid.TryGetValue(source, out var sourceEntry) && SameWriter(result, sourceEntry))
            {
                AddResult(source, result);
            }

            foreach (var callId in ContentBlockIds(result, "tool_result", "tool_use_id"))
            {
                if (callOwner.GetValueOrDefault(callId) is { } owner && SameWriter(result, owner))
                {
                    AddResult(Uuid(owner), result);
                }
            }
        }

        // 只有放 stray 時才需要（很少見），第一次用到才建
        HashSet<string>? answeredOnChain = null;
        Dictionary<string, int>? filePosition = null;

        var processed = new HashSet<string>(StringComparer.Ordinal);
        var inserts = new Dictionary<string, List<JsonObject>>(StringComparer.Ordinal);
        foreach (var assistant in chainAssistants)
        {
            var messageId = AssistantMessageId(assistant);
            if (messageId is null || !processed.Add(messageId))
            {
                continue;
            }

            var group = siblingsByMessageId.GetValueOrDefault(messageId) ?? [assistant];
            var memberUuids = group.Select(Uuid).ToHashSet(StringComparer.Ordinal);
            var orphanedSiblings = group.Where(m => !seen.Contains(Uuid(m))).ToList();
            var orphanedResults = new List<JsonObject>();
            // 掛在這個回應下、父紀錄卻不是它的紀錄：只有回答了還沒回答的呼叫才補回
            var strays = new List<JsonObject>();
            var collected = new HashSet<string>(StringComparer.Ordinal);
            foreach (var member in group)
            {
                foreach (var result in resultsByAssistant.GetValueOrDefault(Uuid(member)) ?? [])
                {
                    var resultUuid = Uuid(result);
                    if (seen.Contains(resultUuid) || !collected.Add(resultUuid))
                    {
                        continue;
                    }

                    if (memberUuids.Contains(PyJson.Str(result, "parentUuid")!))
                    {
                        orphanedResults.Add(result);
                    }
                    else
                    {
                        strays.Add(result);
                    }
                }
            }

            if (strays.Count > 0)
            {
                if (answeredOnChain is null || filePosition is null)
                {
                    answeredOnChain = chain.SelectMany(e => ContentBlockIds(e, "tool_result", "tool_use_id")).ToHashSet(StringComparer.Ordinal);
                    filePosition = new Dictionary<string, int>(StringComparer.Ordinal);
                    var position = 0;
                    foreach (var uid in byUuid.Keys)
                    {
                        filePosition[uid] = position++;
                    }
                }

                var answered = new HashSet<string>(answeredOnChain, StringComparer.Ordinal);
                foreach (var result in orphanedResults)
                {
                    answered.UnionWith(ContentBlockIds(result, "tool_result", "tool_use_id"));
                }

                var positions = filePosition;
                foreach (var result in strays.OrderBy(e => positions[Uuid(e)]).ToList())
                {
                    var callIds = ContentBlockIds(result, "tool_result", "tool_use_id");
                    var answersOpenCall = callIds.Any(callId =>
                        !answered.Contains(callId) && callOwner.GetValueOrDefault(callId) is { } owner && memberUuids.Contains(Uuid(owner)));
                    if (!answersOpenCall)
                    {
                        continue;
                    }

                    answered.UnionWith(callIds);
                    orphanedResults.Add(result);
                }
            }

            if (orphanedSiblings.Count == 0 && orphanedResults.Count == 0)
            {
                continue;
            }

            // 依時間排序保留內容區塊與完成的順序；排序是穩定的，同時間時照檔案順序與呼叫順序
            var recovered = orphanedSiblings.OrderBy(TimestampSortKey, PyText.CodePointComparer)
                .Concat(orphanedResults.OrderBy(TimestampSortKey, PyText.CodePointComparer))
                .ToList();
            foreach (var entry in recovered)
            {
                seen.Add(Uuid(entry));
            }

            inserts[Uuid(anchorByMessageId[messageId])] = recovered;
        }

        if (inserts.Count == 0)
        {
            return chain;
        }

        var resultChain = new List<JsonObject>();
        foreach (var entry in chain)
        {
            resultChain.Add(entry);
            if (inserts.TryGetValue(Uuid(entry), out var recovered))
            {
                resultChain.AddRange(recovered);
            }
        }

        return resultChain;
    }

    /// <summary>要回傳的訊息：user/assistant，不是 isMeta、sidechain 或 team 的。compact 摘要要留著。對應 <c>_is_visible_message</c>。</summary>
    public static bool IsVisibleMessage(JsonObject entry) =>
        IsUserOrAssistant(entry)
        && !PyJson.Truthy(Get(entry, "isMeta"))
        && !PyJson.Truthy(Get(entry, "isSidechain"))
        && !PyJson.Truthy(Get(entry, "teamName"));

    /// <summary>紀錄轉成 <see cref="SessionMessage"/>。對應 <c>_to_session_message</c>。</summary>
    public static SessionMessage ToSessionMessage(JsonObject entry, string? parentToolUseId = null, string? parentAgentId = null)
    {
        var message = Get(entry, "message");
        return new SessionMessage
        {
            Type = PyJson.Str(entry, "type") == "user" ? "user" : "assistant",
            Uuid = PyJson.Str(entry, "uuid") ?? "",
            SessionId = PyJson.Str(entry, "sessionId") ?? "",
            Message = message is null ? null : PyJson.ToElement(message),
            ParentToolUseId = parentToolUseId,
            ParentAgentId = parentAgentId,
        };
    }

    /// <summary>主線轉成訊息並分頁。對應 <c>_entries_to_session_messages</c>。</summary>
    public static List<SessionMessage> EntriesToSessionMessages(IReadOnlyList<JsonObject> entries, int? limit, int offset)
    {
        var messages = BuildConversationChain(entries).Where(IsVisibleMessage).Select(e => ToSessionMessage(e)).ToList();
        return Page(messages, limit, offset);
    }

    /// <summary>Python 的 <c>messages[offset:offset + limit]</c>（limit &gt; 0）或 <c>messages[offset:]</c>（offset &gt; 0）。</summary>
    public static List<SessionMessage> Page(List<SessionMessage> messages, int? limit, int offset)
    {
        if (limit is > 0)
        {
            return PyText.Slice(messages, offset, (int)Math.Clamp((long)offset + limit.Value, int.MinValue, int.MaxValue));
        }

        return offset > 0 ? PyText.Slice(messages, offset, null) : messages;
    }

    /// <summary>
    /// subagent 的紀錄是線性的：最後一筆 user/assistant 是葉節點，沿 <c>parentUuid</c> 走回根。對應 <c>_build_subagent_chain</c>。
    /// </summary>
    public static List<JsonObject> BuildSubagentChain(IReadOnlyList<JsonObject> entries)
    {
        if (entries.Count == 0)
        {
            return [];
        }

        var byUuid = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            byUuid[Uuid(entry)] = entry;
        }

        JsonObject? leaf = null;
        for (var i = entries.Count - 1; i >= 0; i--)
        {
            if (IsUserOrAssistant(entries[i]))
            {
                leaf = entries[i];
                break;
            }
        }

        if (leaf is null)
        {
            return [];
        }

        var chain = new List<JsonObject>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var current = leaf;
        while (current is not null)
        {
            if (!seen.Add(Uuid(current)))
            {
                break;
            }

            chain.Add(current);
            current = Parent(current, byUuid);
        }

        chain.Reverse();
        return chain;
    }

    /// <summary>subagent 的主線轉成訊息並分頁；每則訊息帶同樣的上層 ID。對應 <c>_entries_to_subagent_messages</c>。</summary>
    public static List<SessionMessage> EntriesToSubagentMessages(
        IReadOnlyList<JsonObject> entries, int? limit, int offset, string? parentToolUseId, string? parentAgentId)
    {
        var messages = BuildSubagentChain(entries)
            .Where(IsUserOrAssistant)
            .Select(e => ToSessionMessage(e, parentToolUseId, parentAgentId))
            .ToList();
        return Page(messages, limit, offset);
    }
}
