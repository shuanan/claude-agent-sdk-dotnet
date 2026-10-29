using System.Text.Json;

namespace PamisWang.ClaudeAgentSdk.Messages;

/// <summary>訊息裡的一個內容區塊。不認得的區塊是 <see cref="UnknownContentBlock"/>，不會被丟掉。</summary>
public abstract record ContentBlock
{
    private protected ContentBlock()
    {
    }

    /// <summary>原始 JSON；沒有對應屬性的欄位從這裡讀。自己建立的區塊是 <see langword="default"/>。</summary>
    public JsonElement Raw { get; init; }
}
