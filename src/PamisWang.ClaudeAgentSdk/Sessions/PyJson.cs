using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PamisWang.ClaudeAgentSdk.Sessions;

/// <summary>
/// 跟 Python <c>json</c> 模組一樣地讀寫 JSON，session 檔案才會跟 Python SDK 寫出的逐位元組相同：
/// <c>json.loads</c>（重複的鍵以最後一個為準、位置留在第一次出現的地方）、
/// <c>json.dumps</c>（<c>ensure_ascii</c>、兩種分隔符號、Python 的浮點數寫法），以及 Python 的真假值與相等。
/// 不用反射序列化，AOT 也能用。
/// </summary>
internal static class PyJson
{
    private static readonly JsonDocumentOptions DocumentOptions = new() { MaxDepth = 1000 };

    /// <summary>Python 的 <c>json.loads</c>；解析失敗回傳 <see langword="false"/>（對應 <c>JSONDecodeError</c>）。</summary>
    public static bool TryLoads(string text, out JsonNode? node)
    {
        try
        {
            using var document = JsonDocument.Parse(text, DocumentOptions);
            node = ToNode(document.RootElement);
            return true;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentException)
        {
            node = null;
            return false;
        }
    }

    /// <summary>解析成 JSON 物件；不是合法 JSON 或不是物件時回傳 <see langword="null"/>。</summary>
    public static JsonObject? LoadsObject(string text) => TryLoads(text, out var node) ? node as JsonObject : null;

    /// <summary>把 <see cref="JsonElement"/> 轉成可以修改的 <see cref="JsonNode"/>（重複的鍵跟 <see cref="TryLoads"/> 一樣處理）。</summary>
    public static JsonNode? FromElement(JsonElement element) => ToNode(element);

    private static JsonNode? ToNode(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var obj = new JsonObject();
                foreach (var property in element.EnumerateObject())
                {
                    // Python dict：重複的鍵取最後的值，位置不變
                    obj[property.Name] = ToNode(property.Value);
                }

                return obj;
            case JsonValueKind.Array:
                var array = new JsonArray();
                foreach (var item in element.EnumerateArray())
                {
                    array.Add(ToNode(item));
                }

                return array;
            case JsonValueKind.String:
                return JsonValue.Create(element.GetString()!);
            case JsonValueKind.Number:
                return JsonValue.Create(element.Clone());
            case JsonValueKind.True:
                return JsonValue.Create(true);
            case JsonValueKind.False:
                return JsonValue.Create(false);
            default:
                return null;
        }
    }

    /// <summary>Python 的 <c>json.dumps(value)</c>；<paramref name="compact"/> 是 <c>separators=(",", ":")</c>，否則是預設的 <c>", "</c>、<c>": "</c>。</summary>
    public static string Dumps(JsonNode? node, bool compact = true)
    {
        var builder = new StringBuilder();
        Write(builder, node, compact);
        return builder.ToString();
    }

    private static void Write(StringBuilder builder, JsonNode? node, bool compact)
    {
        switch (node)
        {
            case null:
                builder.Append("null");
                return;
            case JsonObject obj:
                builder.Append('{');
                var firstProperty = true;
                foreach (var (key, value) in obj)
                {
                    if (!firstProperty)
                    {
                        builder.Append(compact ? "," : ", ");
                    }

                    firstProperty = false;
                    WriteString(builder, key);
                    builder.Append(compact ? ":" : ": ");
                    Write(builder, value, compact);
                }

                builder.Append('}');
                return;
            case JsonArray array:
                builder.Append('[');
                for (var i = 0; i < array.Count; i++)
                {
                    if (i > 0)
                    {
                        builder.Append(compact ? "," : ", ");
                    }

                    Write(builder, array[i], compact);
                }

                builder.Append(']');
                return;
            case JsonValue value:
                WriteValue(builder, value);
                return;
        }
    }

    private static void WriteValue(StringBuilder builder, JsonValue value)
    {
        if (value.TryGetValue(out JsonElement element))
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.String:
                    WriteString(builder, element.GetString()!);
                    return;
                case JsonValueKind.Number:
                    builder.Append(FormatNumber(element.GetRawText()));
                    return;
                case JsonValueKind.True:
                    builder.Append("true");
                    return;
                case JsonValueKind.False:
                    builder.Append("false");
                    return;
                default:
                    builder.Append("null");
                    return;
            }
        }

        if (value.TryGetValue(out string? text))
        {
            WriteString(builder, text);
        }
        else if (value.TryGetValue(out bool flag))
        {
            builder.Append(flag ? "true" : "false");
        }
        else if (value.TryGetValue(out long integer))
        {
            builder.Append(integer.ToString(CultureInfo.InvariantCulture));
        }
        else if (value.TryGetValue(out double number))
        {
            builder.Append(FormatFloat(number));
        }
        else
        {
            // 其他數字型別：先寫成 JSON 再依 Python 的規則格式化
            builder.Append(FormatNumber(value.ToJsonString()));
        }
    }

    /// <summary>JSON 數字的原文照 Python 的方式重寫：整數照抄（<c>-0</c> 變 <c>0</c>），有小數點或指數的轉成 float 再用 <c>repr</c> 的寫法。</summary>
    private static string FormatNumber(string raw)
    {
        if (raw.AsSpan().IndexOfAny('.', 'e', 'E') < 0)
        {
            return raw == "-0" ? "0" : raw;
        }

        return FormatFloat(double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture));
    }

    /// <summary>Python 的 <c>float.__repr__</c>（<c>json.dumps</c> 用的寫法）：最短可還原的數字，指數 &lt; -4 或 ≥ 16 時用科學記號。</summary>
    internal static string FormatFloat(double value)
    {
        if (double.IsNaN(value))
        {
            return "NaN";
        }

        if (double.IsInfinity(value))
        {
            return value > 0 ? "Infinity" : "-Infinity";
        }

        if (value == 0)
        {
            return double.IsNegative(value) ? "-0.0" : "0.0";
        }

        // "R" 給最短可還原的位數（跟 Python 的 repr 一樣），從裡面取出數字與指數再照 Python 的格式排
        var roundTrip = Math.Abs(value).ToString("R", CultureInfo.InvariantCulture);
        ParseDigits(roundTrip, out var digits, out var decimalPoint);

        var sign = value < 0 ? "-" : "";
        if (decimalPoint is > -4 and <= 16)
        {
            if (decimalPoint <= 0)
            {
                return sign + "0." + new string('0', -decimalPoint) + digits;
            }

            if (decimalPoint >= digits.Length)
            {
                return sign + digits + new string('0', decimalPoint - digits.Length) + ".0";
            }

            return sign + digits[..decimalPoint] + "." + digits[decimalPoint..];
        }

        var exponent = decimalPoint - 1;
        var mantissa = digits.Length == 1 ? digits : digits[..1] + "." + digits[1..];
        return sign + mantissa + "e" + (exponent < 0 ? "-" : "+") + Math.Abs(exponent).ToString("00", CultureInfo.InvariantCulture);
    }

    /// <summary>把 "R" 格式的文字拆成有效數字（不含前後的 0）與小數點位置：值 = 0.digits × 10^decimalPoint。</summary>
    private static void ParseDigits(string text, out string digits, out int decimalPoint)
    {
        var exponent = 0;
        var e = text.IndexOfAny(['E', 'e']);
        if (e >= 0)
        {
            exponent = int.Parse(text.AsSpan(e + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            text = text[..e];
        }

        var dot = text.IndexOf('.', StringComparison.Ordinal);
        var integerPart = dot >= 0 ? text[..dot] : text;
        var fractionPart = dot >= 0 ? text[(dot + 1)..] : "";
        var all = integerPart + fractionPart;
        var point = integerPart.Length + exponent;
        var leading = 0;
        while (leading < all.Length - 1 && all[leading] == '0')
        {
            leading++;
        }

        all = all[leading..];
        point -= leading;
        digits = all.TrimEnd('0');
        if (digits.Length == 0)
        {
            digits = "0";
        }

        decimalPoint = point;
    }

    /// <summary>Python <c>json.dumps</c>（<c>ensure_ascii=True</c>）的字串寫法：非 ASCII 一律 <c>\uXXXX</c>（小寫）。</summary>
    private static void WriteString(StringBuilder builder, string value)
    {
        builder.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                case '\b':
                    builder.Append("\\b");
                    break;
                case '\f':
                    builder.Append("\\f");
                    break;
                default:
                    if (c is < ' ' or > '~')
                    {
                        builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(c);
                    }

                    break;
            }
        }

        builder.Append('"');
    }

    /// <summary>把 <see cref="JsonNode"/> 轉成獨立的 <see cref="JsonElement"/>（給公開型別的 <c>Raw</c> 欄位）。</summary>
    public static JsonElement ToElement(JsonNode node)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            node.WriteTo(writer);
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory, DocumentOptions);
        return document.RootElement.Clone();
    }

    /// <summary>Python 的真假值：<c>None</c>、<c>False</c>、0、空字串、空 list、空 dict 是假。</summary>
    public static bool Truthy(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return false;
            case JsonObject obj:
                return obj.Count > 0;
            case JsonArray array:
                return array.Count > 0;
            case JsonValue value:
                if (TryGetBool(value, out var flag))
                {
                    return flag;
                }

                if (TryGetString(value, out var text))
                {
                    return text.Length > 0;
                }

                return TryGetDouble(value, out var number) && number != 0;
            default:
                return true;
        }
    }

    /// <summary>欄位是字串時回傳它（Python 的 <c>isinstance(x, str)</c>），否則 <see langword="null"/>。</summary>
    public static string? Str(JsonNode? node) => node is JsonValue value && TryGetString(value, out var text) ? text : null;

    /// <summary><c>obj.get(name)</c> 是字串時回傳它。</summary>
    public static string? Str(JsonObject obj, string name) => obj.TryGetPropertyValue(name, out var value) ? Str(value) : null;

    /// <summary>Python 的 <c>x is True</c>：只有 JSON 的 <c>true</c> 算。</summary>
    public static bool IsTrue(JsonNode? node) => node is JsonValue value && TryGetBool(value, out var flag) && flag;

    /// <summary>Python 的 <c>isinstance(x, int)</c>（不含 bool），整數放得進 <see cref="long"/> 時回傳。</summary>
    public static long? Integer(JsonNode? node)
    {
        if (node is not JsonValue value || TryGetBool(value, out _))
        {
            return null;
        }

        if (value.TryGetValue(out JsonElement element))
        {
            return element.ValueKind == JsonValueKind.Number
                && element.GetRawText().AsSpan().IndexOfAny('.', 'e', 'E') < 0
                && element.TryGetInt64(out var parsed)
                ? parsed
                : null;
        }

        if (value.TryGetValue(out long integer))
        {
            return integer;
        }

        return value.TryGetValue(out int small) ? small : null;
    }

    /// <summary>Python 的 <c>==</c>：數字比值（<c>True == 1</c> 也成立）、dict 不看鍵的順序。</summary>
    public static bool PyEquals(JsonNode? left, JsonNode? right)
    {
        switch (left)
        {
            case null:
                return right is null;
            case JsonObject leftObject:
                if (right is not JsonObject rightObject || leftObject.Count != rightObject.Count)
                {
                    return false;
                }

                foreach (var (key, value) in leftObject)
                {
                    if (!rightObject.TryGetPropertyValue(key, out var other) || !PyEquals(value, other))
                    {
                        return false;
                    }
                }

                return true;
            case JsonArray leftArray:
                if (right is not JsonArray rightArray || leftArray.Count != rightArray.Count)
                {
                    return false;
                }

                for (var i = 0; i < leftArray.Count; i++)
                {
                    if (!PyEquals(leftArray[i], rightArray[i]))
                    {
                        return false;
                    }
                }

                return true;
            case JsonValue leftValue:
                if (right is not JsonValue rightValue)
                {
                    return false;
                }

                var leftIsString = TryGetString(leftValue, out var leftText);
                var rightIsString = TryGetString(rightValue, out var rightText);
                if (leftIsString || rightIsString)
                {
                    return leftIsString && rightIsString && leftText == rightText;
                }

                return TryGetNumeric(leftValue, out var leftNumber) && TryGetNumeric(rightValue, out var rightNumber) && leftNumber == rightNumber;
            default:
                return false;
        }
    }

    private static bool TryGetNumeric(JsonValue value, out double number)
    {
        if (TryGetBool(value, out var flag))
        {
            number = flag ? 1 : 0;
            return true;
        }

        return TryGetDouble(value, out number);
    }

    private static bool TryGetBool(JsonValue value, out bool flag)
    {
        if (value.TryGetValue(out JsonElement element))
        {
            flag = element.ValueKind == JsonValueKind.True;
            return element.ValueKind is JsonValueKind.True or JsonValueKind.False;
        }

        return value.TryGetValue(out flag);
    }

    private static bool TryGetString(JsonValue value, out string text)
    {
        if (value.TryGetValue(out JsonElement element))
        {
            text = element.ValueKind == JsonValueKind.String ? element.GetString()! : "";
            return element.ValueKind == JsonValueKind.String;
        }

        if (value.TryGetValue(out string? s))
        {
            text = s;
            return true;
        }

        text = "";
        return false;
    }

    private static bool TryGetDouble(JsonValue value, out double number)
    {
        if (value.TryGetValue(out JsonElement element))
        {
            number = element.ValueKind == JsonValueKind.Number
                ? double.Parse(element.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture)
                : 0;
            return element.ValueKind == JsonValueKind.Number;
        }

        if (value.TryGetValue(out long integer))
        {
            number = integer;
            return true;
        }

        if (value.TryGetValue(out int small))
        {
            number = small;
            return true;
        }

        return value.TryGetValue(out number);
    }
}
