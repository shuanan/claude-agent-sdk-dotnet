using System.Text.Json;

namespace PamisWang.ClaudeAgentSdk.Messages;

/// <summary>模型的一則回覆（<c>type: assistant</c>）。</summary>
public sealed record AssistantMessage : ClaudeMessage
{
    /// <summary>內容區塊：文字、思考、工具呼叫等。</summary>
    public required IReadOnlyList<ContentBlock> Content { get; init; }

    /// <summary>產生這則回覆的模型。</summary>
    public required string Model { get; init; }

    /// <summary>subagent 的訊息是呼叫它的那個工具的 ID；主對話是 <see langword="null"/>。</summary>
    public string? ParentToolUseId { get; init; }

    /// <summary>API 給這則訊息的 ID。</summary>
    public string? MessageId { get; init; }

    /// <summary>模型停下來的原因，例如 <c>end_turn</c>、<c>tool_use</c>。</summary>
    public string? StopReason { get; init; }

    /// <summary>token 用量，原樣保留。</summary>
    public JsonElement? Usage { get; init; }

    /// <summary>回覆失敗的原因，例如 <c>rate_limit</c>、<c>authentication_failed</c>；正常回覆是 <see langword="null"/>。</summary>
    public string? Error { get; init; }
}
