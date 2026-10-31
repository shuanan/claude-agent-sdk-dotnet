using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PamisWang.ClaudeAgentSdk.Sessions;

/// <summary>session 檔案的開頭與結尾、修改時間與大小。對應 Python 的 <c>_LiteSessionFile</c>。</summary>
internal sealed record LiteSessionFile(long Mtime, long Size, string Head, string Tail);

/// <summary>
/// 不完整解析 JSONL、只讀開頭與結尾就取出 session 資訊。對應 Python <c>_internal/sessions.py</c> 的
/// <c>_read_session_lite</c>、<c>_extract_json_string_field</c>、<c>_extract_first_prompt_from_head</c>、<c>_parse_session_info_from_lite</c>。
/// </summary>
internal static partial class SessionLite
{
    /// <summary>開頭與結尾各讀多少位元組。對應 <c>LITE_READ_BUF_SIZE</c>。</summary>
    public const int ReadBufferSize = 65536;

    // Python 的 \s（str.isspace）比 .NET 的 \s 多了 U+001C–U+001F，所以明列
    private const string PySpace = "[\t\n\v\f\r\x001c-\x001f \x0085\x00a0\x1680\x2000-\x200a\x2028\x2029\x202f\x205f\x3000]";

    /// <summary>找第一個有意義的使用者提示時要跳過的自動產生或系統訊息。對應 <c>_SKIP_FIRST_PROMPT_PATTERN</c>（<c>re.match</c>，只比開頭）。</summary>
    [GeneratedRegex(
        @"\A(?:<local-command-stdout>|<session-start-hook>|<tick>|<goal>|\[Request interrupted by user[^\]]*\]|"
        + PySpace + @"*<ide_opened_file>[\s\S]*</ide_opened_file>" + PySpace + @"*$|"
        + PySpace + @"*<ide_selection>[\s\S]*</ide_selection>" + PySpace + "*$)",
        RegexOptions.CultureInvariant)]
    public static partial Regex SkipFirstPromptRegex();

    /// <summary>對應 <c>_COMMAND_NAME_RE</c>。</summary>
    [GeneratedRegex("<command-name>(.*?)</command-name>", RegexOptions.CultureInvariant)]
    public static partial Regex CommandNameRegex();

    /// <summary>Python 的 <c>str.find(sub, start)</c>：<paramref name="start"/> 超過長度時回傳 -1 而不是丟例外。</summary>
    private static int Find(string text, string value, int start) =>
        start > text.Length ? -1 : text.IndexOf(value, start, StringComparison.Ordinal);

    /// <summary>把當作原文取出的 JSON 字串值還原跳脫字元；還原失敗時照原文回傳。對應 <c>_unescape_json_string</c>。</summary>
    public static string UnescapeJsonString(string raw)
    {
        if (!raw.Contains('\\', StringComparison.Ordinal))
        {
            return raw;
        }

        return PyJson.TryLoads("\"" + raw + "\"", out var node) && PyJson.Str(node) is { } text ? text : raw;
    }

    /// <summary>
    /// 不完整解析就取出 <c>"key":"value"</c> 或 <c>"key": "value"</c> 的第一個值；截斷的行也能用。
    /// 對應 <c>_extract_json_string_field</c>。
    /// </summary>
    public static string? ExtractJsonStringField(string text, string key)
    {
        foreach (var pattern in (ReadOnlySpan<string>)[$"\"{key}\":\"", $"\"{key}\": \""])
        {
            var index = Find(text, pattern, 0);
            if (index < 0)
            {
                continue;
            }

            var valueStart = index + pattern.Length;
            var i = valueStart;
            while (i < text.Length)
            {
                if (text[i] == '\\')
                {
                    i += 2;
                    continue;
                }

                if (text[i] == '"')
                {
                    return UnescapeJsonString(text[valueStart..i]);
                }

                i++;
            }
        }

        return null;
    }

    /// <summary>跟 <see cref="ExtractJsonStringField"/> 一樣，但取最後一個。對應 <c>_extract_last_json_string_field</c>。</summary>
    public static string? ExtractLastJsonStringField(string text, string key)
    {
        string? lastValue = null;
        foreach (var pattern in (ReadOnlySpan<string>)[$"\"{key}\":\"", $"\"{key}\": \""])
        {
            var searchFrom = 0;
            while (true)
            {
                var index = Find(text, pattern, searchFrom);
                if (index < 0)
                {
                    break;
                }

                var valueStart = index + pattern.Length;
                var i = valueStart;
                while (i < text.Length)
                {
                    if (text[i] == '\\')
                    {
                        i += 2;
                        continue;
                    }

                    if (text[i] == '"')
                    {
                        lastValue = UnescapeJsonString(text[valueStart..i]);
                        break;
                    }

                    i++;
                }

                searchFrom = i + 1;
            }
        }

        return lastValue;
    }

    /// <summary>
    /// 從 JSONL 開頭找出第一個有意義的使用者提示：跳過 tool_result、isMeta、isCompactSummary、slash command 與自動產生的訊息，
    /// 超過 200 字截斷。只有 slash command 時回傳第一個指令名稱。對應 <c>_extract_first_prompt_from_head</c>。
    /// </summary>
    public static string ExtractFirstPromptFromHead(string head)
    {
        var start = 0;
        var commandFallback = "";
        var headLength = head.Length;

        while (start < headLength)
        {
            string line;
            var newline = head.IndexOf('\n', start);
            if (newline >= 0)
            {
                line = head[start..newline];
                start = newline + 1;
            }
            else
            {
                line = head[start..];
                start = headLength;
            }

            if (!line.Contains("\"type\":\"user\"", StringComparison.Ordinal) && !line.Contains("\"type\": \"user\"", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.Contains("\"tool_result\"", StringComparison.Ordinal)
                || line.Contains("\"isMeta\":true", StringComparison.Ordinal) || line.Contains("\"isMeta\": true", StringComparison.Ordinal)
                || line.Contains("\"isCompactSummary\":true", StringComparison.Ordinal) || line.Contains("\"isCompactSummary\": true", StringComparison.Ordinal))
            {
                continue;
            }

            if (PyJson.LoadsObject(line) is not { } entry || PyJson.Str(entry, "type") != "user"
                || !entry.TryGetPropertyValue("message", out var messageNode) || messageNode is not JsonObject message)
            {
                continue;
            }

            foreach (var raw in TextBlocks(message))
            {
                var result = PyText.Strip(raw.Replace('\n', ' '));
                if (result.Length == 0)
                {
                    continue;
                }

                var command = CommandNameRegex().Match(result);
                if (command.Success)
                {
                    if (commandFallback.Length == 0)
                    {
                        commandFallback = command.Groups[1].Value;
                    }

                    continue;
                }

                if (SkipFirstPromptRegex().IsMatch(result))
                {
                    continue;
                }

                return TruncatePrompt(result);
            }
        }

        return commandFallback;
    }

    /// <summary>超過 200 個 code point 時截成 200、去掉結尾空白、加上「…」。</summary>
    public static string TruncatePrompt(string result) =>
        PyText.CodePointLength(result) > 200 ? PyText.RStrip(PyText.TakeCodePoints(result, 200)) + "\x2026" : result;

    /// <summary><c>message.content</c> 是字串時就是它；是 list 時取每個 <c>{"type":"text","text":str}</c> 的文字。</summary>
    public static List<string> TextBlocks(JsonObject message)
    {
        var texts = new List<string>();
        message.TryGetPropertyValue("content", out var content);
        if (PyJson.Str(content) is { } text)
        {
            texts.Add(text);
        }
        else if (content is JsonArray blocks)
        {
            foreach (var block in blocks)
            {
                if (block is JsonObject obj && PyJson.Str(obj, "type") == "text" && PyJson.Str(obj, "text") is { } blockText)
                {
                    texts.Add(blockText);
                }
            }
        }

        return texts;
    }

    /// <summary>
    /// 開檔、讀大小與修改時間、讀開頭與結尾各 64 KiB。讀不到或是空檔案時回傳 <see langword="null"/>。
    /// 對應 <c>_read_session_lite</c>。
    /// </summary>
    public static LiteSessionFile? ReadSessionLite(string filePath)
    {
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1);
            var size = stream.Length;
            var mtime = MtimeMs(File.GetLastWriteTimeUtc(stream.SafeFileHandle));

            var buffer = new byte[ReadBufferSize];
            var headLength = stream.ReadAtLeast(buffer, ReadBufferSize, throwOnEndOfStream: false);
            if (headLength == 0)
            {
                return null;
            }

            var head = PyText.DecodeReplace(buffer.AsSpan(0, headLength));
            string tail;
            var tailOffset = Math.Max(0, size - ReadBufferSize);
            if (tailOffset == 0)
            {
                tail = head;
            }
            else
            {
                stream.Seek(tailOffset, SeekOrigin.Begin);
                var tailLength = stream.ReadAtLeast(buffer, ReadBufferSize, throwOnEndOfStream: false);
                tail = PyText.DecodeReplace(buffer.AsSpan(0, tailLength));
            }

            return new LiteSessionFile(mtime, size, head, tail);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Python 的 <c>int(stat.st_mtime * 1000)</c>：<c>st_mtime</c> 是 <c>秒 + 奈秒 * 1e-9</c> 的 double，乘 1000 後無條件捨去。
    /// .NET 的時間只到 100 奈秒。
    /// </summary>
    public static long MtimeMs(DateTime lastWriteUtc)
    {
        var ticks = (lastWriteUtc - DateTime.UnixEpoch).Ticks;
        var seconds = Math.DivRem(ticks, TimeSpan.TicksPerSecond, out var remainder);
        if (remainder < 0)
        {
            seconds--;
            remainder += TimeSpan.TicksPerSecond;
        }

        var stMtime = seconds + remainder * 100 * 1e-9;
        return (long)(stMtime * 1000);
    }

    /// <summary>
    /// 從開頭與結尾取出 <see cref="SdkSessionInfo"/>。sidechain session 或沒有任何標題、摘要、提示的 session 回傳 <see langword="null"/>。
    /// 對應 <c>_parse_session_info_from_lite</c>。
    /// </summary>
    public static SdkSessionInfo? ParseSessionInfo(string sessionId, LiteSessionFile lite, string? projectPath = null)
    {
        var head = lite.Head;
        var tail = lite.Tail;

        var firstNewline = head.IndexOf('\n', StringComparison.Ordinal);
        var firstLine = firstNewline >= 0 ? head[..firstNewline] : head;
        if (firstLine.Contains("\"isSidechain\":true", StringComparison.Ordinal) || firstLine.Contains("\"isSidechain\": true", StringComparison.Ordinal))
        {
            return null;
        }

        // 使用者設的標題（customTitle）優先於 AI 產生的（aiTitle）；短 session 的標題可能不在結尾，所以也看開頭
        var customTitle = NullIfEmpty(ExtractLastJsonStringField(tail, "customTitle"))
            ?? NullIfEmpty(ExtractLastJsonStringField(head, "customTitle"))
            ?? NullIfEmpty(ExtractLastJsonStringField(tail, "aiTitle"))
            ?? NullIfEmpty(ExtractLastJsonStringField(head, "aiTitle"));
        var firstPrompt = NullIfEmpty(ExtractFirstPromptFromHead(head));
        var summary = customTitle
            ?? NullIfEmpty(ExtractLastJsonStringField(tail, "lastPrompt"))
            ?? NullIfEmpty(ExtractLastJsonStringField(tail, "summary"))
            ?? firstPrompt;

        if (summary is null)
        {
            return null;
        }

        var gitBranch = NullIfEmpty(ExtractLastJsonStringField(tail, "gitBranch")) ?? NullIfEmpty(ExtractJsonStringField(head, "gitBranch"));
        var sessionCwd = NullIfEmpty(ExtractJsonStringField(head, "cwd")) ?? NullIfEmpty(projectPath);

        // tag 只看 {"type":"tag"} 開頭的行：直接掃 "tag" 會撞到 tool_use 的輸入（git tag、Docker tag）
        string? tagLine = null;
        var tailLines = tail.Split('\n');
        for (var i = tailLines.Length - 1; i >= 0; i--)
        {
            if (tailLines[i].StartsWith("{\"type\":\"tag\"", StringComparison.Ordinal))
            {
                tagLine = tailLines[i];
                break;
            }
        }

        var tag = tagLine is null ? null : NullIfEmpty(ExtractLastJsonStringField(tagLine, "tag"));

        // created_at：開頭裡第一個 ISO 時間（第一行可能是沒有時間的 metadata，例如 permission-mode）
        var firstTimestamp = ExtractJsonStringField(head, "timestamp");
        long? createdAt = string.IsNullOrEmpty(firstTimestamp) ? null : PyDateTime.IsoToEpochMs(firstTimestamp);

        return new SdkSessionInfo
        {
            SessionId = sessionId,
            Summary = summary,
            LastModified = lite.Mtime,
            FileSize = lite.Size,
            CustomTitle = customTitle,
            FirstPrompt = firstPrompt,
            GitBranch = gitBranch,
            Cwd = sessionCwd,
            Tag = tag,
            CreatedAt = createdAt,
        };
    }

    public static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
