namespace PamisWang.ClaudeAgentSdk.Hooks;

/// <summary>
/// 可以掛 hook 的事件，當作 <see cref="ClaudeAgentOptions.Hooks"/> 的鍵。對應 Python SDK 的 <c>HookEvent</c>，值就是 CLI 用的名字。
/// 鍵是字串：CLI 之後新增、這裡還沒列出的事件，直接寫名字也能註冊。
/// </summary>
public static class HookEvent
{
    /// <summary>工具執行前；可以允許、拒絕或改寫參數。</summary>
    public const string PreToolUse = "PreToolUse";

    /// <summary>工具執行成功後。</summary>
    public const string PostToolUse = "PostToolUse";

    /// <summary>工具執行失敗後。</summary>
    public const string PostToolUseFailure = "PostToolUseFailure";

    /// <summary>使用者送出提示詞、模型處理之前。</summary>
    public const string UserPromptSubmit = "UserPromptSubmit";

    /// <summary>主對話要停下來時。</summary>
    public const string Stop = "Stop";

    /// <summary>subagent 要停下來時。</summary>
    public const string SubagentStop = "SubagentStop";

    /// <summary>壓縮對話之前。</summary>
    public const string PreCompact = "PreCompact";

    /// <summary>CLI 發出通知時。</summary>
    public const string Notification = "Notification";

    /// <summary>subagent 開始時。</summary>
    public const string SubagentStart = "SubagentStart";

    /// <summary>CLI 要詢問權限時。</summary>
    public const string PermissionRequest = "PermissionRequest";
}
