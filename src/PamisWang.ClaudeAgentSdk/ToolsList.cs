namespace PamisWang.ClaudeAgentSdk;

/// <summary>
/// 只開放這些工具（<c>--tools a,b</c>）。空的清單代表一個工具都不給（<c>--tools ""</c>）。對應 Python 直接給清單。
/// </summary>
/// <param name="Names">工具名稱，例如 <c>Read</c>、<c>Bash</c>。</param>
public sealed record ToolsList(IReadOnlyList<string> Names) : ToolsConfig;
