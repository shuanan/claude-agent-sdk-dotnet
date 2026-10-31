using System.Text.Json.Nodes;

namespace PamisWang.ClaudeAgentSdk.Sessions;

/// <summary>
/// 指定 store 裡的一份 session 紀錄或 subagent 紀錄。主紀錄沒有 <see cref="Subpath"/>；subagent 的是
/// <c>"subagents/agent-{id}"</c> 這種跟磁碟結構一樣的路徑。對應 Python 的 <c>SessionKey</c>。
/// </summary>
/// <param name="ProjectKey">範圍；預設是 sanitize 過的工作目錄（<see cref="ClaudeSessions.ProjectKeyForDirectory"/>）。</param>
/// <param name="SessionId">session 的 UUID。</param>
/// <param name="Subpath">主紀錄不給；subagent 檔案才有。不能是空字串。</param>
public sealed record SessionKey(string ProjectKey, string SessionId, string? Subpath = null);

/// <summary>
/// 逐步維護的 session 摘要。store 在附加紀錄時用 <see cref="ClaudeSessions.FoldSessionSummary"/> 算出來、原樣存起來。
/// 對應 Python 的 <c>SessionSummaryEntry</c>。
/// </summary>
/// <param name="SessionId">session 的 UUID。</param>
/// <param name="Mtime">
/// 摘要存進 store 的時間（Unix epoch 毫秒），要跟 store 列出 session 時的修改時間用同一個時鐘；不要用紀錄裡的 ISO 時間。
/// <see cref="ClaudeSessions.FoldSessionSummary"/> 不會設它，存檔後由 store 自己蓋上。
/// </param>
/// <param name="Data">SDK 自己的摘要狀態；原樣存起來，不要解讀。</param>
public sealed record SessionSummaryEntry(string SessionId, long Mtime, JsonObject Data);
