namespace PamisWang.ClaudeAgentSdk;

/// <summary>
/// 開啟找得到的每一個 skill：<c>--allowedTools</c> 補上 <c>Skill</c>。initialize 不送 <c>skills</c>（跟沒設一樣不過濾）。
/// 對應 Python 的 <c>skills="all"</c>。
/// </summary>
public sealed record SkillsAll() : SkillsConfig;
