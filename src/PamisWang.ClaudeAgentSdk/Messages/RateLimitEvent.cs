namespace PamisWang.ClaudeAgentSdk.Messages;

/// <summary>用量限制的狀態（<c>type: rate_limit_event</c>）。</summary>
public sealed record RateLimitEvent : ClaudeMessage
{
    /// <summary>限制的內容。</summary>
    public required RateLimitInfo Info { get; init; }
}
