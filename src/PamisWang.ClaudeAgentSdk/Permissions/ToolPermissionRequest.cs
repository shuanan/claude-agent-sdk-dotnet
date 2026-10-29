using System.Text.Json;

namespace PamisWang.ClaudeAgentSdk.Permissions;

/// <summary>
/// CLI 詢問能不能使用某個工具。對應 Python SDK 權限回呼的 <c>tool_name</c>、<c>input</c> 與 <c>ToolPermissionContext</c>。
/// </summary>
public sealed class ToolPermissionRequest
{
    /// <summary>工具名稱，例如 <c>Write</c>、<c>Bash</c>。</summary>
    public required string ToolName { get; init; }

    /// <summary>工具的參數，原樣來自 CLI。</summary>
    public required JsonElement Input { get; init; }

    /// <summary>
    /// CLI 建議的權限變更。「一律允許」就是把它原樣放進 <see cref="PermissionResultAllow.UpdatedPermissions"/>。
    /// </summary>
    public IReadOnlyList<PermissionUpdate> Suggestions { get; init; } = [];

    /// <summary>這次工具呼叫的 ID；同一則訊息裡的多個工具呼叫各有不同的 ID。</summary>
    public string? ToolUseId { get; init; }

    /// <summary>發出這次呼叫的 subagent；主對話是 <see langword="null"/>。</summary>
    public string? AgentId { get; init; }

    /// <summary>觸發詢問的路徑，例如寫入工作目錄以外的檔案時。</summary>
    public string? BlockedPath { get; init; }

    /// <summary>CLI 說明為什麼需要詢問。</summary>
    public string? DecisionReason { get; init; }

    /// <summary>給人看的標題。</summary>
    public string? Title { get; init; }

    /// <summary>給人看的工具名稱。</summary>
    public string? DisplayName { get; init; }

    /// <summary>給人看的說明。</summary>
    public string? Description { get; init; }
}
