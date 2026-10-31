using System.Text.Json;

namespace PamisWang.ClaudeAgentSdk.Mcp;

/// <summary>一張圖片（<c>{"type":"image","data":…,"mimeType":…}</c>）。</summary>
/// <param name="Data">Base64 編碼的圖片內容。</param>
/// <param name="MimeType">圖片格式，例如 <c>image/png</c>。</param>
public sealed record McpImageContent(string Data, string MimeType) : McpContent
{
    internal override void WriteTo(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteString("type", "image");
        writer.WriteString("data", Data);
        writer.WriteString("mimeType", MimeType);
        writer.WriteEndObject();
    }
}
