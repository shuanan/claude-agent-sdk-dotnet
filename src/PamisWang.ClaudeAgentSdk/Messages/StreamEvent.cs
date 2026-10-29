using System.Text.Json;

namespace PamisWang.ClaudeAgentSdk.Messages;

/// <summary>
/// 部分訊息（<c>type: stream_event</c>）：模型輸出時逐段送來的 API 串流事件。
/// 要設定 <see cref="ClaudeAgentOptions.IncludePartialMessages"/> 才會收到。
/// </summary>
public sealed record StreamEvent : ClaudeMessage
{
    /// <summary>原樣的串流事件，例如 <c>message_start</c>、<c>content_block_delta</c>。</summary>
    public required JsonElement Event { get; init; }

    /// <summary>subagent 的事件是呼叫它的那個工具的 ID；主對話是 <see langword="null"/>。</summary>
    public string? ParentToolUseId { get; init; }

    /// <summary>事件的型別，例如 <c>content_block_delta</c>。</summary>
    public string? EventType => GetString(Event, "type");

    /// <summary>新輸出的一段文字：只有 <c>content_block_delta</c> 的 <c>text_delta</c> 才有，其他事件是 <see langword="null"/>。</summary>
    public string? TextDelta =>
        EventType == "content_block_delta"
        && Event.TryGetProperty("delta", out var delta)
        && GetString(delta, "type") == "text_delta"
            ? GetString(delta, "text")
            : null;

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
