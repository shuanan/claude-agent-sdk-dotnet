namespace PamisWang.ClaudeAgentSdk;

/// <summary>
/// 只開啟列出的 skill：每個名稱在 <c>--allowedTools</c> 補上 <c>Skill(名稱)</c>，initialize 也送出 <c>skills</c> 讓 CLI 過濾清單。
/// 空的清單代表一個 skill 都不給。對應 Python 的 <c>skills=[...]</c>。
/// 名稱要完全相同：SKILL.md 的 <c>name</c>／資料夾名稱，plugin 的 skill 寫成 <c>plugin:skill</c>；
/// 萬用字元、括號、逗號、控制字元、前後空白、<c>/</c> 開頭這類名稱跟 Python 一樣在連線時丟出 <see cref="ArgumentException"/>。
/// </summary>
/// <param name="Names">skill 名稱。</param>
public sealed record SkillsList(IReadOnlyList<string> Names) : SkillsConfig;
