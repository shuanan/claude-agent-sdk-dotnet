namespace PamisWang.ClaudeAgentSdk;

/// <summary>
/// API 端的工作預算（token，<c>--task-budget</c>）：模型知道還剩多少，會調整工具的使用並在用完前收尾。
/// CLI 以 <c>output_config.task_budget</c> 與 <c>task-budgets-2026-03-13</c> beta 送出。對應 Python 的 <c>TaskBudget</c>。
/// </summary>
/// <param name="Total">預算的總 token 數。</param>
public sealed record TaskBudget(long Total);
