using System.Buffers;
using System.Collections;
using System.Text.Json;

namespace PamisWang.ClaudeAgentSdk.Mcp;

/// <summary>
/// 產生工具參數的 JSON Schema。對應 Python 的 <c>@tool</c> 用 <c>{"a": float, "b": float}</c> 這種「名稱對型別」的簡寫：
/// 產生 <c>{"type":"object","properties":{…},"required":[所有參數]}</c>，每個參數都必填。
/// </summary>
public static class McpSchema
{
    /// <summary>沒有參數的工具：<c>{"type":"object","properties":{},"required":[]}</c>，跟 Python 給 <c>{}</c> 一樣。</summary>
    public static JsonElement Parameters() => Build([]);

    /// <summary>
    /// 每個參數都必填的物件 schema。型別對應跟 Python 的 <c>_python_type_to_json_schema</c> 一樣：
    /// <see cref="string"/> 是 <c>string</c>、整數型別是 <c>integer</c>、<see cref="double"/>／<see cref="float"/>／<see cref="decimal"/> 是 <c>number</c>、
    /// <see cref="bool"/> 是 <c>boolean</c>、字典是 <c>object</c>、陣列與其他集合是 <c>array</c>（有元素型別時加上 <c>items</c>）、
    /// <see cref="Nullable{T}"/> 看裡面的型別；其他型別跟 Python 一樣當成 <c>string</c>。
    /// </summary>
    /// <example><c>McpSchema.Parameters(("a", typeof(double)), ("b", typeof(double)))</c></example>
    public static JsonElement Parameters(params ReadOnlySpan<(string Name, Type Type)> parameters)
    {
        var withoutDescriptions = new (string Name, Type Type, string? Description)[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
        {
            withoutDescriptions[i] = (parameters[i].Name, parameters[i].Type, null);
        }

        return Build(withoutDescriptions);
    }

    /// <summary>
    /// 同 <see cref="Parameters(ReadOnlySpan{ValueTuple{string, Type}})"/>，每個參數另外附上說明（<c>description</c>），
    /// 對應 Python 的 <c>Annotated[type, "說明"]</c>。
    /// </summary>
    public static JsonElement Parameters(params ReadOnlySpan<(string Name, Type Type, string Description)> parameters)
    {
        var withDescriptions = new (string Name, Type Type, string? Description)[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
        {
            withDescriptions[i] = parameters[i];
        }

        return Build(withDescriptions);
    }

    private static JsonElement Build((string Name, Type Type, string? Description)[] parameters)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "object");
            writer.WriteStartObject("properties");
            foreach (var (name, type, description) in parameters)
            {
                ArgumentNullException.ThrowIfNull(name, nameof(parameters));
                ArgumentNullException.ThrowIfNull(type, nameof(parameters));
                writer.WritePropertyName(name);
                WriteType(writer, type, description);
            }

            writer.WriteEndObject();
            writer.WriteStartArray("required");
            foreach (var (name, _, _) in parameters)
            {
                writer.WriteStringValue(name);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    private static void WriteType(Utf8JsonWriter writer, Type type, string? description = null)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        writer.WriteStartObject();
        var itemType = type.IsArray ? type.GetElementType() : null;
        var jsonType = JsonTypeOf(type, ref itemType);
        writer.WriteString("type", jsonType);
        if (itemType is not null)
        {
            writer.WritePropertyName("items");
            WriteType(writer, itemType);
        }

        if (description is not null)
        {
            writer.WriteString("description", description);
        }

        writer.WriteEndObject();
    }

    private static string JsonTypeOf(Type type, ref Type? itemType)
    {
        if (type == typeof(string))
        {
            return "string";
        }

        if (type == typeof(bool))
        {
            return "boolean";
        }

        if (type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(sbyte)
            || type == typeof(uint) || type == typeof(ulong) || type == typeof(ushort) || type == typeof(byte))
        {
            return "integer";
        }

        if (type == typeof(double) || type == typeof(float) || type == typeof(decimal))
        {
            return "number";
        }

        if (typeof(IDictionary).IsAssignableFrom(type) || IsGenericDictionary(type))
        {
            itemType = null;
            return "object";
        }

        if (typeof(IEnumerable).IsAssignableFrom(type))
        {
            // List<T>、IReadOnlyList<T> 這類只有一個型別參數的集合，元素型別就是它
            if (itemType is null && type.IsGenericType && type.GetGenericArguments() is [var argument])
            {
                itemType = argument;
            }

            return "array";
        }

        return "string";
    }

    private static bool IsGenericDictionary(Type type)
    {
        if (!type.IsGenericType)
        {
            return false;
        }

        var definition = type.GetGenericTypeDefinition();
        return definition == typeof(Dictionary<,>) || definition == typeof(IDictionary<,>) || definition == typeof(IReadOnlyDictionary<,>);
    }
}
