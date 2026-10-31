using System.Text.Json;

namespace PamisWang.ClaudeAgentSdk.Mcp;

/// <summary>
/// 一個 MCP server 的設定：<see cref="McpStdioServerConfig"/>、<see cref="McpSseServerConfig"/> 或 <see cref="McpHttpServerConfig"/>，
/// 由 Claude Code 自己啟動或連線。對應 Python SDK 的 <c>McpServerConfig</c>。
/// SDK 內的 MCP server（Python 的 <c>type: sdk</c>）還不支援；之後加的時候也是這個型別的子類別。
/// </summary>
public abstract record McpServerConfig
{
    private protected McpServerConfig()
    {
    }

    /// <summary>寫成 <c>--mcp-config</c> 裡 <c>mcpServers</c> 的一個值，鍵名跟 Python 的 TypedDict 一樣。</summary>
    internal abstract void WriteTo(Utf8JsonWriter writer);
}
