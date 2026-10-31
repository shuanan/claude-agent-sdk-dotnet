using PamisWang.ClaudeAgentSdk.Permissions;
using PamisWang.ClaudeAgentSdk.Transport;

namespace PamisWang.ClaudeAgentSdk.Tests.Transport;

public sealed class CliArgumentsTests
{
    [Fact]
    public void Build_Defaults_MatchesPythonSdkBaseCommand()
    {
        var args = CliArguments.Build(new ClaudeAgentOptions(), isWindows: false);

        Assert.Equal(
            ["--output-format", "stream-json", "--verbose", "--system-prompt", "", "--input-format", "stream-json"],
            args);
    }

    [Fact]
    public void Build_ChatOptions_AddsFlagsInPythonSdkOrder()
    {
        var options = new ClaudeAgentOptions
        {
            Model = "haiku",
            Resume = "34953276-f9da-4b8c-a789-77d8ee05bc37",
            IncludePartialMessages = true,
            DisallowedTools = ["AskUserQuestion", "Bash"],
        };

        var args = CliArguments.Build(options, isWindows: true);

        Assert.Equal(
            [
                "--output-format", "stream-json", "--verbose", "--system-prompt", "",
                "--disallowedTools", "AskUserQuestion,Bash",
                "--model", "haiku",
                "--resume=34953276-f9da-4b8c-a789-77d8ee05bc37",
                "--include-partial-messages",
                "--input-format", "stream-json",
            ],
            args);
    }

    [Fact]
    public void Build_WithCanUseTool_RoutesPermissionPromptsOverStdio()
    {
        var options = new ClaudeAgentOptions
        {
            Model = "haiku",
            Resume = "abc",
            CanUseTool = (_, _) => Task.FromResult<PermissionResult>(new PermissionResultAllow()),
        };

        var args = CliArguments.Build(options, isWindows: false);

        // 跟 Python SDK 一樣排在 --model 之後、--resume 之前
        Assert.Equal(
            [
                "--output-format", "stream-json", "--verbose", "--system-prompt", "",
                "--model", "haiku",
                "--permission-prompt-tool", "stdio",
                "--resume=abc",
                "--input-format", "stream-json",
            ],
            args);
    }

    [Fact]
    public void Build_SystemPromptPreset_LeavesClaudeCodeDefault()
    {
        var args = CliArguments.Build(new ClaudeAgentOptions { SystemPrompt = new SystemPromptPreset() }, isWindows: false);

        Assert.Equal(["--output-format", "stream-json", "--verbose", "--input-format", "stream-json"], args);
    }

    [Fact]
    public void Build_SystemPromptPresetWithAppend_AppendsToDefault()
    {
        var options = new ClaudeAgentOptions { SystemPrompt = new SystemPromptPreset(Append: "回答一律用繁體中文") };

        var args = CliArguments.Build(options, isWindows: false);

        Assert.Equal(
            ["--output-format", "stream-json", "--verbose", "--append-system-prompt", "回答一律用繁體中文", "--input-format", "stream-json"],
            args);
    }

    [Fact]
    public void Build_SystemPromptCustom_ReplacesDefaultAsOneArgument()
    {
        // 換行、引號、- 開頭都原樣放在一個參數裡；不經過 cmd.exe，Windows 上也不擋
        const string prompt = "- 你是紫苑\n- 說話\"溫柔\" & 簡短";
        var options = new ClaudeAgentOptions { SystemPrompt = new SystemPromptCustom(prompt), Model = "haiku" };

        var args = CliArguments.Build(options, isWindows: true);

        Assert.Equal(
            [
                "--output-format", "stream-json", "--verbose", "--system-prompt", prompt,
                "--model", "haiku",
                "--input-format", "stream-json",
            ],
            args);
    }

    [Fact]
    public void Build_SystemPromptFile_PassesPath()
    {
        var options = new ClaudeAgentOptions { SystemPrompt = new SystemPromptFile(@"D:\prompts\紫苑.md") };

        var args = CliArguments.Build(options, isWindows: true);

        Assert.Equal(
            ["--output-format", "stream-json", "--verbose", "--system-prompt-file", @"D:\prompts\紫苑.md", "--input-format", "stream-json"],
            args);
    }

    [Fact]
    public void Build_Effort_ComesAfterPartialMessagesLikePythonSdk()
    {
        var options = new ClaudeAgentOptions { Model = "haiku", Effort = "low", IncludePartialMessages = true };

        var args = CliArguments.Build(options, isWindows: false);

        Assert.Equal(
            [
                "--output-format", "stream-json", "--verbose", "--system-prompt", "",
                "--model", "haiku",
                "--include-partial-messages",
                "--effort", "low",
                "--input-format", "stream-json",
            ],
            args);
    }

    [Fact]
    public void Build_ResumeStartingWithDash_StaysOneArgument()
    {
        // 分成兩個參數的話，CLI 會把 --model 當成另一個旗標
        var args = CliArguments.Build(new ClaudeAgentOptions { Resume = "--model" }, isWindows: false);

        Assert.Contains("--resume=--model", args);
        Assert.DoesNotContain("--model", args);
    }

    [Theory]
    [InlineData("a&b")]
    [InlineData("a|b")]
    [InlineData("a>b")]
    [InlineData("a^b")]
    [InlineData("%PATH%")]
    [InlineData("a!b")]
    [InlineData("a\"b")]
    [InlineData("a\nb")]
    public void Build_OnWindows_RejectsCmdMetacharactersInResume(string resume)
    {
        var options = new ClaudeAgentOptions { Resume = resume };

        Assert.Throws<ArgumentException>(() => CliArguments.Build(options, isWindows: true));
    }

    [Fact]
    public void Build_OnWindows_AcceptsOrdinarySessionTitle()
    {
        var args = CliArguments.Build(new ClaudeAgentOptions { Resume = "重構 server (v2)" }, isWindows: true);

        Assert.Contains("--resume=重構 server (v2)", args);
    }

    [Fact]
    public void Build_OffWindows_AllowsAnyResumeValue()
    {
        var args = CliArguments.Build(new ClaudeAgentOptions { Resume = "a&b" }, isWindows: false);

        Assert.Contains("--resume=a&b", args);
    }

    [Fact]
    public void Build_EmptyResumeAndModel_AreOmitted()
    {
        var args = CliArguments.Build(new ClaudeAgentOptions { Resume = "", Model = "" }, isWindows: false);

        Assert.DoesNotContain(args, arg => arg.StartsWith("--resume", StringComparison.Ordinal));
        Assert.DoesNotContain("--model", args);
    }
}
