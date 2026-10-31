using System.Text.Json;
using System.Text.Json.Nodes;
using PamisWang.ClaudeAgentSdk.Protocol;
using PamisWang.ClaudeAgentSdk.Transport;

namespace PamisWang.ClaudeAgentSdk.Tests.Protocol;

/// <summary>
/// 跟真的 Python SDK 對照（tmp/phase4-cc/golden_cmd.py 的輸出 golden_cmd.txt）：
/// 每個選項 <c>_build_command</c> 的 argv（去掉開頭的執行檔，跟 <see cref="CliArguments.Build"/> 一樣）、
/// <c>Query.initialize</c> 送出的 request，以及 <c>stamp_user_message</c> 標記後的使用者訊息。
/// argv 要逐項相同、順序也一樣；JSON 要完全相同（鍵的順序可以不同）。
/// </summary>
/// <remarks>
/// Python 那邊沒辦法對照的：<c>skills="pdf"</c>（字串而不是清單）在 Python 是 TypeError，C# 的型別根本寫不出來；
/// <c>load_timeout_ms</c> 只用在 session store，這一版沒有移植；initialize 的 <c>timeout=12.5</c> 是直接傳給 <c>Query</c> 的參數，
/// 實際的 client 一律取 <c>max(CLAUDE_CODE_STREAM_CLOSE_TIMEOUT/1000, 60)</c>，對照的是那條規則。
/// </remarks>
public sealed class RemainingOptionsGoldenParityTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    // golden_cmd.txt 的 CMD 行，原樣貼上
    private static readonly Dictionary<string, string> GoldenArgv = new()
    {
        ["skills_all"] = """["--output-format", "stream-json", "--verbose", "--system-prompt", "", "--allowedTools", "Skill", "--setting-sources=user,project", "--input-format", "stream-json"]""",
        ["skills_list"] = """["--output-format", "stream-json", "--verbose", "--system-prompt", "", "--allowedTools", "Skill(pdf),Skill(my-plugin:review)", "--setting-sources=user,project", "--input-format", "stream-json"]""",
        ["skills_list_with_allowed_and_sources"] = """["--output-format", "stream-json", "--verbose", "--system-prompt", "", "--allowedTools", "Read,Skill(pdf)", "--setting-sources=project", "--input-format", "stream-json"]""",
        ["skills_empty"] = """["--output-format", "stream-json", "--verbose", "--system-prompt", "", "--setting-sources=user,project", "--input-format", "stream-json"]""",
        ["skills_slash"] = """["--output-format", "stream-json", "--verbose", "--system-prompt", "", "--allowedTools", "Skill(a/b)", "--setting-sources=user,project", "--input-format", "stream-json"]""",
        ["task_budget"] = """["--output-format", "stream-json", "--verbose", "--system-prompt", "", "--task-budget", "50000", "--input-format", "stream-json"]""",
        ["include_hook_events"] = """["--output-format", "stream-json", "--verbose", "--system-prompt", "", "--include-hook-events", "--input-format", "stream-json"]""",
        ["resume_session_at"] = """["--output-format", "stream-json", "--verbose", "--system-prompt", "", "--resume=abc", "--resume-session-at=uuid-1", "--input-format", "stream-json"]""",
        ["resume_drops_turn"] = """["--output-format", "stream-json", "--verbose", "--system-prompt", "", "--resume=abc", "--resume-drops-turn=uuid-2", "--input-format", "stream-json"]""",
        ["forward_subagent_text"] = """["--output-format", "stream-json", "--verbose", "--system-prompt", "", "--input-format", "stream-json"]""",
        ["verbatim_prompts"] = """["--output-format", "stream-json", "--verbose", "--system-prompt", "", "--input-format", "stream-json"]""",
        ["enable_file_checkpointing"] = """["--output-format", "stream-json", "--verbose", "--system-prompt", "", "--input-format", "stream-json"]""",
        ["user"] = """["--output-format", "stream-json", "--verbose", "--system-prompt", "", "--input-format", "stream-json"]""",
    };

    // golden_cmd.txt 的 INIT 行裡的 req
    private static readonly Dictionary<string, string> GoldenInitialize = new()
    {
        ["none"] = """{"subtype": "initialize", "hooks": null}""",
        ["skills_list"] = """{"subtype": "initialize", "hooks": null, "skills": ["pdf"]}""",
        ["skills_all"] = """{"subtype": "initialize", "hooks": null}""",
        ["forward_subagent_text"] = """{"subtype": "initialize", "hooks": null, "forwardSubagentText": true}""",
        ["exclude_dynamic"] = """{"subtype": "initialize", "hooks": null, "excludeDynamicSections": true, "systemPromptSnapshot": false}""",
    };

    private const string GoldenStampOn =
        """{"type": "user", "message": {"role": "user", "content": "hi @x"}, "parent_tool_use_id": null, "session_id": "default", "client_composed": true}""";

    private const string GoldenStampOff =
        """{"type": "user", "message": {"role": "user", "content": "hi @x"}, "parent_tool_use_id": null, "session_id": "default", "client_composed": false}""";

    public static TheoryData<string> ArgvCases => [.. GoldenArgv.Keys];

    public static TheoryData<string> InitializeCases => [.. GoldenInitialize.Keys];

    [Theory]
    [MemberData(nameof(ArgvCases))]
    public void Build_MatchesPythonArgv(string name)
    {
        var expected = JsonSerializer.Deserialize<string[]>(GoldenArgv[name])!;

        var actual = CliArguments.Build(ArgvOptions(name), isWindows: false);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Build_StarSkill_ThrowsLikePythonValueError()
    {
        // Python：ValueError: Invalid skill name '*': use skills="all" to enable every skill.
        var error = Assert.Throws<ArgumentException>(() =>
            CliArguments.Build(new ClaudeAgentOptions { Skills = new SkillsList(["*"]) }, isWindows: false));

        Assert.Contains("'*'", error.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(SkillsAll), error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(InitializeCases))]
    public async Task ConnectAsync_InitializeRequestMatchesPython(string name)
    {
        var transport = new ScriptedTransport();
        await using var client = new ClaudeAgentClient(InitializeOptions(name), transport);

        var connecting = client.ConnectAsync();
        var initialize = await transport.NextWriteAsync();
        Respond(transport, initialize);
        await connecting.WaitAsync(Timeout);

        AssertJsonEqual(GoldenInitialize[name], initialize.GetProperty("request"));
    }

    [Fact]
    public void InitializeTimeout_DefaultMatchesPythonSixtySeconds()
    {
        // golden 的 none：timeout 60.0；Python 的 client 是 max(CLAUDE_CODE_STREAM_CLOSE_TIMEOUT / 1000, 60)
        Assert.Equal(TimeSpan.FromSeconds(60), ClaudeAgentClient.InitializeTimeout(null));
        Assert.Equal(TimeSpan.FromSeconds(60), ClaudeAgentClient.InitializeTimeout("12500"));
        Assert.Equal(TimeSpan.FromSeconds(90), ClaudeAgentClient.InitializeTimeout("90000"));
    }

    [Fact]
    public async Task SendAsync_VerbatimPrompts_StampsLikePython()
    {
        var sent = await SendThroughClientAsync(verbatimPrompts: true);

        AssertJsonEqual(GoldenStampOn, sent);
    }

    [Fact]
    public async Task SendAsync_WithoutVerbatimPrompts_LeavesMessageUnstamped()
    {
        // Python 的輸入本來就帶 client_composed: false，關掉時原樣留著；這裡的訊息由 SDK 自己組，關掉時就沒有這個欄位
        var expected = JsonNode.Parse(GoldenStampOff)!.AsObject();
        expected.Remove("client_composed");

        var sent = await SendThroughClientAsync(verbatimPrompts: false);

        AssertJsonEqual(expected.ToJsonString(), sent);
    }

    private static ClaudeAgentOptions ArgvOptions(string name) => name switch
    {
        "skills_all" => new() { Skills = new SkillsAll() },
        "skills_list" => new() { Skills = new SkillsList(["pdf", "my-plugin:review"]) },
        "skills_list_with_allowed_and_sources" => new() { Skills = new SkillsList(["pdf"]), AllowedTools = ["Read"], SettingSources = ["project"] },
        "skills_empty" => new() { Skills = new SkillsList([]) },
        "skills_slash" => new() { Skills = new SkillsList(["a/b"]) },
        "task_budget" => new() { TaskBudget = new TaskBudget(50000) },
        "include_hook_events" => new() { IncludeHookEvents = true },
        "resume_session_at" => new() { Resume = "abc", ResumeSessionAt = "uuid-1" },
        "resume_drops_turn" => new() { Resume = "abc", ResumeDropsTurn = "uuid-2" },
        "forward_subagent_text" => new() { ForwardSubagentText = true },
        "verbatim_prompts" => new() { VerbatimPrompts = true },
        "enable_file_checkpointing" => new() { EnableFileCheckpointing = true },
        "user" => new() { User = "nobody" },
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
    };

    private static ClaudeAgentOptions InitializeOptions(string name) => name switch
    {
        "none" => new(),
        "skills_list" => new() { Skills = new SkillsList(["pdf"]) },
        "skills_all" => new() { Skills = new SkillsAll() },
        "forward_subagent_text" => new() { ForwardSubagentText = true },
        "exclude_dynamic" => new() { SystemPrompt = new SystemPromptPreset { ExcludeDynamicSections = true, Snapshot = false } },
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
    };

    private static async Task<JsonElement> SendThroughClientAsync(bool verbatimPrompts)
    {
        var transport = new ScriptedTransport();
        await using var client = new ClaudeAgentClient(new ClaudeAgentOptions { VerbatimPrompts = verbatimPrompts }, transport);
        var connecting = client.ConnectAsync();
        Respond(transport, await transport.NextWriteAsync());
        await connecting.WaitAsync(Timeout);

        await client.SendAsync("hi @x");
        return await transport.NextWriteAsync();
    }

    private static void Respond(ScriptedTransport transport, JsonElement request) =>
        transport.Push($$$$"""{"type":"control_response","response":{"subtype":"success","request_id":"{{{{request.GetProperty("request_id").GetString()}}}}","response":{"commands":[]}}}""");

    private static void AssertJsonEqual(string expected, JsonElement actual) =>
        Assert.True(
            JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(actual.GetRawText())),
            $"預期：{expected}\n實際：{actual.GetRawText()}");
}
