namespace PamisWang.ClaudeAgentSdk;

/// <summary>
/// 結構化輸出的格式，目前只有 <see cref="OutputFormatJsonSchema"/>。對應 Python SDK 的 <c>output_format</c>；
/// 結果放在 <see cref="Messages.ResultMessage.StructuredOutput"/>。
/// </summary>
public abstract record OutputFormat
{
    private protected OutputFormat()
    {
    }
}
