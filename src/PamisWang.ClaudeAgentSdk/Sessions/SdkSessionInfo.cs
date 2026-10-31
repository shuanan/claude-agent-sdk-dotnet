namespace PamisWang.ClaudeAgentSdk.Sessions;

/// <summary>
/// <see cref="ClaudeSessions.ListSessions"/> 回傳的 session 資訊；只用檔案大小、修改時間與開頭結尾就能取得，不完整解析 JSONL。
/// 對應 Python 的 <c>SDKSessionInfo</c>。
/// </summary>
public sealed record SdkSessionInfo
{
    /// <summary>session 的 UUID。</summary>
    public required string SessionId { get; init; }

    /// <summary>顯示用的標題：自訂標題、自動產生的摘要或第一個提示。</summary>
    public required string Summary { get; init; }

    /// <summary>最後修改時間（Unix epoch 毫秒）。</summary>
    public required long LastModified { get; init; }

    /// <summary>session 檔案的位元組數。只有本機的 JSONL 有；session store 的是 <see langword="null"/>。</summary>
    public long? FileSize { get; init; }

    /// <summary>session 標題：使用者設的標題或 AI 產生的標題。</summary>
    public string? CustomTitle { get; init; }

    /// <summary>第一個有意義的使用者提示。</summary>
    public string? FirstPrompt { get; init; }

    /// <summary>session 結束時的 git 分支。</summary>
    public string? GitBranch { get; init; }

    /// <summary>session 的工作目錄。</summary>
    public string? Cwd { get; init; }

    /// <summary>使用者設的標籤。</summary>
    public string? Tag { get; init; }

    /// <summary>建立時間（Unix epoch 毫秒），取自第一筆紀錄的 ISO 時間；比檔案的建立時間可靠（有些檔案系統沒有）。</summary>
    public long? CreatedAt { get; init; }
}
