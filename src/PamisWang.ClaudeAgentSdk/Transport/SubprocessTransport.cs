using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace PamisWang.ClaudeAgentSdk.Transport;

/// <summary>
/// 以子程序啟動 CLI，透過 stdin、stdout 收發 stream-json。對應 Python SDK 的 <c>SubprocessCLITransport</c>。
/// </summary>
internal sealed class SubprocessTransport : ITransport
{
    // 不指定的話 Windows 會用主控台的字碼頁，中文會變亂碼；BOM 會讓 CLI 讀到的第一行解析失敗
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly ClaudeAgentOptions _options;
    private readonly CliLocator _locator;
    // stdin 同一時間只能有一個寫入者：好幾個權限回覆同時寫，兩行 JSON 會混在一起
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private Process? _process;
    private PipeReader? _stdout;
    private StreamWriter? _stdin;
    private Task? _stderrPump;
    private volatile bool _ready;
    private volatile bool _closing;

    public SubprocessTransport(ClaudeAgentOptions options)
        : this(options, CliLocator.ForCurrentProcess())
    {
    }

    internal SubprocessTransport(ClaudeAgentOptions options, CliLocator locator)
    {
        _options = options;
        _locator = locator;
    }

    /// <summary>關 stdin 後等 CLI 自己結束的時間，也是每個收尾步驟的等待上限。測試會調短。</summary>
    internal TimeSpan ShutdownGracePeriod { get; init; } = TimeSpan.FromSeconds(5);

    internal int? ProcessId => _process?.Id;

    public bool IsReady => _ready;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_process is not null)
        {
            return;
        }

        var cliPath = _locator.Find(_options.CliPath);
        _locator.EnsureRunnable(cliPath);

        if (_options.Cwd is not null && !Directory.Exists(_options.Cwd))
        {
            throw new CliConnectionException($"工作目錄不存在：{_options.Cwd}");
        }

        if (_options.User is not null && OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "ClaudeAgentOptions.User 在 Windows 上不支援：用另一個使用者的身分啟動程序需要那個帳號的密碼。Python SDK 的 user 也只在 POSIX 有效。");
        }

        // 跟 Python 一樣在組參數、啟動之前檢查版本；檢查本身不會失敗
        if (!CliVersionCheck.IsSkipped(Environment.GetEnvironmentVariable(CliVersionCheck.SkipEnvironmentVariable)))
        {
            await CliVersionCheck.RunAsync(cliPath, _options, cancellationToken).ConfigureAwait(false);
        }

        var startInfo = new ProcessStartInfo(cliPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            StandardInputEncoding = Utf8NoBom,
            WorkingDirectory = _options.Cwd ?? "",
        };
        // 有 callback 才導向 stderr：沒導向就不用讀，也就不會因為沒人讀而塞滿管線。
        // 編碼只能在有導向時設定，不然 Process.Start 會丟例外
        if (_options.Stderr is not null)
        {
            startInfo.RedirectStandardError = true;
            startInfo.StandardErrorEncoding = Utf8NoBom;
        }

        // ArgumentList 逐一加上參數，不經過 shell 也不用自己處理引號
        foreach (var argument in CliArguments.Build(_options, OperatingSystem.IsWindows()))
        {
            startInfo.ArgumentList.Add(argument);
        }

        CliEnvironment.Apply(startInfo.Environment, _options);

        // 對應 Python 的 open_process(user=…)：.NET 在 Unix 上會在子程序裡切換 uid、gid
        if (_options.User is { } user)
        {
            startInfo.UserName = user;
        }

        Process process;
        try
        {
            process = Process.Start(startInfo)
                ?? throw new CliConnectionException($"無法啟動 Claude Code：{cliPath}");
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 2)
        {
            // ERROR_FILE_NOT_FOUND／ENOENT
            throw new CliNotFoundException($"找不到 Claude Code：{cliPath}", e);
        }
        catch (Win32Exception e)
        {
            throw new CliConnectionException($"無法啟動 Claude Code：{cliPath}（{e.Message}）", e);
        }

        _process = process;
        // stdout 直接讀位元組，由 NdjsonReader 切行後再解碼
        _stdout = PipeReader.Create(process.StandardOutput.BaseStream);
        _stdin = process.StandardInput;
        if (_options.Stderr is { } callback)
        {
            _stderrPump = PumpStderrAsync(process.StandardError, callback);
        }

        _ready = true;
    }

    public async Task WriteAsync(string data, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 檢查都放在鎖裡面，避免檢查完、寫入前被 DisposeAsync 關掉
            if (!_ready || _stdin is null || _process is null)
            {
                throw new CliConnectionException("還沒連上 Claude Code，或已經關閉，無法寫入。");
            }

            if (_process.HasExited)
            {
                throw new CliConnectionException($"Claude Code 已經結束（exit code {_process.ExitCode}），無法寫入。");
            }

            try
            {
                await _stdin.WriteAsync(data.AsMemory(), cancellationToken).ConfigureAwait(false);
                await _stdin.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (IOException e)
            {
                _ready = false;
                throw new CliConnectionException("寫入 Claude Code 的 stdin 失敗。", e);
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async IAsyncEnumerable<JsonElement> ReadMessagesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (_process is not { } process || _stdout is not { } stdout)
        {
            throw new CliConnectionException("還沒連上 Claude Code。");
        }

        await foreach (var message in NdjsonReader.ReadAsync(stdout, _options.MaxBufferSize, cancellationToken)
            .ConfigureAwait(false))
        {
            yield return message;
        }

        // stdout 關了：程序正在結束。主動關閉時的結束代碼不算錯誤
        if (_closing)
        {
            yield break;
        }

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            _ready = false;
            throw new CliProcessException("Claude Code 異常結束", process.ExitCode);
        }
    }

    public async Task EndInputAsync(CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await CloseStdinAsync().ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// 關閉 stdin，等 CLI 自己結束；等不到才強制結束整個程序樹。每一步都有時間上限，不會卡住呼叫端。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_process is not { } process)
        {
            _ready = false;
            return;
        }

        _closing = true;

        // 拿著寫入鎖關 stdin，避免跟寫入撞在一起；寫入卡在塞滿的管線時也不能讓收尾跟著卡住
        var locked = await _writeLock.WaitAsync(ShutdownGracePeriod).ConfigureAwait(false);
        try
        {
            _ready = false;
            await CloseStdinAsync().ConfigureAwait(false);
        }
        finally
        {
            if (locked)
            {
                _writeLock.Release();
            }
        }

        // 收到 stdin EOF 後，CLI 要時間把 session 檔寫完；太早強制結束，最後一則訊息會遺失（Python SDK #625）
        if (!await WaitForExitAsync(process, ShutdownGracePeriod).ConfigureAwait(false))
        {
            try
            {
                // CLI 可能開了子程序（Bash 工具、MCP server），強制結束時一起收掉
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // 剛好在這之間自己結束了
            }

            await WaitForExitAsync(process, ShutdownGracePeriod).ConfigureAwait(false);
        }

        if (_stderrPump is not null)
        {
            // 程序結束後 stderr 會讀到 EOF，最後幾行要送完 callback 才算收尾
            await _stderrPump.WaitAsync(ShutdownGracePeriod)
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        if (_stdout is not null)
        {
            await _stdout.CompleteAsync().ConfigureAwait(false);
        }

        process.Dispose();
        _process = null;
        _stdout = null;
        _stderrPump = null;
    }

    private async Task CloseStdinAsync()
    {
        if (_stdin is null)
        {
            return;
        }

        try
        {
            await _stdin.DisposeAsync().ConfigureAwait(false);
        }
        catch (IOException)
        {
            // CLI 已經結束，管線斷了；本來就是要關掉它
        }

        _stdin = null;
    }

    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout)
    {
        using var timeoutSource = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static async Task PumpStderrAsync(StreamReader stderr, Action<string> callback)
    {
        try
        {
            while (await stderr.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                line = line.TrimEnd();
                if (line.Length == 0)
                {
                    continue;
                }

                try
                {
                    callback(line);
                }
#pragma warning disable CA1031 // 呼叫端的 callback 出錯不能中斷讀取，不然之後的 stderr 都會不見
                catch (Exception)
#pragma warning restore CA1031
                {
                }
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            // 程序結束、管線關閉
        }
    }
}
