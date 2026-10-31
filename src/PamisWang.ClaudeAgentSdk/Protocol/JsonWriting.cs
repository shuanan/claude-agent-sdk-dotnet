using System.Buffers;
using System.Text;
using System.Text.Json;

namespace PamisWang.ClaudeAgentSdk.Protocol;

/// <summary>
/// 寫 JSON 的小工具：選項轉成 CLI 參數或 control request 時，<see langword="null"/> 的欄位不寫，跟 Python 省略 <c>None</c> 一樣。
/// 不用反射序列化，AOT 也能用。
/// </summary>
internal static class JsonWriting
{
    /// <summary>寫成一行緊湊的 JSON。</summary>
    public static string ToCompactString(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            write(writer);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static void WriteOptional(this Utf8JsonWriter writer, string name, string? value)
    {
        if (value is not null)
        {
            writer.WriteString(name, value);
        }
    }

    public static void WriteOptional(this Utf8JsonWriter writer, string name, bool? value)
    {
        if (value is { } flag)
        {
            writer.WriteBoolean(name, flag);
        }
    }

    public static void WriteOptional(this Utf8JsonWriter writer, string name, int? value)
    {
        if (value is { } number)
        {
            writer.WriteNumber(name, number);
        }
    }

    public static void WriteOptional(this Utf8JsonWriter writer, string name, IReadOnlyList<string>? values)
    {
        if (values is null)
        {
            return;
        }

        writer.WriteStartArray(name);
        foreach (var value in values)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }

    public static void WriteOptional(this Utf8JsonWriter writer, string name, IReadOnlyDictionary<string, string>? values)
    {
        if (values is null)
        {
            return;
        }

        writer.WriteStartObject(name);
        foreach (var (key, value) in values)
        {
            writer.WriteString(key, value);
        }

        writer.WriteEndObject();
    }
}
