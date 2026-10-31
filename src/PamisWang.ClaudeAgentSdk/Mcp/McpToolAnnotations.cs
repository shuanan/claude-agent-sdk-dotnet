namespace PamisWang.ClaudeAgentSdk.Mcp;

/// <summary>
/// 工具行為的提示，出現在 <c>tools/list</c> 的 <c>annotations</c>。對應 Python SDK 的 <c>ToolAnnotations</c>；
/// <see langword="null"/> 的欄位不送。
/// </summary>
public sealed record McpToolAnnotations
{
    /// <summary>給人看的工具名稱（<c>title</c>）。</summary>
    public string? Title { get; init; }

    /// <summary>工具不會改變環境（<c>readOnlyHint</c>）。</summary>
    public bool? ReadOnlyHint { get; init; }

    /// <summary>工具可能做出破壞性的修改（<c>destructiveHint</c>）。</summary>
    public bool? DestructiveHint { get; init; }

    /// <summary>用同樣的參數重複呼叫不會有額外的效果（<c>idempotentHint</c>）。</summary>
    public bool? IdempotentHint { get; init; }

    /// <summary>工具會跟外部世界互動（<c>openWorldHint</c>）。</summary>
    public bool? OpenWorldHint { get; init; }

    /// <summary>
    /// 不是 MCP 的提示，是 Claude Code 的：結果在這個字元數以內就直接放進對話，超過才存成檔案、只給預覽。
    /// 跟 Python 一樣放在工具的 <c>_meta["anthropic/maxResultSizeChars"]</c> 送出，不出現在 <c>annotations</c>。
    /// </summary>
    public int? MaxResultSizeChars { get; init; }
}
