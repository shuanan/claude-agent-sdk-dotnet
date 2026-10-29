using System.Text.Json;

namespace PamisWang.ClaudeAgentSdk;

/// <summary>認得的訊息型別缺少必要欄位或格式不對。不認得的型別不會丟這個例外，而是成為 <c>UnknownMessage</c>。</summary>
public sealed class MessageParseException : ClaudeAgentException
{
    /// <summary>建立例外。</summary>
    public MessageParseException()
    {
    }

    /// <summary>以訊息建立例外。</summary>
    public MessageParseException(string message)
        : base(message)
    {
    }

    /// <summary>以訊息與內部例外建立例外。</summary>
    public MessageParseException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    /// <summary>以訊息、解析不了的原始資料與內部例外建立例外。</summary>
    public MessageParseException(string message, JsonElement raw, Exception? innerException = null)
        : base(message, innerException)
    {
        Raw = raw;
    }

    /// <summary>解析不了的那則原始訊息。</summary>
    public JsonElement? Raw { get; }
}
