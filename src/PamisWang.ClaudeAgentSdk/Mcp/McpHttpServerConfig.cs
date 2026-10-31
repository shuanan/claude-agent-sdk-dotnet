using System.Text.Json;
using PamisWang.ClaudeAgentSdk.Protocol;

namespace PamisWang.ClaudeAgentSdk.Mcp;

/// <summary>用 Streamable HTTP 連線的遠端 MCP server。對應 Python SDK 的 <c>McpHttpServerConfig</c>。</summary>
/// <param name="Url">server 的網址。</param>
public sealed record McpHttpServerConfig(string Url) : McpServerConfig
{
    /// <summary>連線時附上的 HTTP 標頭。</summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    internal override void WriteTo(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteString("type", "http");
        writer.WriteString("url", Url);
        writer.WriteOptional("headers", Headers);
        writer.WriteEndObject();
    }
}
