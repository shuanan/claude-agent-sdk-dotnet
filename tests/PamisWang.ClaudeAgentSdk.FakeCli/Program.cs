// 測試用的假 Claude Code CLI：SubprocessTransport 的測試把 CliPath 指到這個執行檔，
// 用環境變數 FAKE_CLAUDE_MODE 決定行為。stdin、stdout、stderr 都直接讀寫位元組、不經過主控台的編碼，
// 測到的才是 SDK 那一端的 UTF-8 處理。

using System.Text;
using System.Text.Json;

var mode = Environment.GetEnvironmentVariable("FAKE_CLAUDE_MODE") ?? "echo";
using var stdin = Console.OpenStandardInput();
using var stdout = Console.OpenStandardOutput();
using var stderr = Console.OpenStandardError();

switch (mode)
{
    case "echo":
        // 先回報收到的參數、工作目錄與環境變數，再把 stdin 原樣送回 stdout，直到 stdin 關閉
        WriteLine(stdout, Report(args));
        await stdin.CopyToAsync(stdout);
        return 0;

    case "stderr":
        WriteText(stderr, "第一行錯誤\n\n第二行錯誤\n");
        WriteLine(stdout, """{"type":"done"}""");
        return 0;

    case "exit":
        WriteLine(stdout, """{"type":"bye"}""");
        return 3;

    case "hang":
        // stdin 關了也不結束，要等 SDK 強制結束
        WriteLine(stdout, """{"type":"hanging"}""");
        await Task.Delay(Timeout.Infinite);
        return 0;

    default:
        WriteText(stderr, $"不認得的 FAKE_CLAUDE_MODE：{mode}\n");
        return 2;
}

static string Report(string[] args)
{
    string[] names =
    [
        "CLAUDECODE",
        "CLAUDE_CODE_ENTRYPOINT",
        "CLAUDE_AGENT_SDK_VERSION",
        "CLAUDE_AGENT_SDK_CLIENT_APP",
        "CLAUDE_CODE_SDK_READS_SESSION_STATE",
        "PWD",
        "FAKE_CLAUDE_EXTRA",
    ];
    var env = names.ToDictionary(name => name, name => Environment.GetEnvironmentVariable(name));
    return JsonSerializer.Serialize(new { type = "fake_report", argv = args, cwd = Environment.CurrentDirectory, env });
}

static void WriteLine(Stream stream, string json) => WriteText(stream, json + "\n");

static void WriteText(Stream stream, string text)
{
    stream.Write(Encoding.UTF8.GetBytes(text));
    stream.Flush();
}
