using System.Buffers;

namespace PamisWang.ClaudeAgentSdk.Transport;

/// <summary>
/// 把選項轉成 CLI 參數。對應 Python SDK 的 <c>_build_command</c>，參數順序也一樣，方便對照協定樣本裡的 argv。
/// </summary>
internal static class CliArguments
{
    // cmd.exe 會解讀的字元
    private const string CmdExeMetacharacters = "&|<>^%!\"\r\n";
    private static readonly SearchValues<char> CmdExeMetacharacterValues = SearchValues.Create(CmdExeMetacharacters);

    /// <summary>組出參數，不含執行檔本身。</summary>
    /// <exception cref="ArgumentException">Windows 上 <see cref="ClaudeAgentOptions.Resume"/> 含有 cmd.exe 的特殊字元。</exception>
    public static IReadOnlyList<string> Build(ClaudeAgentOptions options, bool isWindows)
    {
        // 沒指定系統提示時傳空字串，不用 Claude Code 預設的系統提示；Python 與 TS SDK 都是這樣
        List<string> args = ["--output-format", "stream-json", "--verbose", "--system-prompt", ""];

        if (options.DisallowedTools.Count > 0)
        {
            args.AddRange(["--disallowedTools", string.Join(',', options.DisallowedTools)]);
        }

        if (!string.IsNullOrEmpty(options.Model))
        {
            args.AddRange(["--model", options.Model]);
        }

        if (!string.IsNullOrEmpty(options.Resume))
        {
            if (isWindows)
            {
                RejectWindowsCmdMetacharacters(nameof(options.Resume), options.Resume);
            }

            // 寫成 --resume=<值>：--resume 的值可以省略，分成兩個參數時，- 開頭的值會被當成另一個旗標
            args.Add($"--resume={options.Resume}");
        }

        if (options.IncludePartialMessages)
        {
            args.Add("--include-partial-messages");
        }

        args.AddRange(["--input-format", "stream-json"]);
        return args;
    }

    /// <summary>
    /// 多一層防護：已經拒絕執行批次檔，這些字元其實無害；但 resume 的值常來自外部輸入，
    /// 萬一之後又多了一層 cmd.exe，也不會變成指令。
    /// </summary>
    private static void RejectWindowsCmdMetacharacters(string optionName, string value)
    {
        if (value.AsSpan().ContainsAny(CmdExeMetacharacterValues))
        {
            throw new ArgumentException(
                $"{optionName} 的值 {value} 含有不能放在 Windows 命令列上的字元（{CmdExeMetacharacters.ReplaceLineEndings(" ")}）。",
                optionName);
        }
    }
}
