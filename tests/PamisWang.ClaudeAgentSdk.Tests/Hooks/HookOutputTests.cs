using System.Text.Json;
using System.Text.Json.Nodes;
using PamisWang.ClaudeAgentSdk.Hooks;
using PamisWang.ClaudeAgentSdk.Protocol;

namespace PamisWang.ClaudeAgentSdk.Tests.Hooks;

/// <summary>hook 結果寫出的 JSON：CLI 的欄位名稱、<see langword="null"/> 不送，每種事件專屬的結果各一個。</summary>
public sealed class HookOutputTests
{
    public static TheoryData<HookSpecificOutput, string> SpecificOutputs => new()
    {
        {
            new PreToolUseHookSpecificOutput
            {
                PermissionDecision = "defer",
                PermissionDecisionReason = "later",
                UpdatedInput = Json("""{"a":1}"""),
                AdditionalContext = "ctx",
            },
            """{"hookEventName":"PreToolUse","permissionDecision":"defer","permissionDecisionReason":"later","updatedInput":{"a":1},"additionalContext":"ctx"}"""
        },
        {
            new PostToolUseHookSpecificOutput
            {
                AdditionalContext = "ctx",
                UpdatedToolOutput = Json("""{"stdout":"x","stderr":"","interrupted":false}"""),
                UpdatedMcpToolOutput = Json("\"mcp\""),
            },
            """{"hookEventName":"PostToolUse","additionalContext":"ctx","updatedToolOutput":{"stdout":"x","stderr":"","interrupted":false},"updatedMCPToolOutput":"mcp"}"""
        },
        { new PostToolUseFailureHookSpecificOutput { AdditionalContext = "ctx" }, """{"hookEventName":"PostToolUseFailure","additionalContext":"ctx"}""" },
        {
            new UserPromptSubmitHookSpecificOutput { AdditionalContext = "ctx", SessionTitle = "title", SuppressOriginalPrompt = true },
            """{"hookEventName":"UserPromptSubmit","additionalContext":"ctx","sessionTitle":"title","suppressOriginalPrompt":true}"""
        },
        {
            new UserPromptExpansionHookSpecificOutput { AdditionalContext = "ctx", SuppressOriginalPrompt = false },
            """{"hookEventName":"UserPromptExpansion","additionalContext":"ctx","suppressOriginalPrompt":false}"""
        },
        {
            new SessionStartHookSpecificOutput
            {
                AdditionalContext = "ctx",
                InitialUserMessage = "hello",
                SessionTitle = "title",
                WatchPaths = ["/a", "/b"],
                ReloadSkills = true,
            },
            """{"hookEventName":"SessionStart","additionalContext":"ctx","initialUserMessage":"hello","sessionTitle":"title","watchPaths":["/a","/b"],"reloadSkills":true}"""
        },
        { new NotificationHookSpecificOutput { AdditionalContext = "ctx" }, """{"hookEventName":"Notification","additionalContext":"ctx"}""" },
        { new SubagentStartHookSpecificOutput { AdditionalContext = "ctx" }, """{"hookEventName":"SubagentStart","additionalContext":"ctx"}""" },
        {
            new PermissionRequestHookSpecificOutput(Json("""{"behavior":"allow"}""")),
            """{"hookEventName":"PermissionRequest","decision":{"behavior":"allow"}}"""
        },
        { new PostToolBatchHookSpecificOutput { AdditionalContext = "ctx" }, """{"hookEventName":"PostToolBatch","additionalContext":"ctx"}""" },
        { new StopHookSpecificOutput { AdditionalContext = "ctx" }, """{"hookEventName":"Stop","additionalContext":"ctx"}""" },
        { new SubagentStopHookSpecificOutput { AdditionalContext = "ctx" }, """{"hookEventName":"SubagentStop","additionalContext":"ctx"}""" },
        { new PermissionDeniedHookSpecificOutput { Retry = true }, """{"hookEventName":"PermissionDenied","retry":true}""" },
        {
            new PreModelSwitchHookSpecificOutput { PermissionDecision = "deny", PermissionDecisionReason = "no" },
            """{"hookEventName":"PreModelSwitch","permissionDecision":"deny","permissionDecisionReason":"no"}"""
        },
        { new PostModelSwitchHookSpecificOutput { AdditionalContext = "ctx" }, """{"hookEventName":"PostModelSwitch","additionalContext":"ctx"}""" },
        {
            new ElicitationHookSpecificOutput { Action = "accept", Content = Json("""{"name":"x"}""") },
            """{"hookEventName":"Elicitation","action":"accept","content":{"name":"x"}}"""
        },
        {
            new ElicitationResultHookSpecificOutput { Action = "decline" },
            """{"hookEventName":"ElicitationResult","action":"decline"}"""
        },
        { new CwdChangedHookSpecificOutput { WatchPaths = [] }, """{"hookEventName":"CwdChanged","watchPaths":[]}""" },
        { new FileChangedHookSpecificOutput { WatchPaths = ["/x"] }, """{"hookEventName":"FileChanged","watchPaths":["/x"]}""" },
        { new WorktreeCreateHookSpecificOutput("/wt"), """{"hookEventName":"WorktreeCreate","worktreePath":"/wt"}""" },
        { new MessageDisplayHookSpecificOutput { DisplayContent = "shown" }, """{"hookEventName":"MessageDisplay","displayContent":"shown"}""" },
    };

    [Theory]
    [MemberData(nameof(SpecificOutputs))]
    public void HookSpecificOutput_WritesCliFieldNames(HookSpecificOutput specific, string expected)
    {
        AssertJson($$"""{"hookSpecificOutput":{{expected}}}""", new SyncHookOutput { HookSpecificOutput = specific });
    }

    [Fact]
    public void HookSpecificOutput_EveryPythonTypeHasACounterpart()
    {
        // Python 的 HookSpecificOutput 聯集有 21 種
        var names = SpecificOutputs.Select(row => ((HookSpecificOutput)row[0]).HookEventName).ToHashSet();

        Assert.Equal(21, names.Count);
    }

    [Fact]
    public void HookSpecificOutput_OnlyEventName_WhenNothingSet()
    {
        AssertJson(
            """{"hookSpecificOutput":{"hookEventName":"PreToolUse"}}""",
            new SyncHookOutput { HookSpecificOutput = new PreToolUseHookSpecificOutput() });
    }

    [Fact]
    public void SyncHookOutput_WritesEveryCommonField()
    {
        AssertJson(
            """{"continue":false,"suppressOutput":true,"stopReason":"s","decision":"block","systemMessage":"m","reason":"r"}""",
            new SyncHookOutput
            {
                Continue = false,
                SuppressOutput = true,
                StopReason = "s",
                Decision = "block",
                SystemMessage = "m",
                Reason = "r",
            });
    }

    [Fact]
    public void Empty_WritesEmptyObject()
    {
        AssertJson("{}", HookOutput.Empty);
    }

    [Fact]
    public void AsyncHookOutput_WithoutTimeout_WritesOnlyAsync()
    {
        AssertJson("""{"async":true}""", new AsyncHookOutput());
        AssertJson("""{"async":true,"asyncTimeout":100}""", new AsyncHookOutput(100));
    }

    [Fact]
    public void RawHookOutput_PassesThroughAndRenamesPythonSafeKeys()
    {
        var raw = new JsonObject
        {
            ["continue_"] = false,
            ["async_"] = true,
            ["custom"] = new JsonObject { ["continue_"] = 1 },
            ["nothing"] = null,
        };

        AssertJson("""{"continue":false,"async":true,"custom":{"continue_":1},"nothing":null}""", new RawHookOutput(raw));
    }

    private static JsonElement Json(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    private static void AssertJson(string expected, HookOutput output)
    {
        var actual = JsonWriting.ToCompactString(writer =>
        {
            writer.WriteStartObject();
            output.WriteFields(writer);
            writer.WriteEndObject();
        });

        Assert.Equal(expected, actual);
    }
}
