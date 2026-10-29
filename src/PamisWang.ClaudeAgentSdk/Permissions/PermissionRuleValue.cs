namespace PamisWang.ClaudeAgentSdk.Permissions;

/// <summary>一條權限規則，例如工具 <c>Bash</c> 配上內容 <c>npm test</c>。</summary>
/// <param name="ToolName">工具名稱。</param>
/// <param name="RuleContent">規則內容；<see langword="null"/> 表示整個工具。</param>
public sealed record PermissionRuleValue(string ToolName, string? RuleContent = null);
