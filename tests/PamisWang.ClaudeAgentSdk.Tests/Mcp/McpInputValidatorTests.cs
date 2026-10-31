using System.Text.Json;
using PamisWang.ClaudeAgentSdk.Mcp;

namespace PamisWang.ClaudeAgentSdk.Tests.Mcp;

/// <summary>參數驗證的錯誤訊息要跟 Python 的 jsonschema 一樣（值用 Python 的 repr）。</summary>
public sealed class McpInputValidatorTests
{
    [Theory]
    [InlineData("""{"type":"object","properties":{"name":{"type":"string","minLength":2}},"required":["name"]}""", """{"name":"xy"}""", null)]
    [InlineData("""{"type":"object","properties":{"name":{"type":"string","minLength":2}},"required":["name"]}""", """{"name":"x"}""", "'x' is too short")]
    [InlineData("""{"type":"object","properties":{"name":{"type":"string","minLength":1}}}""", """{"name":""}""", "'' should be non-empty")]
    [InlineData("""{"type":"object","properties":{"n":{"type":"integer"}}}""", """{"n":1.0}""", null)]
    [InlineData("""{"type":"object","properties":{"n":{"type":"integer"}}}""", """{"n":1.5}""", "1.5 is not of type 'integer'")]
    [InlineData("""{"type":"object","properties":{"n":{"type":["string","null"]}}}""", """{"n":true}""", "True is not of type 'string', 'null'")]
    [InlineData("""{"type":"object","properties":{"mode":{"enum":["a","b"]}}}""", """{"mode":"c"}""", "'c' is not one of ['a', 'b']")]
    [InlineData("""{"type":"object","properties":{"v":{"const":1}}}""", """{"v":1.0}""", null)]
    [InlineData("""{"type":"object","properties":{},"additionalProperties":false}""", """{"x":1,"y":2}""", "Additional properties are not allowed ('x', 'y' were unexpected)")]
    [InlineData("""{"type":"object","properties":{"x":{}},"additionalProperties":false}""", """{"x":1,"y":2}""", "Additional properties are not allowed ('y' was unexpected)")]
    [InlineData("""{"type":"object","properties":{"n":{"type":"number","minimum":0}}}""", """{"n":-1}""", "-1 is less than the minimum of 0")]
    [InlineData("""{"type":"object","properties":{"n":{"type":"number","exclusiveMaximum":10}}}""", """{"n":1e1}""", "10.0 is greater than or equal to the maximum of 10")]
    [InlineData("""{"type":"object","properties":{"tags":{"type":"array","items":{"type":"string"}}}}""", """{"tags":["a",2]}""", "2 is not of type 'string'")]
    [InlineData("""{"type":"object","properties":{"v":{"anyOf":[{"type":"string"},{"type":"integer"}]}}}""", """{"v":{"k":null}}""", "{'k': None} is not valid under any of the given schemas")]
    [InlineData("""{"type":"object","properties":{"s":{"type":"string","pattern":"^a"}}}""", """{"s":"it's"}""", "\"it's\" does not match '^a'")]
    public void Validate_ReportsJsonschemaMessages(string schema, string instance, string? expected)
    {
        Assert.Equal(expected, McpInputValidator.Validate(Json(schema), Json(instance)));
    }

    [Fact]
    public void Validate_ShallowestErrorWins()
    {
        var schema = Json("""{"type":"object","properties":{"a":{"type":"number"}},"required":["a","b"]}""");

        Assert.Equal("'b' is a required property", McpInputValidator.Validate(schema, Json("""{"a":"x"}""")));
    }

    [Fact]
    public void Validate_InvalidTypeAnywhereInSchema_ThrowsBeforeValidating()
    {
        var schema = Json("""{"type":"object","properties":{"unused":{"type":"bogus"}}}""");

        var error = Assert.Throws<McpSchemaException>(() => McpInputValidator.Validate(schema, Json("{}")));
        Assert.Equal("'bogus' is not valid under any of the given schemas", error.Message);
    }

    private static JsonElement Json(string json) => JsonSerializer.Deserialize<JsonElement>(json);
}
