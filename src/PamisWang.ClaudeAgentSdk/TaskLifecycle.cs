using System.Collections.Frozen;

namespace PamisWang.ClaudeAgentSdk;

/// <summary>
/// 背景工作（<c>task_started</c>、<c>task_updated</c>、<c>task_notification</c> 系統訊息）的生命週期。
/// </summary>
public static class TaskLifecycle
{
    /// <summary>
    /// 代表工作已經結束的狀態，對應 Python 的 <c>TERMINAL_TASK_STATUSES</c>：<c>completed</c>、<c>failed</c>、<c>stopped</c>、<c>killed</c>。
    /// 涵蓋兩種說法：<c>task_notification</c> 回報 <c>stopped</c>（CLI 把 killed 換過的說法），<c>task_updated</c> 的 <c>patch.status</c> 回報原本的 <c>killed</c>。
    /// 不是每個結束的工作都會送 <c>task_notification</c>，追蹤進行中的工作時，兩種訊息帶這些狀態都要當成結束。
    /// </summary>
    public static IReadOnlySet<string> TerminalStatuses { get; } =
        new[] { "completed", "failed", "stopped", "killed" }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// 結束後還會讓主對話多跑一輪的工作類型，對應 Python 的 <c>DEFERRING_TASK_TYPES</c>：<c>local_agent</c>、<c>local_workflow</c>。
    /// 背景 shell、monitor、teammate、remote agent 可能永遠不結束，不算在內，不然 stdin 會一直不關。
    /// </summary>
    internal static FrozenSet<string> DeferringTaskTypes { get; } =
        new[] { "local_agent", "local_workflow" }.ToFrozenSet(StringComparer.Ordinal);
}
