using System.Text.Json;

namespace PamisWang.ClaudeAgentSdk.Permissions;

/// <summary>允許使用工具。</summary>
/// <param name="UpdatedInput">改過的工具參數；<see langword="null"/> 表示照原本的參數執行。</param>
/// <param name="UpdatedPermissions">
/// 同時套用的權限變更，例如把 <see cref="ToolPermissionRequest.Suggestions"/> 原樣帶回，之後同類的操作就不再詢問。
/// </param>
public sealed record PermissionResultAllow(
    JsonElement? UpdatedInput = null,
    IReadOnlyList<PermissionUpdate>? UpdatedPermissions = null) : PermissionResult;
