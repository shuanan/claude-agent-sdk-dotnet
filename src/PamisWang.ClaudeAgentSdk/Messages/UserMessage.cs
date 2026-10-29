using System.Text.Json;

namespace PamisWang.ClaudeAgentSdk.Messages;

/// <summary>
/// 使用者這一側的訊息（<c>type: user</c>）：主要是工具執行結果，也包括 CLI 自己補上的文字，例如中斷時的提示。
/// </summary>
public sealed record UserMessage : ClaudeMessage
{
    /// <summary>內容區塊。原始內容是一段字串時，這裡是一個 <see cref="TextBlock"/>。</summary>
    public required IReadOnlyList<ContentBlock> Content { get; init; }

    /// <summary>subagent 的訊息是呼叫它的那個工具的 ID；主對話是 <see langword="null"/>。</summary>
    public string? ParentToolUseId { get; init; }

    /// <summary>工具執行結果的結構化內容，依工具而不同，原樣保留。</summary>
    public JsonElement? ToolUseResult { get; init; }
}
