using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using PamisWang.ClaudeAgentSdk.Transport;

namespace PamisWang.ClaudeAgentSdk.Tests.Transport;

/// <summary>用假的 CLI（FakeClaude）跑真的子程序，驗證參數、編碼、stderr、結束代碼與收尾。</summary>
public sealed class SubprocessTransportTests : IDisposable
{
    private readonly TempDirectory _cwd = new();

    public void Dispose() => _cwd.Dispose();

    [Fact]
    public async Task ConnectAsync_StartsCliWithArgumentsWorkingDirectoryAndEnvironment()
    {
        var options = Options("echo", env: new() { ["FAKE_CLAUDE_EXTRA"] = "額外的值" });
        await using var transport = new SubprocessTransport(options);

        await transport.ConnectAsync();
        await transport.EndInputAsync();
        var report = Assert.Single(await ReadAllAsync(transport));

        // 包含 --system-prompt 後面那個空字串：空參數也要原樣送到
        Assert.Equal(
            CliArguments.Build(options, OperatingSystem.IsWindows()),
            report.GetProperty("argv").EnumerateArray().Select(arg => arg.GetString()));
        Assert.Equal(Normalize(_cwd.Path), Normalize(report.GetProperty("cwd").GetString()!));
        var env = report.GetProperty("env");
        Assert.Equal("sdk-py", env.GetProperty("CLAUDE_CODE_ENTRYPOINT").GetString());
        Assert.Equal("0.2.174", env.GetProperty("CLAUDE_AGENT_SDK_VERSION").GetString());
        Assert.Equal("1", env.GetProperty("CLAUDE_CODE_SDK_READS_SESSION_STATE").GetString());
        Assert.Equal(JsonValueKind.Null, env.GetProperty("CLAUDECODE").ValueKind);
        Assert.Equal(_cwd.Path, env.GetProperty("PWD").GetString());
        Assert.Equal("額外的值", env.GetProperty("FAKE_CLAUDE_EXTRA").GetString());
    }

    [Fact]
    public async Task WriteAsync_Utf8TextRoundTripsThroughProcess()
    {
        const string line = """{"type":"user","message":{"role":"user","content":"你好，世界 🐱"},"parent_tool_use_id":null,"session_id":"default"}""";
        await using var transport = new SubprocessTransport(Options("echo"));
        await transport.ConnectAsync();

        await transport.WriteAsync(line + "\n");
        await transport.EndInputAsync();
        var messages = await ReadAllAsync(transport);

        Assert.Equal(2, messages.Count);
        Assert.Equal(line, messages[1].GetRawText());
    }

    [Fact]
    public async Task WriteAsync_ConcurrentWrites_AreNotInterleaved()
    {
        var lines = Enumerable.Range(0, 50)
            .Select(i => $$"""{"type":"control_response","index":{{i}},"padding":"{{new string('長', 2000)}}"}""")
            .ToList();
        await using var transport = new SubprocessTransport(Options("echo"));
        await transport.ConnectAsync();

        // 邊寫邊讀：假 CLI 回寫的資料沒人讀，stdout 管線塞滿後它就不再讀 stdin，兩邊會互等
        var reading = ReadAllAsync(transport);
        await Task.WhenAll(lines.Select(line => Task.Run(() => transport.WriteAsync(line + "\n"))));
        await transport.EndInputAsync();
        var echoed = (await reading).Skip(1).ToList();

        Assert.Equal(
            Enumerable.Range(0, 50),
            echoed.Select(message => message.GetProperty("index").GetInt32()).Order());
    }

    [Fact]
    public async Task ReadMessagesAsync_NonZeroExit_ThrowsCliProcessException()
    {
        await using var transport = new SubprocessTransport(Options("exit"));
        await transport.ConnectAsync();
        List<JsonElement> received = [];

        var error = await Assert.ThrowsAsync<CliProcessException>(async () =>
        {
            await foreach (var message in transport.ReadMessagesAsync())
            {
                received.Add(message);
            }
        });

        Assert.Equal(3, error.ExitCode);
        Assert.Equal("bye", Assert.Single(received).GetProperty("type").GetString());
    }

    [Fact]
    public async Task Stderr_CallbackReceivesEachNonEmptyLine()
    {
        var lines = new ConcurrentQueue<string>();
        var transport = new SubprocessTransport(Options("stderr", lines.Enqueue));
        await transport.ConnectAsync();

        await ReadAllAsync(transport);
        await transport.DisposeAsync();

        Assert.Equal(["第一行錯誤", "第二行錯誤"], lines);
    }

    [Fact]
    public async Task Stderr_CallbackThrowing_DoesNotStopLaterLines()
    {
        var lines = new ConcurrentQueue<string>();
        var transport = new SubprocessTransport(Options("stderr", line =>
        {
            lines.Enqueue(line);
            throw new InvalidOperationException("callback 出錯");
        }));
        await transport.ConnectAsync();

        await ReadAllAsync(transport);
        await transport.DisposeAsync();

        Assert.Equal(2, lines.Count);
    }

    [Fact]
    public async Task DisposeAsync_CliIgnoringEndOfInput_IsKilledAfterGracePeriod()
    {
        var transport = new SubprocessTransport(Options("hang")) { ShutdownGracePeriod = TimeSpan.FromMilliseconds(300) };
        await transport.ConnectAsync();
        await using (var messages = transport.ReadMessagesAsync().GetAsyncEnumerator())
        {
            // 等它開始執行
            Assert.True(await messages.MoveNextAsync());
        }

        var processId = transport.ProcessId!.Value;
        var stopwatch = Stopwatch.StartNew();

        await transport.DisposeAsync();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"收尾花了 {stopwatch.Elapsed}");
        Assert.True(HasExited(processId));
    }

    [Fact]
    public async Task DisposeAsync_CalledTwice_IsHarmless()
    {
        var transport = new SubprocessTransport(Options("echo"));
        await transport.ConnectAsync();

        await transport.DisposeAsync();
        await transport.DisposeAsync();

        Assert.False(transport.IsReady);
    }

    [Fact]
    public async Task WriteAsync_AfterDispose_ThrowsCliConnectionException()
    {
        var transport = new SubprocessTransport(Options("echo"));
        await transport.ConnectAsync();
        await transport.DisposeAsync();

        await Assert.ThrowsAsync<CliConnectionException>(() => transport.WriteAsync("{}\n"));
    }

    [Fact]
    public async Task WriteAsync_BeforeConnect_ThrowsCliConnectionException()
    {
        await using var transport = new SubprocessTransport(Options("echo"));

        await Assert.ThrowsAsync<CliConnectionException>(() => transport.WriteAsync("{}\n"));
    }

    [Fact]
    public async Task ConnectAsync_MissingWorkingDirectory_ThrowsCliConnectionException()
    {
        var options = new ClaudeAgentOptions { CliPath = FakeClaude.Path, Cwd = Path.Combine(_cwd.Path, "沒有這個資料夾") };
        await using var transport = new SubprocessTransport(options);

        var error = await Assert.ThrowsAsync<CliConnectionException>(() => transport.ConnectAsync());
        Assert.Contains("工作目錄不存在", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConnectAsync_MissingExecutable_ThrowsCliNotFoundException()
    {
        var options = new ClaudeAgentOptions { CliPath = Path.Combine(_cwd.Path, "claude.exe"), Cwd = _cwd.Path };
        await using var transport = new SubprocessTransport(options);

        await Assert.ThrowsAsync<CliNotFoundException>(() => transport.ConnectAsync());
    }

    [Fact]
    public async Task ConnectAsync_BatchFileOnWindows_IsRefusedBeforeStarting()
    {
        var options = new ClaudeAgentOptions { CliPath = _cwd.CreateFile("claude.cmd"), Cwd = _cwd.Path };
        var locator = new CliLocator(pathVariable: null, _cwd.Path, isWindows: true);
        await using var transport = new SubprocessTransport(options, locator);

        // 確切是 CliConnectionException，不是子類別 CliNotFoundException
        var error = await Assert.ThrowsAsync<CliConnectionException>(() => transport.ConnectAsync());
        Assert.Contains("批次檔", error.Message, StringComparison.Ordinal);
        Assert.Null(transport.ProcessId);
    }

    private ClaudeAgentOptions Options(string mode, Action<string>? stderr = null, Dictionary<string, string>? env = null)
    {
        var allEnv = env ?? [];
        allEnv["FAKE_CLAUDE_MODE"] = mode;
        return new ClaudeAgentOptions
        {
            CliPath = FakeClaude.Path,
            Cwd = _cwd.Path,
            Model = "haiku",
            Resume = "34953276-f9da-4b8c-a789-77d8ee05bc37",
            IncludePartialMessages = true,
            DisallowedTools = ["AskUserQuestion"],
            Env = allEnv,
            Stderr = stderr,
        };
    }

    private static async Task<List<JsonElement>> ReadAllAsync(SubprocessTransport transport)
    {
        List<JsonElement> messages = [];
        await foreach (var message in transport.ReadMessagesAsync())
        {
            messages.Add(message);
        }

        return messages;
    }

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)).ToUpperInvariant();

    private static bool HasExited(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }
}
