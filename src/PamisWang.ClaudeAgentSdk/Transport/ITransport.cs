using System.Text.Json;

namespace PamisWang.ClaudeAgentSdk.Transport;

/// <summary>
/// 跟 Claude Code 收發 stream-json 的通道，上層的 control 協定與讀取迴圈建在它上面。
/// 對應 Python SDK 的 <c>Transport</c>；測試用假的實作重播錄下的協定樣本。
/// </summary>
internal interface ITransport : IAsyncDisposable
{
    /// <summary>已經連線，而且還能寫入。</summary>
    bool IsReady { get; }

    /// <summary>啟動 CLI；已經連線就什麼都不做。</summary>
    Task ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>寫入原始資料。每則訊息後面的換行由呼叫端加上。</summary>
    Task WriteAsync(string data, CancellationToken cancellationToken = default);

    /// <summary>讀取 CLI 送來的訊息，每行一則，直到 CLI 關掉 stdout。</summary>
    IAsyncEnumerable<JsonElement> ReadMessagesAsync(CancellationToken cancellationToken = default);

    /// <summary>關閉 stdin，告訴 CLI 不會再有輸入。</summary>
    Task EndInputAsync(CancellationToken cancellationToken = default);
}
