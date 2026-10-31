using System.Text.Json;
using PamisWang.ClaudeAgentSdk.Protocol;

namespace PamisWang.ClaudeAgentSdk;

/// <summary>一個用量類別。對應 Python 的 <c>ContextUsageCategory</c>。</summary>
/// <param name="Name">類別名稱。</param>
/// <param name="Tokens">用掉的 token 數。</param>
/// <param name="Color">CLI 顯示用的顏色。</param>
/// <param name="IsDeferred">是否延後載入（<c>isDeferred</c>）；CLI 沒送時是 <see langword="null"/>。</param>
public sealed record ContextUsageCategory(string Name, long Tokens, string Color, bool? IsDeferred)
{
    internal static ContextUsageCategory FromJson(JsonElement data) => new(
        ResponseJson.String(data, "name") ?? "",
        ResponseJson.Int64(data, "tokens") ?? 0,
        ResponseJson.String(data, "color") ?? "",
        ResponseJson.Boolean(data, "isDeferred"));
}
