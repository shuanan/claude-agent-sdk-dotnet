using System.Text.Json;
using PamisWang.ClaudeAgentSdk.Mcp;

namespace PamisWang.ClaudeAgentSdk.Tests.Mcp;

/// <summary>建立工具與 server 的 API，以及「名稱對型別」產生 schema 的規則（對照 Python 的 <c>_build_input_schema</c>、<c>_python_type_to_json_schema</c>）。</summary>
public sealed class SdkMcpToolTests
{
    private static readonly JsonSerializerOptions Unescaped = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    [Fact]
    public void Parameters_MapsTypesLikePythonAndRequiresEveryParameter()
    {
        var schema = McpSchema.Parameters(
            ("text", typeof(string)),
            ("count", typeof(int)),
            ("big", typeof(long)),
            ("ratio", typeof(double)),
            ("price", typeof(decimal)),
            ("flag", typeof(bool)),
            ("maybe", typeof(int?)),
            ("tags", typeof(string[])),
            ("ids", typeof(List<int>)),
            ("map", typeof(Dictionary<string, int>)),
            ("other", typeof(Uri)));

        Assert.Equal(
            """{"type":"object","properties":{"text":{"type":"string"},"count":{"type":"integer"},"big":{"type":"integer"},"ratio":"""
            + """{"type":"number"},"price":{"type":"number"},"flag":{"type":"boolean"},"maybe":{"type":"integer"},"tags":"""
            + """{"type":"array","items":{"type":"string"}},"ids":{"type":"array","items":{"type":"integer"}},"map":{"type":"object"},"other":"""
            + """{"type":"string"}},"required":["text","count","big","ratio","price","flag","maybe","tags","ids","map","other"]}""",
            schema.GetRawText());
    }

    [Fact]
    public void Parameters_Empty_IsObjectWithNoProperties()
    {
        Assert.Equal("""{"type":"object","properties":{},"required":[]}""", McpSchema.Parameters().GetRawText());
    }

    [Fact]
    public void Parameters_WithDescriptions_AddsDescriptionLikeAnnotated()
    {
        var schema = McpSchema.Parameters(("city", typeof(string), "城市名稱"));

        Assert.Equal(
            """{"type":"object","properties":{"city":{"type":"string","description":"城市名稱"}},"required":["city"]}""",
            JsonSerializer.Serialize(schema, Unescaped));
    }

    [Fact]
    public async Task Create_KeepsDefinitionAndHandlerIsCallable()
    {
        var annotations = new McpToolAnnotations { ReadOnlyHint = true };
        var tool = SdkMcpTool.Create(
            "echo",
            "Echo input",
            McpSchema.Parameters(("input", typeof(string))),
            args => ValueTask.FromResult(McpToolResult.Text(args.GetProperty("input").GetString()!)),
            annotations);

        var result = await tool.Handler(JsonSerializer.Deserialize<JsonElement>("""{"input":"test"}"""), CancellationToken.None);

        Assert.Equal("echo", tool.Name);
        Assert.Equal("Echo input", tool.Description);
        Assert.Same(annotations, tool.Annotations);
        Assert.Equal([new McpTextContent("test")], result.Content);
        Assert.False(result.IsError);
    }

    [Fact]
    public void Create_SchemaNotAnObject_Throws()
    {
        Assert.Throws<ArgumentException>(() => SdkMcpTool.Create(
            "t",
            "d",
            JsonSerializer.Deserialize<JsonElement>("[]"),
            _ => ValueTask.FromResult(McpToolResult.Text(""))));
    }

    [Fact]
    public void ToolResultHelpers_BuildSingleTextContent()
    {
        Assert.Equal([new McpTextContent("ok")], McpToolResult.Text("ok").Content);
        Assert.False(McpToolResult.Text("ok").IsError);
        Assert.Equal([new McpTextContent("bad")], McpToolResult.Error("bad").Content);
        Assert.True(McpToolResult.Error("bad").IsError);
    }

    [Fact]
    public void SdkMcpServerCreate_DefaultsVersionAndKeepsTools()
    {
        var tool = SdkMcpTool.Create("t", "d", McpSchema.Parameters(), _ => ValueTask.FromResult(McpToolResult.Text("")));

        var server = SdkMcpServer.Create("calc", tool);
        var versioned = SdkMcpServer.Create("calc", "2.0.0", [tool]);

        Assert.Equal(("calc", "1.0.0"), (server.Name, server.Version));
        Assert.Equal([tool], server.Tools);
        Assert.Equal("2.0.0", versioned.Version);
        Assert.Empty(SdkMcpServer.Create("empty").Tools);
    }
}
