namespace PamisWang.ClaudeAgentSdk;

/// <summary>
/// Claude Code 預設的系統提示詞，跟在終端機用 Claude Code 時一樣。對應 Python 的 <c>{"type": "preset", "preset": "claude_code"}</c>。
/// </summary>
/// <param name="Append">接在預設提示詞後面的一段（<c>--append-system-prompt</c>）；<see langword="null"/> 表示不加。</param>
public sealed record SystemPromptPreset(string? Append = null) : SystemPrompt
{
    /// <summary>
    /// 從系統提示詞拿掉因使用者而異的段落（工作目錄、auto-memory、git 狀態），讓提示詞固定、可以跨使用者命中 prompt cache；
    /// 拿掉的內容改放進第一則使用者訊息。不是命令列參數，在 initialize 的 <c>excludeDynamicSections</c> 送出；
    /// <see langword="null"/> 時不送。舊版 CLI 會忽略。對應 Python <c>SystemPromptPreset</c> 的 <c>exclude_dynamic_sections</c>。
    /// </summary>
    public bool? ExcludeDynamicSections { get; init; }

    /// <summary>
    /// session 是否沿用第一次請求記下的系統提示詞（initialize 的 <c>systemPromptSnapshot</c>）。
    /// <see langword="true"/> 時之後每次請求（含 resume）都送記下的那份，改 <see cref="Append"/> 要等壓縮或開新 session 才生效；
    /// <see langword="false"/> 時每次重組。<see langword="null"/> 時不送，CLI 視同 <see langword="true"/>（<c>--bare</c> 時視同 <see langword="false"/>）。
    /// 需要 Claude Code 2.1.257 以上。對應 Python 的 <c>snapshot</c>。
    /// </summary>
    public bool? Snapshot { get; init; }
}
