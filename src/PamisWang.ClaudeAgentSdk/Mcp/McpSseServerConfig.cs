using System.Text.Json;
using PamisWang.ClaudeAgentSdk.Protocol;

namespace PamisWang.ClaudeAgentSdk.Mcp;

/// <summary>用 Server-Sent Events 連線的遠端 MCP server。對應 Python SDK 的 <c>McpSSEServerConfig</c>。</summary>
/// <param name="Url">server 的網址。</param>
public sealed record McpSseServerConfig(string Url) : McpServerConfig
{
    /// <summary>連線時附上的 HTTP 標頭。</summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    internal override void WriteTo(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteString("type", "sse");
        writer.WriteString("url", Url);
        writer.WriteOptional("headers", Headers);
        writer.WriteEndObject();
    }
}
