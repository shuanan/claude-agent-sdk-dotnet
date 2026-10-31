namespace PamisWang.ClaudeAgentSdk;

/// <summary>
/// 自訂的系統提示詞，整份取代 Claude Code 預設的（<c>--system-prompt</c>）。對應 Python 直接給字串，或 <c>{"type": "custom"}</c>。
/// 提示詞放在命令列上，Windows 的命令列有長度上限，很長的提示詞改用 <see cref="SystemPromptFile"/>。
/// </summary>
/// <param name="Prompt">系統提示詞全文。</param>
public sealed record SystemPromptCustom(string Prompt) : SystemPrompt
{
    /// <summary>
    /// 跟 <see cref="SystemPromptPreset.Snapshot"/> 一樣，套用在 <see cref="Prompt"/> 上（initialize 的 <c>systemPromptSnapshot</c>）。
    /// <see langword="null"/> 時不送，跟 Python 直接給字串一樣。
    /// </summary>
    public bool? Snapshot { get; init; }
}
