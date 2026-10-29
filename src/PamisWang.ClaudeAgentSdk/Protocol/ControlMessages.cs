using System.Buffers;
using System.Text;
using System.Text.Json;

namespace PamisWang.ClaudeAgentSdk.Protocol;

/// <summary>
/// SDK 寫進 CLI stdin 的訊息，每個方法回傳一整行（含結尾換行）。欄位與順序跟 Python SDK 送出的一樣。
/// </summary>
internal static class ControlMessages
{
    /// <summary>SDK 發給 CLI 的 control request。</summary>
    public static string Request(string requestId, string subtype, Action<Utf8JsonWriter>? writeFields = null) =>
        Line(writer =>
        {
            writer.WriteString("type", "control_request");
            writer.WriteString("request_id", requestId);
            writer.WriteStartObject("request");
            writer.WriteString("subtype", subtype);
            writeFields?.Invoke(writer);
            writer.WriteEndObject();
        });

    /// <summary>回覆 CLI 的 control request：成功。</summary>
    public static string Success(string requestId, Action<Utf8JsonWriter> writeResponseFields) =>
        Response(requestId, "success", writer =>
        {
            writer.WriteStartObject("response");
            writeResponseFields(writer);
            writer.WriteEndObject();
        });

    /// <summary>回覆 CLI 的 control request：處理失敗。</summary>
    public static string Error(string requestId, string error) =>
        Response(requestId, "error", writer => writer.WriteString("error", error));

    /// <summary>使用者說的一句話。</summary>
    public static string UserMessage(string content, string sessionId) =>
        Line(writer =>
        {
            writer.WriteString("type", "user");
            writer.WriteStartObject("message");
            writer.WriteString("role", "user");
            writer.WriteString("content", content);
            writer.WriteEndObject();
            writer.WriteNull("parent_tool_use_id");
            writer.WriteString("session_id", sessionId);
        });

    private static string Response(string requestId, string subtype, Action<Utf8JsonWriter> writeFields) =>
        Line(writer =>
        {
            writer.WriteString("type", "control_response");
            writer.WriteStartObject("response");
            writer.WriteString("subtype", subtype);
            writer.WriteString("request_id", requestId);
            writeFields(writer);
            writer.WriteEndObject();
        });

    private static string Line(Action<Utf8JsonWriter> writeFields)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writeFields(writer);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan) + "\n";
    }
}
