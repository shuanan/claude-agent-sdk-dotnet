namespace PamisWang.ClaudeAgentSdk.Mcp;

/// <summary>MCP 握手時 server 回報的資訊。對應 Python 的 <c>McpServerInfo</c>。</summary>
/// <param name="Name">server 名稱。</param>
/// <param name="Version">server 版本。</param>
public sealed record McpServerInfo(string Name, string Version);
