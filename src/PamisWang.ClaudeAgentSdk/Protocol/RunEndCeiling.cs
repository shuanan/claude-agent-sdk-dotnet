using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

namespace PamisWang.ClaudeAgentSdk.Protocol;

/// <summary>
/// 結果之後等 CLI 回到 idle 的上限。對應 Python 的 <c>run_end_ceiling_ms</c>：CLI 在 stdin 關掉後等背景工作的上限也是這個值，
/// SDK 等 idle 用同一個數字。
/// </summary>
internal static partial class RunEndCeiling
{
    public const string EnvironmentVariable = "CLAUDE_CODE_PRINT_BG_WAIT_CEILING_MS";

    /// <summary>CLI 的預設：10 分鐘。</summary>
    public const long DefaultMs = 600_000;

    /// <summary>最長只等這麼久（約 24.8 天），跟 TypeScript SDK 一樣，那邊的計時器不能更長。</summary>
    public const long MaxMs = int.MaxValue;

    /// <summary>
    /// 照 CLI 會看到的值讀：<see cref="ClaudeAgentOptions.Env"/> 優先於繼承的環境變數。<c>0</c> 代表沒有上限；
    /// 不是一般的非負整數（Python <c>int()</c> 看得懂的寫法：前後空白、正負號、數字間的底線）就用預設的 10 分鐘。
    /// </summary>
    public static long FromEnvironment(IReadOnlyDictionary<string, string> optionsEnv, Func<string, string?> inherited)
    {
        var raw = optionsEnv.TryGetValue(EnvironmentVariable, out var fromOptions) ? fromOptions : inherited(EnvironmentVariable);
        if (raw is null || !PythonInteger().IsMatch(raw))
        {
            return DefaultMs;
        }

        var value = BigInteger.Parse(raw.Trim().Replace("_", "", StringComparison.Ordinal), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        if (value < 0)
        {
            return DefaultMs;
        }

        return value > long.MaxValue ? long.MaxValue : (long)value;
    }

    [GeneratedRegex(@"\A\s*[+-]?[0-9]+(_[0-9]+)*\s*\z", RegexOptions.CultureInvariant)]
    private static partial Regex PythonInteger();
}
