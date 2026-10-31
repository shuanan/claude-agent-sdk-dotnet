namespace PamisWang.ClaudeAgentSdk;

/// <summary>
/// Claude Code 預設的整組工具（<c>--tools default</c>）。對應 Python 的 <c>{"type": "preset", "preset": "claude_code"}</c>。
/// </summary>
public sealed record ToolsPreset() : ToolsConfig;
