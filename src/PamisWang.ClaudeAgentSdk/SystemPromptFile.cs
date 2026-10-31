namespace PamisWang.ClaudeAgentSdk;

/// <summary>
/// 從檔案讀系統提示詞，整份取代 Claude Code 預設的（<c>--system-prompt-file</c>）。對應 Python 的 <c>{"type": "file"}</c>。
/// </summary>
/// <param name="Path">提示詞檔案的路徑。</param>
public sealed record SystemPromptFile(string Path) : SystemPrompt;
