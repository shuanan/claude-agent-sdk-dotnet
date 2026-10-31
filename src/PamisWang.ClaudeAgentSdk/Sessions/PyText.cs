using System.Text;

namespace PamisWang.ClaudeAgentSdk.Sessions;

/// <summary>
/// Python <c>str</c> 的幾個行為，讓移植的 session 程式跟 Python SDK 得到一樣的結果：
/// 空白字元的定義（<c>str.isspace</c>）、<c>splitlines</c>、以 code point 計算的長度與排序、list 的切片。
/// </summary>
internal static class PyText
{
    /// <summary>Python <c>str.isspace()</c> 認定的空白；比 <see cref="char.IsWhiteSpace(char)"/> 多了 U+001C–U+001F。</summary>
    public static bool IsSpace(char c) =>
        c is (>= '\t' and <= '\r') or (>= '\x001c' and <= ' ') or '\x0085' or '\x00a0' or '\x1680'
            or (>= '\x2000' and <= '\x200a') or '\x2028' or '\x2029' or '\x202f' or '\x205f' or '\x3000';

    /// <summary>Python 的 <c>str.strip()</c>。</summary>
    public static string Strip(string value)
    {
        var start = 0;
        var end = value.Length;
        while (start < end && IsSpace(value[start]))
        {
            start++;
        }

        while (end > start && IsSpace(value[end - 1]))
        {
            end--;
        }

        return value[start..end];
    }

    /// <summary>Python 的 <c>str.rstrip()</c>。</summary>
    public static string RStrip(string value)
    {
        var end = value.Length;
        while (end > 0 && IsSpace(value[end - 1]))
        {
            end--;
        }

        return value[..end];
    }

    /// <summary>
    /// Python 的 <c>str.splitlines()</c>：除了 \n、\r、\r\n，還有 \v、\f、U+001C–U+001E、U+0085、U+2028、U+2029 都算換行；
    /// 結尾的換行不會多出一個空字串。
    /// </summary>
    public static List<string> SplitLines(string value)
    {
        var lines = new List<string>();
        var start = 0;
        var i = 0;
        while (i < value.Length)
        {
            var c = value[i];
            if (c is '\n' or '\r' or '\v' or '\f' or '\x001c' or '\x001d' or '\x001e' or '\x0085' or '\x2028' or '\x2029')
            {
                lines.Add(value[start..i]);
                i += c == '\r' && i + 1 < value.Length && value[i + 1] == '\n' ? 2 : 1;
                start = i;
                continue;
            }

            i++;
        }

        if (start < value.Length)
        {
            lines.Add(value[start..]);
        }

        return lines;
    }

    /// <summary>
    /// Python 文字模式（<c>Path.read_text()</c>）的換行轉換：\r\n 與單獨的 \r 都變成 \n。
    /// </summary>
    public static string TranslateNewlines(string value) =>
        value.Contains('\r', StringComparison.Ordinal) ? value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n') : value;

    /// <summary>以 code point 計算的長度（Python 的 <c>len(str)</c>）；不成對的 surrogate 算一個。</summary>
    public static int CodePointLength(string value)
    {
        var count = 0;
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                i++;
            }

            count++;
        }

        return count;
    }

    /// <summary>前 <paramref name="count"/> 個 code point（Python 的 <c>s[:count]</c>）。</summary>
    public static string TakeCodePoints(string value, int count)
    {
        var i = 0;
        for (var taken = 0; taken < count && i < value.Length; taken++)
        {
            i += char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]) ? 2 : 1;
        }

        return value[..i];
    }

    /// <summary>依 code point 比較字串，跟 Python 的 <c>str</c> 比較一樣（<see cref="StringComparer.Ordinal"/> 比的是 UTF-16 單位）。</summary>
    public static readonly IComparer<string?> CodePointComparer = Comparer<string?>.Create(CompareCodePoints);

    private static int CompareCodePoints(string? left, string? right)
    {
        if (left is null || right is null)
        {
            return left is null ? (right is null ? 0 : -1) : 1;
        }

        int i = 0, j = 0;
        while (i < left.Length && j < right.Length)
        {
            var a = CodePointAt(left, ref i);
            var b = CodePointAt(right, ref j);
            if (a != b)
            {
                return a < b ? -1 : 1;
            }
        }

        return (i < left.Length).CompareTo(j < right.Length);
    }

    private static int CodePointAt(string value, ref int index)
    {
        var c = value[index];
        if (char.IsHighSurrogate(c) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))
        {
            var codePoint = char.ConvertToUtf32(c, value[index + 1]);
            index += 2;
            return codePoint;
        }

        index++;
        return c;
    }

    /// <summary>Python list 的切片 <c>items[start:stop]</c>，負數從尾端算起。</summary>
    public static List<T> Slice<T>(IReadOnlyList<T> items, int? start, int? stop)
    {
        var length = items.Count;
        var from = Normalize(start ?? 0, length);
        var to = stop is null ? length : Normalize(stop.Value, length);
        var result = new List<T>(Math.Max(0, to - from));
        for (var i = from; i < to; i++)
        {
            result.Add(items[i]);
        }

        return result;
    }

    private static int Normalize(int index, int length)
    {
        long value = index < 0 ? (long)index + length : index;
        return (int)Math.Clamp(value, 0, length);
    }

    /// <summary>不合法的位元組換成 U+FFFD 的 UTF-8 解碼（Python <c>errors="replace"</c>）。不會去掉 BOM。</summary>
    public static string DecodeReplace(ReadOnlySpan<byte> bytes) => Encoding.UTF8.GetString(bytes);

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Python 的 <c>Path.read_text(encoding="utf-8")</c>：不合法的 UTF-8 丟出 <see cref="DecoderFallbackException"/>（Python 的 <c>UnicodeDecodeError</c>），
    /// BOM 留著，換行轉成 \n。
    /// </summary>
    public static string ReadTextFile(string path) => TranslateNewlines(StrictUtf8.GetString(File.ReadAllBytes(path)));
}
