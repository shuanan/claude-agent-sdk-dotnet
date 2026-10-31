using System.Text.Json;
using System.Text.Json.Nodes;
using PamisWang.ClaudeAgentSdk.Protocol;

namespace PamisWang.ClaudeAgentSdk;

/// <summary>
/// 自訂的 subagent。對應 Python SDK 的 <c>AgentDefinition</c>；不是命令列參數，在初始化握手的 <c>agents</c> 裡送出。
/// <see langword="null"/> 的欄位不會送出，由 Claude Code 用預設。
/// </summary>
/// <param name="Description">什麼時候該用這個 agent，給主 agent 判斷。</param>
/// <param name="Prompt">這個 agent 的系統提示詞。</param>
public sealed record AgentDefinition(string Description, string Prompt)
{
    /// <summary>可以用的工具；<see langword="null"/> 沿用主 agent 的。</summary>
    public IReadOnlyList<string>? Tools { get; init; }

    /// <summary>停用的工具。</summary>
    public IReadOnlyList<string>? DisallowedTools { get; init; }

    /// <summary>模型：<c>sonnet</c>、<c>opus</c>、<c>haiku</c>、<c>inherit</c> 或完整的模型 ID。</summary>
    public string? Model { get; init; }

    /// <summary>可以用的 skill 名稱。</summary>
    public IReadOnlyList<string>? Skills { get; init; }

    /// <summary>記憶存在哪一層：<c>user</c>、<c>project</c> 或 <c>local</c>。</summary>
    public string? Memory { get; init; }

    /// <summary>
    /// MCP server：每一項是 server 名稱（字串），或 <c>{名稱: 設定}</c> 的物件。
    /// 字串可以直接放進清單，例如 <c>["github", new JsonObject { … }]</c>。
    /// </summary>
    public IReadOnlyList<JsonNode?>? McpServers { get; init; }

    /// <summary>agent 啟動時先送出的第一句話。</summary>
    public string? InitialPrompt { get; init; }

    /// <summary>最多來回幾次。</summary>
    public int? MaxTurns { get; init; }

    /// <summary>是否在背景執行。</summary>
    public bool? Background { get; init; }

    /// <summary>思考的深度：<c>low</c>、<c>medium</c>、<c>high</c>、<c>xhigh</c>、<c>max</c>，或整數。字串與整數都可以直接指定。</summary>
    public JsonNode? Effort { get; init; }

    /// <summary>權限模式：<c>default</c>、<c>acceptEdits</c>、<c>plan</c>、<c>bypassPermissions</c>、<c>dontAsk</c> 或 <c>auto</c>。</summary>
    public string? PermissionMode { get; init; }

    /// <summary>寫成 initialize 裡 <c>agents</c> 的一個值。鍵名與順序跟 Python 的 <c>asdict()</c> 一樣，省略 <see langword="null"/>。</summary>
    internal void WriteTo(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteString("description", Description);
        writer.WriteString("prompt", Prompt);
        writer.WriteOptional("tools", Tools);
        writer.WriteOptional("disallowedTools", DisallowedTools);
        writer.WriteOptional("model", Model);
        writer.WriteOptional("skills", Skills);
        writer.WriteOptional("memory", Memory);
        if (McpServers is not null)
        {
            writer.WriteStartArray("mcpServers");
            foreach (var server in McpServers)
            {
                if (server is null)
                {
                    writer.WriteNullValue();
                }
                else
                {
                    server.WriteTo(writer);
                }
            }

            writer.WriteEndArray();
        }

        writer.WriteOptional("initialPrompt", InitialPrompt);
        writer.WriteOptional("maxTurns", MaxTurns);
        writer.WriteOptional("background", Background);
        if (Effort is not null)
        {
            writer.WritePropertyName("effort");
            Effort.WriteTo(writer);
        }

        writer.WriteOptional("permissionMode", PermissionMode);
        writer.WriteEndObject();
    }
}
