using System.Text.Json;

namespace PamisWang.ClaudeAgentSdk.Mcp;

/// <summary>
/// 一個 MCP server 的設定：由 Claude Code 自己啟動或連線的 <see cref="McpStdioServerConfig"/>、<see cref="McpSseServerConfig"/>、<see cref="McpHttpServerConfig"/>，
/// 或在這個程序裡跑的 <see cref="McpSdkServerConfig"/>。對應 Python SDK 的 <c>McpServerConfig</c>。
/// </summary>
public abstract record McpServerConfig
{
    private protected McpServerConfig()
    {
    }

    /// <summary>寫成 <c>--mcp-config</c> 裡 <c>mcpServers</c> 的一個值，鍵名跟 Python 的 TypedDict 一樣。</summary>
    internal abstract void WriteTo(Utf8JsonWriter writer);
}
