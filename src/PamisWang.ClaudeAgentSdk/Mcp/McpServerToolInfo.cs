using System.Text.Json;
using PamisWang.ClaudeAgentSdk.Protocol;

namespace PamisWang.ClaudeAgentSdk.Mcp;

/// <summary>MCP server 提供的一個工具。對應 Python 的 <c>McpToolInfo</c>。</summary>
public sealed record McpServerToolInfo
{
    /// <summary>工具名稱。</summary>
    public required string Name { get; init; }

    /// <summary>工具說明。</summary>
    public string? Description { get; init; }

    /// <summary>工具的提示資訊；沒有時是 <see langword="null"/>。</summary>
    public McpServerToolAnnotations? Annotations { get; init; }

    internal static McpServerToolInfo FromJson(JsonElement data) => new()
    {
        Name = ResponseJson.String(data, "name") ?? "",
        Description = ResponseJson.String(data, "description"),
        Annotations = ResponseJson.Object(data, "annotations") is { } annotations
            ? new McpServerToolAnnotations(
                ResponseJson.Boolean(annotations, "readOnly"),
                ResponseJson.Boolean(annotations, "destructive"),
                ResponseJson.Boolean(annotations, "openWorld"))
            : null,
    };
}
