namespace PamisWang.ClaudeAgentSdk;

/// <summary>
/// 可用工具的基本組合（<c>--tools</c>）：<see cref="ToolsList"/> 或 <see cref="ToolsPreset"/>。
/// 對應 Python SDK 的 <c>tools</c>。
/// </summary>
public abstract record ToolsConfig
{
    private protected ToolsConfig()
    {
    }
}
