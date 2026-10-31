using System.Buffers;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PamisWang.ClaudeAgentSdk.Protocol;

namespace PamisWang.ClaudeAgentSdk.Transport;

/// <summary>
/// 把選項轉成 CLI 參數。對應 Python SDK 的 <c>_build_command</c>，參數順序與條件也一樣，方便對照協定樣本裡的 argv。
/// JSON 一律寫成緊湊的一行；Python 的 <c>json.dumps</c> 會在逗號與冒號後面加空白，內容相同。
/// </summary>
internal static class CliArguments
{
    // cmd.exe 會解讀的字元
    private const string CmdExeMetacharacters = "&|<>^%!\"\r\n";
    private static readonly SearchValues<char> CmdExeMetacharacterValues = SearchValues.Create(CmdExeMetacharacters);

    // skill 名稱不能有的字元，跟 Python 的 _SKILL_NAME_INVALID_CHARS 一樣：括號與逗號是 --allowedTools 的分隔字元，
    // 控制字元（C0、DEL、C1）不會出現在資料夾名稱裡；U+FEFF 是因為 CLI 把它當空白去掉
    private static readonly SearchValues<char> SkillNameInvalidCharValues = SearchValues.Create(
        string.Concat(
            Enumerable.Range(0, 0x20).Concat(Enumerable.Range(0x7F, 0x21)).Append(0xFEFF).Select(code => (char)code)
                .Concat("(),")));

    /// <summary>組出參數，不含執行檔本身。</summary>
    /// <exception cref="ArgumentException">
    /// 選項互相衝突；<see cref="ClaudeAgentOptions.Skills"/> 的名稱不合規則；或 Windows 上 <see cref="ClaudeAgentOptions.Resume"/>、
    /// <see cref="ClaudeAgentOptions.SessionId"/>、<see cref="ClaudeAgentOptions.ResumeSessionAt"/>、<see cref="ClaudeAgentOptions.ResumeDropsTurn"/> 含有 cmd.exe 的特殊字元。
    /// </exception>
    public static IReadOnlyList<string> Build(ClaudeAgentOptions options, bool isWindows)
    {
        options.Validate();
        List<string> args = ["--output-format", "stream-json", "--verbose"];

        switch (options.SystemPrompt)
        {
            case null:
                // 沒指定系統提示時傳空字串，不用 Claude Code 預設的系統提示；Python 與 TS SDK 都是這樣
                args.AddRange(["--system-prompt", ""]);
                break;
            case SystemPromptCustom custom:
                args.AddRange(["--system-prompt", custom.Prompt]);
                break;
            case SystemPromptFile file:
                args.AddRange(["--system-prompt-file", file.Path]);
                break;
            case SystemPromptPreset { Append: { } append }:
                args.AddRange(["--append-system-prompt", append]);
                break;
            default:
                // 只用預設的系統提示：不帶參數，CLI 就用自己的
                break;
        }

        switch (options.Tools)
        {
            case ToolsList list:
                // 空的清單也要帶：--tools "" 代表一個工具都不給
                args.AddRange(["--tools", string.Join(',', list.Names)]);
                break;
            case ToolsPreset:
                args.AddRange(["--tools", "default"]);
                break;
            default:
                break;
        }

        var (allowedTools, settingSources) = ApplySkillsDefaults(options);
        if (allowedTools.Count > 0)
        {
            args.AddRange(["--allowedTools", string.Join(',', allowedTools)]);
        }

        // Python 判斷的是真假值，0 跟沒指定一樣
        if (options.MaxTurns is { } maxTurns and not 0)
        {
            args.AddRange(["--max-turns", maxTurns.ToString(CultureInfo.InvariantCulture)]);
        }

        if (options.MaxBudgetUsd is { } maxBudgetUsd)
        {
            args.AddRange(["--max-budget-usd", maxBudgetUsd.ToString(CultureInfo.InvariantCulture)]);
        }

        if (options.DisallowedTools.Count > 0)
        {
            args.AddRange(["--disallowedTools", string.Join(',', options.DisallowedTools)]);
        }

        if (options.TaskBudget is { } taskBudget)
        {
            args.AddRange(["--task-budget", taskBudget.Total.ToString(CultureInfo.InvariantCulture)]);
        }

        if (!string.IsNullOrEmpty(options.Model))
        {
            args.AddRange(["--model", options.Model]);
        }

        if (!string.IsNullOrEmpty(options.FallbackModel))
        {
            args.AddRange(["--fallback-model", options.FallbackModel]);
        }

        if (options.Betas.Count > 0)
        {
            args.AddRange(["--betas", string.Join(',', options.Betas)]);
        }

        // Python 在 _configure_can_use_tool 把 permission_prompt_tool_name 換成 stdio；兩者衝突已經在 Validate 擋掉
        var permissionPromptTool = options.CanUseTool is not null ? "stdio" : options.PermissionPromptToolName;
        if (!string.IsNullOrEmpty(permissionPromptTool))
        {
            args.AddRange(["--permission-prompt-tool", permissionPromptTool]);
        }

        if (!string.IsNullOrEmpty(options.PermissionMode))
        {
            args.AddRange(["--permission-mode", options.PermissionMode]);
        }

        if (options.ContinueConversation)
        {
            args.Add("--continue");
        }

        // 寫成 --旗標=<值>：--resume 的值可以省略，分成兩個參數時，- 開頭的值會被當成另一個旗標
        if (!string.IsNullOrEmpty(options.Resume))
        {
            if (isWindows)
            {
                RejectWindowsCmdMetacharacters(nameof(options.Resume), options.Resume);
            }

            args.Add($"--resume={options.Resume}");
        }

        if (!string.IsNullOrEmpty(options.SessionId))
        {
            if (isWindows)
            {
                RejectWindowsCmdMetacharacters(nameof(options.SessionId), options.SessionId);
            }

            args.Add($"--session-id={options.SessionId}");
        }

        if (BuildSettingsValue(options) is { Length: > 0 } settings)
        {
            args.AddRange(["--settings", settings]);
        }

        foreach (var directory in options.AddDirs)
        {
            args.AddRange(["--add-dir", directory]);
        }

        // SDK MCP server 跟外部的混在一起送：跟 Python 一樣拿掉 instance，只留 type 與 name（McpSdkServerConfig.WriteTo）
        if (options.McpServers.Count > 0)
        {
            args.AddRange(["--mcp-config", JsonWriting.ToCompactString(writer =>
            {
                writer.WriteStartObject();
                writer.WriteStartObject("mcpServers");
                foreach (var (name, config) in options.McpServers)
                {
                    writer.WritePropertyName(name);
                    config.WriteTo(writer);
                }

                writer.WriteEndObject();
                writer.WriteEndObject();
            })]);
        }
        else if (!string.IsNullOrEmpty(options.McpConfig))
        {
            args.AddRange(["--mcp-config", options.McpConfig]);
        }

        if (options.IncludePartialMessages)
        {
            args.Add("--include-partial-messages");
        }

        if (options.IncludeHookEvents)
        {
            args.Add("--include-hook-events");
        }

        if (options.StrictMcpConfig)
        {
            args.Add("--strict-mcp-config");
        }

        if (options.ForkSession)
        {
            args.Add("--fork-session");
        }

        // 跟 --resume 一樣寫成等號的形式，值永遠不會被當成另一個旗標
        if (!string.IsNullOrEmpty(options.ResumeSessionAt))
        {
            if (isWindows)
            {
                RejectWindowsCmdMetacharacters(nameof(options.ResumeSessionAt), options.ResumeSessionAt);
            }

            args.Add($"--resume-session-at={options.ResumeSessionAt}");
        }

        // 判斷 null 而不是空字串：空字串也送，讓 CLI 當成格式錯誤拒絕，不要默默拿掉呼叫端以為有的檢查
        if (options.ResumeDropsTurn is not null)
        {
            if (isWindows)
            {
                RejectWindowsCmdMetacharacters(nameof(options.ResumeDropsTurn), options.ResumeDropsTurn);
            }

            args.Add($"--resume-drops-turn={options.ResumeDropsTurn}");
        }

        // 等號的寫法：空的清單也要帶，代表一個設定檔都不載入
        if (settingSources is not null)
        {
            args.Add($"--setting-sources={string.Join(',', settingSources)}");
        }

        foreach (var plugin in options.Plugins)
        {
            args.AddRange(["--plugin-dir", plugin.Path]);
        }

        foreach (var (flag, value) in options.ExtraArgs)
        {
            if (value is null)
            {
                args.Add($"--{flag}");
            }
            else if (value.StartsWith('-'))
            {
                // 跟 --resume 一樣：分成兩個參數時，- 開頭的值可能被當成另一個旗標
                args.Add($"--{flag}={value}");
            }
            else
            {
                args.AddRange([$"--{flag}", value]);
            }
        }

        switch (options.Thinking)
        {
            case ThinkingConfigAdaptive adaptive:
                args.AddRange(["--thinking", "adaptive"]);
                AddThinkingDisplay(args, adaptive.Display);
                break;
            case ThinkingConfigEnabled enabled:
                args.AddRange(["--max-thinking-tokens", enabled.BudgetTokens.ToString(CultureInfo.InvariantCulture)]);
                AddThinkingDisplay(args, enabled.Display);
                break;
            case ThinkingConfigDisabled:
                args.AddRange(["--thinking", "disabled"]);
                break;
            case null when options.MaxThinkingTokens is { } maxThinkingTokens:
                args.AddRange(["--max-thinking-tokens", maxThinkingTokens.ToString(CultureInfo.InvariantCulture)]);
                break;
            default:
                break;
        }

        if (options.Effort is not null)
        {
            args.AddRange(["--effort", options.Effort]);
        }

        if (options.OutputFormat is OutputFormatJsonSchema { Schema.ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) } jsonSchema)
        {
            args.AddRange(["--json-schema", JsonWriting.ToCompactString(jsonSchema.Schema.WriteTo)]);
        }

        args.AddRange(["--input-format", "stream-json"]);
        return args;
    }

    /// <summary>
    /// <c>--settings</c> 的值。對應 Python 的 <c>_build_settings_value</c>：
    /// 沒有 sandbox 時 <see cref="ClaudeAgentOptions.Settings"/> 原樣傳；有 sandbox 時先把設定讀成物件（JSON 字串或檔案），
    /// 放進 <c>sandbox</c> 後整段寫成 JSON。兩個都沒有時是 <see langword="null"/>。
    /// </summary>
    private static string? BuildSettingsValue(ClaudeAgentOptions options)
    {
        if (options.Sandbox is not { } sandbox)
        {
            return options.Settings;
        }

        var settings = new JsonObject();
        if (options.Settings is { } value)
        {
            var text = value.Trim();
            if (text.StartsWith('{') && text.EndsWith('}'))
            {
                try
                {
                    settings = ParseSettingsObject(text);
                }
                catch (JsonException)
                {
                    // 解析失敗時跟 Python 一樣記一筆警告，當成檔案路徑
                    options.ReportWarning($"Failed to parse settings as JSON, treating as file path: {text}");
                    settings = ReadSettingsFile(text) ?? settings;
                }
            }
            else if (ReadSettingsFile(text) is { } fromFile)
            {
                settings = fromFile;
            }
            else
            {
                // 檔案不存在時 Python 只記一筆警告，用空的設定繼續
                options.ReportWarning($"Settings file not found: {text}");
            }
        }

        settings["sandbox"] = JsonNode.Parse(JsonWriting.ToCompactString(sandbox.WriteTo));
        return settings.ToJsonString();
    }

    /// <summary>
    /// 有 <see cref="ClaudeAgentOptions.Skills"/> 時實際送出的 allowed tools 與設定檔來源。對應 Python 的 <c>_apply_skills_defaults</c>：
    /// <see cref="SkillsAll"/> 補上 <c>Skill</c>，<see cref="SkillsList"/> 每個名稱補上 <c>Skill(名稱)</c>（已經有的不重複）；
    /// 兩者都在沒設 <see cref="ClaudeAgentOptions.SettingSources"/> 時改成 <c>user,project</c>。不改原本的選項。
    /// </summary>
    /// <exception cref="ArgumentException">skill 名稱無法安全地放進 <c>Skill(名稱)</c> 規則。</exception>
    internal static (List<string> AllowedTools, IReadOnlyList<string>? SettingSources) ApplySkillsDefaults(ClaudeAgentOptions options)
    {
        List<string> allowedTools = [.. options.AllowedTools];
        var settingSources = options.SettingSources;
        switch (options.Skills)
        {
            case null:
                return (allowedTools, settingSources);
            case SkillsAll:
                if (!allowedTools.Contains("Skill"))
                {
                    allowedTools.Add("Skill");
                }

                break;
            case SkillsList list:
                foreach (var name in list.Names)
                {
                    ValidateSkillName(name);
                    var pattern = $"Skill({name})";
                    if (!allowedTools.Contains(pattern))
                    {
                        allowedTools.Add(pattern);
                    }
                }

                break;
            default:
                break;
        }

        return (allowedTools, settingSources ?? ["user", "project"]);
    }

    /// <summary>
    /// 拒絕放不進 <c>Skill(名稱)</c> 規則的 skill 名稱，對應 Python 的 <c>_validate_skill_name</c>，檢查的順序也一樣。
    /// <c>--allowedTools</c> 在括號外以逗號與空白切開規則，而且不認跳脫字元，含分隔字元的名稱沒辦法可靠地傳過去；
    /// 切得開、但永遠對不到 skill 的名稱也拒絕，免得規則默默什麼都沒開放。
    /// </summary>
    /// <exception cref="ArgumentException">名稱不合規則。</exception>
    internal static void ValidateSkillName(string name)
    {
        if (name is null)
        {
            throw new ArgumentException("skill 名稱不能是 null。", nameof(name));
        }

        if (name.Trim().Length == 0)
        {
            throw new ArgumentException("skill 名稱不能是空的。", nameof(name));
        }

        if (HasUnpairedSurrogate(name))
        {
            throw new ArgumentException($"skill 名稱 {name} 不合法：含有不成對的 surrogate，永遠對不到 CLI 找到的 skill。", nameof(name));
        }

        if (name != name.Trim())
        {
            throw new ArgumentException($"skill 名稱 '{name}' 不合法：前後的空白永遠對不到，Skill 工具會把呼叫的名稱去掉前後空白。", nameof(name));
        }

        if (name.AsSpan().ContainsAny(SkillNameInvalidCharValues))
        {
            throw new ArgumentException(
                $"skill 名稱 {name} 不合法：不能有括號、逗號、控制字元與 BOM。名稱是 skill 的資料夾名稱，plugin 的 skill 寫成 plugin:skill。",
                nameof(name));
        }

        if (name == "*")
        {
            throw new ArgumentException("skill 名稱 '*' 不合法：要開啟每一個 skill 請用 SkillsAll。", nameof(name));
        }

        if (name.EndsWith(":*", StringComparison.Ordinal) || name.EndsWith(" *", StringComparison.Ordinal))
        {
            throw new ArgumentException($"skill 名稱 {name} 不合法：不能用萬用字元結尾，請逐一列出完整的名稱。", nameof(name));
        }

        if (name.StartsWith('/'))
        {
            throw new ArgumentException($"skill 名稱 {name} 不合法：不能以 / 開頭。這裡要的是 skill 的正式名稱，不是斜線指令的寫法。", nameof(name));
        }

        if (name.Contains(@"\\", StringComparison.Ordinal))
        {
            throw new ArgumentException($"skill 名稱 {name} 不合法：不能有連續的反斜線，規則解析時會被合併，變成別的 skill。", nameof(name));
        }

        if (name.EndsWith('\\'))
        {
            throw new ArgumentException($"skill 名稱 {name} 不合法：不能以單獨的反斜線結尾。", nameof(name));
        }
    }

    private static bool HasUnpairedSurrogate(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                i++;
            }
            else if (char.IsSurrogate(value[i]))
            {
                return true;
            }
        }

        return false;
    }

    private static JsonObject? ReadSettingsFile(string path) =>
        File.Exists(path) ? ParseSettingsObject(File.ReadAllText(path)) : null;

    private static JsonObject ParseSettingsObject(string json) =>
        JsonNode.Parse(json) as JsonObject
        ?? throw new ArgumentException("Settings 的內容不是 JSON 物件，無法合併 Sandbox。", nameof(json));

    private static void AddThinkingDisplay(List<string> args, string? display)
    {
        if (display is not null)
        {
            args.AddRange(["--thinking-display", display]);
        }
    }

    /// <summary>
    /// 多一層防護：已經拒絕執行批次檔，這些字元其實無害；但 resume、session ID 的值常來自外部輸入，
    /// 萬一之後又多了一層 cmd.exe，也不會變成指令。
    /// </summary>
    private static void RejectWindowsCmdMetacharacters(string optionName, string value)
    {
        if (value.AsSpan().ContainsAny(CmdExeMetacharacterValues))
        {
            throw new ArgumentException(
                $"{optionName} 的值 {value} 含有不能放在 Windows 命令列上的字元（{CmdExeMetacharacters.ReplaceLineEndings(" ")}）。",
                optionName);
        }
    }
}
