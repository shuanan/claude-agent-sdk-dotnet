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

    /// <summary>組出參數，不含執行檔本身。</summary>
    /// <exception cref="ArgumentException">
    /// 選項互相衝突；或 Windows 上 <see cref="ClaudeAgentOptions.Resume"/>、<see cref="ClaudeAgentOptions.SessionId"/> 含有 cmd.exe 的特殊字元。
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

        if (options.AllowedTools.Count > 0)
        {
            args.AddRange(["--allowedTools", string.Join(',', options.AllowedTools)]);
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

        // SDK 內的 MCP server 之後也從 McpServers 進來：Python 送出時拿掉 instance，只留 type 與 name，
        // 由 McpServerConfig.WriteTo 決定寫什麼，這裡不用改
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

        if (options.StrictMcpConfig)
        {
            args.Add("--strict-mcp-config");
        }

        if (options.ForkSession)
        {
            args.Add("--fork-session");
        }

        // 等號的寫法：空的清單也要帶，代表一個設定檔都不載入
        if (options.SettingSources is not null)
        {
            args.Add($"--setting-sources={string.Join(',', options.SettingSources)}");
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
                    // 解析失敗時跟 Python 一樣當成檔案路徑
                    settings = ReadSettingsFile(text) ?? settings;
                }
            }
            else
            {
                // 檔案不存在時 Python 只記一筆警告，用空的設定繼續
                settings = ReadSettingsFile(text) ?? settings;
            }
        }

        settings["sandbox"] = JsonNode.Parse(JsonWriting.ToCompactString(sandbox.WriteTo));
        return settings.ToJsonString();
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
