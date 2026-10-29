using System.Text.Json;

namespace PamisWang.ClaudeAgentSdk.Messages;

/// <summary>API 端工具的結果。跟 Python SDK 一樣，目前只對應 <c>advisor_tool_result</c>；其他種類是 <see cref="UnknownContentBlock"/>。</summary>
/// <param name="ToolUseId">對應的 <see cref="ServerToolUseBlock.Id"/>。</param>
/// <param name="Content">結果內容，原樣保留。</param>
public sealed record ServerToolResultBlock(string ToolUseId, JsonElement Content) : ContentBlock;
