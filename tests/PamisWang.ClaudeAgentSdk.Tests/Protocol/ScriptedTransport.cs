using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using PamisWang.ClaudeAgentSdk.Transport;

namespace PamisWang.ClaudeAgentSdk.Tests.Protocol;

/// <summary>由測試逐則推送 CLI 的訊息，並檢查 SDK 寫出了什麼。用在樣本裡沒有的情況，例如錯誤與逾時。</summary>
internal sealed class ScriptedTransport : ITransport
{
    private readonly Channel<JsonElement> _incoming = Channel.CreateUnbounded<JsonElement>();
    private readonly Channel<JsonElement> _written = Channel.CreateUnbounded<JsonElement>();

    public bool IsReady => true;

    public bool Disposed { get; private set; }

    /// <summary>推送一則 CLI 送來的訊息。</summary>
    public void Push(string json) => _incoming.Writer.TryWrite(JsonSerializer.Deserialize<JsonElement>(json));

    /// <summary>CLI 的輸出正常結束。</summary>
    public void End() => _incoming.Writer.TryComplete();

    /// <summary>讀取失敗，例如 CLI 異常結束。</summary>
    public void Fail(Exception exception) => _incoming.Writer.TryComplete(exception);

    /// <summary>等 SDK 寫出下一則訊息。</summary>
    public async Task<JsonElement> NextWriteAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        return await _written.Reader.ReadAsync(timeout.Token);
    }

    /// <summary>SDK 目前沒有寫出任何還沒被取走的訊息。</summary>
    public bool HasNoPendingWrites => !_written.Reader.TryPeek(out _);

    public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task WriteAsync(string data, CancellationToken cancellationToken = default)
    {
        foreach (var line in data.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            _written.Writer.TryWrite(JsonSerializer.Deserialize<JsonElement>(line));
        }

        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<JsonElement> ReadMessagesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (await _incoming.Reader.WaitToReadAsync(cancellationToken))
        {
            while (_incoming.Reader.TryRead(out var message))
            {
                yield return message;
            }
        }
    }

    public Task EndInputAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        _incoming.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
