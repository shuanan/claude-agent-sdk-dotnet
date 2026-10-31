using System.Text.Json;

namespace PamisWang.ClaudeAgentSdk.Mcp;

/// <summary>
/// 工具結果裡的一段內容：<see cref="McpTextContent"/>、<see cref="McpImageContent"/>、<see cref="McpResourceLinkContent"/> 或 <see cref="McpEmbeddedResourceContent"/>。
/// 送出時的轉換跟 Python 的 <c>_convert_tool_content</c> 一樣：文字與圖片原樣送，資源連結與文字資源攤平成文字，二進位資源略過。
/// </summary>
public abstract record McpContent
{
    private protected McpContent()
    {
    }

    /// <summary>寫成 MCP 的內容項目；這種內容 CLI 不顯示時什麼都不寫。</summary>
    internal abstract void WriteTo(Utf8JsonWriter writer);

    private protected static void WriteText(Utf8JsonWriter writer, string text)
    {
        writer.WriteStartObject();
        writer.WriteString("type", "text");
        writer.WriteString("text", text);
        writer.WriteEndObject();
    }
}
