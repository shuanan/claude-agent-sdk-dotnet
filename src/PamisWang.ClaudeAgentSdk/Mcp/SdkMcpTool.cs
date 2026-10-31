using System.Text.Json;

namespace PamisWang.ClaudeAgentSdk.Mcp;

/// <summary>
/// 在這個程序裡執行的 MCP 工具。對應 Python SDK 的 <c>SdkMcpTool</c> 與 <c>@tool</c> 裝飾器；用 <see cref="Create(string, string, JsonElement, Func{JsonElement, CancellationToken, ValueTask{McpToolResult}}, McpToolAnnotations?)"/> 建立。
/// CLI 送來的參數會先用 <see cref="InputSchema"/> 驗證，不合格時不呼叫 <see cref="Handler"/>，直接回給 Claude 一個出錯的結果。
/// </summary>
/// <param name="Name">工具名稱，Claude 用這個名字呼叫；完整名稱是 <c>mcp__&lt;server 鍵&gt;__&lt;Name&gt;</c>。</param>
/// <param name="Description">工具在做什麼，讓 Claude 判斷何時用。</param>
/// <param name="InputSchema">參數的 JSON Schema，原樣出現在 <c>tools/list</c> 的 <c>inputSchema</c>。簡單的物件可以用 <see cref="McpSchema.Parameters(ReadOnlySpan{ValueTuple{string, Type}})"/> 產生。</param>
/// <param name="Handler">
/// 執行工具：收到參數物件，回傳 <see cref="McpToolResult"/>。丟出的例外跟 Python 一樣變成出錯的結果（訊息是例外的 <see cref="Exception.Message"/>），不會讓請求失敗。
/// CancellationToken 在 CLI 撤回這次呼叫或 client 關閉時取消。
/// </param>
public sealed record SdkMcpTool(
    string Name,
    string Description,
    JsonElement InputSchema,
    Func<JsonElement, CancellationToken, ValueTask<McpToolResult>> Handler)
{
    /// <summary>給 Claude 的提示（唯讀、破壞性等），以及 Claude Code 的 <see cref="McpToolAnnotations.MaxResultSizeChars"/>。</summary>
    public McpToolAnnotations? Annotations { get; init; }

    /// <summary>建立工具。</summary>
    /// <exception cref="ArgumentException"><paramref name="inputSchema"/> 不是 JSON 物件。</exception>
    public static SdkMcpTool Create(
        string name,
        string description,
        JsonElement inputSchema,
        Func<JsonElement, CancellationToken, ValueTask<McpToolResult>> handler,
        McpToolAnnotations? annotations = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(handler);
        if (inputSchema.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("inputSchema 要是 JSON Schema 物件，例如 {\"type\":\"object\",\"properties\":{}}。", nameof(inputSchema));
        }

        return new SdkMcpTool(name, description, inputSchema.Clone(), handler) { Annotations = annotations };
    }

    /// <summary>建立工具；處理函式不需要 CancellationToken。</summary>
    /// <exception cref="ArgumentException"><paramref name="inputSchema"/> 不是 JSON 物件。</exception>
    public static SdkMcpTool Create(
        string name,
        string description,
        JsonElement inputSchema,
        Func<JsonElement, ValueTask<McpToolResult>> handler,
        McpToolAnnotations? annotations = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Create(name, description, inputSchema, (args, _) => handler(args), annotations);
    }
}
