using System.Runtime.Versioning;
using System.Text.Json;
using PamisWang.ClaudeAgentSdk.Protocol;
using PamisWang.ClaudeAgentSdk.Transport;

namespace PamisWang.ClaudeAgentSdk.Tests.Transport;

/// <summary>
/// Phase 4 的選項在 transport 這一層：skill 名稱的檢查與 allowed tools 合併、Windows 的參數安全、設定檔警告、
/// 檔案 checkpoint 的環境變數、CLI 版本檢查與 <see cref="ClaudeAgentOptions.User"/>。argv 的對照在 RemainingOptionsGoldenParityTests。
/// </summary>
public sealed class RemainingOptionsTests : IDisposable
{
    private readonly TempDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" pdf")]
    [InlineData("pdf ")]
    [InlineData("a(b")]
    [InlineData("a)b")]
    [InlineData("a,b")]
    [InlineData("a\tb")]
    [InlineData("a\u007fb")]
    [InlineData("a\u0085b")]
    [InlineData("*")]
    [InlineData("plugin:*")]
    [InlineData("pdf *")]
    [InlineData("/pdf")]
    [InlineData(@"a\\b")]
    [InlineData(@"pdf\")]
    public void Build_InvalidSkillName_ThrowsLikePython(string name)
    {
        Assert.Throws<ArgumentException>(() => CliArguments.Build(new ClaudeAgentOptions { Skills = new SkillsList([name]) }, isWindows: false));
    }

    [Theory]
    [InlineData(0x00)]
    [InlineData(0x1F)]
    [InlineData(0x9F)]
    [InlineData(0xFEFF)]
    public void Build_SkillNameWithControlCharOrBom_Throws(int codePoint)
    {
        // 用程式組，原始碼裡不放看不見的字元
        var name = "pdf" + (char)codePoint + "x";

        Assert.Throws<ArgumentException>(() => CliArguments.ValidateSkillName(name));
        Assert.Throws<ArgumentException>(() => CliArguments.ValidateSkillName((char)codePoint + "pdf"));
    }

    [Fact]
    public void Build_SkillNameWithUnpairedSurrogate_Throws()
    {
        // 屬性裡放不了不成對的 surrogate，在這裡組
        var name = "a" + (char)0xD800 + "b";

        var error = Assert.Throws<ArgumentException>(() => CliArguments.ValidateSkillName(name));
        Assert.Contains("surrogate", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("pdf")]
    [InlineData("my-plugin:review")]
    [InlineData("a/b")]
    [InlineData(@"a\b")]
    [InlineData("技能 🐱")]
    public void Build_ValidSkillName_IsAllowed(string name)
    {
        var args = CliArguments.Build(new ClaudeAgentOptions { Skills = new SkillsList([name]) }, isWindows: false);

        Assert.Contains($"Skill({name})", args);
    }

    [Fact]
    public void ApplySkillsDefaults_DoesNotDuplicateExistingEntriesOrChangeOptions()
    {
        var options = new ClaudeAgentOptions
        {
            AllowedTools = ["Skill", "Skill(pdf)"],
            Skills = new SkillsList(["pdf", "pdf", "docx"]),
            SettingSources = [],
        };

        var (allowedTools, settingSources) = CliArguments.ApplySkillsDefaults(options);
        var (allAllowed, _) = CliArguments.ApplySkillsDefaults(new ClaudeAgentOptions { AllowedTools = options.AllowedTools, Skills = new SkillsAll() });

        Assert.Equal(["Skill", "Skill(pdf)", "Skill(docx)"], allowedTools);
        Assert.Equal(["Skill", "Skill(pdf)"], allAllowed);
        // 空的清單是呼叫端明確的選擇，不換成預設
        Assert.Empty(settingSources!);
        Assert.Equal(["Skill", "Skill(pdf)"], options.AllowedTools);
    }

    [Fact]
    public void Build_NoSkills_LeavesSettingSourcesUnset()
    {
        Assert.DoesNotContain(CliArguments.Build(new ClaudeAgentOptions(), isWindows: false), arg => arg.StartsWith("--setting-sources", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_ResumeFlagsAndHookEvents_FollowPythonOrder()
    {
        var options = new ClaudeAgentOptions
        {
            Resume = "abc",
            IncludePartialMessages = true,
            IncludeHookEvents = true,
            StrictMcpConfig = true,
            ForkSession = true,
            ResumeSessionAt = "uuid-1",
            ResumeDropsTurn = "uuid-2",
            SettingSources = ["project"],
            TaskBudget = new TaskBudget(123),
            MaxBudgetUsd = 1m,
            DisallowedTools = ["Bash"],
            Model = "haiku",
        };

        var args = CliArguments.Build(options, isWindows: false);

        Assert.Equal(
            [
                "--output-format", "stream-json", "--verbose", "--system-prompt", "",
                "--max-budget-usd", "1",
                "--disallowedTools", "Bash",
                "--task-budget", "123",
                "--model", "haiku",
                "--resume=abc",
                "--include-partial-messages",
                "--include-hook-events",
                "--strict-mcp-config",
                "--fork-session",
                "--resume-session-at=uuid-1",
                "--resume-drops-turn=uuid-2",
                "--setting-sources=project",
                "--input-format", "stream-json",
            ],
            args);
    }

    [Fact]
    public void Build_EmptyResumeDropsTurn_IsStillSentButEmptyResumeSessionAtIsNot()
    {
        var args = CliArguments.Build(new ClaudeAgentOptions { ResumeSessionAt = "", ResumeDropsTurn = "" }, isWindows: false);

        Assert.Contains("--resume-drops-turn=", args);
        Assert.DoesNotContain(args, arg => arg.StartsWith("--resume-session-at", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("a&b")]
    [InlineData("a|b")]
    [InlineData("a%PATH%")]
    [InlineData("a\"b")]
    [InlineData("a\nb")]
    public void Build_WindowsCmdMetacharactersInResumeSessionAtOrDropsTurn_Throw(string value)
    {
        Assert.Throws<ArgumentException>(() => CliArguments.Build(new ClaudeAgentOptions { ResumeSessionAt = value }, isWindows: true));
        Assert.Throws<ArgumentException>(() => CliArguments.Build(new ClaudeAgentOptions { ResumeDropsTurn = value }, isWindows: true));

        // POSIX 不檢查，跟 Python 一樣
        Assert.Contains($"--resume-session-at={value}", CliArguments.Build(new ClaudeAgentOptions { ResumeSessionAt = value }, isWindows: false));
    }

    [Fact]
    public void Build_SandboxWithMissingSettingsFile_WarnsLikePython()
    {
        List<string> warnings = [];
        var missing = Path.Combine(_directory.Path, "missing.json");

        var args = CliArguments.Build(
            new ClaudeAgentOptions { Settings = missing, Sandbox = new SandboxSettings { Enabled = true }, Warning = warnings.Add },
            isWindows: false);

        Assert.Equal([$"Settings file not found: {missing}"], warnings);
        Assert.Equal("""{"sandbox":{"enabled":true}}""", args[args.ToList().IndexOf("--settings") + 1]);
    }

    [Fact]
    public void Build_SandboxWithUnparsableJsonSettings_WarnsAndTreatsAsPath()
    {
        List<string> warnings = [];

        CliArguments.Build(
            new ClaudeAgentOptions { Settings = "{not json}", Sandbox = new SandboxSettings { Enabled = true }, Warning = warnings.Add },
            isWindows: false);

        Assert.Equal(["Failed to parse settings as JSON, treating as file path: {not json}"], warnings);
    }

    [Fact]
    public void Build_SettingsFileWithoutSandbox_DoesNotWarn()
    {
        List<string> warnings = [];

        CliArguments.Build(new ClaudeAgentOptions { Settings = "/no/such/file.json", Warning = warnings.Add }, isWindows: false);

        Assert.Empty(warnings);
    }

    [Fact]
    public void Apply_EnableFileCheckpointing_SetsEnvAfterCallerEnv()
    {
        Dictionary<string, string?> env = [];
        CliEnvironment.Apply(env, new ClaudeAgentOptions
        {
            EnableFileCheckpointing = true,
            Env = new Dictionary<string, string> { ["CLAUDE_CODE_ENABLE_SDK_FILE_CHECKPOINTING"] = "false" },
        });
        Dictionary<string, string?> off = [];
        CliEnvironment.Apply(off, new ClaudeAgentOptions());

        Assert.Equal("true", env["CLAUDE_CODE_ENABLE_SDK_FILE_CHECKPOINTING"]);
        Assert.False(off.ContainsKey("CLAUDE_CODE_ENABLE_SDK_FILE_CHECKPOINTING"));
    }

    [Theory]
    [InlineData("2.1.312 (Claude Code)", false, 0)]
    [InlineData("2.0.0", false, 0)]
    [InlineData("1.9.99 (Claude Code)", false, 1)]
    [InlineData("2.1.247", true, 1)]
    [InlineData("2.1.248", true, 0)]
    [InlineData("1.0.0", true, 2)]
    [InlineData("  1.0.0\n", false, 1)]
    [InlineData("v1.0.0", false, 0)]
    [InlineData("garbage", true, 0)]
    [InlineData("", true, 0)]
    public void VersionCheck_Warnings_FollowPython(string output, bool verbatim, int count)
    {
        Assert.Equal(count, CliVersionCheck.Warnings(output, "/bin/claude", verbatim).Count);
    }

    [Fact]
    public void VersionCheck_WarningText_MatchesPython()
    {
        Assert.Equal(
            [
                "Claude Code version 1.5.0 at /bin/claude is unsupported in the Agent SDK. Minimum required version is 2.0.0. Some features may not work correctly.",
                "verbatim_prompts is enabled, but Claude Code version 1.5.0 at /bin/claude ignores it: prompts will still have @path mentions expanded and slash commands dispatched. Claude Code 2.1.248 or later is required.",
            ],
            CliVersionCheck.Warnings("1.5.0", "/bin/claude", verbatimPrompts: true));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("1", true)]
    [InlineData("0", true)]
    public void VersionCheck_SkipEnvironmentVariable_AnyNonEmptyValueSkips(string? value, bool skipped)
    {
        Assert.Equal(skipped, CliVersionCheck.IsSkipped(value));
    }

    [Fact]
    public async Task VersionCheck_RunsCliWithDashV_AndReportsWarnings()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var script = Script("old-claude", "#!/bin/sh\necho \"1.2.3 (Claude Code)\"\n");
        List<string> warnings = [];

        await CliVersionCheck.RunAsync(script, new ClaudeAgentOptions { VerbatimPrompts = true, Warning = warnings.Add }, CancellationToken.None);

        Assert.Equal(2, warnings.Count);
        Assert.Contains($"1.2.3 at {script}", warnings[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task VersionCheck_HangingOrMissingCli_NeverThrows()
    {
        List<string> warnings = [];
        var options = new ClaudeAgentOptions { Warning = warnings.Add };

        await CliVersionCheck.RunAsync(Path.Combine(_directory.Path, "no-such-claude"), options, CancellationToken.None);
        if (!OperatingSystem.IsWindows())
        {
            var started = DateTime.UtcNow;
            await CliVersionCheck.RunAsync(Script("hang", "#!/bin/sh\nsleep 30\n"), options, CancellationToken.None);
            Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
        }

        Assert.Empty(warnings);
    }

    [Fact]
    public async Task Connect_FakeCli_ReportsCheckpointingEnvAndPassesVersionCheckSilently()
    {
        List<string> warnings = [];
        var options = new ClaudeAgentOptions
        {
            CliPath = FakeClaude.Path,
            Cwd = _directory.Path,
            EnableFileCheckpointing = true,
            VerbatimPrompts = true,
            Warning = warnings.Add,
            Env = new Dictionary<string, string> { ["FAKE_CLAUDE_MODE"] = "echo" },
        };
        await using var transport = new SubprocessTransport(options);

        await transport.ConnectAsync();
        await transport.EndInputAsync();
        var report = await SingleReportAsync(transport);

        Assert.Equal("true", report.GetProperty("env").GetProperty("CLAUDE_CODE_ENABLE_SDK_FILE_CHECKPOINTING").GetString());
        Assert.Empty(warnings);
    }

    [Fact]
    public async Task Connect_UserIsCurrentUser_RunsCliAsThatUser()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var options = new ClaudeAgentOptions
        {
            CliPath = FakeClaude.Path,
            Cwd = _directory.Path,
            User = Environment.UserName,
            Env = new Dictionary<string, string> { ["FAKE_CLAUDE_MODE"] = "echo" },
        };
        await using var transport = new SubprocessTransport(options);

        await transport.ConnectAsync();
        await transport.EndInputAsync();
        var report = await SingleReportAsync(transport);

        Assert.Equal(Environment.UserName, report.GetProperty("user").GetString());
    }

    [Fact]
    public async Task Connect_UnknownUser_FailsToStart()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var options = new ClaudeAgentOptions { CliPath = FakeClaude.Path, Cwd = _directory.Path, User = "no_such_user_pamis_sdk" };
        await using var transport = new SubprocessTransport(options);

        await Assert.ThrowsAsync<CliConnectionException>(() => transport.ConnectAsync());
        Assert.Null(transport.ProcessId);
    }

    [Fact]
    public async Task Connect_UserOnWindows_ThrowsPlatformNotSupported()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var options = new ClaudeAgentOptions { CliPath = FakeClaude.Path, Cwd = _directory.Path, User = "someone" };
        await using var transport = new SubprocessTransport(options);

        await Assert.ThrowsAsync<PlatformNotSupportedException>(() => transport.ConnectAsync());
        Assert.Null(transport.ProcessId);
    }

    [Fact]
    public void UserMessage_NonAscii_IsEscapedLikePythonEnsureAsciiAndDecodesTheSame()
    {
        // Python 的 json.dumps 預設 ensure_ascii，非 ASCII 寫成 \uXXXX；Utf8JsonWriter 預設也是，CLI 解析出來的字串一樣
        const string content = "你好 🐱 <a&b> 'q'";

        var line = ControlMessages.UserMessage(content, "default");

        Assert.All(line, c => Assert.True(c < 0x80));
        // 🐱 跟 Python 一樣寫成一對 surrogate 的跳脫（大小寫不同不影響解析）
        Assert.Contains(string.Concat("\\", "uD83D", "\\", "uDC31"), line, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(content, JsonDocument.Parse(line).RootElement.GetProperty("message").GetProperty("content").GetString());
    }

    [UnsupportedOSPlatform("windows")]
    private string Script(string name, string content)
    {
        var path = Path.Combine(_directory.Path, name);
        File.WriteAllText(path, content);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private static async Task<JsonElement> SingleReportAsync(SubprocessTransport transport)
    {
        List<JsonElement> messages = [];
        await foreach (var message in transport.ReadMessagesAsync())
        {
            messages.Add(message);
        }

        return Assert.Single(messages);
    }
}
