using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using PamisWang.ClaudeAgentSdk.Hooks;
using PamisWang.ClaudeAgentSdk.Mcp;
using PamisWang.ClaudeAgentSdk.Permissions;
using PamisWang.ClaudeAgentSdk.Transport;

namespace PamisWang.ClaudeAgentSdk.Protocol;

/// <summary>
/// 在 transport 上跑 control 協定：初始化握手、讀取迴圈分流、回覆 CLI 的權限詢問、hook 回呼與 SDK MCP server 的訊息、
/// 送出中斷、換模型、套用設定、MCP server 與背景工作的控制。對應 Python SDK 的 <c>Query</c>，
/// 包括一次性 <c>query()</c> 用來判斷何時關 stdin 的 run 生命週期（背景工作、session state、上限計時）；互動式 client 的 stdin 只在關閉時才關。
/// </summary>
internal sealed class ControlProtocol : IAsyncDisposable
{
    /// <summary>initialize 以外的 control request 等回應的時間上限，跟 Python SDK 一樣。</summary>
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(60);

    private static readonly JsonElement EmptyObject = CreateEmptyObject();

    private readonly ITransport _transport;
    private readonly Func<ToolPermissionRequest, CancellationToken, Task<PermissionResult>>? _canUseTool;

    // SDK MCP server，鍵是 McpServers 的鍵，也就是 CLI 在 mcp_message 的 server_name 裡用的名字
    private readonly Dictionary<string, SdkMcpServerSession> _sdkMcpServers = [];

    // hook 回呼，鍵是 initialize 時登記給 CLI 的 hook_<n>
    private readonly Dictionary<string, HookCallback> _hookCallbacks = [];

    // initialize 的 hooks 內容：事件 → 各 matcher 的 matcher、回呼 ID 與逾時；沒有就送 null
    private readonly List<(string Event, List<(string? Matcher, List<string> CallbackIds, double? Timeout)> Matchers)> _hooksConfig = [];
    private readonly bool _hasHooks;

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

    // 一次性 query 什麼時候關 stdin（對應 Python Query 的 run 生命週期，#1088、#1190）。
    // 讀取迴圈、上限計時與等待關 stdin 的工作都會碰，一律在 _runGate 裡讀寫
    private readonly Lock _runGate = new();

    // run 結束時完成，等待關 stdin 的工作就醒來；run 結束後 CLI 又接了新的工作時換一個新的（ReopenRun）
    private TaskCompletionSource _runEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _resultReceived;

    // CLI 最近一次 session_state_changed 的 state；CLI 太舊、不送時是 null。
    // 回報 state 的 CLI 在背景 agent 還在跑、或它結束後還欠一輪時維持 running，不再欠任何一輪時才回 idle
    private string? _sessionState;

    // 主對話的一輪正在進行（assistant／stream_event 開始了、結果還沒到）：上限只算兩輪之間的等待，進行中不計時
    private bool _turnInProgress;

    // stdin 關了或讀取迴圈結束了：run 從此保持結束，沒有人能等重新開始的 run
    private bool _runFinal;

    // 開始了、還沒結束的工作 ID（只追蹤 TaskLifecycle.DeferringTaskTypes）。結果只代表一輪結束：
    // 背景工作還在跑時還要 stdin 回覆 hook、SDK MCP 的 control request，這時的結果不能關 stdin
    private readonly HashSet<string> _inflightTasks = new(StringComparer.Ordinal);

    // 結果之後一直沒有新的一輪時，過了上限就結束 run；換代數讓已經醒來的計時知道自己被清掉或重設了
    private CancellationTokenSource? _runEndCeiling;
    private int _runEndCeilingGeneration;

    private Task? _readLoop;
    private int _requestCounter;
    private volatile bool _closed;

    public ControlProtocol(
        ITransport transport,
        Func<ToolPermissionRequest, CancellationToken, Task<PermissionResult>>? canUseTool,
        IReadOnlyDictionary<string, McpServerConfig>? mcpServers = null,
        IReadOnlyDictionary<string, IReadOnlyList<HookMatcher>>? hooks = null)
    {
        _transport = transport;
        _canUseTool = canUseTool;

        // 對應 Python Query.initialize 組 hooks_config：回呼依登記順序編號，沒有 matcher 的事件不送，逾時有給才送
        _hasHooks = hooks is { Count: > 0 };
        foreach (var (hookEvent, matchers) in hooks ?? ReadOnlyDictionary<string, IReadOnlyList<HookMatcher>>.Empty)
        {
            if (matchers is not { Count: > 0 })
            {
                continue;
            }

            List<(string?, List<string>, double?)> matcherConfigs = [];
            foreach (var matcher in matchers)
            {
                List<string> callbackIds = [];
                foreach (var callback in matcher.Hooks)
                {
                    var callbackId = $"hook_{_hookCallbacks.Count}";
                    _hookCallbacks[callbackId] = callback;
                    callbackIds.Add(callbackId);
                }

                matcherConfigs.Add((matcher.Matcher, callbackIds, matcher.Timeout));
            }

            _hooksConfig.Add((hookEvent, matcherConfigs));
        }

        // 對應 Python client 從 mcp_servers 挑出 type 是 sdk 的那些，交給 Query 建 bridge
        foreach (var (name, config) in mcpServers ?? ReadOnlyDictionary<string, McpServerConfig>.Empty)
        {
            if (config is McpSdkServerConfig sdkServer)
            {
                _sdkMcpServers[name] = new SdkMcpServerSession(sdkServer);
            }
        }
    }

    /// <summary>
    /// CLI 可能還會送來要回覆的 control request：有 SDK MCP server、hook 或權限回呼。這時一次性的 query 要等結果才能關 stdin。
    /// 對應 Python 的 <c>_has_bidirectional_needs</c>。
    /// </summary>
    public bool HasBidirectionalNeeds => _sdkMcpServers.Count > 0 || _hasHooks || _canUseTool is not null;

    /// <summary>
    /// 結果之後 CLI 還回報 running、卻一直沒有新的一輪時，最多留著 stdin 多久（毫秒）；<c>0</c> 代表等 idle 等多久都行。
    /// 由 <see cref="RunEndCeiling.FromEnvironment"/> 讀 <c>CLAUDE_CODE_PRINT_BG_WAIT_CEILING_MS</c>。
    /// </summary>
    public long RunEndCeilingMs { get; init; } = RunEndCeiling.DefaultMs;

    /// <summary>關閉時等權限回呼收尾的時間上限。測試會調短。</summary>
    internal TimeSpan HandlerShutdownTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>開始讀取迴圈；要在送出任何 control request 之前呼叫。</summary>
    public void Start() => _readLoop ??= Task.Run(ReadLoopAsync);

    /// <summary>
    /// 初始化握手，回傳 CLI 的回應（指令、模型、帳號等資訊）。CLI 回應前不要送使用者訊息。
    /// 有 <paramref name="agents"/> 時跟 Python 一樣放在 <c>agents</c>，空的就不送。
    /// hook 跟 Python 一樣放在 <c>hooks</c>：<c>{事件: [{"matcher", "hookCallbackIds", "timeout"?}]}</c>，沒有就是 <see langword="null"/>。
    /// </summary>
    public Task<JsonElement> InitializeAsync(
        TimeSpan timeout,
        IReadOnlyDictionary<string, AgentDefinition>? agents = null,
        CancellationToken cancellationToken = default) =>
        InitializeAsync(timeout, new InitializeSettings { Agents = agents }, cancellationToken);

    /// <summary>
    /// 初始化握手，其他欄位照 <paramref name="settings"/>（見 <see cref="InitializeSettings"/>）。回傳的回應也存在 <see cref="InitializationResult"/>。
    /// </summary>
    public async Task<JsonElement> InitializeAsync(
        TimeSpan timeout,
        InitializeSettings settings,
        CancellationToken cancellationToken = default)
    {
        var response = await SendControlRequestAsync(
                "initialize",
                writer =>
                {
                    WriteHooksConfig(writer);
                    if (settings.Agents is { Count: > 0 } agents)
                    {
                        writer.WriteStartObject("agents");
                        foreach (var (name, agent) in agents)
                        {
                            writer.WritePropertyName(name);
                            agent.WriteTo(writer);
                        }

                        writer.WriteEndObject();
                    }

                    writer.WriteOptional("excludeDynamicSections", settings.ExcludeDynamicSections);
                    writer.WriteOptional("systemPromptSnapshot", settings.SystemPromptSnapshot);
                    writer.WriteOptional("skills", settings.Skills);
                    if (settings.ForwardSubagentText)
                    {
                        writer.WriteBoolean("forwardSubagentText", true);
                    }
                },
                timeout,
                cancellationToken)
            .ConfigureAwait(false);
        InitializationResult = response;
        return response;
    }

    /// <summary>initialize 的回應；還沒完成握手時是 <see langword="null"/>。對應 Python 的 <c>_initialization_result</c>。</summary>
    public JsonElement? InitializationResult { get; private set; }

    private void WriteHooksConfig(Utf8JsonWriter writer)
    {
        if (_hooksConfig.Count == 0)
        {
            writer.WriteNull("hooks");
            return;
        }

        writer.WriteStartObject("hooks");
        foreach (var (hookEvent, matchers) in _hooksConfig)
        {
            writer.WriteStartArray(hookEvent);
            foreach (var (matcher, callbackIds, timeout) in matchers)
            {
                writer.WriteStartObject();
                writer.WriteString("matcher", matcher);
                writer.WriteOptional("hookCallbackIds", callbackIds);
                if (timeout is { } seconds)
                {
                    writer.WriteNumber("timeout", seconds);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }

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

    /// <summary>把檔案還原到某則使用者訊息時的狀態（<c>rewind_files</c>）。要開 <see cref="ClaudeAgentOptions.EnableFileCheckpointing"/>。</summary>
    public Task RewindFilesAsync(string userMessageId, CancellationToken cancellationToken = default) =>
        SendControlRequestAsync(
            "rewind_files",
            writer => writer.WriteString("user_message_id", userMessageId),
            DefaultRequestTimeout,
            cancellationToken);

    /// <summary>重新連線斷掉或失敗的 MCP server（<c>mcp_reconnect</c>）。</summary>
    public Task ReconnectMcpServerAsync(string serverName, CancellationToken cancellationToken = default) =>
        SendControlRequestAsync(
            "mcp_reconnect",
            writer => writer.WriteString("serverName", serverName),
            DefaultRequestTimeout,
            cancellationToken);

    /// <summary>開啟或停用 MCP server（<c>mcp_toggle</c>）。</summary>
    public Task ToggleMcpServerAsync(string serverName, bool enabled, CancellationToken cancellationToken = default) =>
        SendControlRequestAsync(
            "mcp_toggle",
            writer =>
            {
                writer.WriteString("serverName", serverName);
                writer.WriteBoolean("enabled", enabled);
            },
            DefaultRequestTimeout,
            cancellationToken);

    /// <summary>停止背景工作（<c>stop_task</c>）。</summary>
    public Task StopTaskAsync(string taskId, CancellationToken cancellationToken = default) =>
        SendControlRequestAsync(
            "stop_task",
            writer => writer.WriteString("task_id", taskId),
            DefaultRequestTimeout,
            cancellationToken);

    /// <summary>MCP server 的連線狀態（<c>mcp_status</c>），回傳回應的 <c>response</c> 物件。</summary>
    public Task<JsonElement> GetMcpStatusAsync(CancellationToken cancellationToken = default) =>
        SendControlRequestAsync("mcp_status", writeFields: null, DefaultRequestTimeout, cancellationToken);

    /// <summary>context window 的用量（<c>get_context_usage</c>），回傳回應的 <c>response</c> 物件。</summary>
    public Task<JsonElement> GetContextUsageAsync(CancellationToken cancellationToken = default) =>
        SendControlRequestAsync("get_context_usage", writeFields: null, DefaultRequestTimeout, cancellationToken);

    /// <summary>
    /// 送出使用者說的一句話。互動式 client 的 session ID 跟 Python 一樣是 <c>default</c>，一次性的 <c>query()</c> 是空字串。
    /// 開了 <see cref="VerbatimPrompts"/> 時標上 <c>client_composed</c>。
    /// </summary>
    public Task SendUserMessageAsync(string content, string sessionId = "default", CancellationToken cancellationToken = default) =>
        _transport.WriteAsync(ControlMessages.UserMessage(content, sessionId, VerbatimPrompts), cancellationToken);

    /// <summary>每則送出的使用者訊息都標上 <c>client_composed: true</c>（<see cref="ClaudeAgentOptions.VerbatimPrompts"/>）。</summary>
    public bool VerbatimPrompts { get; init; }

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
        lock (_runGate)
        {
            ClearRunEndCeiling();
        }

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

            // 提早結束時也要叫醒等著關 stdin 的工作，不然它會一直等
            lock (_runGate)
            {
                _runFinal = true;
                EndRun();
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

            default:
                if (!TrackRun(message))
                {
                    return;
                }

                _messages.Writer.TryWrite(message);
                return;
        }
    }

    /// <summary>
    /// 一般訊息經過時更新 run 的狀態與最近的錯誤結果，回傳要不要往上送。對應 Python <c>_read_messages</c> 裡 control 訊息以外的部分，順序也一樣：
    /// 系統訊息先追蹤背景工作與 session state（<c>sdk_host_only</c> 的 session state 是 SDK 自己要求的，不往上送）；
    /// 結果結束一輪，再看 session state 決定要結束 run 還是開始計時；其他訊息代表對話往下走了，
    /// 主對話的 assistant／stream_event 代表新的一輪開始，停掉計時、重新打開已經結束的 run。
    /// </summary>
    private bool TrackRun(JsonElement message)
    {
        var type = GetString(message, "type");
        var isSessionState = false;
        if (type == "system")
        {
            var subtype = GetString(message, "subtype");
            isSessionState = subtype == "session_state_changed";
            lock (_runGate)
            {
                var hadTasksInFlight = _inflightTasks.Count > 0;
                TrackTaskLifecycle(message, subtype);
                if (hadTasksInFlight && _inflightTasks.Count == 0)
                {
                    // 上限放過了最後一個追蹤中的 agent；它結束了，兩輪之間的等待重新算
                    RearmRunEndCeilingBetweenTurns();
                }

                if (isSessionState)
                {
                    OnSessionState(GetString(message, "state"));
                }
            }

            // CLAUDE_CODE_SDK_READS_SESSION_STATE 要求才送的訊框，呼叫端沒有要求，不往上送
            if (isSessionState && message.TryGetProperty("sdk_host_only", out var hostOnly) && hostOnly.ValueKind == JsonValueKind.True)
            {
                return false;
            }
        }

        if (type == "result")
        {
            lock (_runGate)
            {
                _resultReceived = true;
                _turnInProgress = false;

                // 結果結束的是一輪，不一定是整個 run：剛結束的背景 agent 還會讓 session 再跑一輪，那一輪的 hook、權限與 SDK MCP 要用 stdin。
                // 回報 state 的 CLI 欠著一輪時維持 running，所以等 idle（有些 CLI 在結果之前就送了）；不回報 state 的只能看結果
                if (_sessionState is null or "idle" || !HasBidirectionalNeeds)
                {
                    MaybeEndRun();
                }
                else if (_sessionState != "requires_action")
                {
                    // SDK 還在回覆 request 時，等之後的 running 才開始計時
                    ArmRunEndCeiling();
                }
            }

            // 對應 Python 的 _last_error_result：出錯的結果記住，CLI 接著異常結束時換成帶結果內容的例外
            _lastErrorResult = message.TryGetProperty("is_error", out var isError) && IsTruthy(isError) ? message : null;
        }
        else if (!isSessionState)
        {
            // 一輪結束後的 session_state_changed 以外的訊息都代表對話往下走了，之後的異常結束是新的錯誤
            _lastErrorResult = null;
            if (type is "assistant" or "stream_event" && IsMainThread(message))
            {
                lock (_runGate)
                {
                    _turnInProgress = true;
                    ReopenRun();
                    ClearRunEndCeiling();
                }
            }
        }

        return true;
    }

    /// <summary>跟 Python 的 <c>message.get("parent_tool_use_id") is None</c> 一樣：沒有這個欄位或是 null 都算主對話。</summary>
    private static bool IsMainThread(JsonElement message) =>
        !message.TryGetProperty("parent_tool_use_id", out var parent) || parent.ValueKind == JsonValueKind.Null;

    /// <summary>
    /// 從系統訊息追蹤進行中的工作，對應 Python 的 <c>_track_task_lifecycle</c>：<c>task_started</c> 加入（只限 <see cref="TaskLifecycle.DeferringTaskTypes"/>），
    /// <c>task_notification</c> 或 <c>patch.status</c> 是結束狀態的 <c>task_updated</c> 移除。<c>background_tasks_changed</c> 刻意不用（見 Python 的說明）。
    /// </summary>
    private void TrackTaskLifecycle(JsonElement message, string? subtype)
    {
        if (GetString(message, "task_id") is not { Length: > 0 } taskId)
        {
            return;
        }

        switch (subtype)
        {
            case "task_started" when GetString(message, "task_type") is { } taskType && TaskLifecycle.DeferringTaskTypes.Contains(taskType):
                _inflightTasks.Add(taskId);
                break;
            case "task_notification":
                _inflightTasks.Remove(taskId);
                break;
            case "task_updated" when message.TryGetProperty("patch", out var patch)
                && GetString(patch, "status") is { } status && TaskLifecycle.TerminalStatuses.Contains(status):
                _inflightTasks.Remove(taskId);
                break;
            default:
                break;
        }
    }

    /// <summary>記住 CLI 的 session state，對應 Python 的 <c>_on_session_state</c>。呼叫時要拿著 <see cref="_runGate"/>。</summary>
    private void OnSessionState(string? state)
    {
        _sessionState = state;
        if (state == "idle")
        {
            if (_resultReceived)
            {
                MaybeEndRun();
            }

            return;
        }

        // run 結束後 CLI 又接了工作（背景工作結束把它叫醒），重新打開，等下一個 idle
        ReopenRun();
        if (state == "requires_action")
        {
            // SDK 正在回覆 request，stdin 要撐過它
            ClearRunEndCeiling();
        }
        else
        {
            RearmRunEndCeilingBetweenTurns();
        }
    }

    /// <summary>還有追蹤中的背景工作就不結束 run：它們可能還要 stdin 回覆 hook、SDK MCP。對應 Python 的 <c>_maybe_end_run</c>。</summary>
    private void MaybeEndRun()
    {
        if (_inflightTasks.Count == 0)
        {
            EndRun();
        }
    }

    /// <summary>run 結束了：叫醒等著關 stdin 的工作。重複呼叫沒關係。對應 Python 的 <c>_end_run</c>。</summary>
    private void EndRun()
    {
        ClearRunEndCeiling();
        _runEnded.TrySetResult();
    }

    /// <summary>
    /// run 結束後才開始的工作重新打開 run，對應 Python 的 <c>_reopen_run</c>。已經被叫醒的等待照樣關 stdin；之後的等待會等新的工作。
    /// stdin 關了或讀取迴圈結束後就不再打開。
    /// </summary>
    private void ReopenRun()
    {
        if (_runEnded.Task.IsCompleted && !_runFinal)
        {
            _runEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    /// <summary>
    /// 過了上限還沒有新的一輪就照樣結束 run，對應 Python 的 <c>_arm_run_end_ceiling</c>。CLI 自己的背景等待上限要 stdin 關了才開始算，
    /// 沒有這個的話，永遠不結束的工作會讓 running 與 stdin 一直開著。只算兩輪之間：每個結果與 CLI 再回報 running 時重算，
    /// 主對話有動靜與 requires_action 時清掉，一輪進行中不計時。
    /// </summary>
    private void ArmRunEndCeiling()
    {
        ClearRunEndCeiling();
        if (RunEndCeilingMs <= 0
            || _runEnded.Task.IsCompleted
            || _runFinal
            || _closed
            || _turnInProgress
            || !HasBidirectionalNeeds)
        {
            return;
        }

        var generation = _runEndCeilingGeneration;
        var ceiling = new CancellationTokenSource();
        _runEndCeiling = ceiling;
        _ = EndRunAtCeilingAsync(generation, Math.Min(RunEndCeilingMs, RunEndCeiling.MaxMs), ceiling.Token);
    }

    /// <summary>run 在兩輪之間、已經有結果、CLI 還回報有工作（running）時重新計時。對應 Python 的 <c>_rearm_run_end_ceiling_between_turns</c>。</summary>
    private void RearmRunEndCeilingBetweenTurns()
    {
        if (_resultReceived && _sessionState is not (null or "idle" or "requires_action"))
        {
            ArmRunEndCeiling();
        }
    }

    private async Task EndRunAtCeilingAsync(int generation, long milliseconds, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(milliseconds), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        lock (_runGate)
        {
            // 醒來時已經被清掉或重設了
            if (generation != _runEndCeilingGeneration)
            {
                return;
            }

            _runEndCeiling?.Dispose();
            _runEndCeiling = null;

            // 追蹤中的背景 agent 還在跑，可能還要 stdin（#1088），不切斷；它結束後上限重新算（TrackRun）
            if (_inflightTasks.Count > 0)
            {
                return;
            }

            EndRun();
        }
    }

    private void ClearRunEndCeiling()
    {
        _runEndCeilingGeneration++;
        if (_runEndCeiling is { } ceiling)
        {
            _runEndCeiling = null;
            ceiling.Cancel();
            ceiling.Dispose();
        }
    }

    /// <summary>
    /// 在背景等 run 結束（需要的話）再關 stdin，對應一次性 <c>query()</c> 送出字串提示後 spawn 的 <c>wait_for_result_and_end_input</c>。
    /// 有 SDK MCP server、hook 或權限回呼時，stdin 留到 run 結束：結果之後 CLI 回報 idle；不回報 session state 的 CLI，則是沒有追蹤中背景工作時的第一個結果。
    /// 兩輪之間的等待有上限（<see cref="RunEndCeilingMs"/>）。讀取迴圈結束（CLI 提早結束）時也一定會醒來。關閉時取消，不關 stdin（關閉本身會關）。
    /// </summary>
    public void StartWaitForResultAndEndInput()
    {
        var task = Task.Run(WaitForResultAndEndInputAsync);
        _handlers.TryAdd(task, 0);
        task.ContinueWith(
            static (finished, handlers) => ((ConcurrentDictionary<Task, byte>)handlers!).TryRemove(finished, out _),
            _handlers,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task WaitForResultAndEndInputAsync()
    {
        try
        {
            if (HasBidirectionalNeeds)
            {
                Task runEnded;
                lock (_runGate)
                {
                    runEnded = _runEnded.Task;
                }

                await runEnded.WaitAsync(_closing.Token).ConfigureAwait(false);
            }

            lock (_runGate)
            {
                _runFinal = true;
                ClearRunEndCeiling();
            }

            await _transport.EndInputAsync(_closing.Token).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // 關閉中被取消，或 transport 已經壞了：讀取迴圈會回報，這裡沒有人能接
        catch (Exception)
#pragma warning restore CA1031
        {
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
                    "hook_callback" => ControlMessages.Success(
                        requestId,
                        await AnswerHookCallbackAsync(request, cancellationToken).ConfigureAwait(false)),
                    "mcp_message" => ControlMessages.Success(
                        requestId,
                        await AnswerMcpMessageAsync(request, cancellationToken).ConfigureAwait(false)),
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

    /// <summary>
    /// 呼叫登記的 hook 回呼，回傳寫出它結果的方法。對應 Python 的 <c>_handle_control_request</c> 裡 hook_callback 的部分
    /// 與 <c>_convert_hook_output_for_cli</c>（<see cref="HookOutput"/> 寫出的已經是 CLI 的欄位名稱）。
    /// </summary>
    private async Task<Action<Utf8JsonWriter>> AnswerHookCallbackAsync(JsonElement request, CancellationToken cancellationToken)
    {
        var callbackId = GetString(request, "callback_id");
        if (callbackId is null || !_hookCallbacks.TryGetValue(callbackId, out var callback))
        {
            throw new InvalidOperationException($"No hook callback found for ID: {callbackId}");
        }

        var input = HookInput.Parse(request.TryGetProperty("input", out var value) ? value : default);
        var output = await callback(input, GetString(request, "tool_use_id"), new HookContext(cancellationToken), cancellationToken)
                .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"hook 回呼 {callbackId} 回傳了 null；什麼都不改請回傳 HookOutput.Empty。");
        return output.WriteFields;
    }

    /// <summary>
    /// 把 JSON-RPC 訊息交給 SDK MCP server，回傳寫出 <c>{"mcp_response": …}</c> 的方法。
    /// 對應 Python 的 <c>_handle_control_request</c> 裡 mcp_message 的部分與 <c>_handle_sdk_mcp_request</c>。
    /// </summary>
    private async Task<Action<Utf8JsonWriter>> AnswerMcpMessageAsync(JsonElement request, CancellationToken cancellationToken)
    {
        // 跟 Python 的 not server_name or not mcp_message 一樣：空字串、空物件也算沒給
        var serverName = GetString(request, "server_name");
        if (string.IsNullOrEmpty(serverName)
            || !request.TryGetProperty("message", out var message)
            || message.ValueKind != JsonValueKind.Object
            || !message.EnumerateObject().Any())
        {
            throw new InvalidOperationException("Missing server_name or message for MCP request");
        }

        var id = message.TryGetProperty("id", out var value) ? value : default;
        string? response;
        if (!_sdkMcpServers.TryGetValue(serverName, out var server))
        {
            response = SdkMcpServerSession.JsonRpcError(id, -32601, $"Server '{serverName}' not found");
        }
        else
        {
            try
            {
                response = await server.HandleAsync(message, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
#pragma warning disable CA1031 // 送不到 server 的訊息跟 Python 一樣回 JSON-RPC 錯誤，讓 CLI 只讓這個請求失敗
            catch (Exception e)
#pragma warning restore CA1031
            {
                response = SdkMcpServerSession.JsonRpcError(id, -32603, e.Message.Length > 0 ? e.Message : e.GetType().Name);
            }
        }

        // 通知沒有 JSON-RPC 回覆，但帶著它的 control request 還是要回
        response ??= """{"jsonrpc":"2.0","result":{}}""";
        return writer =>
        {
            writer.WritePropertyName("mcp_response");
            writer.WriteRawValue(response);
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
