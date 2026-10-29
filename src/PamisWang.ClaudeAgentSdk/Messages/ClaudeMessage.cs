using System.Text.Json;

namespace PamisWang.ClaudeAgentSdk.Messages;

/// <summary>
/// CLI 送來的一則訊息（control 協定以外的）。對應 Python SDK 的 <c>Message</c>、TS SDK 的 <c>SDKMessage</c>。
/// 不認得的訊息型別是 <see cref="UnknownMessage"/>，不會被丟掉。
/// </summary>
public abstract record ClaudeMessage
{
    private protected ClaudeMessage()
    {
    }

    /// <summary>CLI 送來的原始 JSON；沒有對應屬性的欄位從這裡讀。自己建立的訊息是 <see langword="default"/>。</summary>
    public JsonElement Raw { get; init; }

    /// <summary>訊息所屬的 session。</summary>
    public string? SessionId { get; init; }

    /// <summary>訊息的 ID。</summary>
    public string? Uuid { get; init; }
}
