namespace PamisWang.ClaudeAgentSdk;

/// <summary>CLI 輸出的一行解析不了，或一則訊息超過 <see cref="ClaudeAgentOptions.MaxBufferSize"/>。</summary>
public sealed class CliJsonDecodeException : ClaudeAgentException
{
    /// <summary>建立例外。</summary>
    public CliJsonDecodeException()
    {
    }

    /// <summary>以訊息建立例外。</summary>
    public CliJsonDecodeException(string message)
        : base(message)
    {
    }

    /// <summary>以訊息與內部例外建立例外。</summary>
    public CliJsonDecodeException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    /// <summary>以訊息、解析不了的那一行與內部例外建立例外。</summary>
    public CliJsonDecodeException(string message, string line, Exception? innerException)
        : base(message, innerException)
    {
        Line = line;
    }

    /// <summary>解析不了的那一行；超過大小上限時不保留內容，是 <see langword="null"/>。</summary>
    public string? Line { get; }
}
