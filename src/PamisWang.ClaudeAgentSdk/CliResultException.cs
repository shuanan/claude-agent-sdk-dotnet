using System.Text.Json;

namespace PamisWang.ClaudeAgentSdk;

/// <summary>
/// Claude Code 先送出出錯的結果（<c>is_error: true</c> 的 <see cref="Messages.ResultMessage"/>）再以非零的結束代碼結束。
/// 對應 Python SDK 的 <c>ResultError</c>：取代只有「exit code 1」的 <see cref="CliProcessException"/>，帶著結果的內容，
/// 呼叫端不用比對字串就能知道為什麼失敗，例如 <see cref="Subtype"/> 是 <c>error_max_turns</c>、<c>error_max_budget_usd</c>，
/// 或 <see cref="TerminalReason"/> 是 <c>api_error</c>。繼承 <see cref="CliProcessException"/>，原本接它的程式照樣能用。
/// 原本的 <see cref="CliProcessException"/> 放在 <see cref="Exception.InnerException"/>。
/// </summary>
public class CliResultException : CliProcessException
{
    /// <summary>建立例外。</summary>
    public CliResultException()
    {
    }

    /// <summary>以訊息建立例外。</summary>
    public CliResultException(string message)
        : base(message)
    {
    }

    /// <summary>以訊息與內部例外建立例外。</summary>
    public CliResultException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    /// <summary>從出錯的結果訊息建立；訊息文字的選法跟 Python 的 <c>_error_result_text</c> 一樣。</summary>
    internal CliResultException(JsonElement result, CliProcessException processException)
        : base($"Claude Code 回報了出錯的結果：{ErrorText(result)}", processException.ExitCode, processException)
    {
        RawResult = result;
        Subtype = GetString(result, "subtype");
        Errors = NormalizeErrors(result);
        Result = GetString(result, "result");
        ApiErrorStatus = result.TryGetProperty("api_error_status", out var status)
            && status.ValueKind == JsonValueKind.Number
            && status.TryGetInt32(out var code)
                ? code
                : null;
        TerminalReason = GetString(result, "terminal_reason");
        SessionId = GetString(result, "session_id");
    }

    /// <summary>
    /// 結果的 <c>subtype</c>，例如 <c>error_max_turns</c>、<c>error_during_execution</c>；
    /// 最後一輪是 API 錯誤、但流程本身跑完時也可能是 <c>success</c>。
    /// </summary>
    public string? Subtype { get; }

    /// <summary>CLI 回報的錯誤說明，去掉空白與空字串；可能是空的。</summary>
    public IReadOnlyList<string> Errors { get; } = [];

    /// <summary>結果的文字；API 失敗時是 <c>API Error: …</c> 的說明。</summary>
    public string? Result { get; }

    /// <summary>失敗的 API 呼叫的 HTTP 狀態碼。</summary>
    public int? ApiErrorStatus { get; }

    /// <summary>為什麼結束，例如 <c>api_error</c>、<c>max_turns</c>；CLI 沒回報時是 <see langword="null"/>。</summary>
    public string? TerminalReason { get; }

    /// <summary>結果所屬的 session。</summary>
    public string? SessionId { get; }

    /// <summary>CLI 送來的原始 <c>result</c> 訊息（Python 的 <c>data</c>）。</summary>
    public JsonElement? RawResult { get; }

    /// <summary>
    /// 挑最有用的說明：先用 <c>errors</c>，再用 <c>result</c>，再用不是 <c>success</c> 的 <c>subtype</c>，最後是 HTTP 狀態碼。
    /// </summary>
    private static string ErrorText(JsonElement result)
    {
        var errors = NormalizeErrors(result);
        if (errors.Count > 0)
        {
            return string.Join("; ", errors);
        }

        if (GetString(result, "result") is { } text && !string.IsNullOrWhiteSpace(text))
        {
            return text.Trim();
        }

        if (GetString(result, "subtype") is { Length: > 0 } subtype && subtype != "success")
        {
            return subtype;
        }

        // Python 只要欄位不是 None 就用，不限整數
        if (result.TryGetProperty("api_error_status", out var status) && status.ValueKind != JsonValueKind.Null)
        {
            return $"API error (HTTP {(status.ValueKind == JsonValueKind.String ? status.GetString() : status.GetRawText())})";
        }

        return "unknown error";
    }

    /// <summary>對應 Python 的 <c>_normalize_result_errors</c>：單一字串也接受，丟掉非字串與空白的項目。</summary>
    private static List<string> NormalizeErrors(JsonElement result)
    {
        if (!result.TryGetProperty("errors", out var errors))
        {
            return [];
        }

        IEnumerable<JsonElement> items = errors.ValueKind switch
        {
            JsonValueKind.String => [errors],
            JsonValueKind.Array => errors.EnumerateArray(),
            _ => [],
        };
        return
        [
            .. items
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!.Trim())
                .Where(item => item.Length > 0),
        ];
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
