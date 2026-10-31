namespace PamisWang.ClaudeAgentSdk;

/// <summary>Claude Code 程序以非零的結束代碼結束。</summary>
public class CliProcessException : ClaudeAgentException
{
    /// <summary>建立例外。</summary>
    public CliProcessException()
    {
    }

    /// <summary>以訊息建立例外。</summary>
    public CliProcessException(string message)
        : base(message)
    {
    }

    /// <summary>以訊息與內部例外建立例外。</summary>
    public CliProcessException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    /// <summary>以訊息與結束代碼建立例外。</summary>
    public CliProcessException(string message, int exitCode)
        : base($"{message}（exit code {exitCode}）")
    {
        ExitCode = exitCode;
    }

    /// <summary>以訊息、結束代碼與內部例外建立例外。</summary>
    public CliProcessException(string message, int? exitCode, Exception? innerException)
        : base(exitCode is { } code ? $"{message}（exit code {code}）" : message, innerException)
    {
        ExitCode = exitCode;
    }

    /// <summary>程序的結束代碼；不知道時是 <see langword="null"/>。</summary>
    public int? ExitCode { get; }
}
