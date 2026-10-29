namespace PamisWang.ClaudeAgentSdk.Tests;

/// <summary>
/// 假的 Claude Code CLI（tests/PamisWang.ClaudeAgentSdk.FakeCli）在測試輸出目錄裡的執行檔。
/// 行為由環境變數 FAKE_CLAUDE_MODE 決定，見它的 Program.cs。
/// </summary>
internal static class FakeClaude
{
    public static string Path { get; } = System.IO.Path.Combine(
        AppContext.BaseDirectory,
        OperatingSystem.IsWindows() ? "PamisWang.ClaudeAgentSdk.FakeCli.exe" : "PamisWang.ClaudeAgentSdk.FakeCli");
}
