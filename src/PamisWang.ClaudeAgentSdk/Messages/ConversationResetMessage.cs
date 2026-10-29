namespace PamisWang.ClaudeAgentSdk.Messages;

/// <summary>對話被重設，換成新的對話（<c>type: conversation_reset</c>），例如執行了 <c>/clear</c>。</summary>
public sealed record ConversationResetMessage : ClaudeMessage
{
    /// <summary>新對話的 ID。</summary>
    public required string NewConversationId { get; init; }
}
