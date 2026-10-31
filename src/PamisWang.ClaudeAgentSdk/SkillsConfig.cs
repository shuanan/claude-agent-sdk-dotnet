namespace PamisWang.ClaudeAgentSdk;

/// <summary>
/// 主對話要開啟哪些 skill：<see cref="SkillsAll"/> 或 <see cref="SkillsList"/>。對應 Python SDK 的 <c>skills</c>。
/// 設了就不必自己把 <c>Skill</c> 放進 <see cref="ClaudeAgentOptions.AllowedTools"/>，也不必設 <see cref="ClaudeAgentOptions.SettingSources"/>：
/// SDK 會補上（<see cref="ClaudeAgentOptions.SettingSources"/> 沒設時預設 <c>user,project</c>，CLI 才找得到裝好的 skill）。
/// 這是給模型看的清單過濾，不是 sandbox：沒列出的 skill 不出現在清單裡、Skill 工具也會拒絕，但檔案還在磁碟上，Read、Bash 讀得到；
/// 不要把機密放在 skill 檔案裡。
/// </summary>
public abstract record SkillsConfig
{
    private protected SkillsConfig()
    {
    }
}
