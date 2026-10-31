using System.Text.Json;

namespace PamisWang.ClaudeAgentSdk.Mcp;

/// <summary>
/// 在這個程序裡跑的 MCP server（SDK MCP server）：工具就是 C# 的委派，CLI 透過 control 協定的 <c>mcp_message</c> 呼叫，不另外開程序。
/// 對應 Python SDK 的 <c>McpSdkServerConfig</c>；通常用 <see cref="SdkMcpServer.Create(string, IEnumerable{SdkMcpTool})"/> 建立。
/// 放進 <see cref="ClaudeAgentOptions.McpServers"/> 時，CLI 用字典的鍵稱呼這個 server，工具的完整名稱是 <c>mcp__&lt;鍵&gt;__&lt;工具名稱&gt;</c>，
/// 要讓 Claude 不經詢問就能用，把這個名稱放進 <see cref="ClaudeAgentOptions.AllowedTools"/>。
/// </summary>
/// <param name="Name">server 名稱，握手時回報給 CLI（<c>serverInfo.name</c>）。</param>
/// <param name="Version">server 版本，只是資訊，不影響功能。</param>
/// <param name="Tools">這個 server 提供的工具。</param>
public sealed record McpSdkServerConfig(string Name, string Version, IReadOnlyList<SdkMcpTool> Tools) : McpServerConfig
{
    internal override void WriteTo(Utf8JsonWriter writer)
    {
        // 跟 Python 一樣只給 type 與 name：工具在這個程序裡，CLI 不需要也拿不到
        writer.WriteStartObject();
        writer.WriteString("type", "sdk");
        writer.WriteString("name", Name);
        writer.WriteEndObject();
    }
}
