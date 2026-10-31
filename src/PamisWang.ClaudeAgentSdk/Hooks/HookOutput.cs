using System.Text.Json;
using System.Text.Json.Nodes;
using PamisWang.ClaudeAgentSdk.Protocol;

namespace PamisWang.ClaudeAgentSdk.Hooks;

/// <summary>
/// hook 回呼的結果，原樣成為 <c>hook_callback</c> 回應的內容。對應 Python SDK 的 <c>HookJSONOutput</c>：
/// <see cref="SyncHookOutput"/>（<c>SyncHookJSONOutput</c>）、<see cref="AsyncHookOutput"/>（<c>AsyncHookJSONOutput</c>），
/// 或直接給 JSON 的 <see cref="RawHookOutput"/>。什麼都不改就回傳 <see cref="Empty"/>。
/// </summary>
public abstract record HookOutput
{
    private protected HookOutput()
    {
    }

    /// <summary>沒有任何欄位（<c>{}</c>）：照 CLI 原本的流程走。</summary>
    public static HookOutput Empty { get; } = new SyncHookOutput();

    /// <summary>寫出回應物件裡的欄位，用 CLI 的欄位名稱。</summary>
    internal abstract void WriteFields(Utf8JsonWriter writer);
}

/// <summary>
/// 一般的 hook 結果：控制要不要繼續、決定與給模型或使用者看的訊息。對應 Python 的 <c>SyncHookJSONOutput</c>；
/// <see langword="null"/> 的欄位不送。Python 的 <c>continue_</c> 在這裡是 <see cref="Continue"/>，送出時一樣是 <c>continue</c>。
/// </summary>
public sealed record SyncHookOutput : HookOutput
{
    /// <summary>hook 之後 Claude 要不要繼續（<c>continue</c>，CLI 預設 <see langword="true"/>）；<see langword="false"/> 會停下這一輪。</summary>
    public bool? Continue { get; init; }

    /// <summary>不在對話紀錄模式顯示 hook 的輸出（<c>suppressOutput</c>）。</summary>
    public bool? SuppressOutput { get; init; }

    /// <summary><see cref="Continue"/> 是 <see langword="false"/> 時給使用者看的原因（<c>stopReason</c>）。</summary>
    public string? StopReason { get; init; }

    /// <summary>
    /// 決定（<c>decision</c>）：<c>block</c> 表示擋下。PreToolUse 的 <c>approve</c> 已經不建議用，改用
    /// <see cref="PreToolUseHookSpecificOutput.PermissionDecision"/>。
    /// </summary>
    public string? Decision { get; init; }

    /// <summary>給使用者看的警告（<c>systemMessage</c>）。</summary>
    public string? SystemMessage { get; init; }

    /// <summary>給 Claude 看的決定理由（<c>reason</c>）。</summary>
    public string? Reason { get; init; }

    /// <summary>事件專屬的控制（<c>hookSpecificOutput</c>），例如 PreToolUse 的權限決定。</summary>
    public HookSpecificOutput? HookSpecificOutput { get; init; }

    internal override void WriteFields(Utf8JsonWriter writer)
    {
        writer.WriteOptional("continue", Continue);
        writer.WriteOptional("suppressOutput", SuppressOutput);
        writer.WriteOptional("stopReason", StopReason);
        writer.WriteOptional("decision", Decision);
        writer.WriteOptional("systemMessage", SystemMessage);
        writer.WriteOptional("reason", Reason);
        if (HookSpecificOutput is not null)
        {
            writer.WriteStartObject("hookSpecificOutput");
            HookSpecificOutput.WriteTo(writer);
            writer.WriteEndObject();
        }
    }
}

/// <summary>
/// 延後執行：告訴 CLI 這個 hook 在背景跑，不必等它（<c>{"async": true}</c>）。對應 Python 的 <c>AsyncHookJSONOutput</c>；
/// Python 的 <c>async_</c> 送出時是 <c>async</c>。
/// </summary>
/// <param name="AsyncTimeout">背景執行的時間上限（毫秒，<c>asyncTimeout</c>）；<see langword="null"/> 不送。</param>
public sealed record AsyncHookOutput(int? AsyncTimeout = null) : HookOutput
{
    internal override void WriteFields(Utf8JsonWriter writer)
    {
        writer.WriteBoolean("async", true);
        writer.WriteOptional("asyncTimeout", AsyncTimeout);
    }
}

/// <summary>
/// 自己組的 JSON，原樣送出；用在這個 SDK 還沒有對應屬性的欄位。
/// 跟 Python 的 <c>_convert_hook_output_for_cli</c> 一樣，最外層的 <c>async_</c>、<c>continue_</c> 會改成 <c>async</c>、<c>continue</c>。
/// </summary>
/// <param name="Json">回應的內容。</param>
public sealed record RawHookOutput(JsonObject Json) : HookOutput
{
    internal override void WriteFields(Utf8JsonWriter writer)
    {
        foreach (var (key, value) in Json)
        {
            writer.WritePropertyName(key switch
            {
                "async_" => "async",
                "continue_" => "continue",
                _ => key,
            });
            if (value is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                value.WriteTo(writer);
            }
        }
    }
}
