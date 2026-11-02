namespace PamisWang.ClaudeAgentSdk.Transport;

/// <summary>
/// 找 <c>claude</c> 執行檔，並拒絕在 Windows 上執行批次檔。
/// 對應 Python SDK <c>SubprocessCLITransport</c> 的 <c>_find_cli</c> 與 <c>_reject_windows_batch_cli</c>。
/// </summary>
internal sealed class CliLocator
{
    private readonly string? _pathVariable;
    private readonly string _homeDirectory;
    private readonly bool _isWindows;

    public CliLocator(string? pathVariable, string homeDirectory, bool isWindows)
    {
        _pathVariable = pathVariable;
        _homeDirectory = homeDirectory;
        _isWindows = isWindows;
    }

    public static CliLocator ForCurrentProcess() => new(
        Environment.GetEnvironmentVariable("PATH"),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        OperatingSystem.IsWindows());

    /// <summary>有指定路徑就用它，否則從 PATH 與常見的安裝位置找。</summary>
    public string Find(string? cliPath)
    {
        if (!string.IsNullOrEmpty(cliPath))
        {
            return cliPath;
        }

        return _isWindows ? FindOnWindows() : FindOnUnix();
    }

    /// <summary>Windows 上拒絕執行批次檔；其他平台什麼都不做。</summary>
    /// <exception cref="CliConnectionException">路徑是 .bat 或 .cmd。</exception>
    public void EnsureRunnable(string cliPath)
    {
        if (!_isWindows || !IsWindowsBatchPath(cliPath))
        {
            return;
        }

        // Windows 用 cmd.exe 執行批次檔，cmd.exe 會重新解讀整串參數，參數裡的字元可能被當成指令執行，
        // 而且沒有可靠的跳脫方式（CVE-2024-27980，BatBadBut）。Node.js 的修正也是直接拒絕。
        var bundled = ExistingFile(NpmBundledExe(cliPath));
        throw new CliConnectionException(
            $"拒絕執行批次檔 {cliPath}：Windows 透過 cmd.exe 執行 .bat、.cmd，參數可能被注入指令。"
            + (bundled is not null
                ? $"npm 套件附有原生的 {bundled}，請把 ClaudeAgentOptions.CliPath 指到它。"
                : "請改用原生的 claude.exe：用 PowerShell 執行 irm https://claude.ai/install.ps1 | iex 安裝，"
                    + "或把 ClaudeAgentOptions.CliPath 指到 claude.exe。"));
    }

    /// <summary>
    /// 路徑的任何一段帶 .bat 或 .cmd 副檔名就算批次檔。
    /// 不只看最後一段：Windows 開檔前會正規化路徑（收掉 . 與 ..、去掉結尾的點和空白），
    /// 只要有一段是批次檔就拒絕，才不會被 <c>claude.cmd\..\..</c> 這類寫法繞過。
    /// 冒號也拆開看，涵蓋 <c>C:claude.cmd</c> 與 NTFS 資料流 <c>claude.cmd:stream</c>。
    /// </summary>
    public static bool IsWindowsBatchPath(string path) =>
        path.Replace('\\', '/').Split('/')
            .SelectMany(component => component.Split(':'))
            .Select(segment => segment.TrimEnd('.', ' '))
            .Any(segment => segment.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)
                || segment.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase));

    private string FindOnWindows()
    {
        // 只用原生的 claude.exe。PATH 前面的目錄有 npm 的 claude.cmd 時，也優先用後面目錄的 claude.exe。
        // 不找沒有副檔名的 claude：那是 git-bash 或 WSL 的腳本，CreateProcess 執行不了。
        var native = SearchPath("claude.exe")
            ?? ExistingFile(Path.Combine(_homeDirectory, ".local", "bin", "claude.exe"));
        if (native is not null)
        {
            return native;
        }

        var shim = SearchPath("claude.cmd");

        // npm 裝的 claude.cmd 只是轉呼叫套件附的原生 claude.exe；直接用那個 exe，不經過 cmd.exe
        if (shim is not null && ExistingFile(NpmBundledExe(shim)) is { } bundled)
        {
            return bundled;
        }

        // 只找得到批次檔：交給 EnsureRunnable 以「拒絕執行批次檔」的說明失敗，而不是籠統的找不到
        return shim ?? SearchPath("claude.bat") ?? throw new CliNotFoundException(
            "找不到 Claude Code。請用 PowerShell 執行 irm https://claude.ai/install.ps1 | iex 安裝原生的 claude.exe，"
            + "或把 ClaudeAgentOptions.CliPath 指到 claude.exe。"
            + "（舊版 npm 套件只有 claude.cmd，沒有附 claude.exe，這個套件不會執行它。）");
    }

    /// <summary>npm 全域安裝時，claude.cmd 旁邊 node_modules 裡套件附的 claude.exe。</summary>
    private static string? NpmBundledExe(string shimPath) => Path.GetDirectoryName(shimPath) is { Length: > 0 } directory
        ? Path.Combine(directory, "node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe")
        : null;

    private string FindOnUnix()
    {
        if (SearchPath("claude") is { } onPath)
        {
            return onPath;
        }

        string[] locations =
        [
            Path.Combine(_homeDirectory, ".npm-global", "bin", "claude"),
            "/usr/local/bin/claude",
            Path.Combine(_homeDirectory, ".local", "bin", "claude"),
            Path.Combine(_homeDirectory, "node_modules", ".bin", "claude"),
            Path.Combine(_homeDirectory, ".yarn", "bin", "claude"),
            Path.Combine(_homeDirectory, ".claude", "local", "claude"),
        ];
        return locations.FirstOrDefault(File.Exists) ?? throw new CliNotFoundException(
            "找不到 Claude Code。請用 npm install -g @anthropic-ai/claude-code 安裝，"
            + "或把 ClaudeAgentOptions.CliPath 指到 claude 執行檔。");
    }

    private string? SearchPath(string fileName)
    {
        if (string.IsNullOrEmpty(_pathVariable))
        {
            return null;
        }

        return _pathVariable
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(directory => Path.Combine(directory.Trim('"'), fileName))
            .FirstOrDefault(File.Exists);
    }

    private static string? ExistingFile(string? path) => File.Exists(path) ? path : null;
}
