namespace PamisWang.ClaudeAgentSdk.Protocol;

/// <summary>
/// initialize 除了 <c>hooks</c> 以外要送的欄位。對應 Python 的 client 交給 <c>Query</c> 的那些參數，
/// 送出的條件與順序跟 <c>Query.initialize</c> 一樣：<c>agents</c>、<c>excludeDynamicSections</c>、<c>systemPromptSnapshot</c>、<c>skills</c>、<c>forwardSubagentText</c>。
/// </summary>
internal sealed record InitializeSettings
{
    public static InitializeSettings None { get; } = new();

    /// <summary>空的就不送。</summary>
    public IReadOnlyDictionary<string, AgentDefinition>? Agents { get; init; }

    /// <summary><see langword="null"/> 就不送。</summary>
    public bool? ExcludeDynamicSections { get; init; }

    /// <summary><see langword="null"/> 就不送。</summary>
    public bool? SystemPromptSnapshot { get; init; }

    /// <summary>只有明確的清單才送（空的也送）；全部與沒設在協定上一樣，都不送。</summary>
    public IReadOnlyList<string>? Skills { get; init; }

    /// <summary>有開才送 <see langword="true"/>。</summary>
    public bool ForwardSubagentText { get; init; }

    /// <summary>
    /// 從選項取值。<c>excludeDynamicSections</c> 只來自 <see cref="SystemPromptPreset"/>，<c>systemPromptSnapshot</c> 來自
    /// <see cref="SystemPromptPreset"/> 或 <see cref="SystemPromptCustom"/>，跟 Python 從 <c>system_prompt</c> 的 dict 取一樣。
    /// </summary>
    public static InitializeSettings FromOptions(ClaudeAgentOptions options) => new()
    {
        Agents = options.Agents,
        ExcludeDynamicSections = (options.SystemPrompt as SystemPromptPreset)?.ExcludeDynamicSections,
        SystemPromptSnapshot = options.SystemPrompt switch
        {
            SystemPromptPreset preset => preset.Snapshot,
            SystemPromptCustom custom => custom.Snapshot,
            _ => null,
        },
        Skills = (options.Skills as SkillsList)?.Names,
        ForwardSubagentText = options.ForwardSubagentText,
    };
}
