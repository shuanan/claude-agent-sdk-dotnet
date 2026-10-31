namespace PamisWang.ClaudeAgentSdk.Mcp;

/// <summary>建立在這個程序裡跑的 MCP server。對應 Python SDK 的 <c>create_sdk_mcp_server()</c>。</summary>
/// <example>
/// <code>
/// var add = SdkMcpTool.Create(
///     "add",
///     "把兩個數字相加",
///     McpSchema.Parameters(("a", typeof(double)), ("b", typeof(double))),
///     args => ValueTask.FromResult(McpToolResult.Text($"{args.GetProperty("a").GetDouble() + args.GetProperty("b").GetDouble()}")));
///
/// var options = new ClaudeAgentOptions
/// {
///     McpServers = new Dictionary&lt;string, McpServerConfig&gt; { ["calc"] = SdkMcpServer.Create("calculator", add) },
///     AllowedTools = ["mcp__calc__add"],
/// };
/// </code>
/// </example>
public static class SdkMcpServer
{
    /// <summary>建立版本是 <c>1.0.0</c> 的 server。</summary>
    /// <param name="name">server 名稱，握手時回報給 CLI。</param>
    /// <param name="tools">這個 server 提供的工具；沒有工具也可以，但很少有用。</param>
    public static McpSdkServerConfig Create(string name, params IEnumerable<SdkMcpTool> tools) =>
        Create(name, "1.0.0", tools);

    /// <summary>建立 server。</summary>
    /// <param name="name">server 名稱，握手時回報給 CLI。</param>
    /// <param name="version">server 版本，只是資訊，不影響功能。</param>
    /// <param name="tools">這個 server 提供的工具。</param>
    public static McpSdkServerConfig Create(string name, string version, params IEnumerable<SdkMcpTool> tools)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(tools);
        SdkMcpTool[] list = [.. tools];
        foreach (var tool in list)
        {
            ArgumentNullException.ThrowIfNull(tool, nameof(tools));
        }

        return new McpSdkServerConfig(name, version, list);
    }
}
