using System.Text.Json;
using PamisWang.ClaudeAgentSdk.Hooks;

namespace PamisWang.ClaudeAgentSdk.Tests.Hooks;

/// <summary>hook 輸入依 <c>hook_event_name</c> 轉成對應的型別，欄位名稱跟 Python 的 <c>*HookInput</c> 一樣。</summary>
public sealed class HookInputTests
{
    // 各事件共用的欄位，接在物件的開頭
    private const string Base =
        """ "session_id":"s1","transcript_path":"/t.jsonl","cwd":"/w","permission_mode":"default","prompt_id":"p-1","effort":{"level":"high"}""";

    [Fact]
    public void Base_ReadsCommonFields()
    {
        var input = Parse($$"""{{{Base}},"hook_event_name":"UserPromptSubmit","prompt":"hi"}""");

        Assert.Equal("UserPromptSubmit", input.HookEventName);
        Assert.Equal("s1", input.SessionId);
        Assert.Equal("/t.jsonl", input.TranscriptPath);
        Assert.Equal("/w", input.Cwd);
        Assert.Equal("default", input.PermissionMode);
        Assert.Equal("p-1", input.PromptId);
        Assert.Equal("high", input.Effort!.Level);
        Assert.Equal("hi", Assert.IsType<UserPromptSubmitHookInput>(input).Prompt);
    }

    [Fact]
    public void Base_MissingFields_AreEmptyOrNull()
    {
        var input = Parse("""{"hook_event_name":"Stop"}""");

        Assert.Equal("", input.SessionId);
        Assert.Equal("", input.TranscriptPath);
        Assert.Equal("", input.Cwd);
        Assert.Null(input.PermissionMode);
        Assert.Null(input.PromptId);
        Assert.Null(input.Effort);
        var stop = Assert.IsType<StopHookInput>(input);
        Assert.False(stop.StopHookActive);
        Assert.Null(stop.BackgroundTasks);
        Assert.Null(stop.SessionCrons);
    }

    [Fact]
    public void PreToolUse()
    {
        var input = Assert.IsType<PreToolUseHookInput>(Parse(
            $$"""{{{Base}},"hook_event_name":"PreToolUse","tool_name":"Bash","tool_input":{"command":"ls"},"tool_use_id":"tu","agent_id":"a1","agent_type":"general-purpose"}"""));

        Assert.Equal("Bash", input.ToolName);
        Assert.Equal("ls", input.ToolInput.GetProperty("command").GetString());
        Assert.Equal("tu", input.ToolUseId);
        Assert.Equal("a1", input.AgentId);
        Assert.Equal("general-purpose", input.AgentType);
    }

    [Fact]
    public void PreToolUse_MainThread_HasNoAgent()
    {
        var input = Assert.IsType<PreToolUseHookInput>(Parse("""{"hook_event_name":"PreToolUse","tool_name":"Read"}"""));

        Assert.Null(input.AgentId);
        Assert.Null(input.AgentType);
        Assert.Equal(JsonValueKind.Object, input.ToolInput.ValueKind);
        Assert.Equal("", input.ToolUseId);
    }

    [Fact]
    public void PostToolUse()
    {
        var input = Assert.IsType<PostToolUseHookInput>(Parse(
            """{"hook_event_name":"PostToolUse","tool_name":"Read","tool_input":{"file_path":"/a"},"tool_response":{"lines":3},"tool_use_id":"tu","duration_ms":42,"agent_id":"a1"}"""));

        Assert.Equal("Read", input.ToolName);
        Assert.Equal("/a", input.ToolInput.GetProperty("file_path").GetString());
        Assert.Equal(3, input.ToolResponse.GetProperty("lines").GetInt32());
        Assert.Equal("tu", input.ToolUseId);
        Assert.Equal(42, input.DurationMs);
        Assert.Equal("a1", input.AgentId);
        Assert.Null(input.AgentType);
    }

    [Fact]
    public void PostToolUse_WithoutResponse_IsUndefined()
    {
        var input = Assert.IsType<PostToolUseHookInput>(Parse("""{"hook_event_name":"PostToolUse"}"""));

        Assert.Equal(JsonValueKind.Undefined, input.ToolResponse.ValueKind);
        Assert.Null(input.DurationMs);
    }

    [Fact]
    public void PostToolUseFailure()
    {
        var input = Assert.IsType<PostToolUseFailureHookInput>(Parse(
            """{"hook_event_name":"PostToolUseFailure","tool_name":"Bash","tool_input":{},"tool_use_id":"tu","error":"exit 1","is_interrupt":true,"duration_ms":7,"agent_type":"x"}"""));

        Assert.Equal("Bash", input.ToolName);
        Assert.Equal("tu", input.ToolUseId);
        Assert.Equal("exit 1", input.Error);
        Assert.True(input.IsInterrupt);
        Assert.Equal(7, input.DurationMs);
        Assert.Equal("x", input.AgentType);
    }

    [Fact]
    public void Stop_WithBackgroundTasksAndCrons()
    {
        var input = Assert.IsType<StopHookInput>(Parse(
            """
            {"hook_event_name":"Stop","stop_hook_active":true,"last_assistant_message":"done",
             "background_tasks":[
               {"id":"b1","type":"shell","status":"running","description":"build","command":"make"},
               {"id":"b2","type":"subagent","status":"running","description":"d","agent_type":"reviewer"},
               {"id":"b3","type":"monitor","status":"running","description":"d","server":"srv","tool":"t"},
               {"id":"b4","type":"workflow","status":"running","description":"d","name":"wf"}],
             "session_crons":[{"id":"c1","schedule":"0 9 * * 1-5","recurring":true,"prompt":"standup"}]}
            """));

        Assert.True(input.StopHookActive);
        Assert.Equal("done", input.LastAssistantMessage);
        Assert.Collection(
            input.BackgroundTasks!,
            task =>
            {
                Assert.Equal("b1", task.Id);
                Assert.Equal("shell", task.Type);
                Assert.Equal("running", task.Status);
                Assert.Equal("build", task.Description);
                Assert.Equal("make", task.Command);
                Assert.Null(task.AgentType);
            },
            task => Assert.Equal("reviewer", task.AgentType),
            task =>
            {
                Assert.Equal("srv", task.Server);
                Assert.Equal("t", task.Tool);
            },
            task => Assert.Equal("wf", task.Name));
        var cron = Assert.Single(input.SessionCrons!);
        Assert.Equal("c1", cron.Id);
        Assert.Equal("0 9 * * 1-5", cron.Schedule);
        Assert.True(cron.Recurring);
        Assert.Equal("standup", cron.Prompt);
    }

    [Fact]
    public void SubagentStop()
    {
        var input = Assert.IsType<SubagentStopHookInput>(Parse(
            """{"hook_event_name":"SubagentStop","stop_hook_active":false,"agent_id":"a1","agent_transcript_path":"/a.jsonl","agent_type":"reviewer","last_assistant_message":"ok","background_tasks":[],"session_crons":[]}"""));

        Assert.False(input.StopHookActive);
        Assert.Equal("a1", input.AgentId);
        Assert.Equal("/a.jsonl", input.AgentTranscriptPath);
        Assert.Equal("reviewer", input.AgentType);
        Assert.Equal("ok", input.LastAssistantMessage);
        Assert.Empty(input.BackgroundTasks!);
        Assert.Empty(input.SessionCrons!);
    }

    [Fact]
    public void PreCompact()
    {
        var input = Assert.IsType<PreCompactHookInput>(Parse(
            """{"hook_event_name":"PreCompact","trigger":"manual","custom_instructions":null}"""));

        Assert.Equal("manual", input.Trigger);
        Assert.Null(input.CustomInstructions);
        Assert.Equal("keep todos", Assert.IsType<PreCompactHookInput>(Parse(
            """{"hook_event_name":"PreCompact","trigger":"auto","custom_instructions":"keep todos"}""")).CustomInstructions);
    }

    [Fact]
    public void Notification()
    {
        var input = Assert.IsType<NotificationHookInput>(Parse(
            """{"hook_event_name":"Notification","message":"needs input","title":"Claude","notification_type":"idle"}"""));

        Assert.Equal("needs input", input.Message);
        Assert.Equal("Claude", input.Title);
        Assert.Equal("idle", input.NotificationType);
    }

    [Fact]
    public void SubagentStart()
    {
        var input = Assert.IsType<SubagentStartHookInput>(Parse(
            """{"hook_event_name":"SubagentStart","agent_id":"a1","agent_type":"reviewer"}"""));

        Assert.Equal("a1", input.AgentId);
        Assert.Equal("reviewer", input.AgentType);
    }

    [Fact]
    public void PermissionRequest()
    {
        var input = Assert.IsType<PermissionRequestHookInput>(Parse(
            """{"hook_event_name":"PermissionRequest","tool_name":"Write","tool_input":{"file_path":"/x"},"permission_suggestions":[{"type":"setMode","mode":"acceptEdits","destination":"session"}],"agent_id":"a1","agent_type":"t"}"""));

        Assert.Equal("Write", input.ToolName);
        Assert.Equal("/x", input.ToolInput.GetProperty("file_path").GetString());
        Assert.Equal("setMode", Assert.Single(input.PermissionSuggestions!).GetProperty("type").GetString());
        Assert.Equal("a1", input.AgentId);
        Assert.Equal("t", input.AgentType);
    }

    [Fact]
    public void UnknownEvent_KeepsRaw()
    {
        var input = Assert.IsType<UnknownHookInput>(Parse("""{"hook_event_name":"SessionStart","session_id":"s","source":"startup"}"""));

        Assert.Equal("SessionStart", input.HookEventName);
        Assert.Equal("s", input.SessionId);
        Assert.Equal("startup", input.Raw.GetProperty("source").GetString());
    }

    [Fact]
    public void NotAnObject_IsUnknownWithRawValue()
    {
        var input = Assert.IsType<UnknownHookInput>(HookInput.Parse(default));

        Assert.Equal("", input.HookEventName);
        Assert.Equal(JsonValueKind.Undefined, input.Raw.ValueKind);
        Assert.IsType<UnknownHookInput>(Parse("null"));
    }

    private static HookInput Parse(string json) => HookInput.Parse(JsonSerializer.Deserialize<JsonElement>(json));
}
