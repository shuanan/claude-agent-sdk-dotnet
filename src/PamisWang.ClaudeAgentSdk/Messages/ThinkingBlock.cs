namespace PamisWang.ClaudeAgentSdk.Messages;

/// <summary>模型的思考過程。</summary>
/// <param name="Thinking">思考內容；有些設定下 CLI 不提供內容，會是空字串。</param>
/// <param name="Signature">API 用來驗證思考內容的簽章。</param>
public sealed record ThinkingBlock(string Thinking, string Signature) : ContentBlock;
