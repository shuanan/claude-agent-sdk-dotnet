namespace PamisWang.ClaudeAgentSdk.Hooks;

/// <summary>
/// hook 回呼。對應 Python SDK 的 <c>HookCallback</c>：<c>(input, tool_use_id, context)</c>。
/// CLI 撤回這次呼叫（<c>control_cancel_request</c>）或 client 關閉時 <paramref name="cancellationToken"/> 會被取消，這時回傳什麼都不會送出。
/// 丟出例外時 CLI 收到的是錯誤回應，內容是例外的訊息。
/// </summary>
/// <param name="input">CLI 送來的資料，依事件是對應的子類別，例如 <see cref="PreToolUseHookInput"/>。</param>
/// <param name="toolUseId">相關的工具呼叫 ID（<c>tool_use_id</c>）；跟工具無關的事件是 <see langword="null"/>。</param>
/// <param name="context">其他資訊。</param>
/// <param name="cancellationToken">取消。</param>
public delegate ValueTask<HookOutput> HookCallback(
    HookInput input,
    string? toolUseId,
    HookContext context,
    CancellationToken cancellationToken);

/// <summary>hook 回呼的其他資訊。對應 Python 的 <c>HookContext</c>。</summary>
public sealed class HookContext
{
    internal HookContext(CancellationToken signal)
    {
        Signal = signal;
    }

    /// <summary>
    /// 取消的訊號，跟回呼的 <see cref="CancellationToken"/> 是同一個。Python 的 <c>signal</c> 保留給之後用、目前一律是 <c>None</c>；
    /// 這裡已經會在 CLI 撤回時取消。
    /// </summary>
    public CancellationToken Signal { get; }
}

/// <summary>
/// 一組 hook：符合 <see cref="Matcher"/> 時依序登記的回呼。對應 Python SDK 的 <c>HookMatcher</c>。
/// </summary>
/// <param name="Matcher">
/// 要符合什麼，格式見 Claude Code 的 hook 文件；例如 PreToolUse 用工具名稱 <c>Bash</c> 或 <c>Write|MultiEdit|Edit</c>。
/// <see langword="null"/> 表示全部。
/// </param>
/// <param name="Hooks">回呼。</param>
/// <param name="Timeout">這組裡每個回呼的時間上限（秒，CLI 預設 60）；<see langword="null"/> 不送。</param>
public sealed record HookMatcher(string? Matcher, IReadOnlyList<HookCallback> Hooks, double? Timeout = null)
{
    /// <summary>一個回呼的簡寫。</summary>
    public HookMatcher(string? matcher, HookCallback hook, double? timeout = null)
        : this(matcher, [hook], timeout)
    {
    }
}
