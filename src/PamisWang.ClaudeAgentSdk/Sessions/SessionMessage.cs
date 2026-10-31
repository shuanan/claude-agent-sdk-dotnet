using System.Text.Json;

namespace PamisWang.ClaudeAgentSdk.Sessions;

/// <summary>
/// session 紀錄裡的一則使用者或助理訊息，由 <see cref="ClaudeSessions.GetSessionMessages"/> 與
/// <see cref="ClaudeSessions.GetSubagentMessages"/> 回傳。對應 Python 的 <c>SessionMessage</c>。
/// </summary>
public sealed record SessionMessage
{
    /// <summary>訊息類型：<c>"user"</c> 或 <c>"assistant"</c>。</summary>
    public required string Type { get; init; }

    /// <summary>訊息的 UUID。</summary>
    public required string Uuid { get; init; }

    /// <summary>訊息所屬 session 的 ID（紀錄裡的 <c>sessionId</c>；沒有或不是字串時是空字串）。</summary>
    public required string SessionId { get; init; }

    /// <summary>原始的 Anthropic API 訊息（<c>role</c>、<c>content</c> 等）；紀錄裡沒有時是 <see langword="null"/>。</summary>
    public JsonElement? Message { get; init; }

    /// <summary>
    /// <see cref="ClaudeSessions.GetSubagentMessages"/> 的訊息：上層 session 裡產生這個 subagent 的 Agent <c>tool_use</c> 的 ID
    /// （取自 subagent 的 metadata；沒有時是 <see langword="null"/>）。<see cref="ClaudeSessions.GetSessionMessages"/> 一律是 <see langword="null"/>。
    /// </summary>
    public string? ParentToolUseId { get; init; }

    /// <summary>
    /// subagent 的訊息：產生這個 subagent 的上層 subagent 的 agent ID；由主 session 產生（或沒有 metadata）時是 <see langword="null"/>。
    /// 主 session 的訊息一律是 <see langword="null"/>。
    /// </summary>
    public string? ParentAgentId { get; init; }
}
