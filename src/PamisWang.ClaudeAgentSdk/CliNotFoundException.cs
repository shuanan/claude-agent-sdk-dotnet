namespace PamisWang.ClaudeAgentSdk;

/// <summary>找不到 Claude Code CLI，或指定的路徑啟動不了。</summary>
public sealed class CliNotFoundException : CliConnectionException
{
    /// <summary>建立例外。</summary>
    public CliNotFoundException()
    {
    }

    /// <summary>以訊息建立例外。</summary>
    public CliNotFoundException(string message)
        : base(message)
    {
    }

    /// <summary>以訊息與內部例外建立例外。</summary>
    public CliNotFoundException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
