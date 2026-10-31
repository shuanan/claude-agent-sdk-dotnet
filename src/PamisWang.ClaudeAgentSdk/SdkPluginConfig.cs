namespace PamisWang.ClaudeAgentSdk;

/// <summary>
/// 要載入的本機 plugin（<c>--plugin-dir</c>）。對應 Python SDK 的 <c>SdkPluginConfig</c>，目前也只有 <c>local</c> 一種。
/// </summary>
/// <param name="Path">plugin 資料夾的路徑。</param>
public sealed record SdkPluginConfig(string Path);
