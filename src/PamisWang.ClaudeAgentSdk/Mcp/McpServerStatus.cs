using System.Text.Json;
using PamisWang.ClaudeAgentSdk.Protocol;

namespace PamisWang.ClaudeAgentSdk.Mcp;

/// <summary>一個 MCP server 的連線狀態。對應 Python 的 <c>McpServerStatus</c>。</summary>
public sealed record McpServerStatus
{
    /// <summary>設定裡的 server 名稱。</summary>
    public required string Name { get; init; }

    /// <summary>連線狀態：<c>connected</c>、<c>failed</c>、<c>needs-auth</c>、<c>pending</c> 或 <c>disabled</c>。</summary>
    public required string Status { get; init; }

    /// <summary>MCP 握手時 server 回報的名稱與版本；連上時才有（<c>serverInfo</c>）。</summary>
    public McpServerInfo? ServerInfo { get; init; }

    /// <summary>錯誤訊息；<see cref="Status"/> 是 <c>failed</c> 時才有。</summary>
    public string? Error { get; init; }

    /// <summary>server 的設定（stdio、sse、http、sdk 或 claudeai-proxy，HTTP／SSE 含網址），原樣保留。</summary>
    public JsonElement? Config { get; init; }

    /// <summary>設定的來源範圍，例如 <c>project</c>、<c>user</c>、<c>local</c>、<c>claudeai</c>、<c>managed</c>。</summary>
    public string? Scope { get; init; }

    /// <summary>server 提供的工具；連上時才有。</summary>
    public IReadOnlyList<McpServerToolInfo>? Tools { get; init; }

    /// <summary>這一筆的原始 JSON。</summary>
    public required JsonElement Raw { get; init; }

    internal static McpServerStatus FromJson(JsonElement data) => new()
    {
        Name = ResponseJson.String(data, "name") ?? "",
        Status = ResponseJson.String(data, "status") ?? "",
        ServerInfo = ResponseJson.Object(data, "serverInfo") is { } info
            ? new McpServerInfo(ResponseJson.String(info, "name") ?? "", ResponseJson.String(info, "version") ?? "")
            : null,
        Error = ResponseJson.String(data, "error"),
        Config = ResponseJson.Object(data, "config"),
        Scope = ResponseJson.String(data, "scope"),
        Tools = ResponseJson.HasArray(data, "tools")
            ? ResponseJson.Objects(data, "tools").Select(McpServerToolInfo.FromJson).ToList()
            : null,
        Raw = data,
    };
}
