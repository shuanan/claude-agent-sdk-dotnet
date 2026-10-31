using System.Collections.ObjectModel;
using PamisWang.ClaudeAgentSdk.Permissions;

namespace PamisWang.ClaudeAgentSdk;

/// <summary>
/// 啟動 Claude Code 的選項。對應 Python SDK 的 <c>ClaudeAgentOptions</c>，目前只有第一版要用到的部分。
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
    /// 模型，例如 <c>sonnet</c>、<c>haiku</c> 或完整的模型 ID。沒指定就用 Claude Code 的預設。
    /// 連線後可以用 <see cref="ClaudeAgentClient.SetModelAsync"/> 換。
    /// </summary>
    public string? Model { get; init; }

    /// <summary>
    /// 思考的深度（<c>--effort</c>）：<c>low</c>、<c>medium</c>、<c>high</c>、<c>xhigh</c> 或 <c>max</c>。
    /// 各模型支援哪些，見 <see cref="ClaudeAgentClient.ServerInfo"/> 裡 <c>models</c> 的 <c>supportedEffortLevels</c>。沒指定就用模型的預設。
    /// 連線後可以用 <see cref="ClaudeAgentClient.ApplyFlagSettingsAsync"/> 的 <c>effortLevel</c> 換。
    /// </summary>
    public string? Effort { get; init; }

    /// <summary>要接續的 session ID（<c>--resume</c>）。</summary>
    public string? Resume { get; init; }

    /// <summary>是否接收部分訊息（<c>--include-partial-messages</c>）：模型輸出時逐段送來的 <c>stream_event</c>。</summary>
    public bool IncludePartialMessages { get; init; }

    /// <summary>停用的工具名稱（<c>--disallowedTools</c>）。</summary>
    public IReadOnlyList<string> DisallowedTools { get; init; } = [];

    /// <summary>
    /// 工具要執行前、需要使用者批准時呼叫。設定之後 CLI 改用 control 協定詢問（<c>--permission-prompt-tool stdio</c>）。
    /// 等待期間 CLI 撤回詢問（例如這一輪被中斷），<see cref="CancellationToken"/> 會被取消，這時回傳什麼都不會送出。
    /// </summary>
    public Func<ToolPermissionRequest, CancellationToken, Task<PermissionResult>>? CanUseTool { get; init; }

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
}
