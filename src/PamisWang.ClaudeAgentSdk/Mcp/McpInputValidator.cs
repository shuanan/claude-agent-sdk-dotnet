using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PamisWang.ClaudeAgentSdk.Mcp;

/// <summary>
/// 呼叫工具前用 <see cref="SdkMcpTool.InputSchema"/> 驗證參數。Python 用 <c>jsonschema.validate</c>；這裡沒有外部套件，
/// 實作常用的關鍵字：<c>type</c>、<c>properties</c>、<c>required</c>、<c>additionalProperties</c>、<c>items</c>、<c>enum</c>、<c>const</c>、
/// <c>minLength</c>／<c>maxLength</c>、<c>pattern</c>、<c>minimum</c>／<c>maximum</c>、<c>exclusiveMinimum</c>／<c>exclusiveMaximum</c>、
/// <c>minItems</c>／<c>maxItems</c>、<c>anyOf</c>／<c>oneOf</c>／<c>allOf</c>／<c>not</c>，其他的（<c>$ref</c>、<c>format</c> 等）不檢查。
/// 錯誤訊息的寫法跟 jsonschema 一樣（值用 Python 的 repr 表示）；有多個錯誤時跟 <c>best_match</c> 一樣取路徑最淺、最先遇到的那個。
/// </summary>
internal static class McpInputValidator
{
    private static readonly string[] KnownTypes = ["array", "boolean", "integer", "null", "number", "object", "string"];

    /// <summary>回傳第一個錯誤的訊息，參數合格時是 <see langword="null"/>。</summary>
    /// <exception cref="McpSchemaException">schema 本身不合法，例如不認得的 <c>type</c>。</exception>
    public static string? Validate(JsonElement schema, JsonElement instance)
    {
        CheckSchema(schema);
        List<(int Depth, string Message)> failures = [];
        Check(schema, instance, depth: 0, failures);
        return failures.Count == 0 ? null : failures.MinBy(failure => failure.Depth).Message;
    }

    /// <summary>跟 jsonschema 先做的 <c>check_schema</c> 一樣，參數還沒看就先擋下不合法的 schema；這裡只檢查 <c>type</c> 的值。</summary>
    private static void CheckSchema(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var keyword in schema.EnumerateObject())
        {
            var value = keyword.Value;
            switch (keyword.Name)
            {
                case "type":
                    var valid = value.ValueKind switch
                    {
                        JsonValueKind.String => KnownTypes.Contains(value.GetString()),
                        JsonValueKind.Array => value.EnumerateArray().All(type => type.ValueKind == JsonValueKind.String && KnownTypes.Contains(type.GetString())),
                        _ => false,
                    };
                    if (!valid)
                    {
                        throw new McpSchemaException($"{PythonRepr.Of(value)} is not valid under any of the given schemas");
                    }

                    break;
                case "properties" when value.ValueKind == JsonValueKind.Object:
                    foreach (var property in value.EnumerateObject())
                    {
                        CheckSchema(property.Value);
                    }

                    break;
                case "items" or "additionalProperties" or "not":
                    CheckSchema(value);
                    break;
                case "anyOf" or "oneOf" or "allOf" when value.ValueKind == JsonValueKind.Array:
                    foreach (var subschema in value.EnumerateArray())
                    {
                        CheckSchema(subschema);
                    }

                    break;
            }
        }
    }

    private static bool IsValid(JsonElement schema, JsonElement instance)
    {
        List<(int, string)> failures = [];
        Check(schema, instance, 0, failures);
        return failures.Count == 0;
    }

    private static void Check(JsonElement schema, JsonElement instance, int depth, List<(int Depth, string Message)> failures)
    {
        if (schema.ValueKind == JsonValueKind.False)
        {
            failures.Add((depth, $"False schema does not allow {PythonRepr.Of(instance)}"));
            return;
        }

        if (schema.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        void Fail(string message) => failures.Add((depth, message));

        foreach (var keyword in schema.EnumerateObject())
        {
            var value = keyword.Value;
            switch (keyword.Name)
            {
                case "type":
                    string[] types = value.ValueKind == JsonValueKind.Array
                        ? [.. value.EnumerateArray().Select(type => type.GetString()!)]
                        : [value.GetString()!];
                    if (!types.Any(type => IsType(instance, type)))
                    {
                        Fail($"{PythonRepr.Of(instance)} is not of type {string.Join(", ", types.Select(PythonRepr.OfString))}");
                    }

                    break;

                case "properties" when instance.ValueKind == JsonValueKind.Object && value.ValueKind == JsonValueKind.Object:
                    foreach (var property in value.EnumerateObject())
                    {
                        if (instance.TryGetProperty(property.Name, out var propertyValue))
                        {
                            Check(property.Value, propertyValue, depth + 1, failures);
                        }
                    }

                    break;

                case "required" when instance.ValueKind == JsonValueKind.Object && value.ValueKind == JsonValueKind.Array:
                    foreach (var name in value.EnumerateArray())
                    {
                        if (name.ValueKind == JsonValueKind.String && !instance.TryGetProperty(name.GetString()!, out _))
                        {
                            Fail($"{PythonRepr.Of(name)} is a required property");
                        }
                    }

                    break;

                case "additionalProperties" when instance.ValueKind == JsonValueKind.Object:
                    var declared = schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object
                        ? properties
                        : default;
                    var extras = instance.EnumerateObject()
                        .Where(property => declared.ValueKind != JsonValueKind.Object || !declared.TryGetProperty(property.Name, out _))
                        .ToList();
                    if (value.ValueKind == JsonValueKind.False)
                    {
                        if (extras.Count > 0)
                        {
                            var names = string.Join(", ", extras.Select(extra => PythonRepr.OfString(extra.Name)));
                            Fail($"Additional properties are not allowed ({names} {(extras.Count == 1 ? "was" : "were")} unexpected)");
                        }
                    }
                    else
                    {
                        foreach (var extra in extras)
                        {
                            Check(value, extra.Value, depth + 1, failures);
                        }
                    }

                    break;

                case "items" when instance.ValueKind == JsonValueKind.Array:
                    foreach (var item in instance.EnumerateArray())
                    {
                        Check(value, item, depth + 1, failures);
                    }

                    break;

                case "enum" when value.ValueKind == JsonValueKind.Array:
                    if (!value.EnumerateArray().Any(option => JsonEquals(option, instance)))
                    {
                        Fail($"{PythonRepr.Of(instance)} is not one of {PythonRepr.Of(value)}");
                    }

                    break;

                case "const":
                    if (!JsonEquals(value, instance))
                    {
                        Fail($"{PythonRepr.Of(value)} was expected");
                    }

                    break;

                case "minLength" when instance.ValueKind == JsonValueKind.String && value.TryGetInt32(out var minLength):
                    if (CodePoints(instance.GetString()!) < minLength)
                    {
                        Fail($"{PythonRepr.Of(instance)} {(minLength == 1 ? "should be non-empty" : "is too short")}");
                    }

                    break;

                case "maxLength" when instance.ValueKind == JsonValueKind.String && value.TryGetInt32(out var maxLength):
                    if (CodePoints(instance.GetString()!) > maxLength)
                    {
                        Fail($"{PythonRepr.Of(instance)} {(maxLength == 0 ? "is expected to be empty" : "is too long")}");
                    }

                    break;

                case "pattern" when instance.ValueKind == JsonValueKind.String && value.ValueKind == JsonValueKind.String:
                    // Python 用 re.search：只要有一段符合就算
                    if (!Regex.IsMatch(instance.GetString()!, value.GetString()!, RegexOptions.None, TimeSpan.FromSeconds(1)))
                    {
                        Fail($"{PythonRepr.Of(instance)} does not match {PythonRepr.Of(value)}");
                    }

                    break;

                case "minimum" when instance.ValueKind == JsonValueKind.Number && value.ValueKind == JsonValueKind.Number:
                    if (Compare(instance, value) < 0)
                    {
                        Fail($"{PythonRepr.Of(instance)} is less than the minimum of {PythonRepr.Of(value)}");
                    }

                    break;

                case "maximum" when instance.ValueKind == JsonValueKind.Number && value.ValueKind == JsonValueKind.Number:
                    if (Compare(instance, value) > 0)
                    {
                        Fail($"{PythonRepr.Of(instance)} is greater than the maximum of {PythonRepr.Of(value)}");
                    }

                    break;

                case "exclusiveMinimum" when instance.ValueKind == JsonValueKind.Number && value.ValueKind == JsonValueKind.Number:
                    if (Compare(instance, value) <= 0)
                    {
                        Fail($"{PythonRepr.Of(instance)} is less than or equal to the minimum of {PythonRepr.Of(value)}");
                    }

                    break;

                case "exclusiveMaximum" when instance.ValueKind == JsonValueKind.Number && value.ValueKind == JsonValueKind.Number:
                    if (Compare(instance, value) >= 0)
                    {
                        Fail($"{PythonRepr.Of(instance)} is greater than or equal to the maximum of {PythonRepr.Of(value)}");
                    }

                    break;

                case "minItems" when instance.ValueKind == JsonValueKind.Array && value.TryGetInt32(out var minItems):
                    if (instance.GetArrayLength() < minItems)
                    {
                        Fail($"{PythonRepr.Of(instance)} {(minItems == 1 ? "should be non-empty" : "is too short")}");
                    }

                    break;

                case "maxItems" when instance.ValueKind == JsonValueKind.Array && value.TryGetInt32(out var maxItems):
                    if (instance.GetArrayLength() > maxItems)
                    {
                        Fail($"{PythonRepr.Of(instance)} {(maxItems == 0 ? "is expected to be empty" : "is too long")}");
                    }

                    break;

                case "anyOf" when value.ValueKind == JsonValueKind.Array:
                    if (!value.EnumerateArray().Any(subschema => IsValid(subschema, instance)))
                    {
                        Fail($"{PythonRepr.Of(instance)} is not valid under any of the given schemas");
                    }

                    break;

                case "oneOf" when value.ValueKind == JsonValueKind.Array:
                    var matching = value.EnumerateArray().Where(subschema => IsValid(subschema, instance)).ToList();
                    if (matching.Count == 0)
                    {
                        Fail($"{PythonRepr.Of(instance)} is not valid under any of the given schemas");
                    }
                    else if (matching.Count > 1)
                    {
                        Fail($"{PythonRepr.Of(instance)} is valid under each of {string.Join(", ", matching.Select(PythonRepr.Of))}");
                    }

                    break;

                case "allOf" when value.ValueKind == JsonValueKind.Array:
                    foreach (var subschema in value.EnumerateArray())
                    {
                        Check(subschema, instance, depth, failures);
                    }

                    break;

                case "not":
                    if (IsValid(value, instance))
                    {
                        Fail($"{PythonRepr.Of(instance)} should not be valid under {PythonRepr.Of(value)}");
                    }

                    break;
            }
        }
    }

    private static bool IsType(JsonElement instance, string type) => type switch
    {
        "string" => instance.ValueKind == JsonValueKind.String,
        "number" => instance.ValueKind == JsonValueKind.Number,
        // jsonschema 把小數部分是零的數字（例如 1.0）也當成整數
        "integer" => instance.ValueKind == JsonValueKind.Number && IsIntegral(instance),
        "boolean" => instance.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "object" => instance.ValueKind == JsonValueKind.Object,
        "array" => instance.ValueKind == JsonValueKind.Array,
        "null" => instance.ValueKind == JsonValueKind.Null,
        _ => false,
    };

    private static bool IsIntegral(JsonElement number) =>
        number.TryGetDecimal(out var value) ? decimal.Truncate(value) == value : double.IsInteger(number.GetDouble());

    private static int Compare(JsonElement left, JsonElement right) =>
        left.TryGetDecimal(out var l) && right.TryGetDecimal(out var r)
            ? l.CompareTo(r)
            : left.GetDouble().CompareTo(right.GetDouble());

    private static int CodePoints(string text) => text.EnumerateRunes().Count();

    /// <summary>跟 Python 的 <c>==</c> 一樣比較 JSON 值：數字比數值（1 等於 1.0），物件不看鍵的順序。</summary>
    internal static bool JsonEquals(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind)
        {
            return false;
        }

        switch (left.ValueKind)
        {
            case JsonValueKind.Number:
                return Compare(left, right) == 0;
            case JsonValueKind.String:
                return left.GetString() == right.GetString();
            case JsonValueKind.Array:
                return left.GetArrayLength() == right.GetArrayLength()
                    && left.EnumerateArray().Zip(right.EnumerateArray()).All(pair => JsonEquals(pair.First, pair.Second));
            case JsonValueKind.Object:
                var leftProperties = left.EnumerateObject().ToList();
                return leftProperties.Count == right.EnumerateObject().Count()
                    && leftProperties.All(property => right.TryGetProperty(property.Name, out var other) && JsonEquals(property.Value, other));
            default:
                return true;
        }
    }
}

/// <summary>schema 本身不合法，對應 jsonschema 的 <c>SchemaError</c>。</summary>
internal sealed class McpSchemaException(string message) : Exception(message);

/// <summary>把 JSON 值寫成 Python 的 <c>repr()</c>，讓錯誤訊息跟 jsonschema 的一樣。</summary>
internal static class PythonRepr
{
    public static string Of(JsonElement value)
    {
        var builder = new StringBuilder();
        Append(builder, value);
        return builder.ToString();
    }

    public static string OfString(string text)
    {
        // Python 預設用單引號；內容有單引號、沒有雙引號時改用雙引號
        var quote = text.Contains('\'', StringComparison.Ordinal) && !text.Contains('"', StringComparison.Ordinal) ? '"' : '\'';
        var builder = new StringBuilder().Append(quote);
        foreach (var c in text)
        {
            switch (c)
            {
                case '\\':
                    builder.Append(@"\\");
                    break;
                case '\n':
                    builder.Append(@"\n");
                    break;
                case '\r':
                    builder.Append(@"\r");
                    break;
                case '\t':
                    builder.Append(@"\t");
                    break;
                default:
                    if (c == quote)
                    {
                        builder.Append('\\').Append(c);
                    }
                    else if (c < ' ' || c == '\x7f')
                    {
                        builder.Append(CultureInfo.InvariantCulture, $"\\x{(int)c:x2}");
                    }
                    else
                    {
                        builder.Append(c);
                    }

                    break;
            }
        }

        return builder.Append(quote).ToString();
    }

    private static void Append(StringBuilder builder, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                builder.Append(OfString(value.GetString()!));
                break;
            case JsonValueKind.Number:
                builder.Append(Number(value));
                break;
            case JsonValueKind.True:
                builder.Append("True");
                break;
            case JsonValueKind.False:
                builder.Append("False");
                break;
            case JsonValueKind.Array:
                builder.Append('[');
                var firstItem = true;
                foreach (var item in value.EnumerateArray())
                {
                    builder.Append(firstItem ? "" : ", ");
                    Append(builder, item);
                    firstItem = false;
                }

                builder.Append(']');
                break;
            case JsonValueKind.Object:
                builder.Append('{');
                var firstProperty = true;
                foreach (var property in value.EnumerateObject())
                {
                    builder.Append(firstProperty ? "" : ", ").Append(OfString(property.Name)).Append(": ");
                    Append(builder, property.Value);
                    firstProperty = false;
                }

                builder.Append('}');
                break;
            default:
                builder.Append("None");
                break;
        }
    }

    /// <summary>JSON 的整數在 Python 是 int，原樣寫；有小數點或指數的是 float，寫成 Python float 的 repr（例如 <c>1.50</c> 是 <c>1.5</c>、<c>1e5</c> 是 <c>100000.0</c>）。</summary>
    private static string Number(JsonElement value)
    {
        var raw = value.GetRawText();
        if (raw.AsSpan().IndexOfAny('.', 'e', 'E') < 0)
        {
            return raw;
        }

        var text = value.GetDouble().ToString("R", CultureInfo.InvariantCulture);
        if (text.Contains('E', StringComparison.Ordinal))
        {
            // .NET 是 1E+16、1E-05，Python 是 1e+16、1e-05
            return text.Replace('E', 'e');
        }

        return text.Contains('.', StringComparison.Ordinal) || text is "NaN" or "Infinity" or "-Infinity" ? text : text + ".0";
    }
}
