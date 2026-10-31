using System.Runtime.CompilerServices;
using PamisWang.ClaudeAgentSdk.Messages;
using PamisWang.ClaudeAgentSdk.Transport;

namespace PamisWang.ClaudeAgentSdk;

/// <summary>一次性的問答：問一句、讀完回覆就結束。要多輪對話或中途中斷，改用 <see cref="ClaudeAgentClient"/>。</summary>
/// <example>
/// <code>
/// await foreach (var message in ClaudeAgent.QueryAsync("法國的首都是哪裡？"))
/// {
///     if (message is ResultMessage result)
///     {
///         Console.WriteLine(result.Result);
///     }
/// }
/// </code>
/// </example>
public static class ClaudeAgent
{
    /// <summary>
    /// 啟動 Claude Code、送出 <paramref name="prompt"/>，逐則回傳 CLI 送來的訊息，直到 CLI 結束；結束或中途停止讀取時關閉 CLI。
    /// 對應 Python SDK 的 <c>query()</c>：沒有 <see cref="ClaudeAgentOptions.CanUseTool"/>、<see cref="ClaudeAgentOptions.Hooks"/>
    /// 也沒有 SDK MCP server（<see cref="Mcp.McpSdkServerConfig"/>）時送出後就關 stdin；
    /// 有的話要留著 stdin 回覆權限詢問、hook 與工具呼叫，等到整個 run 結束才關：
    /// 結果之後 CLI 回報 <c>idle</c>（<c>session_state_changed</c>），或不回報 session state 的舊版 CLI 送來結果、又沒有追蹤中的背景 agent。
    /// 背景 agent（<c>local_agent</c>、<c>local_workflow</c>）還在跑時，結果只代表一輪結束，stdin 照樣留著。
    /// 結果之後 CLI 一直回報 running 卻沒有新的一輪時，最多等 <c>CLAUDE_CODE_PRINT_BG_WAIT_CEILING_MS</c>（預設 10 分鐘，<c>0</c> 不限）。
    /// </summary>
    /// <exception cref="CliNotFoundException">找不到 Claude Code。</exception>
    /// <exception cref="CliConnectionException">啟動失敗，或 CLI 在握手完成前就結束。</exception>
    /// <exception cref="CliProcessException">CLI 異常結束。</exception>
    /// <exception cref="ArgumentException">選項互相衝突。</exception>
    public static IAsyncEnumerable<ClaudeMessage> QueryAsync(
        string prompt,
        ClaudeAgentOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        return QueryCoreAsync(prompt, options ?? new ClaudeAgentOptions(), transport: null, cancellationToken);
    }

    /// <summary>測試用：可以換掉 transport。</summary>
    internal static async IAsyncEnumerable<ClaudeMessage> QueryCoreAsync(
        string prompt,
        ClaudeAgentOptions options,
        ITransport? transport,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var client = new ClaudeAgentClient(options, transport);
        await using (client.ConfigureAwait(false))
        {
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            await client.SendWithSessionIdAsync(prompt, sessionId: "", cancellationToken).ConfigureAwait(false);

            // 跟 Python 一樣在背景等 run 結束再關 stdin：沒有要回覆 CLI 的回呼、hook 或 SDK MCP server 就馬上關
            client.StartWaitForResultAndEndInput();

            await foreach (var message in client.ReceiveMessagesAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return message;
            }
        }
    }
}
