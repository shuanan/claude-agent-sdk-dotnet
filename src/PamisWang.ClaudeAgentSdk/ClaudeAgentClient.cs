using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
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
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_state == ClientState.Closed, this);
            if (_state != ClientState.New)
            {
                throw new InvalidOperationException("這個 client 已經連線過；要重新開始請建立新的 ClaudeAgentClient。");
            }

            _state = ClientState.Connecting;
        }

        var protocol = new ControlProtocol(_transport, _options.CanUseTool);
        try
        {
            await _transport.ConnectAsync(cancellationToken).ConfigureAwait(false);
            protocol.Start();
            ServerInfo = await protocol
                .InitializeAsync(InitializeTimeout(Environment.GetEnvironmentVariable("CLAUDE_CODE_STREAM_CLOSE_TIMEOUT")), cancellationToken)
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
        return RequireProtocol().SendUserMessageAsync(prompt, cancellationToken);
    }

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
