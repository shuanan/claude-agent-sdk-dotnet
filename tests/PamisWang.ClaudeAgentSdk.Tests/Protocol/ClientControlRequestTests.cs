using System.Text.Json;
using System.Text.Json.Nodes;
using PamisWang.ClaudeAgentSdk.Mcp;
using PamisWang.ClaudeAgentSdk.Messages;

namespace PamisWang.ClaudeAgentSdk.Tests.Protocol;

/// <summary>
/// <see cref="ClaudeAgentClient"/> 對應 Python <c>ClaudeSDKClient</c> 的控制方法：送出的 control request 跟 Python <c>Query</c> 寫的一樣
/// （<c>rewind_files</c>、<c>mcp_reconnect</c>、<c>mcp_toggle</c>、<c>stop_task</c>、<c>mcp_status</c>、<c>get_context_usage</c>），
/// 回應怎麼交給呼叫端，以及 <c>get_server_info()</c>。
/// </summary>
public sealed class ClientControlRequestTests : IAsyncLifetime, IAsyncDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly ScriptedTransport _transport = new();
    private ClaudeAgentClient _client = null!;

    public async Task InitializeAsync()
    {
        _client = new ClaudeAgentClient(new ClaudeAgentOptions(), _transport);
        var connecting = _client.ConnectAsync();
        var initialize = await _transport.NextWriteAsync();
        Respond(initialize, """{"commands":[{"name":"compact"}],"output_style":"default"}""");
        await connecting.WaitAsync(Timeout);
    }

    Task IAsyncLifetime.DisposeAsync() => DisposeAsync().AsTask();

    public async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync();
        await _transport.DisposeAsync();
    }

    public static TheoryData<string, string> Requests => new()
    {
        { "rewind", """{"subtype":"rewind_files","user_message_id":"msg-uuid-1"}""" },
        { "reconnect", """{"subtype":"mcp_reconnect","serverName":"fs"}""" },
        { "toggle_off", """{"subtype":"mcp_toggle","serverName":"fs","enabled":false}""" },
        { "toggle_on", """{"subtype":"mcp_toggle","serverName":"fs","enabled":true}""" },
        { "stop_task", """{"subtype":"stop_task","task_id":"task-abc123"}""" },
        { "mcp_status", """{"subtype":"mcp_status"}""" },
        { "context_usage", """{"subtype":"get_context_usage"}""" },
    };

    [Theory]
    [MemberData(nameof(Requests))]
    public async Task Method_WritesPythonControlRequest(string method, string expectedRequest)
    {
        var calling = Call(method);
        var written = await _transport.NextWriteAsync();
        Respond(written, "{}");
        await calling.WaitAsync(Timeout);

        Assert.Equal("control_request", written.GetProperty("type").GetString());
        Assert.StartsWith("req_", written.GetProperty("request_id").GetString(), StringComparison.Ordinal);
        Assert.True(
            JsonNode.DeepEquals(JsonNode.Parse(expectedRequest), JsonNode.Parse(written.GetProperty("request").GetRawText())),
            written.GetProperty("request").GetRawText());
    }

    [Theory]
    [InlineData("rewind")]
    [InlineData("reconnect")]
    [InlineData("toggle_on")]
    [InlineData("stop_task")]
    public async Task Method_ErrorResponse_Throws(string method)
    {
        var calling = Call(method);
        var written = await _transport.NextWriteAsync();
        _transport.Push($$$"""{"type":"control_response","response":{"subtype":"error","request_id":"{{{written.GetProperty("request_id").GetString()}}}","error":"Server not found: fs"}}""");

        var error = await Assert.ThrowsAsync<ClaudeAgentException>(() => calling.WaitAsync(Timeout));
        Assert.Equal("Server not found: fs", error.Message);
    }

    [Fact]
    public async Task GetMcpStatusAsync_ParsesServersAndKeepsRaw()
    {
        var calling = _client.GetMcpStatusAsync();
        Respond(await _transport.NextWriteAsync(), """
            {"mcpServers":[
              {"name":"fs","status":"connected","serverInfo":{"name":"server-fs","version":"1.2.3"},
               "config":{"type":"stdio","command":"npx"},"scope":"project",
               "tools":[{"name":"read_file","description":"Read","annotations":{"readOnly":true,"openWorld":false}},{"name":"bare"}],
               "future":"x"},
              {"name":"remote","status":"failed","error":"connection refused"}
            ]}
            """);

        var status = await calling.WaitAsync(Timeout);

        Assert.Equal(2, status.McpServers.Count);
        var fs = status.McpServers[0];
        Assert.Equal(("fs", "connected", "project"), (fs.Name, fs.Status, fs.Scope));
        Assert.Equal(new McpServerInfo("server-fs", "1.2.3"), fs.ServerInfo);
        Assert.Equal("npx", fs.Config!.Value.GetProperty("command").GetString());
        Assert.Null(fs.Error);
        Assert.Collection(
            fs.Tools!,
            tool =>
            {
                Assert.Equal(("read_file", "Read"), (tool.Name, tool.Description));
                Assert.Equal(new McpServerToolAnnotations(true, null, false), tool.Annotations);
            },
            tool =>
            {
                Assert.Equal("bare", tool.Name);
                Assert.Null(tool.Description);
                Assert.Null(tool.Annotations);
            });
        Assert.Equal("x", fs.Raw.GetProperty("future").GetString());
        var remote = status.McpServers[1];
        Assert.Equal(("failed", "connection refused"), (remote.Status, remote.Error));
        Assert.Null(remote.Tools);
        Assert.Null(remote.ServerInfo);
        Assert.Equal(2, status.Raw.GetProperty("mcpServers").GetArrayLength());
    }

    [Fact]
    public async Task GetContextUsageAsync_ParsesTypedFieldsAndKeepsRaw()
    {
        var calling = _client.GetContextUsageAsync();
        Respond(await _transport.NextWriteAsync(), """
            {"categories":[{"name":"System prompt","tokens":3100,"color":"promptBorder"},{"name":"MCP tools","tokens":0,"color":"cyan","isDeferred":true}],
             "totalTokens":15000,"maxTokens":167000,"rawMaxTokens":200000,"percentage":7.5,"model":"claude-sonnet-5-5",
             "isAutoCompactEnabled":true,"autoCompactThreshold":167000,
             "memoryFiles":[{"path":"CLAUDE.md","type":"Project","tokens":120}],"mcpTools":[],"agents":[],"gridRows":[[]]}
            """);

        var usage = await calling.WaitAsync(Timeout);

        Assert.Equal(
            [new ContextUsageCategory("System prompt", 3100, "promptBorder", null), new ContextUsageCategory("MCP tools", 0, "cyan", true)],
            usage.Categories);
        Assert.Equal((15000L, 167000L, 200000L, 7.5), (usage.TotalTokens, usage.MaxTokens, usage.RawMaxTokens, usage.Percentage));
        Assert.Equal("claude-sonnet-5-5", usage.Model);
        Assert.True(usage.IsAutoCompactEnabled);
        Assert.Equal(167000L, usage.AutoCompactThreshold);
        Assert.Equal("CLAUDE.md", usage.Raw.GetProperty("memoryFiles")[0].GetProperty("path").GetString());
    }

    [Fact]
    public async Task GetContextUsageAsync_MissingFields_AreDefaults()
    {
        var calling = _client.GetContextUsageAsync();
        Respond(await _transport.NextWriteAsync(), "{}");

        var usage = await calling.WaitAsync(Timeout);

        Assert.Empty(usage.Categories);
        Assert.Equal(0, usage.TotalTokens);
        Assert.Null(usage.AutoCompactThreshold);
        Assert.Null(usage.Model);
    }

    [Fact]
    public async Task GetServerInfoAsync_ReturnsStoredInitializeResponseWithoutAskingCli()
    {
        var info = await _client.GetServerInfoAsync();

        Assert.Equal("default", info!.Value.GetProperty("output_style").GetString());
        Assert.Equal(info.Value.GetRawText(), _client.ServerInfo!.Value.GetRawText());
        Assert.True(_transport.HasNoPendingWrites);
    }

    [Fact]
    public async Task Methods_BeforeConnect_ThrowCliConnectionExceptionLikePython()
    {
        await using var client = new ClaudeAgentClient(new ClaudeAgentOptions(), new ScriptedTransport());

        Assert.Null(client.ServerInfo);
        await Assert.ThrowsAsync<CliConnectionException>(() => client.GetServerInfoAsync());
        await Assert.ThrowsAsync<CliConnectionException>(() => client.RewindFilesAsync("u"));
        await Assert.ThrowsAsync<CliConnectionException>(() => client.ReconnectMcpServerAsync("fs"));
        await Assert.ThrowsAsync<CliConnectionException>(() => client.ToggleMcpServerAsync("fs", enabled: true));
        await Assert.ThrowsAsync<CliConnectionException>(() => client.StopTaskAsync("t"));
        await Assert.ThrowsAsync<CliConnectionException>(() => client.GetMcpStatusAsync());
        await Assert.ThrowsAsync<CliConnectionException>(() => client.GetContextUsageAsync());
    }

    [Fact]
    public async Task ReplayedUserMessage_CarriesUuidForRewindFiles()
    {
        // CLI 加了 --replay-user-messages 時把送出的使用者訊息重播回來；它的 uuid 就是 RewindFilesAsync 要的 ID
        _transport.Push("""{"type":"user","message":{"role":"user","content":"改檔案"},"parent_tool_use_id":null,"session_id":"s","uuid":"user-uuid-7","isReplay":true}""");
        UserMessage? replayed = null;
        using var timeout = new CancellationTokenSource(Timeout);
        await foreach (var message in _client.ReceiveMessagesAsync().WithCancellation(timeout.Token))
        {
            replayed = Assert.IsType<UserMessage>(message);
            break;
        }

        var rewinding = _client.RewindFilesAsync(replayed!.Uuid!);
        var written = await _transport.NextWriteAsync();
        Respond(written, "{}");
        await rewinding.WaitAsync(Timeout);

        Assert.Equal("user-uuid-7", written.GetProperty("request").GetProperty("user_message_id").GetString());
    }

    private Task Call(string method) => method switch
    {
        "rewind" => _client.RewindFilesAsync("msg-uuid-1"),
        "reconnect" => _client.ReconnectMcpServerAsync("fs"),
        "toggle_off" => _client.ToggleMcpServerAsync("fs", enabled: false),
        "toggle_on" => _client.ToggleMcpServerAsync("fs", enabled: true),
        "stop_task" => _client.StopTaskAsync("task-abc123"),
        "mcp_status" => _client.GetMcpStatusAsync(),
        "context_usage" => _client.GetContextUsageAsync(),
        _ => throw new ArgumentOutOfRangeException(nameof(method), method, null),
    };

    private void Respond(JsonElement request, string responseJson) =>
        _transport.Push($$$"""{"type":"control_response","response":{"subtype":"success","request_id":"{{{request.GetProperty("request_id").GetString()}}}","response":{{{responseJson}}}}}""");
}
