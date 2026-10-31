using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using PamisWang.ClaudeAgentSdk.Mcp;
using PamisWang.ClaudeAgentSdk.Messages;
using PamisWang.ClaudeAgentSdk.Protocol;
using PamisWang.ClaudeAgentSdk.Transport;

namespace PamisWang.ClaudeAgentSdk;

/// <summary>
/// 跟 Claude Code 互動式對話：啟動本機的 CLI、送出訊息、接收回覆、批准工具、中斷。對應 Python SDK 的 <c>ClaudeSDKClient</c>。
/// 一個 client 只連線一次；要重新開始就建立新的 client，用 <see cref="ClaudeAgentOptions.Resume"/> 接續原本的 session。
/// </summary>
/// <example>
/// <code>
/// await using var client = new ClaudeAgentClient(new ClaudeAgentOptions { Cwd = @"D:\work" });
/// await client.ConnectAsync();
/// await client.SendAsync("這個專案在做什麼？");
/// await foreach (var message in client.ReceiveResponseAsync())
/// {
///     if (message is AssistantMessage assistant)
///     {
///         foreach (var text in assistant.Content.OfType&lt;TextBlock&gt;())
///         {
///             Console.WriteLine(text.Text);
///         }
///     }
/// }
/// </code>
/// </example>
public sealed class ClaudeAgentClient : IAsyncDisposable
{
    // 跟 Python SDK 一樣：initialize 至少等 60 秒（MCP server 啟動可能很慢），
    // 環境變數 CLAUDE_CODE_STREAM_CLOSE_TIMEOUT（毫秒）設得更長就用它
    private static readonly TimeSpan MinimumInitializeTimeout = TimeSpan.FromSeconds(60);

    private readonly ClaudeAgentOptions _options;
    private readonly ITransport _transport;
    private readonly Lock _gate = new();
    private ClientState _state;
    private ControlProtocol? _protocol;

    /// <summary>建立 client；要呼叫 <see cref="ConnectAsync"/> 才會啟動 Claude Code。</summary>
    public ClaudeAgentClient(ClaudeAgentOptions? options = null)
        : this(options ?? new ClaudeAgentOptions(), transport: null)
    {
    }

    internal ClaudeAgentClient(ClaudeAgentOptions options, ITransport? transport)
    {
        _options = options;
        _transport = transport ?? new SubprocessTransport(options);
    }

    private enum ClientState
    {
        New,
        Connecting,
        Connected,
        Closed,
    }

    /// <summary>
    /// 初始化握手時 CLI 回報的資訊：可用的指令、模型、輸出風格、帳號等。對應 Python 的 <c>get_server_info()</c>。還沒連線時是 <see langword="null"/>。
    /// </summary>
    public JsonElement? ServerInfo { get; private set; }

    /// <summary>已經連線，而且還沒關閉。</summary>
    public bool IsConnected
    {
        get
        {
            lock (_gate)
            {
                return _state == ClientState.Connected;
            }
        }
    }

    /// <summary>啟動 Claude Code 並完成初始化握手。失敗時會把已經啟動的 CLI 關掉，這個 client 也就不能再用。</summary>
    /// <exception cref="CliNotFoundException">找不到 Claude Code。</exception>
    /// <exception cref="CliConnectionException">啟動失敗，或 CLI 在握手完成前就結束。</exception>
    /// <exception cref="TimeoutException">CLI 遲遲沒有回應初始化。</exception>
    /// <exception cref="InvalidOperationException">已經連線過。</exception>
    /// <exception cref="ArgumentException">選項互相衝突，例如同時設了 <see cref="ClaudeAgentOptions.CanUseTool"/> 與 <see cref="ClaudeAgentOptions.PermissionPromptToolName"/>。</exception>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        // 跟 Python 一樣在啟動 CLI 之前檢查；自訂的 transport 不經過 CliArguments，也要擋
        _options.Validate();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_state == ClientState.Closed, this);
            if (_state != ClientState.New)
            {
                throw new InvalidOperationException("這個 client 已經連線過；要重新開始請建立新的 ClaudeAgentClient。");
            }

            _state = ClientState.Connecting;
        }

        // SDK MCP server 跟 Python 一樣從 McpServers 挑出來；CLI 的 mcp_message 由 protocol 交給它們。
        // hook 在 initialize 時登記，CLI 的 hook_callback 也由 protocol 交給回呼
        // VerbatimPrompts 跟 Python 一樣在連線時讀一次，之後每一句話都照它標記
        var protocol = new ControlProtocol(_transport, _options.CanUseTool, _options.McpServers, _options.Hooks)
        {
            VerbatimPrompts = _options.VerbatimPrompts,
            RunEndCeilingMs = RunEndCeiling.FromEnvironment(_options.Env, Environment.GetEnvironmentVariable),
        };
        try
        {
            await _transport.ConnectAsync(cancellationToken).ConfigureAwait(false);
            protocol.Start();
            ServerInfo = await protocol
                .InitializeAsync(
                    InitializeTimeout(Environment.GetEnvironmentVariable("CLAUDE_CODE_STREAM_CLOSE_TIMEOUT")),
                    InitializeSettings.FromOptions(_options),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            lock (_gate)
            {
                _state = ClientState.Closed;
            }

            await protocol.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        bool closedMeanwhile;
        lock (_gate)
        {
            closedMeanwhile = _state == ClientState.Closed;
            if (!closedMeanwhile)
            {
                _protocol = protocol;
                _state = ClientState.Connected;
            }
        }

        // 連線途中就被 DisposeAsync 了
        if (closedMeanwhile)
        {
            await protocol.DisposeAsync().ConfigureAwait(false);
            throw new ObjectDisposedException(GetType().FullName);
        }
    }

    /// <summary>送出一句話。不等回覆；回覆從 <see cref="ReceiveResponseAsync"/> 或 <see cref="ReceiveMessagesAsync"/> 讀。對應 Python 的 <c>query()</c>。</summary>
    public Task SendAsync(string prompt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        return RequireProtocol().SendUserMessageAsync(prompt, cancellationToken: cancellationToken);
    }

    /// <summary>送出一句話，指定訊息的 session ID；一次性的 <see cref="ClaudeAgent.QueryAsync"/> 跟 Python 一樣用空字串。</summary>
    internal Task SendWithSessionIdAsync(string prompt, string sessionId, CancellationToken cancellationToken) =>
        RequireProtocol().SendUserMessageAsync(prompt, sessionId, cancellationToken);

    /// <summary>
    /// 在背景等 run 結束再關 stdin（一次性 <see cref="ClaudeAgent.QueryAsync"/> 用）。對應 Python 的 <c>wait_for_result_and_end_input</c>，
    /// 規則見 <see cref="ControlProtocol.StartWaitForResultAndEndInput"/>。
    /// </summary>
    internal void StartWaitForResultAndEndInput() => RequireProtocol().StartWaitForResultAndEndInput();

    /// <summary>
    /// 讀取 CLI 送來的每一則訊息，跨越多輪，直到 CLI 結束或 client 關閉。
    /// 同一時間只能有一個讀取者；多輪對話時可以一直讀，或每輪各用一次 <see cref="ReceiveResponseAsync"/>。
    /// </summary>
    /// <exception cref="CliProcessException">CLI 異常結束。</exception>
    /// <exception cref="MessageParseException">認得的訊息缺少必要欄位。</exception>
    public async IAsyncEnumerable<ClaudeMessage> ReceiveMessagesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var protocol = RequireProtocol();
        await foreach (var data in protocol.ReceiveMessagesAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return MessageParser.Parse(data);
        }
    }

    /// <summary>讀到這一輪的 <see cref="ResultMessage"/> 為止，包含它。對應 Python 的 <c>receive_response()</c>。</summary>
    public async IAsyncEnumerable<ClaudeMessage> ReceiveResponseAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var message in ReceiveMessagesAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return message;
            if (message is ResultMessage)
            {
                yield break;
            }
        }
    }

    /// <summary>中斷這一輪。對話還在，之後可以接著 <see cref="SendAsync"/>；被中斷的那一輪仍會送來一個出錯的 <see cref="ResultMessage"/>。</summary>
    public Task InterruptAsync(CancellationToken cancellationToken = default) =>
        RequireProtocol().InterruptAsync(cancellationToken);

    /// <summary>
    /// 換模型：之後的回覆改用 <paramref name="model"/>（例如 <c>sonnet</c> 或完整的模型 ID），<see langword="null"/> 回到 Claude Code 的預設。
    /// 對話還在，不用重新連線。對應 Python 的 <c>set_model()</c>。
    /// 回傳 CLI 換過去的完整模型 ID（例如 <c>sonnet</c> 是 <c>claude-sonnet-5-5</c>），CLI 沒回報時是 <see langword="null"/>；
    /// Python 與 TypeScript SDK 不回傳這個值。CLI 也會送來一則重播的 <see cref="UserMessage"/>（原始 JSON 的 <c>isReplay</c> 是 <see langword="true"/>），
    /// 內容是 <c>&lt;local-command-stdout&gt;Set model to …&lt;/local-command-stdout&gt;</c>。
    /// </summary>
    public Task<string?> SetModelAsync(string? model = null, CancellationToken cancellationToken = default) =>
        RequireProtocol().SetModelAsync(model, cancellationToken);

    /// <summary>
    /// 換權限模式：<c>default</c>、<c>acceptEdits</c>、<c>plan</c>、<c>bypassPermissions</c>、<c>dontAsk</c> 或 <c>auto</c>。
    /// 對話還在，不用重新連線。對應 Python 的 <c>set_permission_mode()</c>。
    /// </summary>
    public Task SetPermissionModeAsync(string mode, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mode);
        return RequireProtocol().SetPermissionModeAsync(mode, cancellationToken);
    }

    /// <summary>
    /// 對話中途套用設定，鍵與值跟 Claude Code 的 <c>settings.json</c> 一樣。設定合併進這個 session 的 flag 設定層
    /// （優先於使用者、專案、本機的設定檔，低於組織的管理設定），不寫進設定檔。
    /// 每次呼叫淺層合併最上層的鍵；值給 JSON 的 <c>null</c> 就清掉那個鍵，回到較低層的設定。
    /// 例如 <c>new JsonObject { ["effortLevel"] = "high" }</c> 換思考深度（也接受只在這個 session 有效的 <c>max</c>），
    /// <c>effortLevel</c> 給 <c>null</c> 回到模型預設的深度。
    /// Python SDK 沒有這個功能，對應 TypeScript SDK 的 <c>applyFlagSettings()</c>。
    /// </summary>
    public Task ApplyFlagSettingsAsync(JsonObject settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return RequireProtocol().ApplyFlagSettingsAsync(settings, cancellationToken);
    }

    /// <summary>
    /// 把檔案還原到 <paramref name="userMessageId"/> 那則使用者訊息時的狀態（<c>rewind_files</c>）。對應 Python 的 <c>rewind_files()</c>。
    /// 要開 <see cref="ClaudeAgentOptions.EnableFileCheckpointing"/> 才會記錄檔案的變更；ID 是收到的 <see cref="UserMessage"/> 的 <see cref="ClaudeMessage.Uuid"/>，
    /// 要 CLI 把使用者訊息重播回來，跟 Python 一樣另外設 <c>ExtraArgs = { ["replay-user-messages"] = null }</c>。
    /// </summary>
    /// <example>
    /// <code>
    /// var options = new ClaudeAgentOptions
    /// {
    ///     EnableFileCheckpointing = true,
    ///     ExtraArgs = new Dictionary&lt;string, string?&gt; { ["replay-user-messages"] = null },
    /// };
    /// await using var client = new ClaudeAgentClient(options);
    /// await client.ConnectAsync();
    /// await client.SendAsync("改一下我的檔案");
    /// string? checkpoint = null;
    /// await foreach (var message in client.ReceiveResponseAsync())
    /// {
    ///     if (message is UserMessage { Uuid: { } uuid })
    ///     {
    ///         checkpoint ??= uuid;
    ///     }
    /// }
    ///
    /// await client.RewindFilesAsync(checkpoint!);
    /// </code>
    /// </example>
    /// <exception cref="ClaudeAgentException">CLI 回報還原失敗。</exception>
    public Task RewindFilesAsync(string userMessageId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(userMessageId);
        return RequireProtocol().RewindFilesAsync(userMessageId, cancellationToken);
    }

    /// <summary>重新連線斷掉或連不上的 MCP server（<c>mcp_reconnect</c>）。失敗時丟出例外。對應 Python 的 <c>reconnect_mcp_server()</c>。</summary>
    /// <exception cref="ClaudeAgentException">CLI 回報重新連線失敗。</exception>
    public Task ReconnectMcpServerAsync(string serverName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serverName);
        return RequireProtocol().ReconnectMcpServerAsync(serverName, cancellationToken);
    }

    /// <summary>
    /// 開啟或停用 MCP server（<c>mcp_toggle</c>）：停用會斷線並拿掉它的工具，開啟會重新連線、工具也回來。失敗時丟出例外。
    /// 對應 Python 的 <c>toggle_mcp_server()</c>。
    /// </summary>
    /// <exception cref="ClaudeAgentException">CLI 回報切換失敗。</exception>
    public Task ToggleMcpServerAsync(string serverName, bool enabled, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serverName);
        return RequireProtocol().ToggleMcpServerAsync(serverName, enabled, cancellationToken);
    }

    /// <summary>
    /// 停止背景工作（<c>stop_task</c>）；ID 是 <c>task_started</c> 系統訊息的 <c>task_id</c>。對應 Python 的 <c>stop_task()</c>。
    /// 完成後 CLI 會送來 <c>task_updated</c>，<c>patch.status</c> 是結束狀態（停止的工作是 <c>killed</c>）；
    /// 也可能再送 <c>status</c> 是 <c>stopped</c> 的 <c>task_notification</c>，但有時不送，兩種都要用 <see cref="TaskLifecycle.TerminalStatuses"/> 判斷。
    /// </summary>
    public Task StopTaskAsync(string taskId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        return RequireProtocol().StopTaskAsync(taskId, cancellationToken);
    }

    /// <summary>各 MCP server 目前的連線狀態（<c>mcp_status</c>）。對應 Python 的 <c>get_mcp_status()</c>。</summary>
    public async Task<McpStatusResponse> GetMcpStatusAsync(CancellationToken cancellationToken = default) =>
        McpStatusResponse.FromJson(await RequireProtocol().GetMcpStatusAsync(cancellationToken).ConfigureAwait(false));

    /// <summary>context window 依類別的用量（<c>get_context_usage</c>），跟 CLI 的 <c>/context</c> 一樣。對應 Python 的 <c>get_context_usage()</c>。</summary>
    public async Task<ContextUsageResponse> GetContextUsageAsync(CancellationToken cancellationToken = default) =>
        ContextUsageResponse.FromJson(await RequireProtocol().GetContextUsageAsync(cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// 初始化握手時 CLI 回報的資訊（可用的指令、輸出風格等），連線時已經拿到，不會再問 CLI。對應 Python 的 <c>get_server_info()</c>：
    /// 還沒連線時跟 Python 一樣丟出 <see cref="CliConnectionException"/>；不想處理例外時讀 <see cref="ServerInfo"/>，沒連線時是 <see langword="null"/>。
    /// </summary>
    /// <exception cref="CliConnectionException">還沒連線。</exception>
    public Task<JsonElement?> GetServerInfoAsync() => Task.FromResult(RequireProtocol().InitializationResult);

    /// <summary>關閉：取消等待中的權限詢問、關閉 stdin 讓 CLI 寫完 session 後結束，逾時才強制結束。</summary>
    public async ValueTask DisposeAsync()
    {
        ControlProtocol? protocol;
        bool wasConnecting;
        lock (_gate)
        {
            if (_state == ClientState.Closed)
            {
                return;
            }

            wasConnecting = _state == ClientState.Connecting;
            _state = ClientState.Closed;
            protocol = _protocol;
            _protocol = null;
        }

        if (protocol is not null)
        {
            await protocol.DisposeAsync().ConfigureAwait(false);
        }
        else if (!wasConnecting)
        {
            // 從沒連線過；連線中的話由 ConnectAsync 自己收尾
            await _transport.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>initialize 的等待上限：至少 60 秒，環境變數設得更長就照它。</summary>
    internal static TimeSpan InitializeTimeout(string? streamCloseTimeoutMs) =>
        int.TryParse(streamCloseTimeoutMs, NumberStyles.Integer, CultureInfo.InvariantCulture, out var milliseconds)
        && TimeSpan.FromMilliseconds(milliseconds) > MinimumInitializeTimeout
            ? TimeSpan.FromMilliseconds(milliseconds)
            : MinimumInitializeTimeout;

    private ControlProtocol RequireProtocol()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_state == ClientState.Closed, this);
            return _protocol ?? throw new CliConnectionException("還沒連上 Claude Code，請先呼叫 ConnectAsync。");
        }
    }
}
