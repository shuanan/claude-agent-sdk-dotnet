using System.Text.Json;

namespace PamisWang.ClaudeAgentSdk.Hooks;

/// <summary>
/// CLI 觸發 hook 時送來的資料（<c>hook_callback</c> 的 <c>input</c>）。對應 Python SDK 的 <c>BaseHookInput</c> 與各事件的 <c>*HookInput</c>；
/// 依 <see cref="HookEventName"/> 是哪個事件，實際的型別是對應的子類別，例如 <see cref="PreToolUseHookInput"/>，
/// 不認得的事件是 <see cref="UnknownHookInput"/>。
/// CLI 少給的欄位：必要的字串是空字串、布林是 <see langword="false"/>，選填的是 <see langword="null"/>；完整的原始內容在 <see cref="Raw"/>。
/// </summary>
public abstract class HookInput
{
    private protected HookInput(JsonElement raw)
    {
        Raw = raw;
        HookEventName = HookJson.String(raw, "hook_event_name") ?? "";
        SessionId = HookJson.String(raw, "session_id") ?? "";
        TranscriptPath = HookJson.String(raw, "transcript_path") ?? "";
        Cwd = HookJson.String(raw, "cwd") ?? "";
        PermissionMode = HookJson.String(raw, "permission_mode");
        PromptId = HookJson.String(raw, "prompt_id");
        Effort = HookJson.Object(raw, "effort") is { } effort ? new HookEffort(effort) : null;
    }

    /// <summary>事件名稱（<c>hook_event_name</c>），例如 <see cref="HookEvent.PreToolUse"/>。</summary>
    public string HookEventName { get; }

    /// <summary>session ID（<c>session_id</c>）。</summary>
    public string SessionId { get; }

    /// <summary>對話紀錄檔的路徑（<c>transcript_path</c>）。</summary>
    public string TranscriptPath { get; }

    /// <summary>CLI 的工作目錄（<c>cwd</c>）。</summary>
    public string Cwd { get; }

    /// <summary>目前的權限模式（<c>permission_mode</c>）。</summary>
    public string? PermissionMode { get; }

    /// <summary>正在處理的使用者提示詞的 UUID（<c>prompt_id</c>），跟 OpenTelemetry 事件的 <c>prompt.id</c> 一樣；第一次輸入之前沒有。</summary>
    public string? PromptId { get; }

    /// <summary>這一輪的思考深度（<c>effort</c>）。只有支援的模型、在工具相關的 hook 裡才有。</summary>
    public HookEffort? Effort { get; }

    /// <summary>CLI 送來的原始 JSON，這裡沒有對應屬性的欄位也在裡面。</summary>
    public JsonElement Raw { get; }

    /// <summary>
    /// 依 <c>hook_event_name</c> 轉成對應的型別。不是物件（例如 CLI 沒給）時是內容為原值的 <see cref="UnknownHookInput"/>。
    /// 也可以在單元測試裡用來建立 hook 的輸入。
    /// </summary>
    public static HookInput Parse(JsonElement json) =>
        (json.ValueKind == JsonValueKind.Object ? HookJson.String(json, "hook_event_name") : null) switch
        {
            HookEvent.PreToolUse => new PreToolUseHookInput(json),
            HookEvent.PostToolUse => new PostToolUseHookInput(json),
            HookEvent.PostToolUseFailure => new PostToolUseFailureHookInput(json),
            HookEvent.UserPromptSubmit => new UserPromptSubmitHookInput(json),
            HookEvent.Stop => new StopHookInput(json),
            HookEvent.SubagentStop => new SubagentStopHookInput(json),
            HookEvent.PreCompact => new PreCompactHookInput(json),
            HookEvent.Notification => new NotificationHookInput(json),
            HookEvent.SubagentStart => new SubagentStartHookInput(json),
            HookEvent.PermissionRequest => new PermissionRequestHookInput(json),
            _ => new UnknownHookInput(json),
        };
}

/// <summary>這個 SDK 還不認得的事件；欄位從 <see cref="HookInput.Raw"/> 讀。</summary>
public sealed class UnknownHookInput : HookInput
{
    internal UnknownHookInput(JsonElement raw)
        : base(raw)
    {
    }
}

/// <summary>工具執行前（<c>PreToolUse</c>）。對應 Python 的 <c>PreToolUseHookInput</c>。</summary>
public sealed class PreToolUseHookInput : HookInput
{
    internal PreToolUseHookInput(JsonElement raw)
        : base(raw)
    {
        ToolName = HookJson.String(raw, "tool_name") ?? "";
        ToolInput = HookJson.Object(raw, "tool_input") ?? HookJson.EmptyObject;
        ToolUseId = HookJson.String(raw, "tool_use_id") ?? "";
        AgentId = HookJson.String(raw, "agent_id");
        AgentType = HookJson.String(raw, "agent_type");
    }

    /// <summary>工具名稱（<c>tool_name</c>）。</summary>
    public string ToolName { get; }

    /// <summary>工具的參數（<c>tool_input</c>）。</summary>
    public JsonElement ToolInput { get; }

    /// <summary>這次工具呼叫的 ID（<c>tool_use_id</c>）。</summary>
    public string ToolUseId { get; }

    /// <summary>在 subagent 裡觸發時是它的 ID（<c>agent_id</c>），主對話沒有。平行的 subagent 共用同一條 control 通道，要靠它分辨。</summary>
    public string? AgentId { get; }

    /// <summary>agent 類型（<c>agent_type</c>），在 subagent 裡、或以 <c>--agent</c> 啟動的主對話才有。</summary>
    public string? AgentType { get; }
}

/// <summary>工具執行成功後（<c>PostToolUse</c>）。對應 Python 的 <c>PostToolUseHookInput</c>。</summary>
public sealed class PostToolUseHookInput : HookInput
{
    internal PostToolUseHookInput(JsonElement raw)
        : base(raw)
    {
        ToolName = HookJson.String(raw, "tool_name") ?? "";
        ToolInput = HookJson.Object(raw, "tool_input") ?? HookJson.EmptyObject;
        ToolResponse = raw.TryGetProperty("tool_response", out var response) ? response : default;
        ToolUseId = HookJson.String(raw, "tool_use_id") ?? "";
        DurationMs = HookJson.Long(raw, "duration_ms");
        AgentId = HookJson.String(raw, "agent_id");
        AgentType = HookJson.String(raw, "agent_type");
    }

    /// <summary>工具名稱（<c>tool_name</c>）。</summary>
    public string ToolName { get; }

    /// <summary>工具的參數（<c>tool_input</c>）。</summary>
    public JsonElement ToolInput { get; }

    /// <summary>工具的結果（<c>tool_response</c>），什麼型別都可能；CLI 沒給時是 <see cref="JsonValueKind.Undefined"/>。</summary>
    public JsonElement ToolResponse { get; }

    /// <summary>這次工具呼叫的 ID（<c>tool_use_id</c>）。</summary>
    public string ToolUseId { get; }

    /// <summary>工具執行了幾毫秒（<c>duration_ms</c>），不含等權限與 hook 的時間。</summary>
    public long? DurationMs { get; }

    /// <inheritdoc cref="PreToolUseHookInput.AgentId"/>
    public string? AgentId { get; }

    /// <inheritdoc cref="PreToolUseHookInput.AgentType"/>
    public string? AgentType { get; }
}

/// <summary>工具執行失敗後（<c>PostToolUseFailure</c>）。對應 Python 的 <c>PostToolUseFailureHookInput</c>。</summary>
public sealed class PostToolUseFailureHookInput : HookInput
{
    internal PostToolUseFailureHookInput(JsonElement raw)
        : base(raw)
    {
        ToolName = HookJson.String(raw, "tool_name") ?? "";
        ToolInput = HookJson.Object(raw, "tool_input") ?? HookJson.EmptyObject;
        ToolUseId = HookJson.String(raw, "tool_use_id") ?? "";
        Error = HookJson.String(raw, "error") ?? "";
        IsInterrupt = HookJson.Bool(raw, "is_interrupt");
        DurationMs = HookJson.Long(raw, "duration_ms");
        AgentId = HookJson.String(raw, "agent_id");
        AgentType = HookJson.String(raw, "agent_type");
    }

    /// <summary>工具名稱（<c>tool_name</c>）。</summary>
    public string ToolName { get; }

    /// <summary>工具的參數（<c>tool_input</c>）。</summary>
    public JsonElement ToolInput { get; }

    /// <summary>這次工具呼叫的 ID（<c>tool_use_id</c>）。</summary>
    public string ToolUseId { get; }

    /// <summary>錯誤訊息（<c>error</c>）。</summary>
    public string Error { get; }

    /// <summary>是不是被中斷的（<c>is_interrupt</c>）。</summary>
    public bool? IsInterrupt { get; }

    /// <inheritdoc cref="PostToolUseHookInput.DurationMs"/>
    public long? DurationMs { get; }

    /// <inheritdoc cref="PreToolUseHookInput.AgentId"/>
    public string? AgentId { get; }

    /// <inheritdoc cref="PreToolUseHookInput.AgentType"/>
    public string? AgentType { get; }
}

/// <summary>使用者送出提示詞（<c>UserPromptSubmit</c>）。對應 Python 的 <c>UserPromptSubmitHookInput</c>。</summary>
public sealed class UserPromptSubmitHookInput : HookInput
{
    internal UserPromptSubmitHookInput(JsonElement raw)
        : base(raw)
    {
        Prompt = HookJson.String(raw, "prompt") ?? "";
    }

    /// <summary>提示詞（<c>prompt</c>）。</summary>
    public string Prompt { get; }
}

/// <summary>主對話要停下來（<c>Stop</c>）。對應 Python 的 <c>StopHookInput</c>。</summary>
public sealed class StopHookInput : HookInput
{
    internal StopHookInput(JsonElement raw)
        : base(raw)
    {
        StopHookActive = HookJson.Bool(raw, "stop_hook_active") ?? false;
        LastAssistantMessage = HookJson.String(raw, "last_assistant_message");
        BackgroundTasks = HookJson.List(raw, "background_tasks", static task => new BackgroundTaskSummary(task));
        SessionCrons = HookJson.List(raw, "session_crons", static cron => new SessionCronSummary(cron));
    }

    /// <summary>是不是已經因為 Stop hook 而繼續過（<c>stop_hook_active</c>）；用來避免一直擋著不讓停。</summary>
    public bool StopHookActive { get; }

    /// <summary>停下前最後一則助理訊息的文字（<c>last_assistant_message</c>）。</summary>
    public string? LastAssistantMessage { get; }

    /// <summary>這個 session 還在跑的背景工作（<c>background_tasks</c>）。</summary>
    public IReadOnlyList<BackgroundTaskSummary>? BackgroundTasks { get; }

    /// <summary>之後會喚醒這個 session 的排程（<c>session_crons</c>）。</summary>
    public IReadOnlyList<SessionCronSummary>? SessionCrons { get; }
}

/// <summary>subagent 要停下來（<c>SubagentStop</c>）。對應 Python 的 <c>SubagentStopHookInput</c>。</summary>
public sealed class SubagentStopHookInput : HookInput
{
    internal SubagentStopHookInput(JsonElement raw)
        : base(raw)
    {
        StopHookActive = HookJson.Bool(raw, "stop_hook_active") ?? false;
        AgentId = HookJson.String(raw, "agent_id") ?? "";
        AgentTranscriptPath = HookJson.String(raw, "agent_transcript_path") ?? "";
        AgentType = HookJson.String(raw, "agent_type") ?? "";
        LastAssistantMessage = HookJson.String(raw, "last_assistant_message");
        BackgroundTasks = HookJson.List(raw, "background_tasks", static task => new BackgroundTaskSummary(task));
        SessionCrons = HookJson.List(raw, "session_crons", static cron => new SessionCronSummary(cron));
    }

    /// <inheritdoc cref="StopHookInput.StopHookActive"/>
    public bool StopHookActive { get; }

    /// <summary>subagent 的 ID（<c>agent_id</c>）。</summary>
    public string AgentId { get; }

    /// <summary>subagent 對話紀錄檔的路徑（<c>agent_transcript_path</c>）。</summary>
    public string AgentTranscriptPath { get; }

    /// <summary>subagent 的類型（<c>agent_type</c>）。</summary>
    public string AgentType { get; }

    /// <summary>subagent 停下前最後一則助理訊息的文字（<c>last_assistant_message</c>）。</summary>
    public string? LastAssistantMessage { get; }

    /// <summary>上層 session 還在跑的背景工作（<c>background_tasks</c>）。</summary>
    public IReadOnlyList<BackgroundTaskSummary>? BackgroundTasks { get; }

    /// <summary>上層 session 的排程（<c>session_crons</c>）。</summary>
    public IReadOnlyList<SessionCronSummary>? SessionCrons { get; }
}

/// <summary>壓縮對話之前（<c>PreCompact</c>）。對應 Python 的 <c>PreCompactHookInput</c>。</summary>
public sealed class PreCompactHookInput : HookInput
{
    internal PreCompactHookInput(JsonElement raw)
        : base(raw)
    {
        Trigger = HookJson.String(raw, "trigger") ?? "";
        CustomInstructions = HookJson.String(raw, "custom_instructions");
    }

    /// <summary>怎麼觸發的（<c>trigger</c>）：<c>manual</c> 或 <c>auto</c>。</summary>
    public string Trigger { get; }

    /// <summary>使用者給的壓縮指示（<c>custom_instructions</c>）。</summary>
    public string? CustomInstructions { get; }
}

/// <summary>CLI 發出通知（<c>Notification</c>）。對應 Python 的 <c>NotificationHookInput</c>。</summary>
public sealed class NotificationHookInput : HookInput
{
    internal NotificationHookInput(JsonElement raw)
        : base(raw)
    {
        Message = HookJson.String(raw, "message") ?? "";
        Title = HookJson.String(raw, "title");
        NotificationType = HookJson.String(raw, "notification_type") ?? "";
    }

    /// <summary>通知內容（<c>message</c>）。</summary>
    public string Message { get; }

    /// <summary>標題（<c>title</c>）。</summary>
    public string? Title { get; }

    /// <summary>通知類型（<c>notification_type</c>）。</summary>
    public string NotificationType { get; }
}

/// <summary>subagent 開始（<c>SubagentStart</c>）。對應 Python 的 <c>SubagentStartHookInput</c>。</summary>
public sealed class SubagentStartHookInput : HookInput
{
    internal SubagentStartHookInput(JsonElement raw)
        : base(raw)
    {
        AgentId = HookJson.String(raw, "agent_id") ?? "";
        AgentType = HookJson.String(raw, "agent_type") ?? "";
    }

    /// <summary>subagent 的 ID（<c>agent_id</c>）。</summary>
    public string AgentId { get; }

    /// <summary>subagent 的類型（<c>agent_type</c>）。</summary>
    public string AgentType { get; }
}

/// <summary>CLI 要詢問權限（<c>PermissionRequest</c>）。對應 Python 的 <c>PermissionRequestHookInput</c>。</summary>
public sealed class PermissionRequestHookInput : HookInput
{
    internal PermissionRequestHookInput(JsonElement raw)
        : base(raw)
    {
        ToolName = HookJson.String(raw, "tool_name") ?? "";
        ToolInput = HookJson.Object(raw, "tool_input") ?? HookJson.EmptyObject;
        PermissionSuggestions = HookJson.List(raw, "permission_suggestions", static suggestion => suggestion);
        AgentId = HookJson.String(raw, "agent_id");
        AgentType = HookJson.String(raw, "agent_type");
    }

    /// <summary>工具名稱（<c>tool_name</c>）。</summary>
    public string ToolName { get; }

    /// <summary>工具的參數（<c>tool_input</c>）。</summary>
    public JsonElement ToolInput { get; }

    /// <summary>CLI 建議的權限變更（<c>permission_suggestions</c>），原樣保留。</summary>
    public IReadOnlyList<JsonElement>? PermissionSuggestions { get; }

    /// <inheritdoc cref="PreToolUseHookInput.AgentId"/>
    public string? AgentId { get; }

    /// <inheritdoc cref="PreToolUseHookInput.AgentType"/>
    public string? AgentType { get; }
}

/// <summary>hook 觸發時的思考深度。對應 Python 的 <c>HookEffort</c>。</summary>
public sealed class HookEffort
{
    internal HookEffort(JsonElement raw)
    {
        Level = HookJson.String(raw, "level") ?? "";
    }

    /// <summary>這一輪的思考深度（<c>level</c>），例如 <c>low</c>、<c>high</c>、<c>max</c>，已套用模型的降級。</summary>
    public string Level { get; }
}

/// <summary>
/// 還在跑的背景工作，列在 Stop 與 SubagentStop 的輸入裡。對應 Python 的 <c>BackgroundTaskSummary</c>。
/// <see cref="Description"/> 與 <see cref="Command"/> 最多 1000 字元，被截掉的結尾有 <c>… [+N chars]</c>。
/// </summary>
public sealed class BackgroundTaskSummary
{
    internal BackgroundTaskSummary(JsonElement raw)
    {
        Id = HookJson.String(raw, "id") ?? "";
        Type = HookJson.String(raw, "type") ?? "";
        Status = HookJson.String(raw, "status") ?? "";
        Description = HookJson.String(raw, "description") ?? "";
        Command = HookJson.String(raw, "command");
        AgentType = HookJson.String(raw, "agent_type");
        Server = HookJson.String(raw, "server");
        Tool = HookJson.String(raw, "tool");
        Name = HookJson.String(raw, "name");
    }

    /// <summary>工作 ID（<c>id</c>）。</summary>
    public string Id { get; }

    /// <summary>工作類型（<c>type</c>），例如 <c>shell</c>、<c>subagent</c>、<c>monitor</c>、<c>workflow</c>。</summary>
    public string Type { get; }

    /// <summary>狀態（<c>status</c>）。</summary>
    public string Status { get; }

    /// <summary>說明（<c>description</c>）。</summary>
    public string Description { get; }

    /// <summary>shell 工作的指令（<c>command</c>）。</summary>
    public string? Command { get; }

    /// <summary>subagent 工作的類型（<c>agent_type</c>）。</summary>
    public string? AgentType { get; }

    /// <summary>monitor／MCP 工作的 server（<c>server</c>）。</summary>
    public string? Server { get; }

    /// <summary>monitor／MCP 工作的工具（<c>tool</c>）。</summary>
    public string? Tool { get; }

    /// <summary>workflow 工作的名稱（<c>name</c>）。</summary>
    public string? Name { get; }
}

/// <summary>會喚醒 session 的排程，列在 Stop 與 SubagentStop 的輸入裡。對應 Python 的 <c>SessionCronSummary</c>。</summary>
public sealed class SessionCronSummary
{
    internal SessionCronSummary(JsonElement raw)
    {
        Id = HookJson.String(raw, "id") ?? "";
        Schedule = HookJson.String(raw, "schedule") ?? "";
        Recurring = HookJson.Bool(raw, "recurring") ?? false;
        Prompt = HookJson.String(raw, "prompt") ?? "";
    }

    /// <summary>排程 ID（<c>id</c>）。</summary>
    public string Id { get; }

    /// <summary>cron 表示式（<c>schedule</c>），例如 <c>0 9 * * 1-5</c>。</summary>
    public string Schedule { get; }

    /// <summary>是否每次符合都觸發（<c>recurring</c>）；<see langword="false"/> 是只觸發一次。</summary>
    public bool Recurring { get; }

    /// <summary>觸發時送出的提示詞（<c>prompt</c>），最多 1000 字元。</summary>
    public string Prompt { get; }
}
