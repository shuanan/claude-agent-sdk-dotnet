using System.Text.Json;
using System.Text.Json.Nodes;
using PamisWang.ClaudeAgentSdk.Mcp;
using PamisWang.ClaudeAgentSdk.Protocol;

namespace PamisWang.ClaudeAgentSdk.Tests.Protocol;

/// <summary>
/// 跟真的 Python SDK 對照：把 Python SDK（這個 repo 對照的版本）搭配 mcp 2.3.0，經 <c>Query._handle_sdk_mcp_request</c> 錄下的
/// JSON-RPC 請求與回應依序重播，SDK 寫出的 <c>mcp_response</c> 要跟 Python 的回應在 JSON 上完全相同（鍵的順序與跳脫可以不同）。
/// Python 那邊的 server：<c>create_sdk_mcp_server("calc", "2.0.0", [add, boom, bad])</c>，註冊的鍵是 <c>calc</c>。
/// Python 對通知回 <see langword="null"/>（沒有 JSON-RPC 回應）；query.py 再把它包成 <c>{"jsonrpc":"2.0","result":{}}</c> 回給 CLI，這裡比的是包好的那個。
/// </summary>
public sealed class SdkMcpGoldenParityTests : IAsyncLifetime, IAsyncDisposable
{
    // 依錄製的順序：前面的握手會影響後面的請求，所以要在同一個 session 裡照順序重播
    private static readonly (string Request, string? Response)[] Golden =
    [
        (
            """{"jsonrpc": "2.0", "id": 0, "method": "initialize", "params": {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "claude-code", "version": "2.1.312"}}}""",
            """{"jsonrpc": "2.0", "id": 0, "result": {"capabilities": {"tools": {"listChanged": false}}, "protocolVersion": "2025-06-18", "serverInfo": {"name": "calc", "version": "2.0.0"}}}"""
        ),
        (
            """{"jsonrpc": "2.0", "method": "notifications/initialized"}""",
            null
        ),
        (
            """{"jsonrpc": "2.0", "id": 1, "method": "tools/list", "params": {}}""",
            """{"jsonrpc": "2.0", "id": 1, "result": {"tools": [{"description": "Add two numbers", "inputSchema": {"properties": {"a": {"type": "number"}, "b": {"type": "number"}}, "required": ["a", "b"], "type": "object"}, "name": "add"}, {"description": "Always fails", "inputSchema": {"properties": {"x": {"type": "string"}}, "required": ["x"], "type": "object"}, "name": "boom"}, {"description": "Returns is_error", "inputSchema": {"properties": {}, "required": [], "type": "object"}, "name": "bad"}]}}"""
        ),
        (
            """{"jsonrpc": "2.0", "id": 2, "method": "tools/call", "params": {"name": "add", "arguments": {"a": 2, "b": 3}}}""",
            """{"jsonrpc": "2.0", "id": 2, "result": {"content": [{"text": "5", "type": "text"}], "isError": false}}"""
        ),
        (
            """{"jsonrpc": "2.0", "id": 3, "method": "tools/call", "params": {"name": "boom", "arguments": {"x": "y"}}}""",
            """{"jsonrpc": "2.0", "id": 3, "result": {"content": [{"text": "kaboom", "type": "text"}], "isError": true}}"""
        ),
        (
            """{"jsonrpc": "2.0", "id": 4, "method": "tools/call", "params": {"name": "bad", "arguments": {}}}""",
            """{"jsonrpc": "2.0", "id": 4, "result": {"content": [{"text": "nope", "type": "text"}], "isError": true}}"""
        ),
        (
            """{"jsonrpc": "2.0", "id": 5, "method": "tools/call", "params": {"name": "nosuch", "arguments": {}}}""",
            """{"jsonrpc": "2.0", "id": 5, "result": {"content": [{"text": "Tool 'nosuch' not found", "type": "text"}], "isError": true}}"""
        ),
        (
            """{"jsonrpc": "2.0", "id": 6, "method": "tools/call", "params": {"name": "add", "arguments": {"a": "str"}}}""",
            """{"jsonrpc": "2.0", "id": 6, "result": {"content": [{"text": "Input validation error: 'b' is a required property", "type": "text"}], "isError": true}}"""
        ),
        (
            """{"jsonrpc": "2.0", "id": 7, "method": "resources/list", "params": {}}""",
            """{"jsonrpc": "2.0", "id": 7, "error": {"code": -32601, "message": "Method not found", "data": "resources/list"}}"""
        ),
        (
            """{"jsonrpc": "2.0", "id": 8, "method": "bogus/method", "params": {}}""",
            """{"jsonrpc": "2.0", "id": 8, "error": {"code": -32601, "message": "Method not found", "data": "bogus/method"}}"""
        ),
        (
            """{"jsonrpc": "2.0", "id": 9, "method": "ping"}""",
            """{"jsonrpc": "2.0", "id": 9, "result": {}}"""
        ),
        (
            """{"jsonrpc": "2.0", "id": 10, "method": "initialize", "params": {"protocolVersion": "1999-01-01", "capabilities": {}, "clientInfo": {"name": "x", "version": "1"}}}""",
            """{"jsonrpc": "2.0", "id": 10, "result": {"capabilities": {"tools": {"listChanged": false}}, "protocolVersion": "2025-11-25", "serverInfo": {"name": "calc", "version": "2.0.0"}}}"""
        ),
    ];

    // Python 對沒有註冊的 server（"nope"）送 tools/list（id 1）的回應
    private const string UnknownServerResponse =
        """{"jsonrpc": "2.0", "id": 1, "error": {"code": -32601, "message": "Server 'nope' not found"}}""";

    // query.py 對通知的 control request 回的確認
    private const string NotificationAck = """{"jsonrpc":"2.0","result":{}}""";

    private readonly ScriptedTransport _transport = new();
    private ControlProtocol? _protocol;
    private int _requestCounter;

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
    public async Task ReplayGolden_EveryResponseMatchesPython()
    {
        Start();

        for (var i = 0; i < Golden.Length; i++)
        {
            var (request, expected) = Golden[i];
            var actual = await McpResponseAsync("calc", request);

            AssertJsonEqual(expected ?? NotificationAck, actual, $"golden #{i}: {request}");
        }
    }

    [Fact]
    public async Task ReplayGolden_UnknownServerMatchesPython()
    {
        Start();

        var actual = await McpResponseAsync("nope", Golden[2].Request);

        AssertJsonEqual(UnknownServerResponse, actual, "unknown server");
    }

    private static void AssertJsonEqual(string expected, string actual, string step)
    {
        var expectedNode = JsonNode.Parse(expected);
        var actualNode = JsonNode.Parse(actual);
        Assert.True(
            JsonNode.DeepEquals(expectedNode, actualNode),
            $"{step}\nexpected: {expectedNode?.ToJsonString()}\nactual:   {actualNode?.ToJsonString()}");
    }

    /// <summary>跟 golden.py 一樣的三個工具。</summary>
    private void Start()
    {
        var add = SdkMcpTool.Create(
            "add",
            "Add two numbers",
            McpSchema.Parameters(("a", typeof(double)), ("b", typeof(double))),
            args => ValueTask.FromResult(McpToolResult.Text($"{args.GetProperty("a").GetDouble() + args.GetProperty("b").GetDouble()}")));
        var boom = SdkMcpTool.Create(
            "boom",
            "Always fails",
            McpSchema.Parameters(("x", typeof(string))),
            _ => throw new InvalidOperationException("kaboom"));
        var bad = SdkMcpTool.Create(
            "bad",
            "Returns is_error",
            McpSchema.Parameters(),
            _ => ValueTask.FromResult(McpToolResult.Error("nope")));

        _protocol = new ControlProtocol(
            _transport,
            canUseTool: null,
            new Dictionary<string, McpServerConfig> { ["calc"] = SdkMcpServer.Create("calc", "2.0.0", add, boom, bad) })
        {
            HandlerShutdownTimeout = TimeSpan.FromSeconds(1),
        };
        _protocol.Start();
    }

    /// <summary>送一則 mcp_message、等它的回應（一次一則，跟 CLI 一樣），回傳包在 control response 裡的 JSON-RPC 回應。</summary>
    private async Task<string> McpResponseAsync(string server, string message)
    {
        var requestId = $"mcp-{++_requestCounter}";
        var compact = JsonNode.Parse(message)!.ToJsonString();
        _transport.Push($$$"""{"type":"control_request","request_id":"{{{requestId}}}","request":{"subtype":"mcp_message","server_name":"{{{server}}}","message":{{{compact}}}}}""");

        var response = (await _transport.NextWriteAsync()).GetProperty("response");
        Assert.Equal("success", response.GetProperty("subtype").GetString());
        Assert.Equal(requestId, response.GetProperty("request_id").GetString());
        return response.GetProperty("response").GetProperty("mcp_response").GetRawText();
    }
}
