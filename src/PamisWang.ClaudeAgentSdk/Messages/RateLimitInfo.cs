using System.Text.Json;

namespace PamisWang.ClaudeAgentSdk.Messages;

/// <summary>用量限制的內容。CLI 還送了其他欄位，從 <see cref="Raw"/> 讀。</summary>
public sealed record RateLimitInfo
{
    /// <summary><c>allowed</c>、<c>allowed_warning</c> 或 <c>rejected</c>。</summary>
    public required string Status { get; init; }

    /// <summary>限制重置的時間（Unix 秒）。</summary>
    public long? ResetsAt { get; init; }

    /// <summary>哪一種限制，例如 <c>five_hour</c>、<c>seven_day</c>。</summary>
    public string? RateLimitType { get; init; }

    /// <summary>已用的比例，0 到 1。</summary>
    public double? Utilization { get; init; }

    /// <summary>超量使用的狀態。</summary>
    public string? OverageStatus { get; init; }

    /// <summary>超量使用重置的時間（Unix 秒）。</summary>
    public long? OverageResetsAt { get; init; }

    /// <summary>不能超量使用的原因。</summary>
    public string? OverageDisabledReason { get; init; }

    /// <summary>原始的 <c>rate_limit_info</c>。</summary>
    public JsonElement Raw { get; init; }
}
