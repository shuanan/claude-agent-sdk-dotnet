namespace PamisWang.ClaudeAgentSdk.Mcp;

/// <summary>
/// 工具的執行結果，對應 Python 工具回傳的 <c>{"content": [...], "is_error": ...}</c>。送給 CLI 時是 MCP 的 <c>{"content":[...],"isError":...}</c>。
/// </summary>
/// <param name="Content">結果內容，依序送出。</param>
public sealed record McpToolResult(IReadOnlyList<McpContent> Content)
{
    /// <summary>這是出錯的結果；Claude 看得到內容，知道工具失敗了。</summary>
    public bool IsError { get; init; }

    /// <summary>只有一段文字的結果。</summary>
    public static McpToolResult Text(string text) => new([new McpTextContent(text)]);

    /// <summary>只有一段文字的出錯結果。</summary>
    public static McpToolResult Error(string message) => new([new McpTextContent(message)]) { IsError = true };
}
