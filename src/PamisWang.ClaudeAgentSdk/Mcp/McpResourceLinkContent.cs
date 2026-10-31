using System.Text.Json;

namespace PamisWang.ClaudeAgentSdk.Mcp;

/// <summary>
/// 指向一個資源的連結。CLI 只顯示文字，所以跟 Python 一樣送成一段文字：名稱、URI、說明中有值的各佔一行，都沒有時是 <c>Resource link</c>。
/// </summary>
/// <param name="Name">資源名稱。</param>
/// <param name="Uri">資源的 URI。</param>
public sealed record McpResourceLinkContent(string? Name, string? Uri) : McpContent
{
    /// <summary>資源的說明。</summary>
    public string? Description { get; init; }

    internal override void WriteTo(Utf8JsonWriter writer)
    {
        string[] parts = [.. new[] { Name, Uri, Description }.Where(part => !string.IsNullOrEmpty(part)).Select(part => part!)];
        WriteText(writer, parts.Length > 0 ? string.Join('\n', parts) : "Resource link");
    }
}
