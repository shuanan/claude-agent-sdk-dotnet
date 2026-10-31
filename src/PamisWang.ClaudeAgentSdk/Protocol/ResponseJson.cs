using System.Text.Json;

namespace PamisWang.ClaudeAgentSdk.Protocol;

/// <summary>
/// 寬鬆地讀 control 回應的欄位：Python 的 TypedDict 不檢查內容，這裡也一樣，型別不對或沒有就當作沒有。
/// </summary>
internal static class ResponseJson
{
    public static string? String(JsonElement data, string name) =>
        TryGet(data, name, JsonValueKind.String, out var value) ? value.GetString() : null;

    public static bool? Boolean(JsonElement data, string name) =>
        data.ValueKind == JsonValueKind.Object && data.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            }
            : null;

    public static long? Int64(JsonElement data, string name)
    {
        if (!TryGet(data, name, JsonValueKind.Number, out var value))
        {
            return null;
        }

        return value.TryGetInt64(out var number) ? number : (long)value.GetDouble();
    }

    public static double? Double(JsonElement data, string name) =>
        TryGet(data, name, JsonValueKind.Number, out var value) ? value.GetDouble() : null;

    public static JsonElement? Object(JsonElement data, string name) =>
        TryGet(data, name, JsonValueKind.Object, out var value) ? value : null;

    public static bool HasArray(JsonElement data, string name) => TryGet(data, name, JsonValueKind.Array, out _);

    /// <summary>陣列裡的物件；不是物件的元素略過。</summary>
    public static IEnumerable<JsonElement> Objects(JsonElement data, string name) =>
        TryGet(data, name, JsonValueKind.Array, out var array)
            ? array.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object)
            : [];

    private static bool TryGet(JsonElement data, string name, JsonValueKind kind, out JsonElement value)
    {
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty(name, out value) && value.ValueKind == kind)
        {
            return true;
        }

        value = default;
        return false;
    }
}
