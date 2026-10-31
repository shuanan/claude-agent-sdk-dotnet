using System.Text.Json;

namespace PamisWang.ClaudeAgentSdk.Hooks;

/// <summary>讀 hook 輸入的小工具：欄位不存在或型別不對時當作沒給，不丟例外。</summary>
internal static class HookJson
{
    public static readonly JsonElement EmptyObject = CreateEmptyObject();

    public static string? String(JsonElement element, string name) =>
        TryGet(element, name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public static bool? Bool(JsonElement element, string name) =>
        TryGet(element, name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    public static long? Long(JsonElement element, string name) =>
        TryGet(element, name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            ? number
            : null;

    public static JsonElement? Object(JsonElement element, string name) =>
        TryGet(element, name, out var value) && value.ValueKind == JsonValueKind.Object ? value : null;

    public static IReadOnlyList<T>? List<T>(JsonElement element, string name, Func<JsonElement, T> convert) =>
        TryGet(element, name, out var value) && value.ValueKind == JsonValueKind.Array
            ? [.. value.EnumerateArray().Select(convert)]
            : null;

    private static bool TryGet(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value))
        {
            return true;
        }

        value = default;
        return false;
    }

    private static JsonElement CreateEmptyObject()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }
}
