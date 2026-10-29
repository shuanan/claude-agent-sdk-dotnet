using System.Text.Json;

namespace PamisWang.ClaudeAgentSdk.Permissions;

/// <summary>
/// 一項權限變更。對應 Python SDK 的 <c>PermissionUpdate</c>；字串欄位的值跟 CLI 的協定一樣，
/// 用字串而不是 enum，CLI 加了新的值也能原樣帶回。
/// </summary>
/// <param name="Type">
/// <c>addRules</c>、<c>replaceRules</c>、<c>removeRules</c>、<c>setMode</c>、<c>addDirectories</c> 或 <c>removeDirectories</c>。
/// </param>
public sealed record PermissionUpdate(string Type)
{
    /// <summary>規則；用於 <c>addRules</c>、<c>replaceRules</c>、<c>removeRules</c>。</summary>
    public IReadOnlyList<PermissionRuleValue>? Rules { get; init; }

    /// <summary><c>allow</c>、<c>deny</c> 或 <c>ask</c>；用於規則類的變更。</summary>
    public string? Behavior { get; init; }

    /// <summary>
    /// 權限模式：<c>default</c>、<c>acceptEdits</c>、<c>plan</c>、<c>bypassPermissions</c>、<c>dontAsk</c> 或 <c>auto</c>；用於 <c>setMode</c>。
    /// </summary>
    public string? Mode { get; init; }

    /// <summary>資料夾；用於 <c>addDirectories</c>、<c>removeDirectories</c>。</summary>
    public IReadOnlyList<string>? Directories { get; init; }

    /// <summary>
    /// 變更存到哪裡：<c>userSettings</c>、<c>projectSettings</c>、<c>localSettings</c> 或 <c>session</c>（只在這個 session 有效）。
    /// </summary>
    public string? Destination { get; init; }

    /// <summary>從 CLI 的 <c>permission_suggestions</c> 解析。對應 Python 的 <c>from_dict</c>。</summary>
    internal static PermissionUpdate FromJson(JsonElement json) => new(json.GetProperty("type").GetString()!)
    {
        Rules = json.TryGetProperty("rules", out var rules) && rules.ValueKind == JsonValueKind.Array
            ? [.. rules.EnumerateArray().Select(rule => new PermissionRuleValue(
                rule.GetProperty("toolName").GetString()!,
                rule.TryGetProperty("ruleContent", out var content) ? content.GetString() : null))]
            : null,
        Behavior = OptionalString(json, "behavior"),
        Mode = OptionalString(json, "mode"),
        Directories = json.TryGetProperty("directories", out var directories) && directories.ValueKind == JsonValueKind.Array
            ? [.. directories.EnumerateArray().Select(directory => directory.GetString()!)]
            : null,
        Destination = OptionalString(json, "destination"),
    };

    /// <summary>寫成協定的格式。對應 Python 的 <c>to_dict</c>：只寫這個類型用得到的欄位。</summary>
    internal void WriteTo(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteString("type", Type);
        if (Destination is not null)
        {
            writer.WriteString("destination", Destination);
        }

        switch (Type)
        {
            case "addRules" or "replaceRules" or "removeRules":
                if (Rules is not null)
                {
                    writer.WriteStartArray("rules");
                    foreach (var rule in Rules)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("toolName", rule.ToolName);
                        writer.WriteString("ruleContent", rule.RuleContent);
                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                }

                if (Behavior is not null)
                {
                    writer.WriteString("behavior", Behavior);
                }

                break;

            case "setMode":
                if (Mode is not null)
                {
                    writer.WriteString("mode", Mode);
                }

                break;

            case "addDirectories" or "removeDirectories":
                if (Directories is not null)
                {
                    writer.WriteStartArray("directories");
                    foreach (var directory in Directories)
                    {
                        writer.WriteStringValue(directory);
                    }

                    writer.WriteEndArray();
                }

                break;
        }

        writer.WriteEndObject();
    }

    private static string? OptionalString(JsonElement json, string name) =>
        json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
