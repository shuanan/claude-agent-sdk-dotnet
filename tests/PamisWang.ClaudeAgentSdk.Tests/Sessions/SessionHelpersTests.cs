using System.Text.Json.Nodes;
using PamisWang.ClaudeAgentSdk.Sessions;
using static PamisWang.ClaudeAgentSdk.Tests.Sessions.SessionTestKit;

namespace PamisWang.ClaudeAgentSdk.Tests.Sessions;

/// <summary>移植 tests/test_sessions.py 的 <c>TestHelpers</c>（內部的小工具）。</summary>
public sealed class SessionHelpersTests
{
    // Python: test_sessions.py::TestHelpers::test_validate_uuid_valid
    [Fact]
    public void ValidateUuidValid()
    {
        Assert.NotNull(SessionPaths.ValidateUuid("550e8400-e29b-41d4-a716-446655440000"));
        Assert.NotNull(SessionPaths.ValidateUuid("550E8400-E29B-41D4-A716-446655440000"));
    }

    // Python: test_sessions.py::TestHelpers::test_validate_uuid_invalid
    [Fact]
    public void ValidateUuidInvalid()
    {
        Assert.Null(SessionPaths.ValidateUuid("not-a-uuid"));
        Assert.Null(SessionPaths.ValidateUuid(""));
        Assert.Null(SessionPaths.ValidateUuid("550e8400-e29b-41d4-a716"));
    }

    // Python: test_sessions.py::TestHelpers::test_sanitize_path_basic
    [Fact]
    public void SanitizePathBasic()
    {
        Assert.Equal("-Users-foo-my-project", SessionPaths.SanitizePath("/Users/foo/my-project"));
        Assert.Equal("plugin-name-server", SessionPaths.SanitizePath("plugin:name:server"));
    }

    // Python: test_sessions.py::TestHelpers::test_sanitize_path_long
    [Fact]
    public void SanitizePathLong()
    {
        var longPath = string.Concat(Enumerable.Repeat("/x", 150)); // 300 字元
        var result = SessionPaths.SanitizePath(longPath);
        Assert.True(result.Length > 200); // 截斷 + 雜湊
        Assert.StartsWith("-x-x", result, StringComparison.Ordinal);
        // 雜湊後綴接在 200 字元的前綴後面
        Assert.Contains('-', result[200..]);
    }

    // Python: test_sessions.py::TestHelpers::test_simple_hash_deterministic
    [Fact]
    public void SimpleHashDeterministic()
    {
        Assert.Equal(SessionPaths.SimpleHash("hello"), SessionPaths.SimpleHash("hello"));
        Assert.NotEqual(SessionPaths.SimpleHash("hello"), SessionPaths.SimpleHash("world"));
    }

    // Python: test_sessions.py::TestHelpers::test_simple_hash_zero
    [Fact]
    public void SimpleHashZero()
    {
        // 空字串是 "0"
        Assert.Equal("0", SessionPaths.SimpleHash(""));
    }

    // Python: test_sessions.py::TestHelpers::test_extract_json_string_field_simple
    [Fact]
    public void ExtractJsonStringFieldSimple()
    {
        const string Text = """{"foo":"bar","baz":"qux"}""";
        Assert.Equal("bar", SessionLite.ExtractJsonStringField(Text, "foo"));
        Assert.Equal("qux", SessionLite.ExtractJsonStringField(Text, "baz"));
        Assert.Null(SessionLite.ExtractJsonStringField(Text, "missing"));
    }

    // Python: test_sessions.py::TestHelpers::test_extract_json_string_field_with_space
    [Fact]
    public void ExtractJsonStringFieldWithSpace()
    {
        Assert.Equal("bar", SessionLite.ExtractJsonStringField("""{"foo": "bar"}""", "foo"));
    }

    // Python: test_sessions.py::TestHelpers::test_extract_json_string_field_escaped
    [Fact]
    public void ExtractJsonStringFieldEscaped()
    {
        Assert.Equal("bar\"baz", SessionLite.ExtractJsonStringField("""{"foo":"bar\"baz"}""", "foo"));
    }

    // Python: test_sessions.py::TestHelpers::test_extract_last_json_string_field
    [Fact]
    public void ExtractLastJsonStringField()
    {
        const string Text = "{\"summary\":\"first\"}\n{\"summary\":\"second\"}\n{\"summary\":\"third\"}";
        Assert.Equal("third", SessionLite.ExtractLastJsonStringField(Text, "summary"));
    }

    // Python: test_sessions.py::TestHelpers::test_extract_first_prompt_simple
    [Fact]
    public void ExtractFirstPromptSimple()
    {
        var head = Dumps(Obj("""{"type": "user", "message": {"content": "Hello!"}}""")) + "\n";
        Assert.Equal("Hello!", SessionLite.ExtractFirstPromptFromHead(head));
    }

    // Python: test_sessions.py::TestHelpers::test_extract_first_prompt_skips_meta
    [Fact]
    public void ExtractFirstPromptSkipsMeta()
    {
        var head = Dumps(Obj("""{"type": "user", "isMeta": true, "message": {"content": "meta"}}""")) + "\n"
            + Dumps(Obj("""{"type": "user", "message": {"content": "real prompt"}}""")) + "\n";
        Assert.Equal("real prompt", SessionLite.ExtractFirstPromptFromHead(head));
    }

    // Python: test_sessions.py::TestHelpers::test_extract_first_prompt_skips_tool_result
    [Fact]
    public void ExtractFirstPromptSkipsToolResult()
    {
        var head = Dumps(Obj("""{"type": "user", "message": {"content": [{"type": "tool_result", "content": "x"}]}}""")) + "\n"
            + Dumps(Obj("""{"type": "user", "message": {"content": "actual prompt"}}""")) + "\n";
        Assert.Equal("actual prompt", SessionLite.ExtractFirstPromptFromHead(head));
    }

    // Python: test_sessions.py::TestHelpers::test_extract_first_prompt_content_blocks
    [Fact]
    public void ExtractFirstPromptContentBlocks()
    {
        var head = Dumps(Obj("""{"type": "user", "message": {"content": [{"type": "text", "text": "block prompt"}]}}""")) + "\n";
        Assert.Equal("block prompt", SessionLite.ExtractFirstPromptFromHead(head));
    }

    // Python: test_sessions.py::TestHelpers::test_extract_first_prompt_truncates
    [Fact]
    public void ExtractFirstPromptTruncates()
    {
        var head = Dumps(new JsonObject { ["type"] = "user", ["message"] = new JsonObject { ["content"] = new string('x', 300) } }) + "\n";
        var result = SessionLite.ExtractFirstPromptFromHead(head);
        Assert.True(result.Length <= 201); // 200 字 + 刪節號
        Assert.EndsWith("\x2026", result, StringComparison.Ordinal);
    }

    // Python: test_sessions.py::TestHelpers::test_extract_first_prompt_command_fallback
    [Fact]
    public void ExtractFirstPromptCommandFallback()
    {
        // 只有 slash command 時用第一個指令名稱
        var head = Dumps(Obj("""{"type": "user", "message": {"content": "<command-name>/help</command-name>stuff"}}""")) + "\n";
        Assert.Equal("/help", SessionLite.ExtractFirstPromptFromHead(head));
    }

    // Python: test_sessions.py::TestHelpers::test_extract_first_prompt_empty
    [Fact]
    public void ExtractFirstPromptEmpty()
    {
        Assert.Equal("", SessionLite.ExtractFirstPromptFromHead(""));
        Assert.Equal("", SessionLite.ExtractFirstPromptFromHead("{\"type\":\"assistant\"}\n"));
    }
}
