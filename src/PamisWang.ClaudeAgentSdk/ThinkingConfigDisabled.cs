namespace PamisWang.ClaudeAgentSdk;

/// <summary>不思考（<c>--thinking disabled</c>）。對應 Python 的 <c>{"type": "disabled"}</c>。</summary>
public sealed record ThinkingConfigDisabled() : ThinkingConfig;
