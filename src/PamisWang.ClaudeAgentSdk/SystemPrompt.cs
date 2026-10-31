namespace PamisWang.ClaudeAgentSdk;

/// <summary>
/// 系統提示詞：<see cref="SystemPromptPreset"/>、<see cref="SystemPromptCustom"/> 或 <see cref="SystemPromptFile"/>。
/// 對應 Python SDK 的 <c>system_prompt</c>。
/// </summary>
public abstract record SystemPrompt
{
    private protected SystemPrompt()
    {
    }
}
