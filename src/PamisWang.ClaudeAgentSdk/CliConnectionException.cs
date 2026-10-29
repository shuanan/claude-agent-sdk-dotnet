namespace PamisWang.ClaudeAgentSdk;

/// <summary>連不上 Claude Code：啟動失敗、程序已經結束，或還沒連線就收發。</summary>
public class CliConnectionException : ClaudeAgentException
{
    /// <summary>建立例外。</summary>
    public CliConnectionException()
    {
    }

    /// <summary>以訊息建立例外。</summary>
    public CliConnectionException(string message)
        : base(message)
    {
    }

    /// <summary>以訊息與內部例外建立例外。</summary>
    public CliConnectionException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
