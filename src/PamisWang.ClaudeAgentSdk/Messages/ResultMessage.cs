using System.Text.Json;

namespace PamisWang.ClaudeAgentSdk.Messages;

/// <summary>一輪結束（<c>type: result</c>）：成功、出錯或被中斷，附上耗時、費用與用量。</summary>
public sealed record ResultMessage : ClaudeMessage
{
    /// <summary><c>success</c>，或 <c>error_during_execution</c>、<c>error_max_turns</c> 這類錯誤。</summary>
    public required string Subtype { get; init; }

    /// <summary>這一輪是否以錯誤結束，例如被中斷。</summary>
    public required bool IsError { get; init; }

    /// <summary>這一輪花的時間（毫秒）。</summary>
    public required long DurationMs { get; init; }

    /// <summary>其中等 API 回應的時間（毫秒）。</summary>
    public required long DurationApiMs { get; init; }

    /// <summary>這一輪來回呼叫模型的次數。</summary>
    public required int NumTurns { get; init; }

    /// <summary>成功時模型最後的回覆文字；出錯時是 <see langword="null"/>，原因見 <see cref="Errors"/>。</summary>
    public string? Result { get; init; }

    /// <summary>出錯的說明。</summary>
    public IReadOnlyList<string> Errors { get; init; } = [];

    /// <summary>估計的費用（美元）。</summary>
    public double? TotalCostUsd { get; init; }

    /// <summary>模型停下來的原因。</summary>
    public string? StopReason { get; init; }

    /// <summary>這一輪怎麼結束的，例如 <c>completed</c>、<c>aborted_streaming</c>、<c>aborted_tools</c>。</summary>
    public string? TerminalReason { get; init; }

    /// <summary>被拒絕的工具呼叫。</summary>
    public IReadOnlyList<PermissionDenial> PermissionDenials { get; init; } = [];

    /// <summary>API 錯誤的 HTTP 狀態碼。</summary>
    public int? ApiErrorStatus { get; init; }

    /// <summary>token 用量，原樣保留。</summary>
    public JsonElement? Usage { get; init; }

    /// <summary>各模型的用量與費用，原樣保留。</summary>
    public JsonElement? ModelUsage { get; init; }

    /// <summary>要求結構化輸出時的結果。</summary>
    public JsonElement? StructuredOutput { get; init; }
}
