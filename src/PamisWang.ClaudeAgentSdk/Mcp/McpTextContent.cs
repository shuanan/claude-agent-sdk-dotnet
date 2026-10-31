using System.Text.Json;

namespace PamisWang.ClaudeAgentSdk.Mcp;

/// <summary>一段文字（<c>{"type":"text","text":…}</c>）。</summary>
/// <param name="Text">文字內容。</param>
public sealed record McpTextContent(string Text) : McpContent
{
    internal override void WriteTo(Utf8JsonWriter writer) => WriteText(writer, Text);
}
