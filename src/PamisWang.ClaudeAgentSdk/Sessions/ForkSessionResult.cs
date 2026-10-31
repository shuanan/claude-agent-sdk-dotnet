namespace PamisWang.ClaudeAgentSdk.Sessions;

/// <summary><see cref="ClaudeSessions.ForkSession"/> 的結果。對應 Python 的 <c>ForkSessionResult</c>。</summary>
/// <param name="SessionId">分支出來的新 session 的 UUID。</param>
public sealed record ForkSessionResult(string SessionId);
