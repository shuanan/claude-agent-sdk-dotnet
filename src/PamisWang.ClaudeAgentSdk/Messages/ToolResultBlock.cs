using System.Text.Json;

namespace PamisWang.ClaudeAgentSdk.Messages;

/// <summary>工具的執行結果。</summary>
/// <param name="ToolUseId">對應的 <see cref="ToolUseBlock.Id"/>。</param>
/// <param name="Content">結果內容：一段字串，或內容區塊的陣列（例如文字加圖片）；沒有內容時是 <see langword="null"/>。</param>
/// <param name="IsError">工具是否執行失敗，包括被拒絕；CLI 沒說時是 <see langword="null"/>。</param>
public sealed record ToolResultBlock(string ToolUseId, JsonElement? Content, bool? IsError) : ContentBlock;
