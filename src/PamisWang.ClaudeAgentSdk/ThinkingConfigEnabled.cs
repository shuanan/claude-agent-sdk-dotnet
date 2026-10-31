namespace PamisWang.ClaudeAgentSdk;

/// <summary>固定的思考預算（<c>--max-thinking-tokens</c>）。對應 Python 的 <c>{"type": "enabled", "budget_tokens": …}</c>。</summary>
/// <param name="BudgetTokens">思考最多用多少 token。</param>
/// <param name="Display">
/// 思考內容怎麼回傳（<c>--thinking-display</c>）：<c>summarized</c> 回傳摘要，<c>omitted</c> 只回傳簽章；<see langword="null"/> 用模型的預設。
/// </param>
public sealed record ThinkingConfigEnabled(int BudgetTokens, string? Display = null) : ThinkingConfig;
