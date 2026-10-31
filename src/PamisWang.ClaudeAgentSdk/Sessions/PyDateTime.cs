namespace PamisWang.ClaudeAgentSdk.Sessions;

/// <summary>
/// Python 的 <c>int(datetime.fromisoformat(ts).timestamp() * 1000)</c>（Python 3.11 以後的 <c>fromisoformat</c>），
/// 連浮點數的捨入都照 Python 算，毫秒數才會一樣。
/// 不支援 ISO 週日期（<c>2026-W03-4</c>）；沒有時區的時間跟 Python 一樣當本地時間。
/// </summary>
internal static class PyDateTime
{
    private static readonly int[] FractionCorrection = [100000, 10000, 1000, 100, 10];

    /// <summary>
    /// <c>created_at</c> 的算法：結尾是 <c>Z</c> 時先換成 <c>+00:00</c>（Python 3.10 的寫法），解析失敗回傳 <see langword="null"/>。
    /// </summary>
    public static long? IsoToEpochMs(string timestamp)
    {
        var normalized = timestamp.EndsWith('Z') ? timestamp.Replace("Z", "+00:00", StringComparison.Ordinal) : timestamp;
        return TryFromIsoFormat(normalized, out var parsed) ? ToEpochMs(parsed) : null;
    }

    private readonly record struct Parsed(DateTime Local, long? OffsetMicroseconds);

    private static long ToEpochMs(Parsed parsed)
    {
        double seconds;
        if (parsed.OffsetMicroseconds is { } offset)
        {
            // aware：(self - _EPOCH).total_seconds()，整數微秒除以 10**6
            var micros = (parsed.Local - DateTime.UnixEpoch).Ticks / 10 - offset;
            seconds = micros / 1e6;
        }
        else
        {
            // naive：_mktime() 的整數秒（本地時間）加上 microsecond / 1e6
            var local = DateTime.SpecifyKind(parsed.Local, DateTimeKind.Unspecified);
            var utcOffset = TimeZoneInfo.Local.GetUtcOffset(local);
            var wholeSeconds = new DateTime(local.Ticks - local.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Unspecified);
            var epochSeconds = (wholeSeconds - DateTime.UnixEpoch - utcOffset).Ticks / TimeSpan.TicksPerSecond;
            var microsecond = local.Ticks % TimeSpan.TicksPerSecond / 10;
            seconds = epochSeconds + microsecond / 1e6;
        }

        return (long)(seconds * 1000);
    }

    private static bool TryFromIsoFormat(string value, out Parsed parsed)
    {
        parsed = default;
        if (value.Length < 7 || !TryFindSeparator(value, out var separator))
        {
            return false;
        }

        var datePart = value[..Math.Min(separator, value.Length)];
        var timePart = separator + 1 < value.Length ? value[(separator + 1)..] : "";
        if (!TryParseDate(datePart, out var year, out var month, out var day))
        {
            return false;
        }

        int hour = 0, minute = 0, second = 0, microsecond = 0;
        long? offset = null;
        var nextDay = false;
        if (timePart.Length > 0 && !TryParseTime(timePart, out hour, out minute, out second, out microsecond, out offset, out nextDay))
        {
            return false;
        }

        if (year is < 1 or > 9999 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month)
            || hour > 23 || minute > 59 || second > 59)
        {
            return false;
        }

        var local = new DateTime(year, month, day, hour, minute, second).AddTicks(microsecond * 10L);
        if (nextDay)
        {
            if (local.Date == DateTime.MaxValue.Date)
            {
                return false;
            }

            local = local.AddDays(1);
        }

        parsed = new Parsed(local, offset);
        return true;
    }

    /// <summary>Python <c>_find_isoformat_datetime_separator</c>（不含週日期的分支會照算，但週日期本身不支援）。</summary>
    private static bool TryFindSeparator(string value, out int separator)
    {
        separator = 0;
        var length = value.Length;
        if (length == 7)
        {
            separator = 7;
            return true;
        }

        if (value[4] == '-')
        {
            if (value[5] == 'W')
            {
                return false;
            }

            separator = 10;
            return true;
        }

        if (value[4] == 'W')
        {
            return false;
        }

        separator = 8;
        return true;
    }

    private static bool TryParseDate(string value, out int year, out int month, out int day)
    {
        year = month = day = 0;
        if (value.Length is not (8 or 10) || !TryDigits(value, 0, 4, out year))
        {
            return false;
        }

        var hasSeparator = value[4] == '-';
        var pos = 4 + (hasSeparator ? 1 : 0);
        if (!TryDigits(value, pos, 2, out month))
        {
            return false;
        }

        pos += 2;
        if ((pos < value.Length && value[pos] == '-') != hasSeparator)
        {
            return false;
        }

        pos += hasSeparator ? 1 : 0;
        return TryDigits(value, pos, 2, out day) && pos + 2 == value.Length;
    }

    private static bool TryParseTime(string value, out int hour, out int minute, out int second, out int microsecond, out long? offset, out bool nextDay)
    {
        hour = minute = second = microsecond = 0;
        offset = null;
        nextDay = false;
        if (value.Length < 2)
        {
            return false;
        }

        var tzPos = FindFirst(value, '-');
        if (tzPos == 0)
        {
            tzPos = FindFirst(value, '+');
        }

        if (tzPos == 0)
        {
            tzPos = FindFirst(value, 'Z');
        }

        var timeText = tzPos > 0 ? value[..(tzPos - 1)] : value;
        if (!TryParseHhMmSsFf(timeText, out var components))
        {
            return false;
        }

        (hour, minute, second, microsecond) = (components[0], components[1], components[2], components[3]);
        if (hour == 24)
        {
            if (minute != 0 || second != 0 || microsecond != 0)
            {
                return false;
            }

            hour = 0;
            nextDay = true;
        }

        if (tzPos == value.Length && value[^1] == 'Z')
        {
            offset = 0;
        }
        else if (tzPos > 0)
        {
            var tzText = value[tzPos..];
            if (tzText.Length is 0 or 1 or 3 || !TryParseHhMmSsFf(tzText, out var tz))
            {
                return false;
            }

            var micros = ((tz[0] * 60L + tz[1]) * 60 + tz[2]) * 1_000_000 + tz[3];
            if (micros >= 24L * 3600 * 1_000_000)
            {
                // timezone() 只接受 -24h < offset < 24h
                return false;
            }

            offset = value[tzPos - 1] == '-' ? -micros : micros;
        }

        return true;
    }

    /// <summary>Python 的 <c>tstr.find(c) + 1</c>。</summary>
    private static int FindFirst(string value, char c) => value.IndexOf(c, StringComparison.Ordinal) + 1;

    /// <summary>Python <c>_parse_hh_mm_ss_ff</c>：<c>HH[:?MM[:?SS[{.,}fff[fff]]]]</c>。</summary>
    private static bool TryParseHhMmSsFf(string value, out int[] components)
    {
        components = [0, 0, 0, 0];
        var length = value.Length;
        var pos = 0;
        var hasSeparator = false;
        for (var component = 0; component < 3; component++)
        {
            if (length - pos < 2 || !TryDigits(value, pos, 2, out components[component]))
            {
                return false;
            }

            pos += 2;
            var next = pos < length ? value[pos] : '\0';
            if (component == 0)
            {
                hasSeparator = next == ':';
            }

            if (pos >= length || component >= 2)
            {
                break;
            }

            if (hasSeparator && next != ':')
            {
                return false;
            }

            pos += hasSeparator ? 1 : 0;
        }

        if (pos < length)
        {
            if (value[pos] is not ('.' or ','))
            {
                return false;
            }

            pos++;
            var remainder = length - pos;
            var toParse = Math.Min(remainder, 6);
            if (!TryDigits(value, pos, toParse, out var fraction))
            {
                return false;
            }

            components[3] = toParse < 6 ? fraction * FractionCorrection[toParse - 1] : fraction;
            for (var i = pos + toParse; i < length; i++)
            {
                if (value[i] is < '0' or > '9')
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool TryDigits(string value, int start, int count, out int number)
    {
        number = 0;
        if (count <= 0 || start + count > value.Length)
        {
            return false;
        }

        for (var i = start; i < start + count; i++)
        {
            var c = value[i];
            if (c is < '0' or > '9')
            {
                return false;
            }

            number = number * 10 + (c - '0');
        }

        return true;
    }
}
