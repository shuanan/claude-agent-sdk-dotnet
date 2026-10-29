using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using PamisWang.ClaudeAgentSdk.Transport;

namespace PamisWang.ClaudeAgentSdk.Tests.Protocol;

/// <summary>
/// 重播錄下的協定樣本：依序交出 recv；遇到 send 就等 SDK 真的寫出一則訊息，跟樣本比對，對得上才繼續。
/// 對不上就讓讀取失敗，錯誤會從 ReceiveMessagesAsync 丟出來。
/// </summary>
/// <remarks>
/// SDK 自己產生的 request_id（req_ 開頭）每次都不同：比對時換成實際送出的，之後 CLI 回應裡的也跟著換。
/// 樣本放完之後就像 CLI 還開著，等到關閉才結束輸出。
/// </remarks>
internal sealed class ReplayTransport(ProtocolFixture fixture) : ITransport
{
    private readonly Channel<JsonElement> _written = Channel.CreateUnbounded<JsonElement>();
    private readonly Dictionary<string, string> _requestIds = [];
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<JsonElement> _writes = [];

    public bool IsReady => !_closed.Task.IsCompleted;

    /// <summary>SDK 寫出的每一則訊息，含樣本放完之後多寫的。</summary>
    public IReadOnlyList<JsonElement> Writes
    {
        get
        {
            lock (_writes)
            {
                return [.. _writes];
            }
        }
    }

    public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task WriteAsync(string data, CancellationToken cancellationToken = default)
    {
        foreach (var line in data.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var message = JsonSerializer.Deserialize<JsonElement>(line);
            lock (_writes)
            {
                _writes.Add(message);
            }

            _written.Writer.TryWrite(message);
        }

        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<JsonElement> ReadMessagesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var sendIndex = 0;
        foreach (var (direction, expected) in fixture.Entries)
        {
            if (direction == "recv")
            {
                yield return RewriteRequestId(expected);
                continue;
            }

            sendIndex++;
            if (!await _written.Reader.WaitToReadAsync(cancellationToken))
            {
                yield break;
            }

            _written.Reader.TryRead(out var actual);
            EnsureMatches(sendIndex, expected, actual);
        }

        await _closed.Task.WaitAsync(cancellationToken);
    }

    public Task EndInputAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _closed.TrySetResult();
        _written.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    private void EnsureMatches(int sendIndex, JsonElement expected, JsonElement actual)
    {
        var expectedNode = JsonNode.Parse(expected.GetRawText())!;
        if (expectedNode["type"]?.GetValue<string>() == "control_request"
            && expectedNode["request_id"]?.GetValue<string>() is { } recordedId
            && actual.TryGetProperty("request_id", out var actualId))
        {
            _requestIds[recordedId] = actualId.GetString()!;
            expectedNode["request_id"] = actualId.GetString();
        }

        if (!JsonNode.DeepEquals(expectedNode, JsonNode.Parse(actual.GetRawText())))
        {
            throw new InvalidOperationException(
                $"第 {sendIndex} 則送出的訊息跟樣本不同。\n預期：{expectedNode.ToJsonString()}\n實際：{actual.GetRawText()}");
        }
    }

    private JsonElement RewriteRequestId(JsonElement received)
    {
        if (received.GetProperty("type").GetString() != "control_response")
        {
            return received;
        }

        var node = JsonNode.Parse(received.GetRawText())!;
        var response = node["response"]!;
        if (_requestIds.TryGetValue(response["request_id"]!.GetValue<string>(), out var actualId))
        {
            response["request_id"] = actualId;
        }

        return JsonSerializer.Deserialize<JsonElement>(node.ToJsonString());
    }
}
