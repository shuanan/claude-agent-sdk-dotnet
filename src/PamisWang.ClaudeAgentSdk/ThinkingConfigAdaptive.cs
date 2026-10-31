namespace PamisWang.ClaudeAgentSdk;

/// <summary>由模型自己決定要不要思考、想多久（<c>--thinking adaptive</c>）。對應 Python 的 <c>{"type": "adaptive"}</c>。</summary>
/// <param name="Display">
/// 思考內容怎麼回傳（<c>--thinking-display</c>）：<c>summarized</c> 回傳摘要，<c>omitted</c> 只回傳簽章；<see langword="null"/> 用模型的預設。
/// </param>
public sealed record ThinkingConfigAdaptive(string? Display = null) : ThinkingConfig;
