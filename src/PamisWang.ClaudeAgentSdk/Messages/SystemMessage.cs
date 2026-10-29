namespace PamisWang.ClaudeAgentSdk.Messages;

/// <summary>
/// CLI 的系統訊息（<c>type: system</c>），用 <see cref="Subtype"/> 區分，例如每一輪開始時的 <c>init</c>、<c>status</c>。
/// 各子型別的欄位從 <see cref="ClaudeMessage.Raw"/> 讀。
/// </summary>
public record SystemMessage : ClaudeMessage
{
    /// <summary>子型別，例如 <c>init</c>、<c>status</c>、<c>session_state_changed</c>。</summary>
    public required string Subtype { get; init; }
}
