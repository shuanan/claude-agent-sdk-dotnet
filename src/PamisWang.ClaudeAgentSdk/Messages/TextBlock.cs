namespace PamisWang.ClaudeAgentSdk.Messages;

/// <summary>一段文字。</summary>
/// <param name="Text">文字內容。</param>
public sealed record TextBlock(string Text) : ContentBlock;
