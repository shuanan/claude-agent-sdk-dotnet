using System.Text.Json.Nodes;
using PamisWang.ClaudeAgentSdk.Sessions;

namespace PamisWang.ClaudeAgentSdk.Tests.Sessions;

/// <summary>
/// 不是移植的測試：檢查 Python 行為的模擬（雜湊、<c>json.dumps</c>、<c>str</c> 的空白與切行、<c>fromisoformat</c>、<c>realpath</c>）。
/// 期望值是 Python 與 JavaScript 的已知結果。
/// </summary>
public sealed class PythonCompatTests
{
    [Fact]
    public void SimpleHash_MatchesJavaScriptStringHash()
    {
        // "hello" 的 31 進位雜湊是 99162322（跟 Java 的 String.hashCode 一樣），36 進位是 1n1e4y
        Assert.Equal("1n1e4y", SessionPaths.SimpleHash("hello"));
    }

    [Fact]
    public void SanitizePath_ReplacesEachCodePointOnce()
    {
        // Python 的 re.sub 逐個 code point 換；表情符號是一個 code point（UTF-16 是兩個單位）
        Assert.Equal("a-b", SessionPaths.SanitizePath("a\U0001F600b"));
        Assert.Equal("caf-", SessionPaths.SanitizePath("caf\x00e9"));
    }

    [Fact]
    public void Dumps_MatchesPythonJsonDumps()
    {
        var node = JsonNode.Parse("""{"a": "café 😀", "b": [1, 2.50, 1e20, 1E-5, -0, 0.0001, true, null], "c": "\u007f\b\t"}""");
        Assert.Equal(
            "{\"a\":\"caf" + U("00e9") + " " + U("d83d") + U("de00") + "\",\"b\":[1,2.5,1e+20,1e-05,0,0.0001,true,null],\"c\":\"" + U("007f") + "\\b\\t\"}",
            PyJson.Dumps(node));
        Assert.Equal("""{"x": 1, "y": [true, false]}""", PyJson.Dumps(JsonNode.Parse("""{"x":1,"y":[true,false]}"""), compact: false));
    }

    [Theory]
    [InlineData(1.0, "1.0")]
    [InlineData(1e16, "1e+16")]
    [InlineData(1234567890123456.0, "1234567890123456.0")]
    [InlineData(0.1, "0.1")]
    [InlineData(1.5e-7, "1.5e-07")]
    [InlineData(-2.5, "-2.5")]
    public void FormatFloat_MatchesPythonRepr(double value, string expected)
    {
        Assert.Equal(expected, PyJson.FormatFloat(value));
    }

    [Fact]
    public void Loads_DuplicateKeyKeepsFirstPositionAndLastValue()
    {
        var node = PyJson.LoadsObject("""{"a": 1, "b": 2, "a": 3}""");
        Assert.Equal("""{"a":3,"b":2}""", PyJson.Dumps(node));
    }

    [Fact]
    public void SplitLines_MatchesPythonStrSplitlines()
    {
        Assert.Equal(["a", "b", "", "c", "d"], PyText.SplitLines("a\r\nb\n\x2028c\x001cd\n"));
        Assert.Empty(PyText.SplitLines(""));
    }

    [Fact]
    public void Strip_UsesPythonWhitespace()
    {
        // U+001C–U+001F 在 Python 算空白，.NET 不算
        Assert.Equal("x", PyText.Strip("\x001f\x00a0 x\x3000\n"));
    }

    [Theory]
    [InlineData("2026-01-15T10:30:00.000Z", 1768473000000L)]
    [InlineData("2026-01-15T10:30:00+00:00", 1768473000000L)]
    [InlineData("2026-01-15T12:30:00+02:00", 1768473000000L)]
    [InlineData("2026-01-15 10:30:00.5Z", 1768473000500L)]
    [InlineData("20260115T103000Z", 1768473000000L)]
    [InlineData("2026-01-15T10:30:00.1234567Z", 1768473000123L)]
    public void IsoToEpochMs_MatchesPythonFromIsoFormat(string timestamp, long expected)
    {
        Assert.Equal(expected, PyDateTime.IsoToEpochMs(timestamp));
    }

    [Theory]
    [InlineData("not-a-valid-iso-date")]
    [InlineData("2026-13-01T00:00:00Z")]
    [InlineData("2026-02-30")]
    [InlineData("2026-01-15T10:30:00+24:00")]
    [InlineData("2026-01-15T10:3")]
    public void IsoToEpochMs_InvalidIsNull(string timestamp)
    {
        Assert.Null(PyDateTime.IsoToEpochMs(timestamp));
    }

    [Fact]
    public void RealPath_ResolvesSymlinksAndDotDot()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = new TempDirectory();
        var root = SessionPaths.RealPath(temp.Path);
        var target = temp.CreateDirectory("real", "inner");
        Directory.CreateSymbolicLink(Path.Combine(temp.Path, "link"), target);

        Assert.Equal(Path.Combine(root, "real", "inner"), SessionPaths.RealPath(Path.Combine(temp.Path, "link")));
        // realpath 先解開連結再處理 ..（不像 GetFullPath 照字面）
        Assert.Equal(Path.Combine(root, "real"), SessionPaths.RealPath(Path.Combine(temp.Path, "link", "..")));
        // 不存在的部分照字面保留
        Assert.Equal(Path.Combine(root, "real", "inner", "missing", "x"), SessionPaths.RealPath(Path.Combine(temp.Path, "link", "missing", "x")));
    }

    [Fact]
    public void RenameSession_EscapesNonAsciiLikePython()
    {
        using var kit = new SessionTestKit();
        var projectDir = kit.MakeProjectDir("/some/project");
        var (sid, filePath) = SessionTestKit.MakeSessionFile(projectDir);

        ClaudeSessions.RenameSession(sid, "café 😀", claudeConfigDir: kit.ConfigDir);

        var last = File.ReadAllText(filePath).TrimEnd('\n').Split('\n')[^1];
        var title = "caf" + U("00e9") + " " + U("d83d") + U("de00");
        Assert.Equal($$"""{"type":"custom-title","customTitle":"{{title}}","sessionId":"{{sid}}"}""", last);
    }

    /// <summary>JSON 跳脫的 <c>\uXXXX</c> 文字（反斜線加 u 加 4 個十六進位數字）。</summary>
    private static string U(string hex) => "\\" + "u" + hex;
}
