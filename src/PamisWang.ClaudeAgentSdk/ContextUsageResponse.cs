using System.Text.Json;
using PamisWang.ClaudeAgentSdk.Protocol;

namespace PamisWang.ClaudeAgentSdk;

/// <summary>
/// <see cref="ClaudeAgentClient.GetContextUsageAsync"/> 的結果：context window 依類別的用量，跟 CLI 的 <c>/context</c> 一樣。
/// 對應 Python 的 <c>ContextUsageResponse</c>。缺的欄位是預設值；
/// Python 標成 <c>dict[str, Any]</c> 的細項（<c>memoryFiles</c>、<c>mcpTools</c>、<c>agents</c>、<c>gridRows</c>、<c>messageBreakdown</c>、<c>apiUsage</c> 等）從 <see cref="Raw"/> 讀。
/// </summary>
public sealed record ContextUsageResponse
{
    /// <summary>各類別的用量（系統提示詞、工具、訊息等）。</summary>
    public required IReadOnlyList<ContextUsageCategory> Categories { get; init; }

    /// <summary>目前 context window 裡的 token 總數（<c>totalTokens</c>）。</summary>
    public long TotalTokens { get; init; }

    /// <summary>實際可用的上限（扣掉自動壓縮的保留量，<c>maxTokens</c>）。</summary>
    public long MaxTokens { get; init; }

    /// <summary>模型原本的 context window 大小（<c>rawMaxTokens</c>）。</summary>
    public long RawMaxTokens { get; init; }

    /// <summary>用掉的百分比（0–100）。</summary>
    public double Percentage { get; init; }

    /// <summary>計算用量所依據的模型。</summary>
    public string? Model { get; init; }

    /// <summary>這個 session 是否開啟自動壓縮（<c>isAutoCompactEnabled</c>）。</summary>
    public bool IsAutoCompactEnabled { get; init; }

    /// <summary>觸發自動壓縮的 token 數（<c>autoCompactThreshold</c>）；CLI 沒送時是 <see langword="null"/>。</summary>
    public long? AutoCompactThreshold { get; init; }

    /// <summary>CLI 回應的原始 JSON。</summary>
    public required JsonElement Raw { get; init; }

    internal static ContextUsageResponse FromJson(JsonElement data) => new()
    {
        Categories = ResponseJson.Objects(data, "categories").Select(ContextUsageCategory.FromJson).ToList(),
        TotalTokens = ResponseJson.Int64(data, "totalTokens") ?? 0,
        MaxTokens = ResponseJson.Int64(data, "maxTokens") ?? 0,
        RawMaxTokens = ResponseJson.Int64(data, "rawMaxTokens") ?? 0,
        Percentage = ResponseJson.Double(data, "percentage") ?? 0,
        Model = ResponseJson.String(data, "model"),
        IsAutoCompactEnabled = ResponseJson.Boolean(data, "isAutoCompactEnabled") ?? false,
        AutoCompactThreshold = ResponseJson.Int64(data, "autoCompactThreshold"),
        Raw = data,
    };
}
