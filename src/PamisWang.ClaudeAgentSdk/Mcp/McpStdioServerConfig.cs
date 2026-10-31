using System.Text.Json;
using PamisWang.ClaudeAgentSdk.Protocol;

namespace PamisWang.ClaudeAgentSdk.Mcp;

/// <summary>由 Claude Code 啟動的子程序，用 stdin／stdout 溝通。對應 Python SDK 的 <c>McpStdioServerConfig</c>。</summary>
/// <param name="Command">要執行的指令。</param>
public sealed record McpStdioServerConfig(string Command) : McpServerConfig
{
    /// <summary>指令的參數。</summary>
    public IReadOnlyList<string>? Args { get; init; }

    /// <summary>給這個程序的環境變數。</summary>
    public IReadOnlyDictionary<string, string>? Env { get; init; }

    internal override void WriteTo(Utf8JsonWriter writer)
    {
        // Python 的 type 可以省略；這裡一律寫出來，CLI 兩種都接受
        writer.WriteStartObject();
        writer.WriteString("type", "stdio");
        writer.WriteString("command", Command);
        writer.WriteOptional("args", Args);
        writer.WriteOptional("env", Env);
        writer.WriteEndObject();
    }
}
