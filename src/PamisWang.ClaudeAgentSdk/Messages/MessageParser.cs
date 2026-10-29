using System.Text.Json;

namespace PamisWang.ClaudeAgentSdk.Messages;

/// <summary>
/// 把 CLI 送來的 JSON 轉成 <see cref="ClaudeMessage"/>。對應 Python SDK 的 <c>message_parser.py</c>：
/// 必要欄位跟 Python 一樣，缺了就丟 <see cref="MessageParseException"/>；
/// 不一樣的是不認得的訊息型別與內容區塊都原樣保留（Python 是丟掉）。
/// </summary>
internal static class MessageParser
{
    /// <exception cref="MessageParseException">不是 JSON 物件、沒有 type，或認得的型別缺少必要欄位。</exception>
    public static ClaudeMessage Parse(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object)
        {
            throw new MessageParseException($"訊息不是 JSON 物件（是 {data.ValueKind}）。", data);
        }

        var type = OptionalString(data, "type") ?? throw new MessageParseException("訊息沒有 type 欄位。", data);
        ClaudeMessage message;
        try
        {
            message = type switch
            {
                "assistant" => ParseAssistant(data),
                "user" => ParseUser(data),
                "system" => new SystemMessage { Subtype = RequiredString(data, "subtype") },
                "result" => ParseResult(data),
                "stream_event" => ParseStreamEvent(data),
                "rate_limit_event" => new RateLimitEvent { Info = ParseRateLimitInfo(Required(data, "rate_limit_info")) },
                "conversation_reset" => new ConversationResetMessage { NewConversationId = RequiredString(data, "new_conversation_id") },
                _ => new UnknownMessage { Type = type },
            };
        }
        catch (Exception e) when (e is FormatException or InvalidOperationException)
        {
            throw new MessageParseException($"{type} 訊息格式不對：{e.Message}", data, e);
        }

        return message with { Raw = data, SessionId = OptionalString(data, "session_id"), Uuid = OptionalString(data, "uuid") };
    }

    private static AssistantMessage ParseAssistant(JsonElement data)
    {
        var message = Required(data, "message");
        var content = Required(message, "content");
        if (content.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException($"message.content 應該是陣列，卻是 {content.ValueKind}");
        }

        return new AssistantMessage
        {
            Content = ParseBlocks(content),
            Model = RequiredString(message, "model"),
            ParentToolUseId = OptionalString(data, "parent_tool_use_id"),
            MessageId = OptionalString(message, "id"),
            StopReason = OptionalString(message, "stop_reason"),
            Usage = Optional(message, "usage"),
            Error = OptionalString(data, "error"),
        };
    }

    private static UserMessage ParseUser(JsonElement data)
    {
        var content = Required(Required(data, "message"), "content");
        return new UserMessage
        {
            Content = content.ValueKind switch
            {
                JsonValueKind.Array => ParseBlocks(content),
                JsonValueKind.String => [new TextBlock(content.GetString()!) { Raw = content }],
                _ => throw new FormatException($"message.content 應該是字串或陣列，卻是 {content.ValueKind}"),
            },
            ParentToolUseId = OptionalString(data, "parent_tool_use_id"),
            ToolUseResult = Optional(data, "tool_use_result"),
        };
    }

    private static ResultMessage ParseResult(JsonElement data) => new()
    {
        Subtype = RequiredString(data, "subtype"),
        IsError = Required(data, "is_error").GetBoolean(),
        DurationMs = RequiredInt64(data, "duration_ms"),
        DurationApiMs = RequiredInt64(data, "duration_api_ms"),
        NumTurns = (int)RequiredInt64(data, "num_turns"),
        Result = OptionalString(data, "result"),
        Errors = Optional(data, "errors") is { ValueKind: JsonValueKind.Array } errors
            ? [.. errors.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)]
            : [],
        TotalCostUsd = Optional(data, "total_cost_usd")?.GetDouble(),
        StopReason = OptionalString(data, "stop_reason"),
        TerminalReason = OptionalString(data, "terminal_reason"),
        PermissionDenials = Optional(data, "permission_denials") is { ValueKind: JsonValueKind.Array } denials
            ? [.. denials.EnumerateArray().Select(denial => new PermissionDenial(
                RequiredString(denial, "tool_name"),
                RequiredString(denial, "tool_use_id"),
                Required(denial, "tool_input")))]
            : [],
        ApiErrorStatus = Optional(data, "api_error_status")?.GetInt32(),
        Usage = Optional(data, "usage"),
        ModelUsage = Optional(data, "modelUsage"),
        StructuredOutput = Optional(data, "structured_output"),
    };

    private static StreamEvent ParseStreamEvent(JsonElement data)
    {
        // 跟 Python 一樣把 uuid 與 session_id 當成必要欄位
        RequiredString(data, "uuid");
        RequiredString(data, "session_id");
        return new StreamEvent
        {
            Event = Required(data, "event"),
            ParentToolUseId = OptionalString(data, "parent_tool_use_id"),
        };
    }

    private static RateLimitInfo ParseRateLimitInfo(JsonElement info) => new()
    {
        Status = RequiredString(info, "status"),
        ResetsAt = OptionalInt64(info, "resetsAt"),
        RateLimitType = OptionalString(info, "rateLimitType"),
        Utilization = Optional(info, "utilization")?.GetDouble(),
        OverageStatus = OptionalString(info, "overageStatus"),
        OverageResetsAt = OptionalInt64(info, "overageResetsAt"),
        OverageDisabledReason = OptionalString(info, "overageDisabledReason"),
        Raw = info,
    };

    private static List<ContentBlock> ParseBlocks(JsonElement blocks) => [.. blocks.EnumerateArray().Select(ParseBlock)];

    private static ContentBlock ParseBlock(JsonElement block)
    {
        if (block.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException($"內容區塊應該是物件，卻是 {block.ValueKind}");
        }

        var type = RequiredString(block, "type");
        ContentBlock parsed = type switch
        {
            "text" => new TextBlock(RequiredString(block, "text")),
            "thinking" => new ThinkingBlock(RequiredString(block, "thinking"), RequiredString(block, "signature")),
            "tool_use" => new ToolUseBlock(RequiredString(block, "id"), RequiredString(block, "name"), Required(block, "input")),
            "tool_result" => new ToolResultBlock(
                RequiredString(block, "tool_use_id"),
                Optional(block, "content"),
                Optional(block, "is_error")?.GetBoolean()),
            "server_tool_use" => new ServerToolUseBlock(RequiredString(block, "id"), RequiredString(block, "name"), Required(block, "input")),
            "advisor_tool_result" => new ServerToolResultBlock(RequiredString(block, "tool_use_id"), Required(block, "content")),
            _ => new UnknownContentBlock(type),
        };
        return parsed with { Raw = block };
    }

    private static JsonElement Required(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value
            : throw new FormatException($"缺少欄位 {name}");

    private static string RequiredString(JsonElement element, string name) =>
        Required(element, name) is { ValueKind: JsonValueKind.String } value
            ? value.GetString()!
            : throw new FormatException($"欄位 {name} 應該是字串");

    /// <summary>整數欄位；也接受帶小數的數字，取整數部分。</summary>
    private static long RequiredInt64(JsonElement element, string name)
    {
        var value = Required(element, name);
        return value.TryGetInt64(out var number) ? number : (long)value.GetDouble();
    }

    /// <summary>有這個欄位而且不是 null 才回傳。</summary>
    private static JsonElement? Optional(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value : null;

    private static string? OptionalString(JsonElement element, string name) =>
        Optional(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    private static long? OptionalInt64(JsonElement element, string name) =>
        Optional(element, name) is { } value ? (value.TryGetInt64(out var number) ? number : (long)value.GetDouble()) : null;
}
