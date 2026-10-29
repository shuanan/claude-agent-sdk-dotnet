using System.Text.Json;

namespace PamisWang.ClaudeAgentSdk.Messages;

/// <summary>一次被拒絕的工具呼叫，列在 <see cref="ResultMessage.PermissionDenials"/>。</summary>
/// <param name="ToolName">工具名稱。</param>
/// <param name="ToolUseId">工具呼叫的 ID。</param>
/// <param name="ToolInput">工具的參數。</param>
public sealed record PermissionDenial(string ToolName, string ToolUseId, JsonElement ToolInput);
