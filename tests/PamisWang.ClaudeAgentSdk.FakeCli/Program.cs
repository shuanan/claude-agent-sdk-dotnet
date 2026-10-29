// 測試用的假 Claude Code CLI：SubprocessTransport 的測試把 CliPath 指到這個執行檔，
// 用環境變數 FAKE_CLAUDE_MODE 決定行為。stdin、stdout、stderr 都直接讀寫位元組、不經過主控台的編碼，
// 測到的才是 SDK 那一端的 UTF-8 處理。

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

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

    case "chat":
        await ChatAsync(stdin, stdout);
        return 0;

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

// 最小的 CLI：回覆 initialize 與 interrupt；每收到一句話就回一則 assistant 與一個 result。
// 收到「請求權限」時先發出權限詢問，等 SDK 回覆後，把回覆的 behavior 放進 result。
static async Task ChatAsync(Stream stdin, Stream stdout)
{
    const string session = "fake-session";
    using var reader = new StreamReader(stdin, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    while (await reader.ReadLineAsync() is { } line)
    {
        var message = JsonNode.Parse(line)!;
        switch (message["type"]!.GetValue<string>())
        {
            case "control_request":
                var subtype = message["request"]!["subtype"]!.GetValue<string>();
                var data = subtype == "initialize" ? new JsonObject { ["claude_code_version"] = "fake" } : new JsonObject();
                Send(new JsonObject
                {
                    ["type"] = "control_response",
                    ["response"] = new JsonObject
                    {
                        ["subtype"] = "success",
                        ["request_id"] = message["request_id"]!.GetValue<string>(),
                        ["response"] = data,
                    },
                });
                break;

            case "control_response":
                var behavior = message["response"]!["response"]!["behavior"]!.GetValue<string>();
                SendResult(behavior);
                break;

            case "user":
                var text = message["message"]!["content"]!.GetValue<string>();
                Send(new JsonObject { ["type"] = "system", ["subtype"] = "init", ["session_id"] = session });
                if (text == "請求權限")
                {
                    Send(new JsonObject
                    {
                        ["type"] = "control_request",
                        ["request_id"] = "fake-permission-1",
                        ["request"] = new JsonObject
                        {
                            ["subtype"] = "can_use_tool",
                            ["tool_name"] = "Write",
                            ["input"] = new JsonObject { ["file_path"] = "a.txt", ["content"] = "甲" },
                            ["permission_suggestions"] = new JsonArray(),
                            ["tool_use_id"] = "toolu_fake",
                        },
                    });
                    break;
                }

                Send(new JsonObject
                {
                    ["type"] = "assistant",
                    ["message"] = new JsonObject
                    {
                        ["model"] = "fake",
                        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = $"收到：{text}" }),
                    },
                    ["parent_tool_use_id"] = null,
                    ["session_id"] = session,
                });
                SendResult($"收到：{text}");
                break;
        }
    }

    void SendResult(string result) => Send(new JsonObject
    {
        ["type"] = "result",
        ["subtype"] = "success",
        ["is_error"] = false,
        ["duration_ms"] = 1,
        ["duration_api_ms"] = 1,
        ["num_turns"] = 1,
        ["result"] = result,
        ["session_id"] = session,
    });

    void Send(JsonObject json) => WriteLine(stdout, json.ToJsonString());
}

static void WriteLine(Stream stream, string json) => WriteText(stream, json + "\n");

static void WriteText(Stream stream, string text)
{
    stream.Write(Encoding.UTF8.GetBytes(text));
    stream.Flush();
}
