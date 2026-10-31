using System.Collections.ObjectModel;
using PamisWang.ClaudeAgentSdk.Hooks;
using PamisWang.ClaudeAgentSdk.Mcp;
using PamisWang.ClaudeAgentSdk.Permissions;

namespace PamisWang.ClaudeAgentSdk;

/// <summary>
/// 啟動 Claude Code 的選項。對應 Python SDK 的 <c>ClaudeAgentOptions</c>；session store 這些還沒有。
/// 各選項轉成 CLI 參數的條件與順序跟 Python 的 <c>_build_command</c> 一樣。
/// </summary>
public sealed class ClaudeAgentOptions
{
    /// <summary>
    /// <c>claude</c> 執行檔的路徑。沒指定就從 PATH 與常見的安裝位置找。
    /// Windows 只執行原生的 <c>claude.exe</c>，不執行 npm 的 <c>claude.cmd</c> 這類批次檔。
    /// </summary>
    public string? CliPath { get; init; }

    /// <summary>
    /// 工作目錄，也決定 session 存在 <c>~/.claude/projects</c> 底下的哪個資料夾。沒指定就沿用目前程序的。
    /// </summary>
    public string? Cwd { get; init; }

    /// <summary>
    /// 系統提示詞。沒指定時跟 Python SDK 一樣傳空的系統提示詞，不用 Claude Code 預設的；
    /// 要跟在終端機用 Claude Code 時一樣，指定 <see cref="SystemPromptPreset"/>。
    /// </summary>
    public SystemPrompt? SystemPrompt { get; init; }

    /// <summary>
    /// 可用工具的基本組合（<c>--tools</c>）：<see cref="ToolsList"/> 只開放列出的工具（空的清單就一個都不給），
    /// <see cref="ToolsPreset"/> 用 Claude Code 預設的整組。沒指定就不帶參數。
    /// </summary>
    public ToolsConfig? Tools { get; init; }

    /// <summary>不用詢問就允許的工具或權限規則（<c>--allowedTools</c>），例如 <c>Read</c>、<c>Bash(git:*)</c>。</summary>
    public IReadOnlyList<string> AllowedTools { get; init; } = [];

    /// <summary>最多來回幾次（<c>--max-turns</c>）。跟 Python 一樣，<c>0</c> 視同沒指定。</summary>
    public int? MaxTurns { get; init; }

    /// <summary>
    /// 費用上限（美元，<c>--max-budget-usd</c>）。數字照 <see cref="decimal"/> 原本的寫法送出，例如 <c>1.50m</c> 是 <c>1.50</c>。
    /// </summary>
    public decimal? MaxBudgetUsd { get; init; }

    /// <summary>
    /// 模型，例如 <c>sonnet</c>、<c>haiku</c> 或完整的模型 ID。沒指定就用 Claude Code 的預設。
    /// 連線後可以用 <see cref="ClaudeAgentClient.SetModelAsync"/> 換。
    /// </summary>
    public string? Model { get; init; }

    /// <summary>主要的模型無法使用時改用的模型（<c>--fallback-model</c>）。</summary>
    public string? FallbackModel { get; init; }

    /// <summary>要開啟的 API beta 功能（<c>--betas</c>），例如 <c>context-1m-2025-08-07</c>。</summary>
    public IReadOnlyList<string> Betas { get; init; } = [];

    /// <summary>
    /// 處理權限詢問的 MCP 工具名稱（<c>--permission-prompt-tool</c>）。
    /// 不能跟 <see cref="CanUseTool"/> 一起用：設了 <see cref="CanUseTool"/> 時固定是 <c>stdio</c>，兩個都設會在連線時丟出 <see cref="ArgumentException"/>。
    /// </summary>
    public string? PermissionPromptToolName { get; init; }

    /// <summary>
    /// 權限模式（<c>--permission-mode</c>）：<c>default</c>、<c>acceptEdits</c>、<c>plan</c>、<c>bypassPermissions</c>、<c>dontAsk</c> 或 <c>auto</c>。
    /// 連線後可以用 <see cref="ClaudeAgentClient.SetPermissionModeAsync"/> 換。
    /// </summary>
    public string? PermissionMode { get; init; }

    /// <summary>接續這個資料夾最近的一次對話（<c>--continue</c>）。</summary>
    public bool ContinueConversation { get; init; }

    /// <summary>要接續的 session ID（<c>--resume</c>）。</summary>
    public string? Resume { get; init; }

    /// <summary>新 session 要用的 ID（<c>--session-id</c>），必須是 UUID。跟 <see cref="Resume"/> 一樣寫成 <c>--session-id=&lt;值&gt;</c>。</summary>
    public string? SessionId { get; init; }

    /// <summary>
    /// 設定檔的路徑，或一段 JSON 字串（<c>--settings</c>）。有 <see cref="Sandbox"/> 時，這裡的設定會先讀出來、合併 <c>sandbox</c> 後再整段送出。
    /// </summary>
    public string? Settings { get; init; }

    /// <summary>bash 指令的 sandbox，合併進 <see cref="Settings"/> 的 <c>sandbox</c>。</summary>
    public SandboxSettings? Sandbox { get; init; }

    /// <summary>工作目錄以外、也讓 Claude 存取的資料夾（每個一個 <c>--add-dir</c>）。</summary>
    public IReadOnlyList<string> AddDirs { get; init; } = [];

    /// <summary>
    /// MCP server，鍵是 server 名稱（<c>--mcp-config {"mcpServers": …}</c>）。可以混用由 Claude Code 啟動或連線的 server，
    /// 與在這個程序裡跑的 <see cref="McpSdkServerConfig"/>（用 <see cref="SdkMcpServer.Create(string, IEnumerable{SdkMcpTool})"/> 建立；CLI 只拿到 <c>{"type":"sdk","name":…}</c>）。
    /// 工具的完整名稱是 <c>mcp__&lt;鍵&gt;__&lt;工具名稱&gt;</c>，例如鍵 <c>calc</c> 的 <c>add</c> 是 <c>mcp__calc__add</c>，放進 <see cref="AllowedTools"/> 就不必詢問。
    /// 不能跟 <see cref="McpConfig"/> 一起用。
    /// </summary>
    public IReadOnlyDictionary<string, McpServerConfig> McpServers { get; init; } = ReadOnlyDictionary<string, McpServerConfig>.Empty;

    /// <summary>
    /// MCP 設定檔的路徑，或一段 JSON 字串，原樣傳給 <c>--mcp-config</c>。對應 Python 的 <c>mcp_servers</c> 給字串或路徑。
    /// 不能跟 <see cref="McpServers"/> 一起用。
    /// </summary>
    public string? McpConfig { get; init; }

    /// <summary>只用 <see cref="McpServers"/>／<see cref="McpConfig"/> 給的 MCP server，忽略其他設定檔裡的（<c>--strict-mcp-config</c>）。</summary>
    public bool StrictMcpConfig { get; init; }

    /// <summary>接續 session 時另開一個新的 session ID，不寫進原本的（<c>--fork-session</c>）。</summary>
    public bool ForkSession { get; init; }

    /// <summary>
    /// 要載入哪些設定檔（<c>--setting-sources</c>）：<c>user</c>、<c>project</c>、<c>local</c>。
    /// <see langword="null"/> 時不帶參數，由 Claude Code 決定；空的清單代表一個都不載入。
    /// </summary>
    public IReadOnlyList<string>? SettingSources { get; init; }

    /// <summary>要載入的本機 plugin（每個一個 <c>--plugin-dir</c>）。</summary>
    public IReadOnlyList<SdkPluginConfig> Plugins { get; init; } = [];

    /// <summary>
    /// 其他 CLI 參數，鍵是不含 <c>--</c> 的旗標名稱。值是 <see langword="null"/> 時只帶旗標；
    /// <c>-</c> 開頭的值寫成 <c>--旗標=值</c>，避免被當成另一個旗標；其他寫成 <c>--旗標 值</c>。
    /// </summary>
    public IReadOnlyDictionary<string, string?> ExtraArgs { get; init; } = ReadOnlyDictionary<string, string?>.Empty;

    /// <summary>延伸思考的設定（<c>--thinking</c>、<c>--max-thinking-tokens</c>、<c>--thinking-display</c>），優先於 <see cref="MaxThinkingTokens"/>。</summary>
    public ThinkingConfig? Thinking { get; init; }

    /// <summary>思考最多用多少 token（<c>--max-thinking-tokens</c>）。已經不建議使用，改用 <see cref="Thinking"/>；有 <see cref="Thinking"/> 時忽略。</summary>
    public int? MaxThinkingTokens { get; init; }

    /// <summary>
    /// 思考的深度（<c>--effort</c>）：<c>low</c>、<c>medium</c>、<c>high</c>、<c>xhigh</c> 或 <c>max</c>。
    /// 各模型支援哪些，見 <see cref="ClaudeAgentClient.ServerInfo"/> 裡 <c>models</c> 的 <c>supportedEffortLevels</c>。沒指定就用模型的預設。
    /// 連線後可以用 <see cref="ClaudeAgentClient.ApplyFlagSettingsAsync"/> 的 <c>effortLevel</c> 換。
    /// </summary>
    public string? Effort { get; init; }

    /// <summary>結構化輸出（<c>--json-schema</c>），結果在 <see cref="Messages.ResultMessage.StructuredOutput"/>。</summary>
    public OutputFormat? OutputFormat { get; init; }

    /// <summary>
    /// 自訂的 subagent，鍵是 agent 名稱。不是命令列參數，在初始化握手的 <c>agents</c> 裡送出；空的就不送。
    /// </summary>
    public IReadOnlyDictionary<string, AgentDefinition>? Agents { get; init; }

    /// <summary>是否接收部分訊息（<c>--include-partial-messages</c>）：模型輸出時逐段送來的 <c>stream_event</c>。</summary>
    public bool IncludePartialMessages { get; init; }

    /// <summary>停用的工具名稱（<c>--disallowedTools</c>）。</summary>
    public IReadOnlyList<string> DisallowedTools { get; init; } = [];

    /// <summary>
    /// 工具要執行前、需要使用者批准時呼叫。設定之後 CLI 改用 control 協定詢問（<c>--permission-prompt-tool stdio</c>）。
    /// 等待期間 CLI 撤回詢問（例如這一輪被中斷），<see cref="CancellationToken"/> 會被取消，這時回傳什麼都不會送出。
    /// </summary>
    public Func<ToolPermissionRequest, CancellationToken, Task<PermissionResult>>? CanUseTool { get; init; }

    /// <summary>
    /// hook 回呼，鍵是事件名稱（<see cref="HookEvent"/>）。對應 Python 的 <c>hooks</c>：在 initialize 時登記給 CLI，
    /// 回呼依登記順序編號為 <c>hook_0</c>、<c>hook_1</c>…；CLI 觸發時以 <c>hook_callback</c> 呼叫。
    /// 同一個事件的多個 matcher 會被 CLI <b>同時</b>觸發，回呼也就可能同時在跑，彼此不要依賴先後。
    /// 參考 https://docs.anthropic.com/en/docs/claude-code/hooks。
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<HookMatcher>> Hooks { get; init; } =
        ReadOnlyDictionary<string, IReadOnlyList<HookMatcher>>.Empty;

    /// <summary>額外的環境變數，蓋過繼承自目前程序的同名變數。</summary>
    public IReadOnlyDictionary<string, string> Env { get; init; } = ReadOnlyDictionary<string, string>.Empty;

    /// <summary>
    /// 每收到一行 CLI 的 stderr 就呼叫一次，空行不算。沒指定時不導向 stderr，CLI 直接寫到目前程序的 stderr。
    /// </summary>
    public Action<string>? Stderr { get; init; }

    /// <summary>
    /// 單一則訊息的大小上限（位元組）。超過就丟出 <see cref="CliJsonDecodeException"/>，避免異常的輸出吃光記憶體。
    /// </summary>
    public int MaxBufferSize { get; init; } = 1024 * 1024;

    /// <summary>
    /// 檢查互相衝突的選項。對應 Python 的 <c>_configure_can_use_tool</c>：<see cref="CanUseTool"/> 與
    /// <see cref="PermissionPromptToolName"/> 只能擇一。
    /// </summary>
    /// <exception cref="ArgumentException">選項互相衝突。</exception>
    internal void Validate()
    {
        if (CanUseTool is not null && !string.IsNullOrEmpty(PermissionPromptToolName))
        {
            throw new ArgumentException(
                "CanUseTool 不能跟 PermissionPromptToolName 一起用，請擇一。",
                nameof(PermissionPromptToolName));
        }

        if (McpServers.Count > 0 && !string.IsNullOrEmpty(McpConfig))
        {
            throw new ArgumentException("McpServers 不能跟 McpConfig 一起用，請擇一。", nameof(McpConfig));
        }
    }
}
