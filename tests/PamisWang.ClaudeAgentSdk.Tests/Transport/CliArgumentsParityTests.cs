using System.Text.Json;
using PamisWang.ClaudeAgentSdk.Mcp;
using PamisWang.ClaudeAgentSdk.Permissions;
using PamisWang.ClaudeAgentSdk.Transport;

namespace PamisWang.ClaudeAgentSdk.Tests.Transport;

/// <summary>跟 Python SDK 的 <c>_build_command</c> 對照：每個選項的旗標、條件、值的寫法與順序。</summary>
public sealed class CliArgumentsParityTests : IDisposable
{
    private static readonly string[] Head = ["--output-format", "stream-json", "--verbose", "--system-prompt", ""];
    private static readonly string[] Tail = ["--input-format", "stream-json"];

    private readonly TempDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    [Fact]
    public void Build_AllOptions_FollowPythonSdkOrder()
    {
        var options = new ClaudeAgentOptions
        {
            Tools = new ToolsList(["Read", "Bash"]),
            AllowedTools = ["Read", "Write"],
            MaxTurns = 3,
            MaxBudgetUsd = 1.50m,
            DisallowedTools = ["Bash"],
            Model = "haiku",
            FallbackModel = "sonnet",
            Betas = ["context-1m-2025-08-07"],
            PermissionPromptToolName = "mcp__perm__ask",
            PermissionMode = "acceptEdits",
            ContinueConversation = true,
            Resume = "abc",
            SessionId = "11111111-2222-3333-4444-555555555555",
            Sandbox = new SandboxSettings { Enabled = true },
            AddDirs = ["/a", "/b"],
            McpServers = new Dictionary<string, McpServerConfig>
            {
                ["fs"] = new McpStdioServerConfig("npx") { Args = ["-y", "server-fs"] },
            },
            IncludePartialMessages = true,
            StrictMcpConfig = true,
            ForkSession = true,
            SettingSources = ["user", "project"],
            Plugins = [new SdkPluginConfig("/plugins/p")],
            ExtraArgs = new Dictionary<string, string?> { ["debug"] = null },
            Thinking = new ThinkingConfigAdaptive(Display: "summarized"),
            Effort = "high",
            OutputFormat = new OutputFormatJsonSchema(Json("""{"type":"object"}""")),
        };

        var args = CliArguments.Build(options, isWindows: false);

        Assert.Equal(
            [
                .. Head,
                "--tools", "Read,Bash",
                "--allowedTools", "Read,Write",
                "--max-turns", "3",
                "--max-budget-usd", "1.50",
                "--disallowedTools", "Bash",
                "--model", "haiku",
                "--fallback-model", "sonnet",
                "--betas", "context-1m-2025-08-07",
                "--permission-prompt-tool", "mcp__perm__ask",
                "--permission-mode", "acceptEdits",
                "--continue",
                "--resume=abc",
                "--session-id=11111111-2222-3333-4444-555555555555",
                "--settings", """{"sandbox":{"enabled":true}}""",
                "--add-dir", "/a",
                "--add-dir", "/b",
                "--mcp-config", """{"mcpServers":{"fs":{"type":"stdio","command":"npx","args":["-y","server-fs"]}}}""",
                "--include-partial-messages",
                "--strict-mcp-config",
                "--fork-session",
                "--setting-sources=user,project",
                "--plugin-dir", "/plugins/p",
                "--debug",
                "--thinking", "adaptive",
                "--thinking-display", "summarized",
                "--effort", "high",
                "--json-schema", """{"type":"object"}""",
                .. Tail,
            ],
            args);
    }

    [Fact]
    public void Build_EmptyToolsList_PassesEmptyValue()
    {
        var args = Build(new ClaudeAgentOptions { Tools = new ToolsList([]) });

        Assert.Equal([.. Head, "--tools", "", .. Tail], args);
    }

    [Fact]
    public void Build_ToolsPreset_MapsToDefault()
    {
        var args = Build(new ClaudeAgentOptions { Tools = new ToolsPreset() });

        Assert.Equal([.. Head, "--tools", "default", .. Tail], args);
    }

    [Fact]
    public void Build_FalsyValues_AreOmittedLikePython()
    {
        var options = new ClaudeAgentOptions
        {
            MaxTurns = 0,
            FallbackModel = "",
            PermissionPromptToolName = "",
            PermissionMode = "",
            SessionId = "",
            Settings = "",
            McpConfig = "",
        };

        Assert.Equal([.. Head, .. Tail], Build(options));
    }

    [Theory]
    [InlineData(0, "0")]
    [InlineData(2.5, "2.5")]
    [InlineData(10, "10")]
    public void Build_MaxBudgetUsd_IsSentWhenNotNullUsingInvariantCulture(double budget, string expected)
    {
        var culture = Thread.CurrentThread.CurrentCulture;
        try
        {
            // 小數點是逗號的語系也要送出 2.5
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            var args = Build(new ClaudeAgentOptions { MaxBudgetUsd = (decimal)budget });

            Assert.Equal([.. Head, "--max-budget-usd", expected, .. Tail], args);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = culture;
        }
    }

    [Fact]
    public void Build_CanUseTool_SendsStdioBetweenBetasAndPermissionMode()
    {
        var options = new ClaudeAgentOptions
        {
            Betas = ["b"],
            PermissionMode = "plan",
            CanUseTool = (_, _) => Task.FromResult<PermissionResult>(new PermissionResultAllow()),
        };

        Assert.Equal(
            [.. Head, "--betas", "b", "--permission-prompt-tool", "stdio", "--permission-mode", "plan", .. Tail],
            Build(options));
    }

    [Fact]
    public void Build_CanUseToolWithPermissionPromptToolName_Throws()
    {
        var options = new ClaudeAgentOptions
        {
            PermissionPromptToolName = "mcp__perm__ask",
            CanUseTool = (_, _) => Task.FromResult<PermissionResult>(new PermissionResultAllow()),
        };

        var error = Assert.Throws<ArgumentException>(() => Build(options));
        Assert.Equal(nameof(ClaudeAgentOptions.PermissionPromptToolName), error.ParamName);
    }

    [Fact]
    public void Build_SessionIdStartingWithDash_StaysOneArgument()
    {
        var args = Build(new ClaudeAgentOptions { SessionId = "--model" });

        Assert.Contains("--session-id=--model", args);
        Assert.DoesNotContain("--model", args);
    }

    [Theory]
    [InlineData("a&b")]
    [InlineData("a|b")]
    [InlineData("%PATH%")]
    [InlineData("a\"b")]
    [InlineData("a\r\nb")]
    public void Build_OnWindows_RejectsCmdMetacharactersInSessionId(string sessionId)
    {
        var options = new ClaudeAgentOptions { SessionId = sessionId };

        var error = Assert.Throws<ArgumentException>(() => CliArguments.Build(options, isWindows: true));
        Assert.Equal(nameof(ClaudeAgentOptions.SessionId), error.ParamName);
        Assert.Contains($"--session-id={sessionId}", CliArguments.Build(options, isWindows: false));
    }

    [Fact]
    public void Build_SettingsWithoutSandbox_PassesThroughAsIs()
    {
        Assert.Equal([.. Head, "--settings", "/path/settings.json", .. Tail], Build(new ClaudeAgentOptions { Settings = "/path/settings.json" }));
        Assert.Equal([.. Head, "--settings", """{ "a": 1 }""", .. Tail], Build(new ClaudeAgentOptions { Settings = """{ "a": 1 }""" }));
    }

    [Fact]
    public void Build_JsonSettingsWithSandbox_MergesSandboxIntoSettings()
    {
        var options = new ClaudeAgentOptions
        {
            Settings = """  {"permissions":{"allow":["Read"]},"sandbox":{"enabled":false},"model":"haiku"}  """,
            Sandbox = new SandboxSettings
            {
                Enabled = true,
                AutoAllowBashIfSandboxed = false,
                ExcludedCommands = ["docker"],
                AllowUnsandboxedCommands = false,
                Network = new SandboxNetworkConfig
                {
                    AllowedDomains = ["example.com"],
                    DeniedDomains = ["evil.example"],
                    AllowManagedDomainsOnly = true,
                    AllowUnixSockets = ["/var/run/docker.sock"],
                    AllowAllUnixSockets = false,
                    AllowLocalBinding = true,
                    AllowMachLookup = ["com.apple.*"],
                    HttpProxyPort = 8080,
                    SocksProxyPort = 1080,
                },
                IgnoreViolations = new Dictionary<string, IReadOnlyList<string>> { ["*"] = ["/etc/hosts"] },
                EnableWeakerNestedSandbox = true,
            },
        };

        var settings = SettingsValue(options);

        // 原本的 sandbox 被換掉，位置不變，跟 Python 的 dict 指派一樣
        Assert.Equal(
            """{"permissions":{"allow":["Read"]},"sandbox":{"enabled":true,"autoAllowBashIfSandboxed":false,"excludedCommands":["docker"]"""
            + ""","allowUnsandboxedCommands":false,"network":{"allowedDomains":["example.com"],"deniedDomains":["evil.example"]"""
            + ""","allowManagedDomainsOnly":true,"allowUnixSockets":["/var/run/docker.sock"],"allowAllUnixSockets":false,"allowLocalBinding":true"""
            + ""","allowMachLookup":["com.apple.*"],"httpProxyPort":8080,"socksProxyPort":1080},"ignoreViolations":{"*":["/etc/hosts"]}"""
            + ""","enableWeakerNestedSandbox":true},"model":"haiku"}""",
            settings);
    }

    [Fact]
    public void Build_SettingsFileWithSandbox_ReadsFileAndMerges()
    {
        var path = Path.Combine(_directory.Path, "settings.json");
        File.WriteAllText(path, """{"model": "haiku"}""");

        var settings = SettingsValue(new ClaudeAgentOptions { Settings = path, Sandbox = new SandboxSettings { Enabled = true } });

        Assert.Equal("""{"model":"haiku","sandbox":{"enabled":true}}""", settings);
    }

    [Fact]
    public void Build_MissingSettingsFileWithSandbox_SendsOnlySandbox()
    {
        var settings = SettingsValue(new ClaudeAgentOptions
        {
            Settings = Path.Combine(_directory.Path, "missing.json"),
            Sandbox = new SandboxSettings(),
        });

        Assert.Equal("""{"sandbox":{}}""", settings);
    }

    [Fact]
    public void Build_InvalidJsonSettingsWithSandbox_FallsBackToFilePath()
    {
        var settings = SettingsValue(new ClaudeAgentOptions { Settings = "{not json}", Sandbox = new SandboxSettings { Enabled = false } });

        Assert.Equal("""{"sandbox":{"enabled":false}}""", settings);
    }

    [Fact]
    public void Build_McpServers_SerializesExternalConfigsWithWireNames()
    {
        var options = new ClaudeAgentOptions
        {
            McpServers = new Dictionary<string, McpServerConfig>
            {
                ["local"] = new McpStdioServerConfig("node")
                {
                    Args = ["server.js"],
                    Env = new Dictionary<string, string> { ["TOKEN_FILE"] = "/tmp/t" },
                },
                ["events"] = new McpSseServerConfig("https://example.com/sse"),
                ["api"] = new McpHttpServerConfig("https://example.com/mcp")
                {
                    Headers = new Dictionary<string, string> { ["X-Team"] = "core" },
                },
            },
        };

        var args = Build(options);

        Assert.Equal(
            [
                .. Head,
                "--mcp-config",
                """{"mcpServers":{"local":{"type":"stdio","command":"node","args":["server.js"],"env":{"TOKEN_FILE":"/tmp/t"}}"""
                + ""","events":{"type":"sse","url":"https://example.com/sse"},"api":{"type":"http","url":"https://example.com/mcp","headers":{"X-Team":"core"}}}}""",
                .. Tail,
            ],
            args);
    }

    [Fact]
    public void Build_McpConfigString_PassesThrough()
    {
        Assert.Equal([.. Head, "--mcp-config", "/path/mcp.json", .. Tail], Build(new ClaudeAgentOptions { McpConfig = "/path/mcp.json" }));
    }

    [Fact]
    public void Build_McpServersAndMcpConfig_Throws()
    {
        var options = new ClaudeAgentOptions
        {
            McpConfig = "/path/mcp.json",
            McpServers = new Dictionary<string, McpServerConfig> { ["a"] = new McpHttpServerConfig("https://example.com") },
        };

        Assert.Throws<ArgumentException>(() => Build(options));
    }

    [Fact]
    public void Build_EmptySettingSources_PassesEmptyEqualsValue()
    {
        Assert.Equal([.. Head, "--setting-sources=", .. Tail], Build(new ClaudeAgentOptions { SettingSources = [] }));
    }

    [Fact]
    public void Build_ExtraArgs_HandlesFlagDashValueAndPlainValue()
    {
        var options = new ClaudeAgentOptions
        {
            Plugins = [new SdkPluginConfig("/p")],
            ExtraArgs = new Dictionary<string, string?>
            {
                ["replay-user-messages"] = null,
                ["agent"] = "-dangerous",
                ["debug-to-stderr"] = "api",
            },
            MaxThinkingTokens = 100,
        };

        Assert.Equal(
            [
                .. Head,
                "--plugin-dir", "/p",
                "--replay-user-messages",
                "--agent=-dangerous",
                "--debug-to-stderr", "api",
                "--max-thinking-tokens", "100",
                .. Tail,
            ],
            Build(options));
    }

    public static TheoryData<ThinkingConfig?, int?, string[]> ThinkingCases => new()
    {
        { new ThinkingConfigAdaptive(), null, ["--thinking", "adaptive"] },
        { new ThinkingConfigAdaptive("omitted"), 500, ["--thinking", "adaptive", "--thinking-display", "omitted"] },
        { new ThinkingConfigEnabled(2048), null, ["--max-thinking-tokens", "2048"] },
        { new ThinkingConfigEnabled(2048, "summarized"), 500, ["--max-thinking-tokens", "2048", "--thinking-display", "summarized"] },
        { new ThinkingConfigDisabled(), 500, ["--thinking", "disabled"] },
        { null, 500, ["--max-thinking-tokens", "500"] },
        { null, 0, ["--max-thinking-tokens", "0"] },
        { null, null, [] },
    };

    [Theory]
    [MemberData(nameof(ThinkingCases))]
    public void Build_Thinking_TakesPrecedenceOverMaxThinkingTokens(ThinkingConfig? thinking, int? maxThinkingTokens, string[] expected)
    {
        var args = Build(new ClaudeAgentOptions { Thinking = thinking, MaxThinkingTokens = maxThinkingTokens, Effort = "low" });

        Assert.Equal([.. Head, .. expected, "--effort", "low", .. Tail], args);
    }

    [Fact]
    public void Build_JsonSchema_IsSentCompact()
    {
        var schema = Json("""
            {
              "type": "object",
              "properties": { "answer": { "type": "string" } },
              "required": ["answer"]
            }
            """);

        var args = Build(new ClaudeAgentOptions { OutputFormat = new OutputFormatJsonSchema(schema) });

        Assert.Equal(
            [.. Head, "--json-schema", """{"type":"object","properties":{"answer":{"type":"string"}},"required":["answer"]}""", .. Tail],
            args);
    }

    [Fact]
    public void Build_NullJsonSchema_IsOmitted()
    {
        Assert.Equal([.. Head, .. Tail], Build(new ClaudeAgentOptions { OutputFormat = new OutputFormatJsonSchema(Json("null")) }));
        Assert.Equal([.. Head, .. Tail], Build(new ClaudeAgentOptions { OutputFormat = new OutputFormatJsonSchema(default) }));
    }

    private static IReadOnlyList<string> Build(ClaudeAgentOptions options) => CliArguments.Build(options, isWindows: false);

    private static string SettingsValue(ClaudeAgentOptions options)
    {
        var args = Build(options);
        return args[args.ToList().IndexOf("--settings") + 1];
    }

    private static JsonElement Json(string json) => JsonSerializer.Deserialize<JsonElement>(json);
}
