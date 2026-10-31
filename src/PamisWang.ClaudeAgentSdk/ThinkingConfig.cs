namespace PamisWang.ClaudeAgentSdk;

/// <summary>
/// 延伸思考的設定：<see cref="ThinkingConfigAdaptive"/>、<see cref="ThinkingConfigEnabled"/> 或 <see cref="ThinkingConfigDisabled"/>。
/// 對應 Python SDK 的 <c>thinking</c>；有設定時優先於 <see cref="ClaudeAgentOptions.MaxThinkingTokens"/>。
/// </summary>
public abstract record ThinkingConfig
{
    private protected ThinkingConfig()
    {
    }
}
