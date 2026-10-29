namespace PamisWang.ClaudeAgentSdk.Messages;

/// <summary>這個版本還不認得的內容區塊，例如圖片；內容從 <see cref="ContentBlock.Raw"/> 讀。</summary>
/// <param name="Type">區塊的 <c>type</c>。</param>
public sealed record UnknownContentBlock(string Type) : ContentBlock;
