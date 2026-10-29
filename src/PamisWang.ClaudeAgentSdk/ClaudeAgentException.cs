namespace PamisWang.ClaudeAgentSdk;

/// <summary>這個套件所有例外的基底。</summary>
public class ClaudeAgentException : Exception
{
    /// <summary>建立例外。</summary>
    public ClaudeAgentException()
    {
    }

    /// <summary>以訊息建立例外。</summary>
    public ClaudeAgentException(string message)
        : base(message)
    {
    }

    /// <summary>以訊息與內部例外建立例外。</summary>
    public ClaudeAgentException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
