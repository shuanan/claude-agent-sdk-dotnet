using System.Text.Json;
using PamisWang.ClaudeAgentSdk.Protocol;

namespace PamisWang.ClaudeAgentSdk.Hooks;

/// <summary>
/// 事件專屬的 hook 結果，放在 <see cref="SyncHookOutput.HookSpecificOutput"/>。對應 Python SDK 的 <c>HookSpecificOutput</c> 聯集，
/// 每個事件一個子類別；送出時第一個欄位是 <c>hookEventName</c>，<see langword="null"/> 的欄位不送。
/// </summary>
public abstract record HookSpecificOutput
{
    private protected HookSpecificOutput()
    {
    }

    /// <summary>這個結果屬於哪個事件（<c>hookEventName</c>）。</summary>
    public abstract string HookEventName { get; }

    internal void WriteTo(Utf8JsonWriter writer)
    {
        writer.WriteString("hookEventName", HookEventName);
        WriteFields(writer);
    }

    private protected abstract void WriteFields(Utf8JsonWriter writer);

    private protected static void WriteOptional(Utf8JsonWriter writer, string name, JsonElement? value)
    {
        if (value is { } json)
        {
            writer.WritePropertyName(name);
            json.WriteTo(writer);
        }
    }
}

/// <summary>PreToolUse 的結果：允許、拒絕、詢問或延後，也可以改寫參數。對應 Python 的 <c>PreToolUseHookSpecificOutput</c>。</summary>
public sealed record PreToolUseHookSpecificOutput : HookSpecificOutput
{
    /// <inheritdoc/>
    public override string HookEventName => HookEvent.PreToolUse;

    /// <summary>權限決定（<c>permissionDecision</c>）：<c>allow</c>、<c>deny</c>、<c>ask</c> 或 <c>defer</c>。</summary>
    public string? PermissionDecision { get; init; }

    /// <summary>決定的理由（<c>permissionDecisionReason</c>）；拒絕時模型會看到。</summary>
    public string? PermissionDecisionReason { get; init; }

    /// <summary>改過的工具參數（<c>updatedInput</c>）。</summary>
    public JsonElement? UpdatedInput { get; init; }

    /// <summary>附加給模型的內容（<c>additionalContext</c>）。</summary>
    public string? AdditionalContext { get; init; }

    private protected override void WriteFields(Utf8JsonWriter writer)
    {
        writer.WriteOptional("permissionDecision", PermissionDecision);
        writer.WriteOptional("permissionDecisionReason", PermissionDecisionReason);
        WriteOptional(writer, "updatedInput", UpdatedInput);
        writer.WriteOptional("additionalContext", AdditionalContext);
    }
}

/// <summary>PostToolUse 的結果。對應 Python 的 <c>PostToolUseHookSpecificOutput</c>。</summary>
public sealed record PostToolUseHookSpecificOutput : HookSpecificOutput
{
    /// <inheritdoc/>
    public override string HookEventName => HookEvent.PostToolUse;

    /// <summary>附加給模型的內容（<c>additionalContext</c>）。</summary>
    public string? AdditionalContext { get; init; }

    /// <summary>
    /// 取代送給模型的工具結果（<c>updatedToolOutput</c>）。內建工具要符合它的輸出格式，例如 Bash 是
    /// <c>{"stdout": …, "stderr": …, "interrupted": …}</c>；格式不對時 CLI 保留原本的結果。
    /// </summary>
    public JsonElement? UpdatedToolOutput { get; init; }

    /// <summary>只對 MCP 工具有效的結果取代（<c>updatedMCPToolOutput</c>）；建議改用 <see cref="UpdatedToolOutput"/>。</summary>
    public JsonElement? UpdatedMcpToolOutput { get; init; }

    private protected override void WriteFields(Utf8JsonWriter writer)
    {
        writer.WriteOptional("additionalContext", AdditionalContext);
        WriteOptional(writer, "updatedToolOutput", UpdatedToolOutput);
        WriteOptional(writer, "updatedMCPToolOutput", UpdatedMcpToolOutput);
    }
}

/// <summary>PostToolUseFailure 的結果。對應 Python 的 <c>PostToolUseFailureHookSpecificOutput</c>。</summary>
public sealed record PostToolUseFailureHookSpecificOutput : HookSpecificOutput
{
    /// <inheritdoc/>
    public override string HookEventName => HookEvent.PostToolUseFailure;

    /// <summary>附加給模型的內容（<c>additionalContext</c>）。</summary>
    public string? AdditionalContext { get; init; }

    private protected override void WriteFields(Utf8JsonWriter writer) =>
        writer.WriteOptional("additionalContext", AdditionalContext);
}

/// <summary>UserPromptSubmit 的結果。對應 Python 的 <c>UserPromptSubmitHookSpecificOutput</c>。</summary>
public sealed record UserPromptSubmitHookSpecificOutput : HookSpecificOutput
{
    /// <inheritdoc/>
    public override string HookEventName => HookEvent.UserPromptSubmit;

    /// <summary>附加給模型的內容（<c>additionalContext</c>）。</summary>
    public string? AdditionalContext { get; init; }

    /// <summary>設定 session 標題（<c>sessionTitle</c>）。</summary>
    public string? SessionTitle { get; init; }

    /// <summary><see cref="SyncHookOutput.Decision"/> 是 <c>block</c> 時，擋下的訊息裡不放原本的提示詞（<c>suppressOriginalPrompt</c>）。</summary>
    public bool? SuppressOriginalPrompt { get; init; }

    private protected override void WriteFields(Utf8JsonWriter writer)
    {
        writer.WriteOptional("additionalContext", AdditionalContext);
        writer.WriteOptional("sessionTitle", SessionTitle);
        writer.WriteOptional("suppressOriginalPrompt", SuppressOriginalPrompt);
    }
}

/// <summary>UserPromptExpansion 的結果。對應 Python 的 <c>UserPromptExpansionHookSpecificOutput</c>。</summary>
public sealed record UserPromptExpansionHookSpecificOutput : HookSpecificOutput
{
    /// <inheritdoc/>
    public override string HookEventName => "UserPromptExpansion";

    /// <summary>附加給模型的內容（<c>additionalContext</c>）。</summary>
    public string? AdditionalContext { get; init; }

    /// <inheritdoc cref="UserPromptSubmitHookSpecificOutput.SuppressOriginalPrompt"/>
    public bool? SuppressOriginalPrompt { get; init; }

    private protected override void WriteFields(Utf8JsonWriter writer)
    {
        writer.WriteOptional("additionalContext", AdditionalContext);
        writer.WriteOptional("suppressOriginalPrompt", SuppressOriginalPrompt);
    }
}

/// <summary>SessionStart 的結果。對應 Python 的 <c>SessionStartHookSpecificOutput</c>。</summary>
public sealed record SessionStartHookSpecificOutput : HookSpecificOutput
{
    /// <inheritdoc/>
    public override string HookEventName => "SessionStart";

    /// <summary>附加給模型的內容（<c>additionalContext</c>）。</summary>
    public string? AdditionalContext { get; init; }

    /// <summary>當作 session 的第一則使用者訊息（<c>initialUserMessage</c>）。</summary>
    public string? InitialUserMessage { get; init; }

    /// <summary>設定 session 標題（<c>sessionTitle</c>）；從 <c>clear</c> 或 <c>compact</c> 開始的 session 不理會。</summary>
    public string? SessionTitle { get; init; }

    /// <summary>這個 session 要監看的絕對路徑（<c>watchPaths</c>），變動時觸發 FileChanged。</summary>
    public IReadOnlyList<string>? WatchPaths { get; init; }

    /// <summary>SessionStart hook 都跑完後重新掃描 skill 與指令的資料夾（<c>reloadSkills</c>），hook 裝好的 skill 同一個 session 就能用。</summary>
    public bool? ReloadSkills { get; init; }

    private protected override void WriteFields(Utf8JsonWriter writer)
    {
        writer.WriteOptional("additionalContext", AdditionalContext);
        writer.WriteOptional("initialUserMessage", InitialUserMessage);
        writer.WriteOptional("sessionTitle", SessionTitle);
        writer.WriteOptional("watchPaths", WatchPaths);
        writer.WriteOptional("reloadSkills", ReloadSkills);
    }
}

/// <summary>Notification 的結果。對應 Python 的 <c>NotificationHookSpecificOutput</c>。</summary>
public sealed record NotificationHookSpecificOutput : HookSpecificOutput
{
    /// <inheritdoc/>
    public override string HookEventName => HookEvent.Notification;

    /// <summary>附加給模型的內容（<c>additionalContext</c>）。</summary>
    public string? AdditionalContext { get; init; }

    private protected override void WriteFields(Utf8JsonWriter writer) =>
        writer.WriteOptional("additionalContext", AdditionalContext);
}

/// <summary>SubagentStart 的結果。對應 Python 的 <c>SubagentStartHookSpecificOutput</c>。</summary>
public sealed record SubagentStartHookSpecificOutput : HookSpecificOutput
{
    /// <inheritdoc/>
    public override string HookEventName => HookEvent.SubagentStart;

    /// <summary>附加給 subagent 的內容（<c>additionalContext</c>）。</summary>
    public string? AdditionalContext { get; init; }

    private protected override void WriteFields(Utf8JsonWriter writer) =>
        writer.WriteOptional("additionalContext", AdditionalContext);
}

/// <summary>PermissionRequest 的結果。對應 Python 的 <c>PermissionRequestHookSpecificOutput</c>。</summary>
/// <param name="Decision">權限決定（<c>decision</c>），原樣送出，例如 <c>{"behavior": "allow"}</c>。</param>
public sealed record PermissionRequestHookSpecificOutput(JsonElement Decision) : HookSpecificOutput
{
    /// <inheritdoc/>
    public override string HookEventName => HookEvent.PermissionRequest;

    private protected override void WriteFields(Utf8JsonWriter writer)
    {
        writer.WritePropertyName("decision");
        Decision.WriteTo(writer);
    }
}

/// <summary>PostToolBatch 的結果。對應 Python 的 <c>PostToolBatchHookSpecificOutput</c>。</summary>
public sealed record PostToolBatchHookSpecificOutput : HookSpecificOutput
{
    /// <inheritdoc/>
    public override string HookEventName => "PostToolBatch";

    /// <summary>附加給模型的內容（<c>additionalContext</c>）。</summary>
    public string? AdditionalContext { get; init; }

    private protected override void WriteFields(Utf8JsonWriter writer) =>
        writer.WriteOptional("additionalContext", AdditionalContext);
}

/// <summary>Stop 的結果。對應 Python 的 <c>StopHookSpecificOutput</c>。</summary>
public sealed record StopHookSpecificOutput : HookSpecificOutput
{
    /// <inheritdoc/>
    public override string HookEventName => HookEvent.Stop;

    /// <summary>給模型的非錯誤回饋（<c>additionalContext</c>）；對話會繼續，讓模型處理它。</summary>
    public string? AdditionalContext { get; init; }

    private protected override void WriteFields(Utf8JsonWriter writer) =>
        writer.WriteOptional("additionalContext", AdditionalContext);
}

/// <summary>SubagentStop 的結果。對應 Python 的 <c>SubagentStopHookSpecificOutput</c>。</summary>
public sealed record SubagentStopHookSpecificOutput : HookSpecificOutput
{
    /// <inheritdoc/>
    public override string HookEventName => HookEvent.SubagentStop;

    /// <summary>給 subagent 的非錯誤回饋（<c>additionalContext</c>）；subagent 會繼續，讓它處理。</summary>
    public string? AdditionalContext { get; init; }

    private protected override void WriteFields(Utf8JsonWriter writer) =>
        writer.WriteOptional("additionalContext", AdditionalContext);
}

/// <summary>PermissionDenied 的結果。對應 Python 的 <c>PermissionDeniedHookSpecificOutput</c>。</summary>
public sealed record PermissionDeniedHookSpecificOutput : HookSpecificOutput
{
    /// <inheritdoc/>
    public override string HookEventName => "PermissionDenied";

    /// <summary>告訴模型可以重試被拒絕的工具呼叫（<c>retry</c>）；沒有分類器判定的拒絕不理會。</summary>
    public bool? Retry { get; init; }

    private protected override void WriteFields(Utf8JsonWriter writer) =>
        writer.WriteOptional("retry", Retry);
}

/// <summary>PreModelSwitch 的結果。對應 Python 的 <c>PreModelSwitchHookSpecificOutput</c>。</summary>
public sealed record PreModelSwitchHookSpecificOutput : HookSpecificOutput
{
    /// <inheritdoc/>
    public override string HookEventName => "PreModelSwitch";

    /// <summary>權限決定（<c>permissionDecision</c>）：<c>allow</c> 照換、<c>deny</c> 取消；<c>ask</c> 在非互動的 session 視同拒絕。</summary>
    public string? PermissionDecision { get; init; }

    /// <summary>決定的理由（<c>permissionDecisionReason</c>）。</summary>
    public string? PermissionDecisionReason { get; init; }

    private protected override void WriteFields(Utf8JsonWriter writer)
    {
        writer.WriteOptional("permissionDecision", PermissionDecision);
        writer.WriteOptional("permissionDecisionReason", PermissionDecisionReason);
    }
}

/// <summary>PostModelSwitch 的結果。對應 Python 的 <c>PostModelSwitchHookSpecificOutput</c>。</summary>
public sealed record PostModelSwitchHookSpecificOutput : HookSpecificOutput
{
    /// <inheritdoc/>
    public override string HookEventName => "PostModelSwitch";

    /// <summary>附加給新模型的內容（<c>additionalContext</c>），隨它處理的下一個請求送出。</summary>
    public string? AdditionalContext { get; init; }

    private protected override void WriteFields(Utf8JsonWriter writer) =>
        writer.WriteOptional("additionalContext", AdditionalContext);
}

/// <summary>Elicitation 的結果：直接接受或拒絕 MCP 的 elicitation 請求。對應 Python 的 <c>ElicitationHookSpecificOutput</c>。</summary>
public sealed record ElicitationHookSpecificOutput : HookSpecificOutput
{
    /// <inheritdoc/>
    public override string HookEventName => "Elicitation";

    /// <summary>動作（<c>action</c>）：<c>accept</c>、<c>decline</c> 或 <c>cancel</c>。</summary>
    public string? Action { get; init; }

    /// <summary>要送出的表單欄位（<c>content</c>），只在 <c>accept</c> 時有用。</summary>
    public JsonElement? Content { get; init; }

    private protected override void WriteFields(Utf8JsonWriter writer)
    {
        writer.WriteOptional("action", Action);
        WriteOptional(writer, "content", Content);
    }
}

/// <summary>ElicitationResult 的結果：送給 MCP server 之前改掉動作或內容。對應 Python 的 <c>ElicitationResultHookSpecificOutput</c>。</summary>
public sealed record ElicitationResultHookSpecificOutput : HookSpecificOutput
{
    /// <inheritdoc/>
    public override string HookEventName => "ElicitationResult";

    /// <inheritdoc cref="ElicitationHookSpecificOutput.Action"/>
    public string? Action { get; init; }

    /// <summary>表單欄位（<c>content</c>）。</summary>
    public JsonElement? Content { get; init; }

    private protected override void WriteFields(Utf8JsonWriter writer)
    {
        writer.WriteOptional("action", Action);
        WriteOptional(writer, "content", Content);
    }
}

/// <summary>CwdChanged 的結果。對應 Python 的 <c>CwdChangedHookSpecificOutput</c>。</summary>
public sealed record CwdChangedHookSpecificOutput : HookSpecificOutput
{
    /// <inheritdoc/>
    public override string HookEventName => "CwdChanged";

    /// <summary>要監看的絕對路徑（<c>watchPaths</c>），取代目前的監看清單。</summary>
    public IReadOnlyList<string>? WatchPaths { get; init; }

    private protected override void WriteFields(Utf8JsonWriter writer) =>
        writer.WriteOptional("watchPaths", WatchPaths);
}

/// <summary>FileChanged 的結果。對應 Python 的 <c>FileChangedHookSpecificOutput</c>。</summary>
public sealed record FileChangedHookSpecificOutput : HookSpecificOutput
{
    /// <inheritdoc/>
    public override string HookEventName => "FileChanged";

    /// <inheritdoc cref="CwdChangedHookSpecificOutput.WatchPaths"/>
    public IReadOnlyList<string>? WatchPaths { get; init; }

    private protected override void WriteFields(Utf8JsonWriter writer) =>
        writer.WriteOptional("watchPaths", WatchPaths);
}

/// <summary>WorktreeCreate 的結果。對應 Python 的 <c>WorktreeCreateHookSpecificOutput</c>。</summary>
/// <param name="WorktreePath">建好的 worktree 資料夾的絕對路徑（<c>worktreePath</c>）。</param>
public sealed record WorktreeCreateHookSpecificOutput(string WorktreePath) : HookSpecificOutput
{
    /// <inheritdoc/>
    public override string HookEventName => "WorktreeCreate";

    private protected override void WriteFields(Utf8JsonWriter writer) =>
        writer.WriteString("worktreePath", WorktreePath);
}

/// <summary>
/// MessageDisplay 的結果：只改畫面上顯示的文字，存下的訊息與模型看到的都不變。對應 Python 的 <c>MessageDisplayHookSpecificOutput</c>。
/// </summary>
public sealed record MessageDisplayHookSpecificOutput : HookSpecificOutput
{
    /// <inheritdoc/>
    public override string HookEventName => "MessageDisplay";

    /// <summary>取代顯示的文字（<c>displayContent</c>）；不給就顯示原本的。</summary>
    public string? DisplayContent { get; init; }

    private protected override void WriteFields(Utf8JsonWriter writer) =>
        writer.WriteOptional("displayContent", DisplayContent);
}
