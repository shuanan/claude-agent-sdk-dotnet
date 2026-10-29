namespace PamisWang.ClaudeAgentSdk.Messages;

/// <summary>
/// 這個版本還不認得的訊息型別。CLI 常加新的型別，原樣保留往上送，不丟例外也不丟掉；內容從 <see cref="ClaudeMessage.Raw"/> 讀。
/// </summary>
public sealed record UnknownMessage : ClaudeMessage
{
    /// <summary>訊息的 <c>type</c>。</summary>
    public required string Type { get; init; }
}
