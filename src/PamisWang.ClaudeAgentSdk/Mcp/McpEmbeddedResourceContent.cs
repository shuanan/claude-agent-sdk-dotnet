using System.Text.Json;

namespace PamisWang.ClaudeAgentSdk.Mcp;

/// <summary>
/// 內嵌在結果裡的資源。跟 Python 一樣：有 <see cref="Text"/> 就送成那段文字；只有 <see cref="Blob"/> 的二進位資源 CLI 無法顯示，略過不送。
/// </summary>
/// <param name="Uri">資源的 URI。</param>
public sealed record McpEmbeddedResourceContent(string Uri) : McpContent
{
    /// <summary>文字資源的內容。</summary>
    public string? Text { get; init; }

    /// <summary>二進位資源的內容（Base64）。</summary>
    public string? Blob { get; init; }

    /// <summary>資源格式。</summary>
    public string? MimeType { get; init; }

    internal override void WriteTo(Utf8JsonWriter writer)
    {
        if (Text is not null)
        {
            WriteText(writer, Text);
        }
    }
}
