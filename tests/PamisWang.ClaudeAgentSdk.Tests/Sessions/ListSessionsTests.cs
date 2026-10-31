using System.Text.Json.Nodes;
using PamisWang.ClaudeAgentSdk.Sessions;
using static PamisWang.ClaudeAgentSdk.Tests.Sessions.SessionTestKit;

namespace PamisWang.ClaudeAgentSdk.Tests.Sessions;

/// <summary>移植 tests/test_sessions.py 的 <c>TestListSessions</c> 與 <c>TestSDKSessionInfoType</c>。</summary>
public sealed class ListSessionsTests : IDisposable
{
    private readonly SessionTestKit _kit = new();

    public void Dispose() => _kit.Dispose();

    private IReadOnlyList<SdkSessionInfo> List(string? directory = null, int? limit = null, int offset = 0, bool includeWorktrees = true) =>
        ClaudeSessions.ListSessions(directory, limit, offset, includeWorktrees, _kit.ConfigDir);

    /// <summary>常見的開頭：<c>tmp_path / name</c> 建好、它的專案資料夾也建好。</summary>
    private (string ProjectPath, string ProjectDir) Project(string name = "proj")
    {
        var projectPath = _kit.MakeProjectPath(name);
        return (projectPath, _kit.MakeProjectDir(RealPath(projectPath)));
    }

    // Python: test_sessions.py::TestListSessions::test_empty_projects_dir
    [Fact]
    public void EmptyProjectsDir()
    {
        Assert.Empty(List());
    }

    // Python: test_sessions.py::TestListSessions::test_no_config_dir
    [Fact]
    public void NoConfigDir()
    {
        Assert.Empty(ClaudeSessions.ListSessions(claudeConfigDir: Path.Combine(_kit.TmpPath, "nonexistent")));
    }

    // Python: test_sessions.py::TestListSessions::test_single_session
    [Fact]
    public void SingleSession()
    {
        var (projectPath, projectDir) = Project("my-project");
        var (sid, _) = MakeSessionFile(projectDir, firstPrompt: "What is 2+2?", gitBranch: "main", cwd: projectPath);

        var sessions = List(projectPath, includeWorktrees: false);
        var s = Assert.Single(sessions);
        Assert.Equal(sid, s.SessionId);
        Assert.Equal("What is 2+2?", s.FirstPrompt);
        Assert.Equal("What is 2+2?", s.Summary); // 沒有自訂標題或摘要 → 第一個提示
        Assert.Equal("main", s.GitBranch);
        Assert.Equal(projectPath, s.Cwd);
        Assert.True(s.FileSize > 0);
        Assert.True(s.LastModified > 0);
        Assert.Null(s.CustomTitle);
    }

    // Python: test_sessions.py::TestListSessions::test_custom_title_wins_summary
    [Fact]
    public void CustomTitleWinsSummary()
    {
        var (projectPath, projectDir) = Project();
        MakeSessionFile(projectDir, firstPrompt: "original question", summary: "auto summary", customTitle: "My Custom Title");

        var sessions = List(projectPath, includeWorktrees: false);
        Assert.Single(sessions);
        Assert.Equal("My Custom Title", sessions[0].Summary);
        Assert.Equal("My Custom Title", sessions[0].CustomTitle);
        Assert.Equal("original question", sessions[0].FirstPrompt);
    }

    // Python: test_sessions.py::TestListSessions::test_summary_wins_first_prompt
    [Fact]
    public void SummaryWinsFirstPrompt()
    {
        var (projectPath, projectDir) = Project();
        MakeSessionFile(projectDir, firstPrompt: "question", summary: "better summary");

        var sessions = List(projectPath, includeWorktrees: false);
        Assert.Single(sessions);
        Assert.Equal("better summary", sessions[0].Summary);
        Assert.Null(sessions[0].CustomTitle);
    }

    // Python: test_sessions.py::TestListSessions::test_multiple_sessions_sorted_by_mtime
    [Fact]
    public void MultipleSessionsSortedByMtime()
    {
        var (projectPath, projectDir) = Project();
        var (sidOld, _) = MakeSessionFile(projectDir, firstPrompt: "old", mtime: 1000.0);
        var (sidNew, _) = MakeSessionFile(projectDir, firstPrompt: "new", mtime: 3000.0);
        var (sidMid, _) = MakeSessionFile(projectDir, firstPrompt: "mid", mtime: 2000.0);

        var sessions = List(projectPath, includeWorktrees: false);
        Assert.Equal(3, sessions.Count);
        Assert.Equal([sidNew, sidMid, sidOld], sessions.Select(s => s.SessionId));
        // 修改時間轉成毫秒
        Assert.Equal(3_000_000, sessions[0].LastModified);
        Assert.Equal(2_000_000, sessions[1].LastModified);
        Assert.Equal(1_000_000, sessions[2].LastModified);
    }

    // Python: test_sessions.py::TestListSessions::test_limit
    [Fact]
    public void Limit()
    {
        var (projectPath, projectDir) = Project();
        for (var i = 0; i < 5; i++)
        {
            MakeSessionFile(projectDir, firstPrompt: $"prompt {i}", mtime: 1000.0 + i);
        }

        var sessions = List(projectPath, limit: 2, includeWorktrees: false);
        Assert.Equal(2, sessions.Count);
        // 最新的兩個
        Assert.True(sessions[0].LastModified >= sessions[1].LastModified);
    }

    // Python: test_sessions.py::TestListSessions::test_offset_pagination
    [Fact]
    public void OffsetPagination()
    {
        var (projectPath, projectDir) = Project();
        for (var i = 0; i < 5; i++)
        {
            MakeSessionFile(projectDir, firstPrompt: $"prompt {i}", mtime: 1000.0 + i);
        }

        var page1 = List(projectPath, limit: 2, offset: 0, includeWorktrees: false);
        Assert.Equal(2, page1.Count);

        var page2 = List(projectPath, limit: 2, offset: 2, includeWorktrees: false);
        Assert.Equal(2, page2.Count);

        // 兩頁的 session 不重疊
        Assert.Empty(page1.Select(s => s.SessionId).Intersect(page2.Select(s => s.SessionId)));

        // 第一頁比第二頁新
        Assert.True(page1[0].LastModified > page2[0].LastModified);

        // offset 超過範圍時是空的
        Assert.Empty(List(projectPath, offset: 100, includeWorktrees: false));
    }

    // Python: test_sessions.py::TestListSessions::test_filters_sidechain_sessions
    [Fact]
    public void FiltersSidechainSessions()
    {
        var (projectPath, projectDir) = Project();
        MakeSessionFile(projectDir, firstPrompt: "normal");
        MakeSessionFile(projectDir, firstPrompt: "sidechain", isSidechain: true);

        var sessions = List(projectPath, includeWorktrees: false);
        Assert.Single(sessions);
        Assert.Equal("normal", sessions[0].FirstPrompt);
    }

    // Python: test_sessions.py::TestListSessions::test_filters_empty_sessions
    [Fact]
    public void FiltersEmptySessions()
    {
        var (projectPath, projectDir) = Project();
        // 只有 meta 訊息 → 沒有第一個提示也沒有摘要
        MakeSessionFile(projectDir, firstPrompt: "ignored meta", isMetaOnly: true);
        MakeSessionFile(projectDir, firstPrompt: "real content");

        var sessions = List(projectPath, includeWorktrees: false);
        Assert.Single(sessions);
        Assert.Equal("real content", sessions[0].FirstPrompt);
    }

    // Python: test_sessions.py::TestListSessions::test_filters_non_uuid_filenames
    [Fact]
    public void FiltersNonUuidFilenames()
    {
        var (projectPath, projectDir) = Project();
        WriteText(Path.Combine(projectDir, "not-a-uuid.jsonl"), Dumps(Obj("""{"type": "user", "message": {"content": "x"}}""")) + "\n");
        MakeSessionFile(projectDir, firstPrompt: "valid session");

        var sessions = List(projectPath, includeWorktrees: false);
        Assert.Single(sessions);
        Assert.Equal("valid session", sessions[0].FirstPrompt);
    }

    // Python: test_sessions.py::TestListSessions::test_ignores_non_jsonl_files
    [Fact]
    public void IgnoresNonJsonlFiles()
    {
        var (projectPath, projectDir) = Project();
        WriteText(Path.Combine(projectDir, "README.md"), "not a session");
        MakeSessionFile(projectDir, firstPrompt: "session");

        Assert.Single(List(projectPath, includeWorktrees: false));
    }

    // Python: test_sessions.py::TestListSessions::test_list_all_sessions
    [Fact]
    public void ListAllSessions()
    {
        var proj1 = _kit.MakeProjectDir("/some/path/one");
        var proj2 = _kit.MakeProjectDir("/some/path/two");
        MakeSessionFile(proj1, firstPrompt: "from proj1", mtime: 1000.0);
        MakeSessionFile(proj2, firstPrompt: "from proj2", mtime: 2000.0);

        var sessions = List();
        Assert.Equal(2, sessions.Count);
        // 新的在前
        Assert.Equal("from proj2", sessions[0].FirstPrompt);
        Assert.Equal("from proj1", sessions[1].FirstPrompt);
    }

    // Python: test_sessions.py::TestListSessions::test_list_all_sessions_dedupes
    [Fact]
    public void ListAllSessionsDedupes()
    {
        var proj1 = _kit.MakeProjectDir("/path/one");
        var proj2 = _kit.MakeProjectDir("/path/two");
        var sharedSid = NewUuid();
        MakeSessionFile(proj1, sessionId: sharedSid, firstPrompt: "older", mtime: 1000.0);
        MakeSessionFile(proj2, sessionId: sharedSid, firstPrompt: "newer", mtime: 2000.0);

        var sessions = List();
        Assert.Single(sessions);
        Assert.Equal("newer", sessions[0].FirstPrompt);
        Assert.Equal(2_000_000, sessions[0].LastModified);
    }

    // Python: test_sessions.py::TestListSessions::test_nonexistent_project_dir
    [Fact]
    public void NonexistentProjectDir()
    {
        var projectPath = _kit.MakeProjectPath("never-used");
        Assert.Empty(List(projectPath, includeWorktrees: false));
    }

    // Python: test_sessions.py::TestListSessions::test_empty_file_filtered
    [Fact]
    public void EmptyFileFiltered()
    {
        var (projectPath, projectDir) = Project();
        WriteText(Path.Combine(projectDir, $"{NewUuid()}.jsonl"), "");

        Assert.Empty(List(projectPath, includeWorktrees: false));
    }

    // Python: test_sessions.py::TestListSessions::test_include_worktrees_disabled
    [Fact]
    public void IncludeWorktreesDisabled()
    {
        var projectPath = _kit.MakeProjectPath("main-proj");
        var canonical = RealPath(projectPath);

        var mainDir = _kit.MakeProjectDir(canonical);
        MakeSessionFile(mainDir, firstPrompt: "main session");

        // 另一個像 worktree 的專案資料夾，不該被掃到
        var otherDir = _kit.MakeProjectDir(canonical + "-worktree");
        MakeSessionFile(otherDir, firstPrompt: "worktree session");

        var sessions = List(projectPath, includeWorktrees: false);
        Assert.Single(sessions);
        Assert.Equal("main session", sessions[0].FirstPrompt);
    }

    // Python: test_sessions.py::TestListSessions::test_limit_zero_returns_all
    [Fact]
    public void LimitZeroReturnsAll()
    {
        var (projectPath, projectDir) = Project();
        for (var i = 0; i < 3; i++)
        {
            MakeSessionFile(projectDir, firstPrompt: $"p{i}");
        }

        Assert.Equal(3, List(projectPath, limit: 0, includeWorktrees: false).Count);
    }

    // Python: test_sessions.py::TestListSessions::test_cwd_from_head_fallback_to_project_path
    [Fact]
    public void CwdFromHeadFallbackToProjectPath()
    {
        var projectPath = _kit.MakeProjectPath("proj");
        var canonical = RealPath(projectPath);
        var projectDir = _kit.MakeProjectDir(canonical);

        // 沒有 cwd 欄位的 session
        MakeSessionFile(projectDir, firstPrompt: "no cwd field");

        var sessions = List(projectPath, includeWorktrees: false);
        Assert.Single(sessions);
        Assert.Equal(canonical, sessions[0].Cwd);
    }

    // Python: test_sessions.py::TestListSessions::test_git_branch_from_tail_preferred
    [Fact]
    public void GitBranchFromTailPreferred()
    {
        var (projectPath, projectDir) = Project();
        var filePath = Path.Combine(projectDir, $"{NewUuid()}.jsonl");
        string[] lines =
        [
            Dumps(Obj("""{"type": "user", "message": {"content": "hello"}, "gitBranch": "old-branch"}""")),
            Dumps(Obj("""{"type": "summary", "gitBranch": "new-branch"}""")),
        ];
        WriteText(filePath, string.Join('\n', lines) + "\n");

        var sessions = List(projectPath, includeWorktrees: false);
        Assert.Single(sessions);
        Assert.Equal("new-branch", sessions[0].GitBranch);
    }

    // Python: test_sessions.py::TestSDKSessionInfoType::test_creation_required_fields
    [Fact]
    public void SdkSessionInfoCreationRequiredFields()
    {
        var info = new SdkSessionInfo { SessionId = "abc", Summary = "test", LastModified = 1000, FileSize = 42 };
        Assert.Equal("abc", info.SessionId);
        Assert.Equal("test", info.Summary);
        Assert.Equal(1000, info.LastModified);
        Assert.Equal(42, info.FileSize);
        Assert.Null(info.CustomTitle);
        Assert.Null(info.FirstPrompt);
        Assert.Null(info.GitBranch);
        Assert.Null(info.Cwd);
    }

    // Python: test_sessions.py::TestSDKSessionInfoType::test_creation_all_fields
    [Fact]
    public void SdkSessionInfoCreationAllFields()
    {
        var info = new SdkSessionInfo
        {
            SessionId = "abc",
            Summary = "test",
            LastModified = 1000,
            FileSize = 42,
            CustomTitle = "title",
            FirstPrompt = "prompt",
            GitBranch = "main",
            Cwd = "/foo",
        };
        Assert.Equal("title", info.CustomTitle);
        Assert.Equal("prompt", info.FirstPrompt);
        Assert.Equal("main", info.GitBranch);
        Assert.Equal("/foo", info.Cwd);
    }
}

/// <summary>移植 tests/test_sessions.py 的 <c>TestTagExtraction</c> 與 <c>TestCreatedAtExtraction</c>。</summary>
public sealed class SessionInfoExtractionTests : IDisposable
{
    private readonly SessionTestKit _kit = new();

    /// <summary>每個測試的 <c>sid = str(uuid.uuid4())</c>。</summary>
    private readonly string _sid = NewUuid();

    public void Dispose() => _kit.Dispose();

    /// <summary><c>tmp_path / "proj"</c> 的專案資料夾裡寫一個 <c>{sid}.jsonl</c>。</summary>
    private (string ProjectPath, string FilePath) SessionWithLines(params string[] lines)
    {
        var projectPath = _kit.MakeProjectPath("proj");
        var projectDir = _kit.MakeProjectDir(RealPath(projectPath));
        var filePath = Path.Combine(projectDir, $"{_sid}.jsonl");
        WriteText(filePath, string.Join('\n', lines) + "\n");
        return (projectPath, filePath);
    }

    private SdkSessionInfo Single(string projectPath) =>
        Assert.Single(ClaudeSessions.ListSessions(projectPath, includeWorktrees: false, claudeConfigDir: _kit.ConfigDir));

    /// <summary><c>json.dumps({"type": "tag", "tag": tag, "sessionId": sid}, **_COMPACT)</c>。</summary>
    private string Tag(string tag) => DumpsCompact(new JsonObject { ["type"] = "tag", ["tag"] = tag, ["sessionId"] = _sid });

    // Python: test_sessions.py::TestTagExtraction::test_tag_extracted_from_tail
    [Fact]
    public void TagExtractedFromTail()
    {
        var (projectPath, _) = SessionWithLines(Dumps(Obj("""{"type": "user", "message": {"content": "hello"}}""")), Tag("my-tag"));
        Assert.Equal("my-tag", Single(projectPath).Tag);
    }

    // Python: test_sessions.py::TestTagExtraction::test_tag_last_wins
    [Fact]
    public void TagLastWins()
    {
        var (projectPath, _) = SessionWithLines(
            Dumps(Obj("""{"type": "user", "message": {"content": "hello"}}""")), Tag("first-tag"), Tag("second-tag"));
        Assert.Equal("second-tag", Single(projectPath).Tag);
    }

    // Python: test_sessions.py::TestTagExtraction::test_tag_empty_string_is_none
    [Fact]
    public void TagEmptyStringIsNone()
    {
        var (projectPath, _) = SessionWithLines(
            Dumps(Obj("""{"type": "user", "message": {"content": "hello"}}""")), Tag("old-tag"), Tag(""));
        Assert.Null(Single(projectPath).Tag);
    }

    // Python: test_sessions.py::TestTagExtraction::test_tag_absent
    [Fact]
    public void TagAbsent()
    {
        var projectPath = _kit.MakeProjectPath("proj");
        MakeSessionFile(_kit.MakeProjectDir(RealPath(projectPath)), firstPrompt: "hello");
        Assert.Null(Single(projectPath).Tag);
    }

    // Python: test_sessions.py::TestTagExtraction::test_tag_ignores_tool_use_inputs
    [Fact]
    public void TagIgnoresToolUseInputs()
    {
        // tag 只看 {type:'tag'} 的行，不管 tool_use 輸入裡的 "tag"（git tag、Docker tag、雲端資源的 tag）
        var (projectPath, _) = SessionWithLines(
            Dumps(Obj("""{"type": "user", "message": {"content": "tag this v1.0"}}""")),
            Tag("real-tag"),
            // tool_use 的輸入有 "tag" 鍵，不能配到
            Dumps(Obj("""
                {"type": "assistant", "message": {"content": [{"type": "tool_use", "name": "mcp__docker__build", "input": {"tag": "myapp:v2", "context": "."}}]}}
                """)));
        Assert.Equal("real-tag", Single(projectPath).Tag); // 不是 "myapp:v2"
    }

    // Python: test_sessions.py::TestTagExtraction::test_tag_none_when_only_tool_use_tag
    [Fact]
    public void TagNoneWhenOnlyToolUseTag()
    {
        var (projectPath, _) = SessionWithLines(
            Dumps(Obj("""{"type": "user", "message": {"content": "build docker"}}""")),
            Dumps(Obj("""{"type": "assistant", "message": {"content": [{"type": "tool_use", "input": {"tag": "prod"}}]}}""")));
        Assert.Null(Single(projectPath).Tag); // 不是 "prod"
    }

    // Python: test_sessions.py::TestTagExtraction::test_parse_session_info_from_lite_helper
    [Fact]
    public void ParseSessionInfoFromLiteHelper()
    {
        var filePath = Path.Combine(_kit.TmpPath, $"{_sid}.jsonl");
        WriteText(filePath, string.Join('\n',
            Dumps(Obj("""{"type": "user", "message": {"content": "test prompt"}, "cwd": "/workspace"}""")),
            Tag("experiment")) + "\n");

        var lite = SessionLite.ReadSessionLite(filePath);
        Assert.NotNull(lite);
        var info = SessionLite.ParseSessionInfo(_sid, lite, "/fallback");
        Assert.NotNull(info);
        Assert.Equal(_sid, info.SessionId);
        Assert.Equal("test prompt", info.Summary);
        Assert.Equal("experiment", info.Tag);
        Assert.Equal("/workspace", info.Cwd); // 開頭的 cwd 優先於後備值
    }

    // Python: test_sessions.py::TestCreatedAtExtraction::test_created_at_from_iso_timestamp
    [Fact]
    public void CreatedAtFromIsoTimestamp()
    {
        // 2026-01-15T10:30:00.000Z → epoch 1768473000000 ms
        var (projectPath, _) = SessionWithLines(
            Dumps(Obj("""{"type": "user", "message": {"content": "hello"}, "timestamp": "2026-01-15T10:30:00.000Z"}""")),
            Dumps(Obj("""{"type": "assistant", "message": {"content": "hi"}, "timestamp": "2026-01-15T10:35:00.000Z"}""")));
        Assert.Equal(1768473000000, Single(projectPath).CreatedAt);
    }

    // Python: test_sessions.py::TestCreatedAtExtraction::test_created_at_leq_last_modified
    [Fact]
    public void CreatedAtLeqLastModified()
    {
        var (projectPath, filePath) = SessionWithLines(
            Dumps(Obj("""{"type": "user", "message": {"content": "hello"}, "timestamp": "2026-01-01T00:00:00.000Z"}""")));
        // 修改時間設成 2026 年 2 月（比一月的時間晚）
        SetMtime(filePath, 1769904000); // 2026-02-01 UTC

        var session = Single(projectPath);
        Assert.NotNull(session.CreatedAt);
        Assert.True(session.CreatedAt <= session.LastModified);
    }

    // Python: test_sessions.py::TestCreatedAtExtraction::test_created_at_when_first_line_lacks_timestamp
    [Fact]
    public void CreatedAtWhenFirstLineLacksTimestamp()
    {
        // 第一行是沒有時間的 metadata（例如 permission-mode）時，從開頭後面的紀錄找。回歸測試 #904
        var (projectPath, _) = SessionWithLines(
            Dumps(Obj("""{"type": "permission-mode", "permissionMode": "acceptEdits"}""")),
            Dumps(Obj("""{"type": "user", "message": {"content": "hello"}, "timestamp": "2026-01-15T10:30:00.000Z"}""")));
        Assert.Equal(1768473000000, Single(projectPath).CreatedAt);
    }

    // Python: test_sessions.py::TestCreatedAtExtraction::test_created_at_none_when_missing
    [Fact]
    public void CreatedAtNoneWhenMissing()
    {
        var projectPath = _kit.MakeProjectPath("proj");
        // _make_session_file 不加 timestamp
        MakeSessionFile(_kit.MakeProjectDir(RealPath(projectPath)), firstPrompt: "no timestamp");
        Assert.Null(Single(projectPath).CreatedAt);
    }

    // Python: test_sessions.py::TestCreatedAtExtraction::test_created_at_none_on_invalid_format
    [Fact]
    public void CreatedAtNoneOnInvalidFormat()
    {
        var sid = NewUuid();
        var filePath = Path.Combine(_kit.TmpPath, $"{sid}.jsonl");
        WriteText(filePath, Dumps(Obj("""{"type": "user", "message": {"content": "hello"}, "timestamp": "not-a-valid-iso-date"}""")) + "\n");

        var lite = SessionLite.ReadSessionLite(filePath);
        Assert.NotNull(lite);
        var info = SessionLite.ParseSessionInfo(sid, lite);
        Assert.NotNull(info);
        Assert.Null(info.CreatedAt);
    }

    // Python: test_sessions.py::TestCreatedAtExtraction::test_created_at_without_z_suffix
    [Fact]
    public void CreatedAtWithoutZSuffix()
    {
        var sid = NewUuid();
        var filePath = Path.Combine(_kit.TmpPath, $"{sid}.jsonl");
        WriteText(filePath, Dumps(Obj("""{"type": "user", "message": {"content": "hello"}, "timestamp": "2026-01-15T10:30:00+00:00"}""")) + "\n");

        var lite = SessionLite.ReadSessionLite(filePath);
        Assert.NotNull(lite);
        var info = SessionLite.ParseSessionInfo(sid, lite);
        Assert.NotNull(info);
        Assert.Equal(1768473000000, info.CreatedAt);
    }

    // Python: test_sessions.py::TestCreatedAtExtraction::test_sdksessioninfo_created_at_default
    [Fact]
    public void SdkSessionInfoCreatedAtDefault()
    {
        var info = new SdkSessionInfo { SessionId = "abc", Summary = "test", LastModified = 1000, FileSize = 42 };
        Assert.Null(info.CreatedAt);
    }
}

/// <summary>移植 tests/test_sessions.py 的 <c>TestGetSessionInfo</c>。</summary>
public sealed class GetSessionInfoTests : IDisposable
{
    private readonly SessionTestKit _kit = new();

    public void Dispose() => _kit.Dispose();

    private SdkSessionInfo? Get(string sessionId, string? directory = null) =>
        ClaudeSessions.GetSessionInfo(sessionId, directory, _kit.ConfigDir);

    // Python: test_sessions.py::TestGetSessionInfo::test_invalid_session_id
    [Fact]
    public void InvalidSessionId()
    {
        Assert.Null(Get("not-a-uuid"));
        Assert.Null(Get(""));
    }

    // Python: test_sessions.py::TestGetSessionInfo::test_nonexistent_session
    [Fact]
    public void NonexistentSession()
    {
        Assert.Null(Get(NewUuid()));
    }

    // Python: test_sessions.py::TestGetSessionInfo::test_no_config_dir
    [Fact]
    public void NoConfigDir()
    {
        Assert.Null(ClaudeSessions.GetSessionInfo(NewUuid(), claudeConfigDir: Path.Combine(_kit.TmpPath, "nonexistent")));
    }

    // Python: test_sessions.py::TestGetSessionInfo::test_found_with_directory
    [Fact]
    public void FoundWithDirectory()
    {
        var projectPath = _kit.MakeProjectPath("proj");
        var (sid, _) = MakeSessionFile(_kit.MakeProjectDir(RealPath(projectPath)), firstPrompt: "hello", gitBranch: "main");

        var info = Get(sid, projectPath);
        Assert.NotNull(info);
        Assert.Equal(sid, info.SessionId);
        Assert.Equal("hello", info.Summary);
        Assert.Equal("main", info.GitBranch);
    }

    // Python: test_sessions.py::TestGetSessionInfo::test_found_without_directory
    [Fact]
    public void FoundWithoutDirectory()
    {
        var (sid, _) = MakeSessionFile(_kit.MakeProjectDir("/some/project"), firstPrompt: "search all");

        var info = Get(sid);
        Assert.NotNull(info);
        Assert.Equal(sid, info.SessionId);
        Assert.Equal("search all", info.Summary);
    }

    // Python: test_sessions.py::TestGetSessionInfo::test_returns_none_for_sidechain
    [Fact]
    public void ReturnsNoneForSidechain()
    {
        var projectPath = _kit.MakeProjectPath("proj");
        var (sid, _) = MakeSessionFile(_kit.MakeProjectDir(RealPath(projectPath)), firstPrompt: "sidechain", isSidechain: true);

        Assert.Null(Get(sid, projectPath));
    }

    // Python: test_sessions.py::TestGetSessionInfo::test_directory_not_containing_session
    [Fact]
    public void DirectoryNotContainingSession()
    {
        var projectA = _kit.MakeProjectPath("proj-a");
        var projectB = _kit.MakeProjectPath("proj-b");
        var dirA = _kit.MakeProjectDir(RealPath(projectA));
        _kit.MakeProjectDir(RealPath(projectB));
        var (sid, _) = MakeSessionFile(dirA, firstPrompt: "in A only");

        // session 在 A，找 B 時是 null（兩者沒有 worktree 關係）
        Assert.Null(Get(sid, projectB));
        // 找所有專案時找得到
        Assert.NotNull(Get(sid));
    }

    // Python: test_sessions.py::TestGetSessionInfo::test_includes_tag
    [Fact]
    public void IncludesTag()
    {
        var projectPath = _kit.MakeProjectPath("proj");
        var projectDir = _kit.MakeProjectDir(RealPath(projectPath));
        var sid = NewUuid();
        WriteText(Path.Combine(projectDir, $"{sid}.jsonl"), string.Join('\n',
            Dumps(Obj("""{"type": "user", "message": {"content": "hello"}}""")),
            DumpsCompact(new JsonObject { ["type"] = "tag", ["tag"] = "urgent", ["sessionId"] = sid })) + "\n");

        var info = Get(sid, projectPath);
        Assert.NotNull(info);
        Assert.Equal("urgent", info.Tag);
    }

    // Python: test_sessions.py::TestGetSessionInfo::test_sdksessioninfo_new_fields_defaults
    [Fact]
    public void SdkSessionInfoNewFieldsDefaults()
    {
        var info = new SdkSessionInfo { SessionId = "abc", Summary = "test", LastModified = 1000, FileSize = 42 };
        Assert.Null(info.Tag);
    }
}
