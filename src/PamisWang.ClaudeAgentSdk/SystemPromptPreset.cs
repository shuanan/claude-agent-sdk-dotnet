namespace PamisWang.ClaudeAgentSdk;

/// <summary>
/// Claude Code 預設的系統提示詞，跟在終端機用 Claude Code 時一樣。對應 Python 的 <c>{"type": "preset", "preset": "claude_code"}</c>。
/// </summary>
/// <param name="Append">接在預設提示詞後面的一段（<c>--append-system-prompt</c>）；<see langword="null"/> 表示不加。</param>
public sealed record SystemPromptPreset(string? Append = null) : SystemPrompt;
