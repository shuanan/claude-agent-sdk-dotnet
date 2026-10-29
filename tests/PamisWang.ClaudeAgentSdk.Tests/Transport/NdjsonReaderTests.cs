using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using PamisWang.ClaudeAgentSdk.Transport;

namespace PamisWang.ClaudeAgentSdk.Tests.Transport;

public sealed class NdjsonReaderTests
{
    [Fact]
    public async Task ReadAsync_OneMessagePerLine_YieldsEach()
    {
        var messages = await ReadAllAsync("{\"type\":\"a\"}\n{\"type\":\"b\"}\n");

        Assert.Equal(["a", "b"], Types(messages));
    }

    [Fact]
    public async Task ReadAsync_SeveralMessagesInOneChunk_YieldsAll()
    {
        // 最後一則沒有換行，在串流結束時才交出
        var messages = await ReadAllAsync("{\"type\":\"message\",\"id\":\"msg1\"}\n{\"type\":\"result\",\"id\":\"res1\"}");

        Assert.Equal(["message", "result"], Types(messages));
    }

    [Fact]
    public async Task ReadAsync_EscapedNewlineInsideString_IsOneMessage()
    {
        var messages = await ReadAllAsync("{\"type\":\"a\",\"text\":\"line1\\nline2\"}\n");

        Assert.Equal("line1\nline2", Assert.Single(messages).GetProperty("text").GetString());
    }

    [Fact]
    public async Task ReadAsync_BlankLinesBetweenMessages_AreSkipped()
    {
        var messages = await ReadAllAsync("{\"type\":\"a\"}\n\n\n  \n{\"type\":\"b\"}\n");

        Assert.Equal(["a", "b"], Types(messages));
    }

    [Fact]
    public async Task ReadAsync_CrLfLineEndings_AreAccepted()
    {
        var messages = await ReadAllAsync("{\"type\":\"a\"}\r\n{\"type\":\"b\"}\r\n");

        Assert.Equal(["a", "b"], Types(messages));
    }

    [Fact]
    public async Task ReadAsync_MessageSplitAcrossChunks_IsReassembled()
    {
        var messages = await ReadAllAsync("{\"type\":\"assis", "tant\",\"text\":\"he", "llo\"}\n{\"type\":", "\"result\"}\n");

        Assert.Equal(["assistant", "result"], Types(messages));
        Assert.Equal("hello", messages[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task ReadAsync_MultibyteCharacterSplitAcrossChunks_IsDecodedIntact()
    {
        var bytes = Encoding.UTF8.GetBytes("{\"type\":\"a\",\"text\":\"你好🐱\"}\n");
        var split = Array.IndexOf(bytes, Encoding.UTF8.GetBytes("好")[0]) + 1;

        var messages = await ReadAllAsync([bytes[..split], bytes[split..]]);

        Assert.Equal("你好🐱", Assert.Single(messages).GetProperty("text").GetString());
    }

    [Fact]
    public async Task ReadAsync_WhitespaceAtChunkBoundary_IsPreserved()
    {
        var messages = await ReadAllAsync("{\"type\":\"a\",\"text\":\"hello ", " world\"}\n");

        Assert.Equal("hello  world", Assert.Single(messages).GetProperty("text").GetString());
    }

    [Fact]
    public async Task ReadAsync_NonJsonLines_AreSkipped()
    {
        var messages = await ReadAllAsync("[SandboxDebug] starting\n{\"type\":\"a\"}\nplain text\n{\"type\":\"b\"}\n");

        Assert.Equal(["a", "b"], Types(messages));
    }

    [Fact]
    public async Task ReadAsync_MalformedCompleteLine_Throws()
    {
        var error = await Assert.ThrowsAsync<CliJsonDecodeException>(() => ReadAllAsync("{\"type\":\"a\"}\n{\"type\":\n"));

        Assert.Equal("{\"type\":", error.Line);
    }

    [Fact]
    public async Task ReadAsync_CompleteLineOverLimit_Throws()
    {
        var line = $"{{\"type\":\"a\",\"text\":\"{new string('x', 200)}\"}}\n";

        await Assert.ThrowsAsync<CliJsonDecodeException>(() => ReadAllAsync([Encoding.UTF8.GetBytes(line)], maxLineBytes: 100));
    }

    [Fact]
    public async Task ReadAsync_PartialLineOverLimit_ThrowsBeforeNewlineArrives()
    {
        // 第二段永遠不會被讀到：沒有換行的部分一超過上限就停
        byte[][] chunks = [Encoding.UTF8.GetBytes("{\"text\":\"" + new string('x', 200)), Encoding.UTF8.GetBytes("\"}\n")];

        await Assert.ThrowsAsync<CliJsonDecodeException>(() => ReadAllAsync(chunks, maxLineBytes: 100));
    }

    [Fact]
    public async Task ReadAsync_TruncatedFinalLine_IsDropped()
    {
        var messages = await ReadAllAsync("{\"type\":\"a\"}\n{\"type\":\"res");

        Assert.Equal(["a"], Types(messages));
    }

    [Theory]
    [MemberData(nameof(ProtocolFixture.Names), MemberType = typeof(ProtocolFixture))]
    public async Task ReadAsync_RecordedFixture_YieldsEveryReceivedMessage(string fixture)
    {
        var expected = ProtocolFixture.Load(fixture).Received;
        var stdout = Encoding.UTF8.GetBytes(string.Concat(expected.Select(message => message.GetRawText() + "\n")));

        // 4 KB 一段，會切在中文字與 JSON 的中間
        var messages = await ReadAllAsync(stdout.Chunk(4096));

        Assert.Equal(expected.Count, messages.Count);
        Assert.All(expected.Zip(messages), pair => Assert.True(JsonElement.DeepEquals(pair.First, pair.Second)));
    }

    private static Task<List<JsonElement>> ReadAllAsync(params string[] chunks) =>
        ReadAllAsync(chunks.Select(Encoding.UTF8.GetBytes));

    private static async Task<List<JsonElement>> ReadAllAsync(IEnumerable<byte[]> chunks, int maxLineBytes = 1024 * 1024)
    {
        var reader = PipeReader.Create(new ChunkedStream(chunks));
        List<JsonElement> messages = [];
        await foreach (var message in NdjsonReader.ReadAsync(reader, maxLineBytes))
        {
            messages.Add(message);
        }

        return messages;
    }

    private static IEnumerable<string?> Types(IEnumerable<JsonElement> messages) =>
        messages.Select(message => message.GetProperty("type").GetString());
}
