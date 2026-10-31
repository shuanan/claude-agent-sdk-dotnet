using System.Text.Json;
using PamisWang.ClaudeAgentSdk.Protocol;

namespace PamisWang.ClaudeAgentSdk.Mcp;

/// <summary>
/// <see cref="ClaudeAgentClient.GetMcpStatusAsync"/> 的結果：每個 MCP server 的連線狀態。對應 Python 的 <c>McpStatusResponse</c>。
/// 欄位照 CLI 送來的讀，缺的欄位是預設值；完整內容在 <see cref="Raw"/>。
/// </summary>
public sealed record McpStatusResponse
{
    /// <summary>各 server 的狀態（<c>mcpServers</c>）。</summary>
    public required IReadOnlyList<McpServerStatus> McpServers { get; init; }

    /// <summary>CLI 回應的原始 JSON。</summary>
    public required JsonElement Raw { get; init; }

    internal static McpStatusResponse FromJson(JsonElement data) => new()
    {
        McpServers = ResponseJson.Objects(data, "mcpServers").Select(McpServerStatus.FromJson).ToList(),
        Raw = data,
    };
}
