namespace PamisWang.ClaudeAgentSdk.Permissions;

/// <summary>權限回呼的結果：<see cref="PermissionResultAllow"/> 或 <see cref="PermissionResultDeny"/>。</summary>
public abstract record PermissionResult
{
    private protected PermissionResult()
    {
    }
}
