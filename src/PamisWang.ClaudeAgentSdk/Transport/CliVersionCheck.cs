using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PamisWang.ClaudeAgentSdk.Transport;

/// <summary>
/// 啟動前用 <c>claude -v</c> 檢查版本，太舊只發警告、不擋。對應 Python <c>SubprocessCLITransport</c> 的 <c>_check_claude_version</c>：
/// 等 2 秒、只讀第一段輸出、任何錯誤都忽略；環境變數 <c>CLAUDE_AGENT_SDK_SKIP_VERSION_CHECK</c> 有值就跳過。
/// </summary>
internal static partial class CliVersionCheck
{
    /// <summary>SDK 支援的最舊 Claude Code，對應 Python 的 <c>MINIMUM_CLAUDE_CODE_VERSION</c>。</summary>
    public const string MinimumVersion = "2.0.0";

    /// <summary>第一個支援 <c>client_composed</c>（<see cref="ClaudeAgentOptions.VerbatimPrompts"/>）的版本，對應 Python 的 <c>VERBATIM_PROMPTS_MINIMUM_CLAUDE_CODE_VERSION</c>。</summary>
    public const string VerbatimPromptsMinimumVersion = "2.1.248";

    public const string SkipEnvironmentVariable = "CLAUDE_AGENT_SDK_SKIP_VERSION_CHECK";

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    /// <summary>跟 Python 一樣，環境變數是空字串時不算有設。</summary>
    public static bool IsSkipped(string? skipValue) => !string.IsNullOrEmpty(skipValue);

    /// <summary>執行 <c>claude -v</c>，依輸出發警告。不會丟出例外。</summary>
    public static async Task RunAsync(string cliPath, ClaudeAgentOptions options, CancellationToken cancellationToken)
    {
        Process? process = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ProbeTimeout);
            var startInfo = new ProcessStartInfo(cliPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.ArgumentList.Add("-v");
            process = Process.Start(startInfo);
            if (process is null)
            {
                return;
            }

            // 跟 Python 的 stdout.receive() 一樣只讀第一段
            var buffer = new byte[4096];
            var read = await process.StandardOutput.BaseStream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
            foreach (var warning in Warnings(Encoding.UTF8.GetString(buffer, 0, read), cliPath, options.VerbatimPrompts))
            {
                options.ReportWarning(warning);
            }
        }
#pragma warning disable CA1031 // 跟 Python 一樣：檢查版本失敗不影響連線
        catch (Exception)
#pragma warning restore CA1031
        {
        }
        finally
        {
            if (process is not null)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
#pragma warning disable CA1031 // 已經結束了
                catch (Exception)
#pragma warning restore CA1031
                {
                }

                process.Dispose();
            }
        }
    }

    /// <summary>依 <c>claude -v</c> 的輸出要發的警告，文字跟 Python 的一樣。輸出開頭不是 x.y.z 就不發。</summary>
    public static IReadOnlyList<string> Warnings(string versionOutput, string cliPath, bool verbatimPrompts)
    {
        var match = VersionPattern().Match(versionOutput.Trim());
        if (!match.Success || Parse(match.Groups[1].Value) is not { } version)
        {
            return [];
        }

        List<string> warnings = [];
        var text = match.Groups[1].Value;
        if (Compare(version, Parse(MinimumVersion)!) < 0)
        {
            warnings.Add(
                $"Claude Code version {text} at {cliPath} is unsupported in the Agent SDK. "
                + $"Minimum required version is {MinimumVersion}. "
                + "Some features may not work correctly.");
        }

        if (verbatimPrompts && Compare(version, Parse(VerbatimPromptsMinimumVersion)!) < 0)
        {
            warnings.Add(
                $"verbatim_prompts is enabled, but Claude Code version {text} at {cliPath} ignores it: "
                + "prompts will still have @path mentions expanded and slash commands dispatched. "
                + $"Claude Code {VerbatimPromptsMinimumVersion} or later is required.");
        }

        return warnings;
    }

    // Python 用 int() 比較每一段，數字再大都可以；這裡用 long，超出範圍就當作看不懂、不發警告
    private static long[]? Parse(string version)
    {
        var parts = version.Split('.');
        var numbers = new long[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!long.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
            {
                return null;
            }
        }

        return numbers;
    }

    private static int Compare(long[] left, long[] right)
    {
        for (var i = 0; i < Math.Min(left.Length, right.Length); i++)
        {
            if (left[i] != right[i])
            {
                return left[i].CompareTo(right[i]);
            }
        }

        return left.Length.CompareTo(right.Length);
    }

    // 跟 Python 的 re.match(r"([0-9]+\.[0-9]+\.[0-9]+)") 一樣，從開頭比對
    [GeneratedRegex(@"\A([0-9]+\.[0-9]+\.[0-9]+)", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
}
