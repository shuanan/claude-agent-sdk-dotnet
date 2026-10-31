namespace PamisWang.ClaudeAgentSdk.Mcp;

/// <summary>
/// 狀態回應裡的工具提示（CLI 的欄位名稱 <c>readOnly</c>、<c>destructive</c>、<c>openWorld</c>）。對應 Python 狀態用的 <c>McpToolAnnotations</c>；
/// 跟定義工具用的 <see cref="McpToolAnnotations"/> 是不同的東西。
/// </summary>
/// <param name="ReadOnly">只讀取、不修改。</param>
/// <param name="Destructive">可能有破壞性。</param>
/// <param name="OpenWorld">會接觸外部世界。</param>
public sealed record McpServerToolAnnotations(bool? ReadOnly, bool? Destructive, bool? OpenWorld);
