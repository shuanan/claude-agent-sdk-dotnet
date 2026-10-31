using PamisWang.ClaudeAgentSdk.Sessions;
using static PamisWang.ClaudeAgentSdk.Tests.Sessions.SessionTestKit;

namespace PamisWang.ClaudeAgentSdk.Tests.Sessions;

/// <summary>
/// 不是移植的測試：Python 的測試沒有涵蓋 git worktree 的掃描（<c>_list_sessions_for_project</c> 的多 worktree 分支與各函式的 worktree 後備）。
/// 這裡用 <see cref="SessionPaths.WorktreePathsOverride"/> 代替 <c>git worktree list</c>，期望值照 Python 的程式邏輯推出。
/// </summary>
public sealed class WorktreeSessionsTests : IDisposable
{
    private readonly SessionTestKit _kit = new();
    private readonly string _repo;
    private readonly string _worktree;
    private readonly string _subdir;

    public WorktreeSessionsTests()
    {
        _repo = RealPath(_kit.MakeProjectPath("repo"));
        _worktree = RealPath(_kit.MakeProjectPath("repo-wt"));
        _subdir = RealPath(_kit.MakeProjectPath(Path.Combine("repo", "packages", "app")));
    }

    public void Dispose() => _kit.Dispose();

    private T WithWorktrees<T>(Func<T> action)
    {
        SessionPaths.WorktreePathsOverride.Value = _ => [_repo, _worktree];
        try
        {
            return action();
        }
        finally
        {
            SessionPaths.WorktreePathsOverride.Value = null;
        }
    }

    private static string Transcript(string projectDir, string sid, string prompt, double mtime)
    {
        var u1 = NewUuid();
        var path = WriteTranscript(projectDir, sid,
        [
            MakeTranscriptEntry("user", u1, null, sid, prompt),
            MakeTranscriptEntry("assistant", NewUuid(), u1, sid, "ok"),
        ]);
        SetMtime(path, mtime);
        return path;
    }

    [Fact]
    public void ListSessions_ScansEveryWorktreeAndTheGivenDirectory()
    {
        var repoDir = _kit.MakeProjectDir(_repo);
        var worktreeDir = _kit.MakeProjectDir(_worktree);
        var subdirDir = _kit.MakeProjectDir(_subdir);
        // 短路徑要完全相同：/…/repo 不能配到 /…/repo-foo
        var siblingDir = _kit.MakeProjectDir(_repo + "-foo");

        var shared = NewUuid();
        MakeSessionFile(repoDir, sessionId: shared, firstPrompt: "old copy", mtime: 1000);
        MakeSessionFile(worktreeDir, sessionId: shared, firstPrompt: "new copy", mtime: 3000);
        var (inWorktree, _) = MakeSessionFile(worktreeDir, firstPrompt: "worktree", mtime: 2000);
        var (inSubdir, _) = MakeSessionFile(subdirDir, firstPrompt: "subdir", mtime: 1500);
        MakeSessionFile(siblingDir, firstPrompt: "sibling", mtime: 4000);

        var sessions = WithWorktrees(() => ClaudeSessions.ListSessions(_subdir, claudeConfigDir: _kit.ConfigDir));

        Assert.Equal([shared, inWorktree, inSubdir], sessions.Select(s => s.SessionId));
        // 同一個 session 留最新的；cwd 沒寫時是它所在的 worktree 路徑
        Assert.Equal("new copy", sessions[0].FirstPrompt);
        Assert.Equal(_worktree, sessions[0].Cwd);
        Assert.Equal(_subdir, sessions[2].Cwd);

        // 不掃 worktree 時只有給的資料夾
        var only = WithWorktrees(() => ClaudeSessions.ListSessions(_subdir, includeWorktrees: false, claudeConfigDir: _kit.ConfigDir));
        Assert.Equal([inSubdir], only.Select(s => s.SessionId));

        // 分頁在去重複與排序之後
        var page = WithWorktrees(() => ClaudeSessions.ListSessions(_subdir, limit: 1, offset: 1, claudeConfigDir: _kit.ConfigDir));
        Assert.Equal([inWorktree], page.Select(s => s.SessionId));
    }

    [Fact]
    public void SingleSessionFunctions_FallBackToOtherWorktrees()
    {
        _kit.MakeProjectDir(_repo);
        var worktreeDir = _kit.MakeProjectDir(_worktree);
        var sid = NewUuid();
        var path = Transcript(worktreeDir, sid, "in the worktree", 2000);
        Directory.CreateDirectory(Path.Combine(worktreeDir, sid, "subagents"));
        WriteText(Path.Combine(worktreeDir, sid, "subagents", "agent-a1.jsonl"), "{}\n");

        var info = WithWorktrees(() => ClaudeSessions.GetSessionInfo(sid, _repo, _kit.ConfigDir));
        Assert.NotNull(info);
        Assert.Equal(_worktree, info.Cwd);

        Assert.Equal(2, WithWorktrees(() => ClaudeSessions.GetSessionMessages(sid, _repo, claudeConfigDir: _kit.ConfigDir)).Count);
        Assert.Equal(["a1"], WithWorktrees(() => ClaudeSessions.ListSubagents(sid, _repo, _kit.ConfigDir)));

        WithWorktrees(() =>
        {
            ClaudeSessions.RenameSession(sid, "Renamed", _repo, _kit.ConfigDir);
            return 0;
        });
        Assert.Contains("\"customTitle\":\"Renamed\"", ReadText(path), StringComparison.Ordinal);

        var fork = WithWorktrees(() => ClaudeSessions.ForkSession(sid, _repo, claudeConfigDir: _kit.ConfigDir));
        // 分支寫在來源檔案旁邊
        Assert.True(File.Exists(Path.Combine(worktreeDir, $"{fork.SessionId}.jsonl")));

        // 沒有 worktree 時找不到
        Assert.Null(ClaudeSessions.GetSessionInfo(sid, _repo, _kit.ConfigDir));
    }

    [Fact]
    public void LongPaths_MatchByPrefixWhenTheHashDiffers()
    {
        // 超過 200 字元的路徑：CLI 的雜湊（Bun.hash）可能不同，用前綴找
        var longName = new string('d', 230);
        var longPath = RealPath(_kit.MakeProjectPath(longName));
        var sanitized = SessionPaths.SanitizePath(longPath);
        Assert.True(sanitized.Length > SessionPaths.MaxSanitizedLength);

        var cliDir = Path.Combine(_kit.ConfigDir, "projects", sanitized[..SessionPaths.MaxSanitizedLength] + "-otherhash");
        Directory.CreateDirectory(cliDir);
        var (sid, _) = MakeSessionFile(cliDir, firstPrompt: "long path");

        var sessions = ClaudeSessions.ListSessions(longPath, includeWorktrees: false, claudeConfigDir: _kit.ConfigDir);
        Assert.Equal([sid], sessions.Select(s => s.SessionId));
        Assert.NotNull(ClaudeSessions.GetSessionInfo(sid, longPath, _kit.ConfigDir));
        Assert.Equal(sanitized, ClaudeSessions.ProjectKeyForDirectory(longPath));
    }
}
