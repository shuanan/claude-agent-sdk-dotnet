using System.Text.Json;

namespace PamisWang.ClaudeAgentSdk.Messages;

/// <summary>模型要呼叫工具。</summary>
/// <param name="Id">這次呼叫的 ID，對應結果的 <see cref="ToolResultBlock.ToolUseId"/>。</param>
/// <param name="Name">工具名稱。</param>
/// <param name="Input">工具的參數。</param>
public sealed record ToolUseBlock(string Id, string Name, JsonElement Input) : ContentBlock;
