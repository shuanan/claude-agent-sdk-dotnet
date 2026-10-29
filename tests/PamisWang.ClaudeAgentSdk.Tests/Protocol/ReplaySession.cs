using System.Text.Json;
using PamisWang.ClaudeAgentSdk.Permissions;
using PamisWang.ClaudeAgentSdk.Protocol;

namespace PamisWang.ClaudeAgentSdk.Tests.Protocol;

/// <summary>用 ReplayTransport 重播一個樣本：已經完成初始化握手，依序送出樣本裡的使用者訊息、讀到每一輪的 result。</summary>
internal sealed class ReplaySession : IAsyncDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly IAsyncEnumerator<JsonElement> _messages;
    private readonly Queue<string> _userMessages;

    private ReplaySession(ProtocolFixture fixture, ReplayTransport transport, ControlProtocol protocol)
    {
        Fixture = fixture;
        Transport = transport;
        Protocol = protocol;
        _messages = protocol.ReceiveMessagesAsync().GetAsyncEnumerator();
        _userMessages = new(fixture.UserPrompts);
    }

    public ProtocolFixture Fixture { get; }

    public ReplayTransport Transport { get; }

    public ControlProtocol Protocol { get; }

    public JsonElement InitializeResponse { get; private set; }

    /// <summary>上層收到的訊息，依序累積。</summary>
    public List<JsonElement> Received { get; } = [];

    /// <summary>樣本裡上層應該收到的訊息：control 協定的訊框與 sdk_host_only 的 session state 都不算。</summary>
    public IReadOnlyList<JsonElement> ExpectedReceived => [.. Fixture.Received.Where(IsForConsumer)];

    public static async Task<ReplaySession> StartAsync(
        string fixtureName,
        Func<ToolPermissionRequest, CancellationToken, Task<PermissionResult>>? canUseTool = null)
    {
        var fixture = ProtocolFixture.Load(fixtureName);
        var transport = new ReplayTransport(fixture);
        var protocol = new ControlProtocol(transport, canUseTool) { HandlerShutdownTimeout = TimeSpan.FromSeconds(1) };
        protocol.Start();
        var session = new ReplaySession(fixture, transport, protocol);
        session.InitializeResponse = await protocol.InitializeAsync(Timeout);
        return session;
    }

    /// <summary>送出樣本裡的下一句使用者訊息。</summary>
    public Task SendNextUserMessageAsync() => Protocol.SendUserMessageAsync(_userMessages.Dequeue());

    /// <summary>讀到這一輪的 result 為止；<paramref name="onMessage"/> 可以在途中做事，例如送出中斷。</summary>
    public async Task<JsonElement> ReceiveUntilResultAsync(Func<JsonElement, Task>? onMessage = null)
    {
        while (await _messages.MoveNextAsync().AsTask().WaitAsync(Timeout))
        {
            var message = _messages.Current;
            Received.Add(message);
            if (onMessage is not null)
            {
                await onMessage(message);
            }

            if (message.GetProperty("type").GetString() == "result")
            {
                return message;
            }
        }

        throw new InvalidOperationException("還沒收到 result 訊息，輸出就結束了。");
    }

    public async ValueTask DisposeAsync()
    {
        await Protocol.DisposeAsync();
        await _messages.DisposeAsync();
    }

    private static bool IsForConsumer(JsonElement message)
    {
        var type = message.GetProperty("type").GetString();
        if (type is "control_request" or "control_response" or "control_cancel_request" or "transcript_mirror")
        {
            return false;
        }

        return !(type == "system"
            && message.GetProperty("subtype").GetString() == "session_state_changed"
            && message.TryGetProperty("sdk_host_only", out var hostOnly)
            && hostOnly.ValueKind == JsonValueKind.True);
    }
}
