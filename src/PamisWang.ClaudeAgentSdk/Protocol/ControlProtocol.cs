using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using PamisWang.ClaudeAgentSdk.Permissions;
using PamisWang.ClaudeAgentSdk.Transport;

namespace PamisWang.ClaudeAgentSdk.Protocol;

/// <summary>
/// 在 transport 上跑 control 協定：初始化握手、讀取迴圈分流、回覆 CLI 的權限詢問、送出中斷、換模型與套用設定。
/// 對應 Python SDK 的 <c>Query</c> 裡互動式 client 用到的部分；
/// 一次性 <c>query()</c> 用來判斷何時關 stdin 的 run 生命週期（session state、上限計時）不在這裡，互動式的 stdin 只在關閉時才關。
/// </summary>
internal sealed class ControlProtocol : IAsyncDisposable
{
    /// <summary>initialize 以外的 control request 等回應的時間上限，跟 Python SDK 一樣。</summary>
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(60);

    private static readonly JsonElement EmptyObject = CreateEmptyObject();

    private readonly ITransport _transport;
    private readonly Func<ToolPermissionRequest, CancellationToken, Task<PermissionResult>>? _canUseTool;

    // 一般訊息交給上層。不設上限：讀取迴圈若因為上層沒在讀而停住，control 回應也會跟著收不到，
    // 例如上層一邊等 interrupt 的回應、一邊沒在讀訊息，就會互等
    private readonly Channel<JsonElement> _messages =
        Channel.CreateUnbounded<JsonElement>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

    // SDK 送出、還在等 CLI 回應的 control request
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pendingResponses = new();

    // CLI 送來、還在處理的 control request；收到 control_cancel_request 時取消
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _inflightRequests = new();
    private readonly ConcurrentDictionary<Task, byte> _handlers = new();
    private readonly CancellationTokenSource _closing = new();

    // 最近一則訊息若是出錯的結果就記在這裡，只有讀取迴圈會讀寫
    private JsonElement? _lastErrorResult;

    private Task? _readLoop;
    private int _requestCounter;
    private volatile bool _closed;

    public ControlProtocol(
        ITransport transport,
        Func<ToolPermissionRequest, CancellationToken, Task<PermissionResult>>? canUseTool)
    {
        _transport = transport;
        _canUseTool = canUseTool;
    }

    /// <summary>關閉時等權限回呼收尾的時間上限。測試會調短。</summary>
    internal TimeSpan HandlerShutdownTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>開始讀取迴圈；要在送出任何 control request 之前呼叫。</summary>
    public void Start() => _readLoop ??= Task.Run(ReadLoopAsync);

    /// <summary>
    /// 初始化握手，回傳 CLI 的回應（指令、模型、帳號等資訊）。CLI 回應前不要送使用者訊息。
    /// 有 <paramref name="agents"/> 時跟 Python 一樣放在 <c>agents</c>，空的就不送。
    /// </summary>
    public Task<JsonElement> InitializeAsync(
        TimeSpan timeout,
        IReadOnlyDictionary<string, AgentDefinition>? agents = null,
        CancellationToken cancellationToken = default) =>
        SendControlRequestAsync(
            "initialize",
            writer =>
            {
                writer.WriteNull("hooks");
                if (agents is { Count: > 0 })
                {
                    writer.WriteStartObject("agents");
                    foreach (var (name, agent) in agents)
                    {
                        writer.WritePropertyName(name);
                        agent.WriteTo(writer);
                    }

                    writer.WriteEndObject();
                }
            },
            timeout,
            cancellationToken);

    /// <summary>中斷這一輪；對話還在，之後可以接著說。</summary>
    public Task InterruptAsync(CancellationToken cancellationToken = default) =>
        SendControlRequestAsync("interrupt", writeFields: null, DefaultRequestTimeout, cancellationToken);

    /// <summary>換模型，之後的回覆改用新的模型；<see langword="null"/> 回到預設。回傳 CLI 換過去的完整模型 ID，沒回報時是 <see langword="null"/>。</summary>
    public async Task<string?> SetModelAsync(string? model, CancellationToken cancellationToken = default)
    {
        var response = await SendControlRequestAsync(
                "set_model",
                writer => writer.WriteString("model", model),
                DefaultRequestTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        return GetString(response, "model");
    }

    /// <summary>換權限模式。</summary>
    public Task SetPermissionModeAsync(string mode, CancellationToken cancellationToken = default) =>
        SendControlRequestAsync(
            "set_permission_mode",
            writer => writer.WriteString("mode", mode),
            DefaultRequestTimeout,
            cancellationToken);

    /// <summary>把設定合併進這個 session 的 flag 設定層。</summary>
    public Task ApplyFlagSettingsAsync(JsonObject settings, CancellationToken cancellationToken = default) =>
        SendControlRequestAsync(
            "apply_flag_settings",
            writer =>
            {
                writer.WritePropertyName("settings");
                settings.WriteTo(writer);
            },
            DefaultRequestTimeout,
            cancellationToken);

    /// <summary>送出使用者說的一句話。互動式 client 的 session ID 跟 Python 一樣是 <c>default</c>，一次性的 <c>query()</c> 是空字串。</summary>
    public Task SendUserMessageAsync(string content, string sessionId = "default", CancellationToken cancellationToken = default) =>
        _transport.WriteAsync(ControlMessages.UserMessage(content, sessionId), cancellationToken);

    /// <summary>關閉 stdin，告訴 CLI 不會再有輸入；之後 CLI 處理完手上的訊息就會結束。</summary>
    public Task EndInputAsync(CancellationToken cancellationToken = default) =>
        _transport.EndInputAsync(cancellationToken);

    /// <summary>
    /// 讀取 control 協定以外的訊息，直到 CLI 的輸出結束。讀取失敗時丟出原本的例外，例如 <see cref="CliProcessException"/>。
    /// </summary>
    public async IAsyncEnumerable<JsonElement> ReceiveMessagesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (await _messages.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (_messages.Reader.TryRead(out var message))
            {
                yield return message;
            }
        }
    }

    /// <summary>送出 control request，等 CLI 回應；回傳回應的 <c>response</c> 物件。</summary>
    /// <exception cref="ClaudeAgentException">CLI 回應處理失敗。</exception>
    /// <exception cref="TimeoutException">逾時沒有回應。</exception>
    public async Task<JsonElement> SendControlRequestAsync(
        string subtype,
        Action<Utf8JsonWriter>? writeFields,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        if (_readLoop is null || _readLoop.IsCompleted)
        {
            throw new CliConnectionException("讀取迴圈沒有在跑，收不到回應：還沒呼叫 Start，或 CLI 的輸出已經結束。");
        }

        var requestId = $"req_{Interlocked.Increment(ref _requestCounter)}_{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4))}";
        // 回應由讀取迴圈完成；接續的程式不能在讀取迴圈上跑，不然會擋住後面的訊息
        var pending = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingResponses[requestId] = pending;
        try
        {
            await _transport.WriteAsync(ControlMessages.Request(requestId, subtype, writeFields), cancellationToken)
                .ConfigureAwait(false);
            return await pending.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException e)
        {
            throw new TimeoutException($"control request 逾時沒有回應：{subtype}", e);
        }
        finally
        {
            _pendingResponses.TryRemove(requestId, out _);
        }
    }

    /// <summary>停止讀取、取消還在處理的權限詢問，再關閉 transport。</summary>
    public async ValueTask DisposeAsync()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        await _closing.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(_handlers.Keys).WaitAsync(HandlerShutdownTimeout)
            .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        // transport 關掉 CLI 之後 stdout 會結束，讀取迴圈也就跟著結束
        await _transport.DisposeAsync().ConfigureAwait(false);
        if (_readLoop is not null)
        {
            await _readLoop.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        _messages.Writer.TryComplete();
        _closing.Dispose();
    }

    private async Task ReadLoopAsync()
    {
        Exception? failure = null;
        try
        {
            await foreach (var message in _transport.ReadMessagesAsync(_closing.Token).ConfigureAwait(false))
            {
                if (_closed)
                {
                    break;
                }

                Route(message);
            }
        }
#pragma warning disable CA1031 // 讀取失敗要交給上層與等回應的人，不能讓它在背景消失
        catch (Exception e)
#pragma warning restore CA1031
        {
            // 主動關閉時 transport 被拆掉，那時的錯誤是預期中的
            if (!_closed)
            {
                // CLI 送出出錯的結果後會故意以非零結束（給 shell script 用），結束代碼本身沒有更多資訊；
                // 跟 Python 一樣換成帶著結果內容的 CliResultException。還在等的 control request（例如被拒絕的
                // resume 時的 initialize）也會拿到它
                failure = e is CliProcessException processException && _lastErrorResult is { } errorResult
                    ? new CliResultException(errorResult, processException)
                    : e;
            }
        }
        finally
        {
            // 還在等回應的 control request 不必等到逾時
            var pendingError = failure ?? new CliConnectionException("Claude Code 的輸出已經結束，收不到回應。");
            foreach (var pending in _pendingResponses.Values)
            {
                pending.TrySetException(pendingError);
            }

            _messages.Writer.TryComplete(failure);
        }
    }

    private void Route(JsonElement message)
    {
        switch (GetString(message, "type"))
        {
            case "control_response":
                if (message.TryGetProperty("response", out var response))
                {
                    CompletePendingRequest(response);
                }

                return;

            case "control_request":
                if (GetString(message, "request_id") is { } requestId && message.TryGetProperty("request", out var request))
                {
                    StartControlRequestHandler(requestId, request);
                }

                return;

            case "control_cancel_request":
                // 被撤回的請求不回覆：CLI 已經不等了
                if (GetString(message, "request_id") is { } cancelledId && _inflightRequests.TryRemove(cancelledId, out var inflight))
                {
                    CancelQuietly(inflight);
                }

                return;

            case "transcript_mirror":
                // 只有 session store 會用到，這裡不支援
                return;

            case "system" when GetString(message, "subtype") == "session_state_changed"
                && message.TryGetProperty("sdk_host_only", out var hostOnly) && hostOnly.ValueKind == JsonValueKind.True:
                // CLAUDE_CODE_SDK_READS_SESSION_STATE 要求才送的訊框，呼叫端沒有要求，不往上送
                return;

            default:
                TrackErrorResult(message);
                _messages.Writer.TryWrite(message);
                return;
        }
    }

    /// <summary>
    /// 記住最近一則訊息是不是出錯的結果。對應 Python 的 <c>_last_error_result</c>：
    /// 結果訊息依 <c>is_error</c> 記住或清掉；其他訊息代表對話往下走了，之後的異常結束是新的錯誤，也清掉。
    /// 一輪結束後的 <c>session_state_changed</c> 不算。control 訊息在這之前就分流掉了，同樣不算。
    /// </summary>
    private void TrackErrorResult(JsonElement message)
    {
        var type = GetString(message, "type");
        if (type == "result")
        {
            _lastErrorResult = message.TryGetProperty("is_error", out var isError) && IsTruthy(isError) ? message : null;
        }
        else if (!(type == "system" && GetString(message, "subtype") == "session_state_changed"))
        {
            _lastErrorResult = null;
        }
    }

    /// <summary>跟 Python 的真假值判斷一樣。</summary>
    private static bool IsTruthy(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.Number => value.GetDouble() != 0,
        JsonValueKind.String => value.GetString()!.Length > 0,
        JsonValueKind.Array => value.GetArrayLength() > 0,
        JsonValueKind.Object => value.EnumerateObject().Any(),
        _ => false,
    };

    private void CompletePendingRequest(JsonElement response)
    {
        if (GetString(response, "request_id") is not { } requestId || !_pendingResponses.TryRemove(requestId, out var pending))
        {
            return;
        }

        if (GetString(response, "subtype") == "error")
        {
            pending.TrySetException(new ClaudeAgentException(GetString(response, "error") ?? "CLI 回報錯誤，但沒有說明。"));
        }
        else
        {
            pending.TrySetResult(response.TryGetProperty("response", out var data) && data.ValueKind == JsonValueKind.Object
                ? data
                : EmptyObject);
        }
    }

    private void StartControlRequestHandler(string requestId, JsonElement request)
    {
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        _inflightRequests[requestId] = cancellation;

        // 另外跑，不在讀取迴圈裡等：權限詢問可能等使用者好幾分鐘，迴圈卡住就收不到中斷的回應
        var handler = Task.Run(() => HandleControlRequestAsync(requestId, request, cancellation));
        _handlers.TryAdd(handler, 0);
        handler.ContinueWith(
            static (task, handlers) => ((ConcurrentDictionary<Task, byte>)handlers!).TryRemove(task, out _),
            _handlers,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task HandleControlRequestAsync(string requestId, JsonElement request, CancellationTokenSource cancellation)
    {
        var cancellationToken = cancellation.Token;
        try
        {
            string response;
            try
            {
                var subtype = GetString(request, "subtype");
                response = subtype switch
                {
                    "can_use_tool" => ControlMessages.Success(
                        requestId,
                        await AnswerPermissionAsync(request, cancellationToken).ConfigureAwait(false)),
                    _ => throw new NotSupportedException($"不支援的 control request：{subtype}"),
                };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // 任何錯誤都要回覆 CLI，不然它會一直等
            catch (Exception e)
#pragma warning restore CA1031
            {
                response = ControlMessages.Error(requestId, e.Message);
            }

            // 回呼沒理會取消、照樣回傳了結果：請求已經撤回，不送
            if (!cancellationToken.IsCancellationRequested)
            {
                await _transport.WriteAsync(response, CancellationToken.None).ConfigureAwait(false);
            }
        }
#pragma warning disable CA1031 // 寫不出去代表 transport 已經壞了，讀取迴圈會回報
        catch (Exception)
#pragma warning restore CA1031
        {
        }
        finally
        {
            _inflightRequests.TryRemove(KeyValuePair.Create(requestId, cancellation));
            cancellation.Dispose();
        }
    }

    /// <summary>呼叫 CanUseTool，回傳寫出回應內容的方法。對應 Python 的 <c>_handle_control_request</c> 裡 can_use_tool 的部分。</summary>
    private async Task<Action<Utf8JsonWriter>> AnswerPermissionAsync(JsonElement request, CancellationToken cancellationToken)
    {
        if (_canUseTool is null)
        {
            throw new InvalidOperationException("CLI 詢問了工具權限，但沒有設定 ClaudeAgentOptions.CanUseTool。");
        }

        var input = request.TryGetProperty("input", out var value) ? value : EmptyObject;
        var permissionRequest = new ToolPermissionRequest
        {
            ToolName = GetString(request, "tool_name") ?? "",
            Input = input,
            Suggestions = request.TryGetProperty("permission_suggestions", out var suggestions) && suggestions.ValueKind == JsonValueKind.Array
                ? [.. suggestions.EnumerateArray().Select(PermissionUpdate.FromJson)]
                : [],
            ToolUseId = GetString(request, "tool_use_id"),
            AgentId = GetString(request, "agent_id"),
            BlockedPath = GetString(request, "blocked_path"),
            DecisionReason = GetString(request, "decision_reason"),
            Title = GetString(request, "title"),
            DisplayName = GetString(request, "display_name"),
            Description = GetString(request, "description"),
        };

        var result = await _canUseTool(permissionRequest, cancellationToken).ConfigureAwait(false);
        return result switch
        {
            PermissionResultAllow allow => writer =>
            {
                writer.WriteString("behavior", "allow");
                writer.WritePropertyName("updatedInput");
                (allow.UpdatedInput ?? input).WriteTo(writer);
                if (allow.UpdatedPermissions is not null)
                {
                    writer.WriteStartArray("updatedPermissions");
                    foreach (var update in allow.UpdatedPermissions)
                    {
                        update.WriteTo(writer);
                    }

                    writer.WriteEndArray();
                }
            },
            PermissionResultDeny deny => writer =>
            {
                writer.WriteString("behavior", "deny");
                writer.WriteString("message", deny.Message);
                if (deny.Interrupt)
                {
                    writer.WriteBoolean("interrupt", true);
                }
            },
            _ => throw new InvalidOperationException("CanUseTool 要回傳 PermissionResultAllow 或 PermissionResultDeny。"),
        };
    }

    private static void CancelQuietly(CancellationTokenSource cancellation)
    {
        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 處理剛好在這之前結束了
        }
    }

    private static JsonElement CreateEmptyObject()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
