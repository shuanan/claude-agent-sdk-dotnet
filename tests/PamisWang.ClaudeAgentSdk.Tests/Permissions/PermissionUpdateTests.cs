using System.Buffers;
using System.Text;
using System.Text.Json;
using PamisWang.ClaudeAgentSdk.Permissions;

namespace PamisWang.ClaudeAgentSdk.Tests.Permissions;

public sealed class PermissionUpdateTests
{
    [Theory]
    [InlineData("""{"type":"setMode","destination":"session","mode":"acceptEdits"}""")]
    [InlineData("""{"type":"addRules","destination":"localSettings","rules":[{"toolName":"Bash","ruleContent":"npm test"},{"toolName":"Read","ruleContent":null}],"behavior":"allow"}""")]
    [InlineData("""{"type":"removeDirectories","destination":"session","directories":["D:\\work","/tmp"]}""")]
    [InlineData("""{"type":"replaceRules","rules":[],"behavior":"deny"}""")]
    public void FromJson_ThenWriteTo_RoundTripsProtocolFormat(string json)
    {
        var update = PermissionUpdate.FromJson(JsonSerializer.Deserialize<JsonElement>(json));

        Assert.Equal(json, Write(update));
    }

    [Fact]
    public void FromJson_ReadsTypedFields()
    {
        var update = PermissionUpdate.FromJson(JsonSerializer.Deserialize<JsonElement>(
            """{"type":"addRules","destination":"session","rules":[{"toolName":"Bash","ruleContent":"git status"}],"behavior":"allow"}"""));

        Assert.Equal("addRules", update.Type);
        Assert.Equal("session", update.Destination);
        Assert.Equal("allow", update.Behavior);
        Assert.Equal(new PermissionRuleValue("Bash", "git status"), Assert.Single(update.Rules!));
    }

    [Fact]
    public void WriteTo_OnlyWritesFieldsUsedByItsType()
    {
        // 跟 Python 的 to_dict 一樣：setMode 不帶 rules 與 behavior
        var update = new PermissionUpdate("setMode") { Mode = "plan", Behavior = "allow", Rules = [new("Bash")] };

        Assert.Equal("""{"type":"setMode","mode":"plan"}""", Write(update));
    }

    private static string Write(PermissionUpdate update)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            update.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
