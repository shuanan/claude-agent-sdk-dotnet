using System.Collections.Concurrent;
using System.Text.Json;
using PamisWang.ClaudeAgentSdk.Protocol;

namespace PamisWang.ClaudeAgentSdk.Mcp;

/// <summary>
/// 回答 CLI 透過 <c>mcp_message</c> 送給一個 SDK MCP server 的 JSON-RPC 訊息。
/// Python 把訊息交給 <c>mcp</c> 套件的 lowlevel <c>Server</c>（經 <c>SdkMcpBridge</c>），工具的語意由 <c>create_sdk_mcp_server</c> 的 <c>run_tool</c> 決定；
/// 這裡直接實作同樣的方法與回應格式（以 Python SDK 搭配 mcp 2.3.0 實際送出的為準，見 <c>SdkMcpGoldenParityTests</c>）：
/// <c>initialize</c>、<c>ping</c>、<c>tools/list</c>、<c>tools/call</c>，通知與回應不回覆，<c>notifications/cancelled</c> 取消還在跑的工具，
/// 其他方法回 <c>Method not found</c>（<c>data</c> 是方法名稱）。跟 mcp 一樣，握手之前的 <c>tools/*</c> 回 <c>Invalid request parameters</c>。
/// </summary>
internal sealed class SdkMcpServerSession
{
    /// <summary>mcp 2.3.0 握手能談的協定版本（<c>HANDSHAKE_PROTOCOL_VERSIONS</c>）；CLI 要的版本在這裡面就照它，不然回最新的 2025-11-25。</summary>
    internal static readonly string[] SupportedProtocolVersions = ["2024-11-05", "2025-03-26", "2025-06-18", "2025-11-25"];

    private const int MethodNotFound = -32601;
    private const int InvalidParams = -32602;

    // 被 CLI 用 notifications/cancelled 撤回的呼叫，回這個錯誤；跟 Python 的 _REQUEST_CANCELLED 一樣
    private const int RequestCancelled = -32800;

    private static readonly JsonElement EmptyObject = CreateEmptyObject();

    private readonly McpSdkServerConfig _config;
    private readonly Dictionary<string, SdkMcpTool> _toolsByName = [];
    private readonly string _toolsListResult;

    // 還在跑的 tools/call，鍵是 JSON-RPC id 的原始 JSON 文字
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _runningCalls = new();

    // 收到過 initialize 或 notifications/initialized；對應 mcp 的 initialize_accepted
    private volatile bool _initialized;

    public SdkMcpServerSession(McpSdkServerConfig config)
    {
        _config = config;
        foreach (var tool in config.Tools)
        {
            // 跟 Python 的 dict comprehension 一樣，同名的工具後面的蓋過前面的
            _toolsByName[tool.Name] = tool;
        }

        _toolsListResult = JsonWriting.ToCompactString(WriteToolsList);
    }

    /// <summary>
    /// 處理一則訊息。請求回傳 JSON-RPC 回應（一段 JSON），通知與回應回傳 <see langword="null"/>。
    /// <paramref name="cancellationToken"/> 取消（CLI 撤回 control request 或 client 關閉）時丟出 <see cref="OperationCanceledException"/>，不回覆。
    /// </summary>
    /// <exception cref="InvalidOperationException">訊息不是合法的 JSON-RPC。</exception>
    public async Task<string?> HandleAsync(JsonElement message, CancellationToken cancellationToken)
    {
        var method = message.TryGetProperty("method", out var methodValue) && methodValue.ValueKind == JsonValueKind.String
            ? methodValue.GetString()!
            : null;
        var hasId = message.TryGetProperty("id", out var id);
        if (method is null)
        {
            // 給 server 的回應（server 從不發請求，直接丟掉）；兩者都不是就不是合法的 JSON-RPC
            if (hasId && (message.TryGetProperty("result", out _) || message.TryGetProperty("error", out _)))
            {
                return null;
            }

            throw new InvalidOperationException("Invalid JSON-RPC message: expected a request, notification or response");
        }

        // 跟 mcp 一樣：id 不是字串或整數的訊息當成通知
        if (!hasId || !IsRequestId(id))
        {
            HandleNotification(method, message);
            return null;
        }

        var parameters = message.TryGetProperty("params", out var value) && value.ValueKind == JsonValueKind.Object ? value : default;
        if (method is not ("initialize" or "ping" or "tools/list" or "tools/call"))
        {
            // 跟 mcp 一樣，沒有處理的方法不論握手了沒都是 Method not found，data 是方法名稱
            return JsonRpcError(id, MethodNotFound, "Method not found", data: method);
        }

        // mcp 的握手關卡：initialize（或 notifications/initialized）之前只接受 ping 與 initialize
        if (method is "tools/list" or "tools/call" && !_initialized)
        {
            return InvalidParameters(id);
        }

        return method switch
        {
            "initialize" => Initialize(id, parameters),
            "ping" => Result(id, writer =>
            {
                writer.WriteStartObject();
                writer.WriteEndObject();
            }),
            "tools/list" => Result(id, writer => writer.WriteRawValue(_toolsListResult)),
            _ => await CallToolAsync(id, parameters, cancellationToken).ConfigureAwait(false),
        };
    }

    /// <summary>JSON-RPC 錯誤回應，<c>id</c> 原樣送回；沒有 id 時是 <c>null</c>。<paramref name="data"/> 是 <see langword="null"/> 時不寫 <c>data</c>。</summary>
    public static string JsonRpcError(JsonElement id, int code, string message, string? data = null) =>
        JsonWriting.ToCompactString(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            WriteId(writer, id);
            writer.WriteStartObject("error");
            writer.WriteNumber("code", code);
            writer.WriteString("message", message);
            writer.WriteOptional("data", data);

            writer.WriteEndObject();
            writer.WriteEndObject();
        });

    private static bool IsRequestId(JsonElement id) =>
        id.ValueKind == JsonValueKind.String || (id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out _));

    private static void WriteId(Utf8JsonWriter writer, JsonElement id)
    {
        writer.WritePropertyName("id");
        if (id.ValueKind == JsonValueKind.Undefined)
        {
            writer.WriteNullValue();
        }
        else
        {
            id.WriteTo(writer);
        }
    }

    private static string Result(JsonElement id, Action<Utf8JsonWriter> writeResult) =>
        JsonWriting.ToCompactString(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            WriteId(writer, id);
            writer.WritePropertyName("result");
            writeResult(writer);
            writer.WriteEndObject();
        });

    // mcp 參數不合格（或握手前就呼叫工具）時的錯誤，帶著空的 data
    private static string InvalidParameters(JsonElement id) => JsonRpcError(id, InvalidParams, "Invalid request parameters", data: "");

    private void HandleNotification(string method, JsonElement message)
    {
        // 單獨的 notifications/initialized 也算握手完成，跟 mcp 一樣；其他通知不需要做什麼
        if (method == "notifications/initialized")
        {
            _initialized = true;
        }
        else if (method == "notifications/cancelled"
            && message.TryGetProperty("params", out var parameters)
            && parameters.ValueKind == JsonValueKind.Object
            && parameters.TryGetProperty("requestId", out var requestId)
            && _runningCalls.TryGetValue(requestId.GetRawText(), out var running))
        {
            try
            {
                running.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 呼叫剛好在這之前結束了
            }
        }
    }

    private string Initialize(JsonElement id, JsonElement parameters)
    {
        // mcp 的 InitializeRequestParams：protocolVersion、capabilities、clientInfo 都必填
        if (parameters.ValueKind != JsonValueKind.Object
            || !parameters.TryGetProperty("protocolVersion", out var requested)
            || requested.ValueKind != JsonValueKind.String
            || !parameters.TryGetProperty("capabilities", out var capabilities)
            || capabilities.ValueKind != JsonValueKind.Object
            || !parameters.TryGetProperty("clientInfo", out var clientInfo)
            || clientInfo.ValueKind != JsonValueKind.Object)
        {
            return InvalidParameters(id);
        }

        _initialized = true;
        var version = SupportedProtocolVersions.Contains(requested.GetString())
            ? requested.GetString()!
            : SupportedProtocolVersions[^1];
        return Result(id, writer =>
        {
            // mcp 2.x 不寫空的 experimental，只有 tools
            writer.WriteStartObject();
            writer.WriteString("protocolVersion", version);
            writer.WriteStartObject("capabilities");
            writer.WriteStartObject("tools");
            writer.WriteBoolean("listChanged", false);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteStartObject("serverInfo");
            writer.WriteString("name", _config.Name);
            writer.WriteString("version", _config.Version);
            writer.WriteEndObject();
            writer.WriteEndObject();
        });
    }

    /// <summary><c>tools/list</c> 的結果。每個工具的欄位與順序跟 mcp 的 <c>Tool</c> 一樣：name、description、inputSchema、annotations、_meta。</summary>
    private void WriteToolsList(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteStartArray("tools");
        foreach (var tool in _config.Tools)
        {
            writer.WriteStartObject();
            writer.WriteString("name", tool.Name);
            writer.WriteString("description", tool.Description);
            writer.WritePropertyName("inputSchema");
            tool.InputSchema.WriteTo(writer);
            if (tool.Annotations is { } annotations)
            {
                // maxResultSizeChars 不是 MCP 的提示，Python 的 annotations 裡也不會出現，改放在 _meta
                writer.WriteStartObject("annotations");
                writer.WriteOptional("title", annotations.Title);
                writer.WriteOptional("readOnlyHint", annotations.ReadOnlyHint);
                writer.WriteOptional("destructiveHint", annotations.DestructiveHint);
                writer.WriteOptional("idempotentHint", annotations.IdempotentHint);
                writer.WriteOptional("openWorldHint", annotations.OpenWorldHint);
                writer.WriteEndObject();
                if (annotations.MaxResultSizeChars is { } maxResultSizeChars)
                {
                    writer.WriteStartObject("_meta");
                    writer.WriteNumber("anthropic/maxResultSizeChars", maxResultSizeChars);
                    writer.WriteEndObject();
                }
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    /// <summary>
    /// 執行工具。跟 Python 的 <c>run_tool</c> 一樣：找不到工具、參數不合格、處理函式丟出例外，都回出錯的結果讓 Claude 看得到，不回協定錯誤。
    /// </summary>
    private async Task<string> CallToolAsync(JsonElement id, JsonElement parameters, CancellationToken cancellationToken)
    {
        if (parameters.ValueKind != JsonValueKind.Object
            || !parameters.TryGetProperty("name", out var nameValue)
            || nameValue.ValueKind != JsonValueKind.String)
        {
            return InvalidParameters(id);
        }

        // 跟 Python 的 params.arguments or {} 一樣，沒給參數就是空物件
        var arguments = parameters.TryGetProperty("arguments", out var argumentsValue) ? argumentsValue : default;
        if (arguments.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            arguments = EmptyObject;
        }
        else if (arguments.ValueKind != JsonValueKind.Object)
        {
            return InvalidParameters(id);
        }

        var name = nameValue.GetString()!;
        if (!_toolsByName.TryGetValue(name, out var tool))
        {
            return ToolResult(id, McpToolResult.Error($"Tool '{name}' not found"));
        }

        var key = id.GetRawText();
        using var call = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (!_runningCalls.TryAdd(key, call))
        {
            // 跟 Python 一樣不讓兩個還沒回覆的請求共用 id，不然回應會對錯人
            throw new InvalidOperationException($"Request id {key} is already in flight");
        }

        try
        {
            McpToolResult result;
            try
            {
                if (McpInputValidator.Validate(tool.InputSchema, arguments) is { } validationError)
                {
                    return ToolResult(id, McpToolResult.Error($"Input validation error: {validationError}"));
                }

                result = await tool.Handler(arguments, call.Token).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"Tool '{name}' returned no result");
                if (result.Content is null || result.Content.Any(content => content is null))
                {
                    // 對應 Python 結果格式不對（例如少了 text）時變成出錯的結果
                    throw new InvalidOperationException($"Tool '{name}' returned a result with missing content");
                }
            }
            catch (OperationCanceledException) when (call.IsCancellationRequested)
            {
                // control request 被撤回或 client 關閉：不回覆；只有這次呼叫被 CLI 撤回：回 Request cancelled
                cancellationToken.ThrowIfCancellationRequested();
                return JsonRpcError(id, RequestCancelled, "Request cancelled");
            }
#pragma warning disable CA1031 // 跟 Python 一樣，工具的任何錯誤都變成出錯的結果交給 Claude
            catch (Exception e)
#pragma warning restore CA1031
            {
                result = McpToolResult.Error(e.Message);
            }

            return ToolResult(id, result);
        }
        finally
        {
            _runningCalls.TryRemove(KeyValuePair.Create(key, call));
        }
    }

    private static JsonElement CreateEmptyObject()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }

    private static string ToolResult(JsonElement id, McpToolResult result) =>
        Result(id, writer =>
        {
            writer.WriteStartObject();
            writer.WriteStartArray("content");
            foreach (var content in result.Content)
            {
                content.WriteTo(writer);
            }

            writer.WriteEndArray();
            writer.WriteBoolean("isError", result.IsError);
            writer.WriteEndObject();
        });
}
