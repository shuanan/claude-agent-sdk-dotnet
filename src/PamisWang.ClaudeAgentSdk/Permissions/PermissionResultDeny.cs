namespace PamisWang.ClaudeAgentSdk.Permissions;

/// <summary>拒絕使用工具。</summary>
/// <param name="Message">告訴模型為什麼被拒絕；它會看到這段文字。</param>
/// <param name="Interrupt">是否同時中斷這一輪。</param>
public sealed record PermissionResultDeny(string Message, bool Interrupt = false) : PermissionResult;
