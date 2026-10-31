using System.Text.Json;
using PamisWang.ClaudeAgentSdk.Mcp;
using PamisWang.ClaudeAgentSdk.Protocol;

namespace PamisWang.ClaudeAgentSdk.Tests.Protocol;

/// <summary>
/// CLI 透過 <c>mcp_message</c> 跟 SDK MCP server 說 JSON-RPC：握手、列出工具、呼叫工具、錯誤與通知。
/// 對照 Python 的 <c>test_sdk_mcp_integration.py</c>，逐字比對 SDK 寫出的 control response。
/// </summary>
public sealed class SdkMcpProtocolTests : IAsyncLifetime, IAsyncDisposable
{
    private const string Initialize =
        """{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test-client","version":"0.0.0"}}}""";

    private readonly ScriptedTransport _transport = new();
    private ControlProtocol? _protocol;
    private int _requestCounter;
    private bool _initializing;

    public Task InitializeAsync() => Task.CompletedTask;

    Task IAsyncLifetime.DisposeAsync() => DisposeAsync().AsTask();

    public async ValueTask DisposeAsync()
    {
        if (_protocol is not null)
        {
            await _protocol.DisposeAsync();
        }

        await _transport.DisposeAsync();
    }

    [Fact]
    public async Task Initialize_ReportsServerInfoCapabilitiesAndRequestedVersion()
    {
        Start(SdkMcpServer.Create("hello", "3.2.1"), initialize: false);

        var response = await SendAsync("srv", Initialize);

        Assert.Equal(
            """{"type":"control_response","response":{"subtype":"success","request_id":"mcp-1","response":{"mcp_response":"""
            + """{"jsonrpc":"2.0","id":0,"result":{"protocolVersion":"2025-06-18","capabilities":{"tools":{"listChanged":false}},"serverInfo":{"name":"hello","version":"3.2.1"}}}}}}""",
            response);
    }

    [Fact]
    public async Task Initialize_UnsupportedVersion_AnswersWithLatest()
    {
        Start(SdkMcpServer.Create("hello"), initialize: false);

        var response = await McpResponseAsync("srv", """{"jsonrpc":"2.0","id":"a","method":"initialize","params":{"protocolVersion":"1999-01-01","capabilities":{},"clientInfo":{"name":"c","version":"0"}}}""");

        Assert.Equal(
            """{"jsonrpc":"2.0","id":"a","result":{"protocolVersion":"2025-11-25","capabilities":{"tools":{"listChanged":false}},"serverInfo":{"name":"hello","version":"1.0.0"}}}""",
            response);
    }

    [Fact]
    public async Task Notification_GetsNoJsonRpcReplyButTheControlRequestIsAcknowledged()
    {
        Start(SdkMcpServer.Create("srv"), initialize: false);

        var response = await SendAsync("srv", """{"jsonrpc":"2.0","method":"notifications/initialized"}""");

        Assert.Equal(
            """{"type":"control_response","response":{"subtype":"success","request_id":"mcp-1","response":{"mcp_response":{"jsonrpc":"2.0","result":{}}}}}""",
            response);
    }

    [Fact]
    public async Task Ping_BeforeInitialize_AnswersEmptyResult()
    {
        Start(SdkMcpServer.Create("srv"), initialize: false);

        Assert.Equal("""{"jsonrpc":"2.0","id":3,"result":{}}""", await McpResponseAsync("srv", """{"jsonrpc":"2.0","id":3,"method":"ping"}"""));
    }

    [Theory]
    [InlineData("""{"jsonrpc":"2.0","id":4,"method":"tools/list","params":{}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"t","arguments":{}}}""")]
    public async Task ToolsBeforeInitialize_IsInvalidParamsLikeMcp(string message)
    {
        Start(SdkMcpServer.Create("srv", Tool("t", "d", McpSchema.Parameters())), initialize: false);

        Assert.Equal(
            """{"jsonrpc":"2.0","id":4,"error":{"code":-32602,"message":"Invalid request parameters","data":""}}""",
            await McpResponseAsync("srv", message));
    }

    [Fact]
    public async Task BareInitializedNotification_OpensTheGateLikeMcp()
    {
        Start(SdkMcpServer.Create("srv"), initialize: false);

        await McpResponseAsync("srv", """{"jsonrpc":"2.0","method":"notifications/initialized"}""");

        Assert.Equal("""{"jsonrpc":"2.0","id":5,"result":{"tools":[]}}""", await McpResponseAsync("srv", """{"jsonrpc":"2.0","id":5,"method":"tools/list"}"""));
    }

    [Fact]
    public async Task Initialize_MissingClientInfo_IsInvalidParams()
    {
        Start(SdkMcpServer.Create("srv"), initialize: false);

        Assert.Equal(
            """{"jsonrpc":"2.0","id":6,"error":{"code":-32602,"message":"Invalid request parameters","data":""}}""",
            await McpResponseAsync("srv", """{"jsonrpc":"2.0","id":6,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{}}}"""));
    }

    [Fact]
    public async Task ToolsList_WireFormat_MatchesPython()
    {
        var readData = Tool("read_data", "Read data from source", McpSchema.Parameters(("source", typeof(string)))) with
        {
            Annotations = new McpToolAnnotations { ReadOnlyHint = true, OpenWorldHint = false },
        };
        var large = Tool("get_large_schema", "Returns a large DB schema.", McpSchema.Parameters()) with
        {
            Annotations = new McpToolAnnotations { ReadOnlyHint = true, MaxResultSizeChars = 500_000 },
        };
        var plain = Tool("plain", "Tool without annotations", McpSchema.Parameters(("x", typeof(string)), ("n", typeof(int))));
        Start(SdkMcpServer.Create("srv", readData, large, plain));

        var response = await McpResponseAsync("srv", """{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}""");

        Assert.Equal(
            """{"jsonrpc":"2.0","id":1,"result":{"tools":["""
            + """{"name":"read_data","description":"Read data from source","inputSchema":{"type":"object","properties":{"source":{"type":"string"}},"required":["source"]},"annotations":{"readOnlyHint":true,"openWorldHint":false}},"""
            + """{"name":"get_large_schema","description":"Returns a large DB schema.","inputSchema":{"type":"object","properties":{},"required":[]},"annotations":{"readOnlyHint":true},"_meta":{"anthropic/maxResultSizeChars":500000}},"""
            + """{"name":"plain","description":"Tool without annotations","inputSchema":{"type":"object","properties":{"x":{"type":"string"},"n":{"type":"integer"}},"required":["x","n"]}}"""
            + "]}}",
            response);
    }

    [Fact]
    public async Task ToolsList_NoTools_IsEmptyList()
    {
        Start(SdkMcpServer.Create("srv"));

        Assert.Equal("""{"jsonrpc":"2.0","id":1,"result":{"tools":[]}}""", await McpResponseAsync("srv", """{"jsonrpc":"2.0","id":1,"method":"tools/list"}"""));
    }

    [Fact]
    public async Task ToolsCall_Success_PassesArgumentsAndReturnsContent()
    {
        JsonElement? received = null;
        var add = SdkMcpTool.Create(
            "add",
            "Add two numbers",
            McpSchema.Parameters(("a", typeof(double)), ("b", typeof(double))),
            args =>
            {
                received = args.Clone();
                return ValueTask.FromResult(McpToolResult.Text($"The sum is {args.GetProperty("a").GetDouble() + args.GetProperty("b").GetDouble()}"));
            });
        Start(SdkMcpServer.Create("calculator", add));

        var response = await SendAsync("srv", """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"add","arguments":{"a":5,"b":3}}}""");

        Assert.Equal(
            """{"type":"control_response","response":{"subtype":"success","request_id":"mcp-1","response":{"mcp_response":"""
            + """{"jsonrpc":"2.0","id":2,"result":{"content":[{"type":"text","text":"The sum is 8"}],"isError":false}}}}}""",
            response);
        Assert.Equal("""{"a":5,"b":3}""", received!.Value.GetRawText());
    }

    [Fact]
    public async Task ToolsCall_ContentTypes_ConvertLikePython()
    {
        var mixed = Tool("get_mixed", "Returns mixed content", McpSchema.Parameters(), _ => new McpToolResult(
        [
            new McpTextContent("Here is the document:"),
            new McpImageContent("iVBORw0KGgo=", "image/png"),
            new McpResourceLinkContent("Report", "https://example.com/report"),
            new McpResourceLinkContent("My Document", "https://example.com/doc.pdf") { Description = "A test document" },
            new McpResourceLinkContent(null, null),
            new McpEmbeddedResourceContent("file:///test.txt") { Text = "File contents here", MimeType = "text/plain" },
            new McpEmbeddedResourceContent("file:///image.png") { Blob = "iVBORw0KGgo=", MimeType = "image/png" },
        ]));
        Start(SdkMcpServer.Create("srv", mixed));

        var response = await McpResponseAsync("srv", """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"get_mixed"}}""");

        Assert.Equal(
            """{"jsonrpc":"2.0","id":4,"result":{"content":["""
            + """{"type":"text","text":"Here is the document:"},"""
            + """{"type":"image","data":"iVBORw0KGgo=","mimeType":"image/png"},"""
            + """{"type":"text","text":"Report\nhttps://example.com/report"},"""
            + """{"type":"text","text":"My Document\nhttps://example.com/doc.pdf\nA test document"},"""
            + """{"type":"text","text":"Resource link"},"""
            + """{"type":"text","text":"File contents here"}"""
            + """],"isError":false}}""",
            response);
    }

    [Fact]
    public async Task ToolsCall_HandlerReturnsIsError_IsPropagated()
    {
        var divide = Tool("divide", "Divide", McpSchema.Parameters(("a", typeof(double)), ("b", typeof(double))), _ => McpToolResult.Error("Division by zero"));
        Start(SdkMcpServer.Create("srv", divide));

        var response = await McpResponseAsync("srv", """{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"divide","arguments":{"a":1,"b":0}}}""");

        Assert.Equal("""{"jsonrpc":"2.0","id":5,"result":{"content":[{"type":"text","text":"Division by zero"}],"isError":true}}""", response);
    }

    [Fact]
    public async Task ToolsCall_HandlerThrows_IsAnErrorResultNotAProtocolError()
    {
        var fail = Tool("fail", "Always fails", McpSchema.Parameters(), _ => throw new InvalidOperationException("Expected error"));
        Start(SdkMcpServer.Create("srv", fail));

        var response = await McpResponseAsync("srv", """{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"fail","arguments":{}}}""");

        Assert.Equal("""{"jsonrpc":"2.0","id":6,"result":{"content":[{"type":"text","text":"Expected error"}],"isError":true}}""", response);
    }

    [Fact]
    public async Task ToolsCall_HandlerReturnsNullContentItem_IsAnErrorResult()
    {
        var sloppy = Tool("sloppy", "Bad payload", McpSchema.Parameters(), _ => new McpToolResult([null!]));
        Start(SdkMcpServer.Create("srv", sloppy));

        var response = await McpResponseAsync("srv", """{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"sloppy"}}""");

        Assert.Equal(Escaped("""{"jsonrpc":"2.0","id":6,"result":{"content":[{"type":"text","text":"Tool 'sloppy' returned a result with missing content"}],"isError":true}}"""), response);
    }

    [Fact]
    public async Task ToolsCall_UnknownTool_IsAnErrorResult()
    {
        Start(SdkMcpServer.Create("srv", Tool("known", "Known", McpSchema.Parameters())));

        var response = await McpResponseAsync("srv", """{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"mystery","arguments":{}}}""");

        Assert.Equal(Escaped("""{"jsonrpc":"2.0","id":7,"result":{"content":[{"type":"text","text":"Tool 'mystery' not found"}],"isError":true}}"""), response);
    }

    [Theory]
    [InlineData("""{"a":1}""", "Input validation error: 'b' is a required property")]
    [InlineData("""{"a":1,"b":"two"}""", "Input validation error: 'two' is not of type 'number'")]
    [InlineData("""{"a":"x"}""", "Input validation error: 'b' is a required property")]
    public async Task ToolsCall_InvalidArguments_AreRejectedBeforeTheHandlerRuns(string arguments, string expected)
    {
        var calls = 0;
        var add = Tool("add", "Add", McpSchema.Parameters(("a", typeof(double)), ("b", typeof(double))), _ =>
        {
            calls++;
            return McpToolResult.Text("ran");
        });
        Start(SdkMcpServer.Create("srv", add));

        var response = await McpResponseAsync("srv", $$$"""{"jsonrpc":"2.0","id":8,"method":"tools/call","params":{"name":"add","arguments":{{{arguments}}}}}""");

        Assert.Equal($$$"""{"jsonrpc":"2.0","id":8,"result":{"content":[{"type":"text","text":"{{{Escaped(expected)}}}"}],"isError":true}}""", response);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task ToolsCall_InvalidSchema_IsAnErrorResult()
    {
        var broken = Tool("broken", "Invalid schema", Json("""{"type":"object","properties":{"x":{"type":"bogus"}}}"""));
        Start(SdkMcpServer.Create("srv", broken));

        var response = await McpResponseAsync("srv", """{"jsonrpc":"2.0","id":9,"method":"tools/call","params":{"name":"broken","arguments":{"x":1}}}""");

        Assert.Equal(Escaped("""{"jsonrpc":"2.0","id":9,"result":{"content":[{"type":"text","text":"'bogus' is not valid under any of the given schemas"}],"isError":true}}"""), response);
    }

    [Fact]
    public async Task ToolsCall_MissingName_IsInvalidParams()
    {
        Start(SdkMcpServer.Create("srv"));

        var response = await McpResponseAsync("srv", """{"jsonrpc":"2.0","id":10,"method":"tools/call","params":{}}""");

        Assert.Equal("""{"jsonrpc":"2.0","id":10,"error":{"code":-32602,"message":"Invalid request parameters","data":""}}""", response);
    }

    [Fact]
    public async Task ToolsCall_CancelledNotification_CancelsTheHandlerAndAnswersRequestCancelled()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slow = SdkMcpTool.Create("slow", "Waits", McpSchema.Parameters(), async (_, cancellationToken) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return McpToolResult.Text("unreachable");
        });
        Start(SdkMcpServer.Create("srv", slow));
        await DrainInitializeAsync();

        Push("call", "srv", """{"jsonrpc":"2.0","id":11,"method":"tools/call","params":{"name":"slow"}}""");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Push("cancel", "srv", """{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":11,"reason":"user"}}""");
        var responses = new[] { await _transport.NextWriteAsync(), await _transport.NextWriteAsync() }
            .ToDictionary(r => r.GetProperty("response").GetProperty("request_id").GetString()!, r => McpResponseOf(r));

        Assert.Equal("""{"jsonrpc":"2.0","result":{}}""", responses["cancel"]);
        Assert.Equal("""{"jsonrpc":"2.0","id":11,"error":{"code":-32800,"message":"Request cancelled"}}""", responses["call"]);
    }

    [Fact]
    public async Task ToolsCall_ControlCancelRequest_SendsNoResponse()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slow = SdkMcpTool.Create("slow", "Waits", McpSchema.Parameters(), async (_, cancellationToken) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return McpToolResult.Text("unreachable");
        });
        Start(SdkMcpServer.Create("srv", slow));
        await DrainInitializeAsync();

        Push("call", "srv", """{"jsonrpc":"2.0","id":12,"method":"tools/call","params":{"name":"slow"}}""");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _transport.Push("""{"type":"control_cancel_request","request_id":"call"}""");
        await Task.Delay(200);

        Assert.True(_transport.HasNoPendingWrites);
    }

    [Fact]
    public async Task UnknownMethod_IsMethodNotFound()
    {
        Start(SdkMcpServer.Create("srv"));

        var response = await McpResponseAsync("srv", """{"jsonrpc":"2.0","id":13,"method":"resources/list","params":{}}""");

        Assert.Equal("""{"jsonrpc":"2.0","id":13,"error":{"code":-32601,"message":"Method not found","data":"resources/list"}}""", response);
    }

    [Fact]
    public async Task UnknownServer_IsJsonRpcErrorInsideASuccessResponse()
    {
        Start(SdkMcpServer.Create("srv"));

        var response = await SendAsync("missing", """{"jsonrpc":"2.0","id":14,"method":"tools/list","params":{}}""");

        Assert.Equal(
            """{"type":"control_response","response":{"subtype":"success","request_id":"mcp-1","response":{"mcp_response":"""
            + Escaped("""{"jsonrpc":"2.0","id":14,"error":{"code":-32601,"message":"Server 'missing' not found"}}}}}"""),
            response);
    }

    [Fact]
    public async Task UnknownServer_Notification_EchoesNullId()
    {
        Start(SdkMcpServer.Create("srv"));

        var response = await McpResponseAsync("missing", """{"jsonrpc":"2.0","method":"notifications/initialized"}""");

        Assert.Equal(Escaped("""{"jsonrpc":"2.0","id":null,"error":{"code":-32601,"message":"Server 'missing' not found"}}"""), response);
    }

    [Fact]
    public async Task MalformedMessage_IsInternalError()
    {
        Start(SdkMcpServer.Create("srv"));

        var response = await McpResponseAsync("srv", """{"jsonrpc":"2.0","id":5}""");

        Assert.Equal(
            """{"jsonrpc":"2.0","id":5,"error":{"code":-32603,"message":"Invalid JSON-RPC message: expected a request, notification or response"}}""",
            response);
    }

    [Fact]
    public async Task NonIntegerId_IsTreatedAsNotification()
    {
        Start(SdkMcpServer.Create("srv"));

        Assert.Equal("""{"jsonrpc":"2.0","result":{}}""", await McpResponseAsync("srv", """{"jsonrpc":"2.0","id":2.5,"method":"tools/list"}"""));
    }

    [Theory]
    [InlineData("""{"subtype":"mcp_message","message":{"jsonrpc":"2.0","id":1,"method":"ping"}}""")]
    [InlineData("""{"subtype":"mcp_message","server_name":"srv","message":{}}""")]
    [InlineData("""{"subtype":"mcp_message","server_name":"","message":{"jsonrpc":"2.0","id":1,"method":"ping"}}""")]
    public async Task MissingServerNameOrMessage_IsControlError(string request)
    {
        Start(SdkMcpServer.Create("srv"), initialize: false);

        _transport.Push($$"""{"type":"control_request","request_id":"bad","request":{{request}}}""");
        var response = await _transport.NextWriteAsync();

        Assert.Equal(
            """{"type":"control_response","response":{"subtype":"error","request_id":"bad","error":"Missing server_name or message for MCP request"}}""",
            response.GetRawText());
    }

    [Fact]
    public void HasBidirectionalNeeds_OnlyWithSdkServerOrCanUseTool()
    {
        Assert.False(new ControlProtocol(_transport, canUseTool: null, new Dictionary<string, McpServerConfig> { ["fs"] = new McpStdioServerConfig("npx") }).HasBidirectionalNeeds);
        Assert.True(new ControlProtocol(_transport, canUseTool: null, new Dictionary<string, McpServerConfig> { ["srv"] = SdkMcpServer.Create("srv") }).HasBidirectionalNeeds);
    }

    private static SdkMcpTool Tool(string name, string description, JsonElement schema, Func<JsonElement, McpToolResult>? handler = null) =>
        SdkMcpTool.Create(name, description, schema, args => ValueTask.FromResult((handler ?? (_ => McpToolResult.Text("ok")))(args)));

    private static JsonElement Json(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    /// <summary>SDK 用 Utf8JsonWriter 預設的編碼寫 JSON，單引號寫成 Unicode 跳脫（跟其他 control 訊息一樣，解析後相同）。</summary>
    private static string Escaped(string text) => text.Replace("'", @"\u" + "0027", StringComparison.Ordinal);

    /// <summary>
    /// server 都用 <c>srv</c> 這個鍵註冊，跟 Python 測試的 <c>connected()</c> 一樣；另一個鍵 <c>calc</c> 也指到同一個設定（是另一個 session，不會被握手）。
    /// <paramref name="initialize"/> 時先送 initialize（mcp 握手前不接受 tools/*），回應由下一次送訊息前的 <see cref="DrainInitializeAsync"/> 取走。
    /// </summary>
    private void Start(McpSdkServerConfig server, bool initialize = true)
    {
        _protocol = new ControlProtocol(
            _transport,
            canUseTool: null,
            new Dictionary<string, McpServerConfig> { ["srv"] = server, ["calc"] = server })
        {
            HandlerShutdownTimeout = TimeSpan.FromSeconds(1),
        };
        _protocol.Start();
        if (initialize)
        {
            Push("init", "srv", Initialize);
            _initializing = true;
        }
    }

    /// <summary>等 Start 送出的 initialize 回應，免得它跟後面的請求同時處理、搶在握手之前。</summary>
    private async Task DrainInitializeAsync()
    {
        if (_initializing)
        {
            _initializing = false;
            var response = await _transport.NextWriteAsync();
            Assert.Equal("init", response.GetProperty("response").GetProperty("request_id").GetString());
        }
    }

    private void Push(string requestId, string server, string message) =>
        _transport.Push($$$"""{"type":"control_request","request_id":"{{{requestId}}}","request":{"subtype":"mcp_message","server_name":"{{{server}}}","message":{{{message}}}}}""");

    /// <summary>送一則 mcp_message，回傳 SDK 寫出的整個 control response。</summary>
    private async Task<string> SendAsync(string server, string message)
    {
        await DrainInitializeAsync();
        Push($"mcp-{++_requestCounter}", server, message);
        return (await _transport.NextWriteAsync()).GetRawText();
    }

    /// <summary>送一則 mcp_message，只回傳裡面的 JSON-RPC 回應。</summary>
    private async Task<string> McpResponseAsync(string server, string message)
    {
        await DrainInitializeAsync();
        Push($"mcp-{++_requestCounter}", server, message);
        return McpResponseOf(await _transport.NextWriteAsync());
    }

    private static string McpResponseOf(JsonElement controlResponse)
    {
        var response = controlResponse.GetProperty("response");
        Assert.Equal("success", response.GetProperty("subtype").GetString());
        return response.GetProperty("response").GetProperty("mcp_response").GetRawText();
    }
}
