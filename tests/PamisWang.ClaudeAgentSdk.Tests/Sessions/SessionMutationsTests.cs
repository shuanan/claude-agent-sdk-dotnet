using System.Text.Json.Nodes;
using PamisWang.ClaudeAgentSdk.Sessions;
using static PamisWang.ClaudeAgentSdk.Tests.Sessions.SessionTestKit;

namespace PamisWang.ClaudeAgentSdk.Tests.Sessions;

/// <summary>
/// 移植 tests/test_session_mutations.py：<c>TestTryAppend</c>、<c>TestRenameSession</c>、<c>TestTagSession</c>、<c>TestSanitizeUnicode</c>、
/// <c>TestDeleteSession</c>、<c>TestForkSession</c>。Python 的 <c>ValueError</c> 對應 <see cref="ArgumentException"/>。
/// </summary>
public sealed class SessionMutationsTests : IDisposable
{
    private readonly SessionTestKit _kit = new();

    public void Dispose() => _kit.Dispose();

    /// <summary>tests/test_session_mutations.py 的 <c>_make_session_file</c>（跟 test_sessions.py 的不同，只有兩行）。</summary>
    private static (string SessionId, string FilePath) MakeSessionFile(string projectDir, string? sessionId = null, string firstPrompt = "Hello Claude")
    {
        var sid = sessionId ?? NewUuid();
        var filePath = Path.Combine(projectDir, $"{sid}.jsonl");
        string[] lines =
        [
            Dumps(new JsonObject { ["type"] = "user", ["message"] = new JsonObject { ["role"] = "user", ["content"] = firstPrompt } }),
            Dumps(Obj("""{"type": "assistant", "message": {"role": "assistant", "content": "Hi!"}}""")),
        ];
        WriteText(filePath, string.Join('\n', lines) + "\n");
        return (sid, filePath);
    }

    /// <summary>tests/test_session_mutations.py 的 <c>_make_transcript_session</c>。回傳 (session_id, file_path, 訊息的 uuid)。</summary>
    private static (string SessionId, string FilePath, List<string> Uuids) MakeTranscriptSession(string projectDir, string? sessionId = null, int numTurns = 2)
    {
        var sid = sessionId ?? NewUuid();
        var filePath = Path.Combine(projectDir, $"{sid}.jsonl");
        var uuids = new List<string>();
        var lines = new List<string>();
        string? parentUuid = null;
        for (var i = 0; i < numTurns; i++)
        {
            var userUuid = NewUuid();
            uuids.Add(userUuid);
            lines.Add(Dumps(new JsonObject
            {
                ["type"] = "user",
                ["uuid"] = userUuid,
                ["parentUuid"] = parentUuid,
                ["sessionId"] = sid,
                ["timestamp"] = "2026-03-01T00:00:00Z",
                ["message"] = new JsonObject { ["role"] = "user", ["content"] = $"Turn {i + 1} question" },
            }));
            parentUuid = userUuid;

            var assistantUuid = NewUuid();
            uuids.Add(assistantUuid);
            lines.Add(Dumps(new JsonObject
            {
                ["type"] = "assistant",
                ["uuid"] = assistantUuid,
                ["parentUuid"] = parentUuid,
                ["sessionId"] = sid,
                ["timestamp"] = "2026-03-01T00:00:00Z",
                ["message"] = new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = $"Turn {i + 1} answer" }),
                },
            }));
            parentUuid = assistantUuid;
        }

        WriteText(filePath, string.Join('\n', lines) + "\n");
        return (sid, filePath, uuids);
    }

    private (string ProjectPath, string ProjectDir) Project()
    {
        var projectPath = _kit.MakeProjectPath("proj");
        return (projectPath, _kit.MakeProjectDir(RealPath(projectPath)));
    }

    /// <summary>Python 的 <c>path.read_text().strip().split("\n")</c>。</summary>
    private static string[] Lines(string path) => PyText.Strip(ReadText(path)).Split('\n');

    private static JsonObject LastEntry(string path) => (JsonObject)JsonNode.Parse(Lines(path)[^1])!;

    private void Rename(string sid, string title, string? directory = null) => ClaudeSessions.RenameSession(sid, title, directory, _kit.ConfigDir);

    private void Tag(string sid, string? tag, string? directory = null) => ClaudeSessions.TagSession(sid, tag, directory, _kit.ConfigDir);

    private void Delete(string sid, string? directory = null) => ClaudeSessions.DeleteSession(sid, directory, _kit.ConfigDir);

    private ForkSessionResult Fork(string sid, string? directory = null, string? upToMessageId = null, string? title = null) =>
        ClaudeSessions.ForkSession(sid, directory, upToMessageId, title, _kit.ConfigDir);

    private IReadOnlyList<SdkSessionInfo> List(string? directory = null, bool includeWorktrees = true) =>
        ClaudeSessions.ListSessions(directory, includeWorktrees: includeWorktrees, claudeConfigDir: _kit.ConfigDir);

    // ---- TestTryAppend ----

    // Python: test_session_mutations.py::TestTryAppend::test_append_to_existing_file
    [Fact]
    public void TryAppendToExistingFile()
    {
        var f = Path.Combine(_kit.TmpPath, "test.jsonl");
        WriteText(f, "line1\n");
        Assert.True(ClaudeSessions.TryAppend(f, "line2\n"));
        Assert.Equal("line1\nline2\n", ReadText(f));
    }

    // Python: test_session_mutations.py::TestTryAppend::test_missing_file_returns_false
    [Fact]
    public void TryAppendMissingFileReturnsFalse()
    {
        // ENOENT 回傳 false（不是錯誤）
        var f = Path.Combine(_kit.TmpPath, "nonexistent.jsonl");
        Assert.False(ClaudeSessions.TryAppend(f, "data\n"));
        Assert.False(File.Exists(f)); // 沒有建立檔案
    }

    // Python: test_session_mutations.py::TestTryAppend::test_missing_parent_dir_returns_false
    [Fact]
    public void TryAppendMissingParentDirReturnsFalse()
    {
        Assert.False(ClaudeSessions.TryAppend(Path.Combine(_kit.TmpPath, "nonexistent", "file.jsonl"), "data\n"));
    }

    // Python: test_session_mutations.py::TestTryAppend::test_zero_byte_file_returns_false
    [Fact]
    public void TryAppendZeroByteFileReturnsFalse()
    {
        // 0 位元組的檔案回傳 false（繼續找）
        var f = Path.Combine(_kit.TmpPath, "stub.jsonl");
        WriteText(f, "");
        Assert.False(ClaudeSessions.TryAppend(f, "data\n"));
        Assert.Equal("", ReadText(f)); // 沒有改
    }

    // Python: test_session_mutations.py::TestTryAppend::test_multiple_appends
    [Fact]
    public void TryAppendMultipleAppends()
    {
        // 多次附加依序寫在檔尾
        var f = Path.Combine(_kit.TmpPath, "test.jsonl");
        WriteText(f, "line1\n");
        ClaudeSessions.TryAppend(f, "line2\n");
        ClaudeSessions.TryAppend(f, "line3\n");
        Assert.Equal("line1\nline2\nline3\n", ReadText(f));
    }

    // ---- TestRenameSession ----

    // Python: test_session_mutations.py::TestRenameSession::test_invalid_session_id_raises
    [Fact]
    public void RenameInvalidSessionIdRaises()
    {
        Assert.Contains("Invalid session_id", Assert.Throws<ArgumentException>(() => Rename("not-a-uuid", "title")).Message, StringComparison.Ordinal);
        Assert.Contains("Invalid session_id", Assert.Throws<ArgumentException>(() => Rename("", "title")).Message, StringComparison.Ordinal);
    }

    // Python: test_session_mutations.py::TestRenameSession::test_empty_title_raises
    [Fact]
    public void RenameEmptyTitleRaises()
    {
        var (projectPath, projectDir) = Project();
        var (sid, _) = MakeSessionFile(projectDir);

        foreach (var title in new[] { "", "   ", "\n\t" })
        {
            var e = Assert.Throws<ArgumentException>(() => Rename(sid, title, projectPath));
            Assert.Contains("title must be non-empty", e.Message, StringComparison.Ordinal);
        }
    }

    // Python: test_session_mutations.py::TestRenameSession::test_session_not_found_raises
    [Fact]
    public void RenameSessionNotFoundRaises()
    {
        var (projectPath, _) = Project();
        Assert.Throws<FileNotFoundException>(() => Rename(NewUuid(), "title", projectPath));
    }

    // Python: test_session_mutations.py::TestRenameSession::test_no_projects_dir_raises
    [Fact]
    public void RenameNoProjectsDirRaises()
    {
        var e = Assert.Throws<FileNotFoundException>(
            () => ClaudeSessions.RenameSession(NewUuid(), "title", claudeConfigDir: Path.Combine(_kit.TmpPath, "nonexistent")));
        Assert.Contains("no projects directory", e.Message, StringComparison.Ordinal);
    }

    // Python: test_session_mutations.py::TestRenameSession::test_appends_custom_title_entry
    [Fact]
    public void RenameAppendsCustomTitleEntry()
    {
        var (projectPath, projectDir) = Project();
        var (sid, filePath) = MakeSessionFile(projectDir);

        Rename(sid, "My New Title", projectPath);

        // 最後一行是 custom-title
        var entry = LastEntry(filePath);
        Assert.Equal("custom-title", (string?)entry["type"]);
        Assert.Equal("My New Title", (string?)entry["customTitle"]);
        Assert.Equal(sid, (string?)entry["sessionId"]);
    }

    // Python: test_session_mutations.py::TestRenameSession::test_title_trimmed_before_storing
    [Fact]
    public void RenameTitleTrimmedBeforeStoring()
    {
        var (projectPath, projectDir) = Project();
        var (sid, filePath) = MakeSessionFile(projectDir);

        Rename(sid, "  Trimmed Title  ", projectPath);

        Assert.Equal("Trimmed Title", (string?)LastEntry(filePath)["customTitle"]);
    }

    // Python: test_session_mutations.py::TestRenameSession::test_last_wins_via_list_sessions
    [Fact]
    public void RenameLastWinsViaListSessions()
    {
        var (projectPath, projectDir) = Project();
        var (sid, _) = MakeSessionFile(projectDir, firstPrompt: "original");

        Rename(sid, "First Title", projectPath);
        Rename(sid, "Second Title", projectPath);
        Rename(sid, "Final Title", projectPath);

        var session = Assert.Single(List(projectPath, includeWorktrees: false));
        Assert.Equal("Final Title", session.CustomTitle);
        Assert.Equal("Final Title", session.Summary);
    }

    // Python: test_session_mutations.py::TestRenameSession::test_search_all_projects
    [Fact]
    public void RenameSearchAllProjects()
    {
        var (sid, filePath) = MakeSessionFile(_kit.MakeProjectDir("/some/project"));

        Rename(sid, "Found Without Dir");

        Assert.Equal("Found Without Dir", (string?)LastEntry(filePath)["customTitle"]);
    }

    // Python: test_session_mutations.py::TestRenameSession::test_skips_zero_byte_stub
    [Fact]
    public void RenameSkipsZeroByteStub()
    {
        // 前面那個資料夾的 0 位元組檔案跳過，後面資料夾的真檔案才是目標
        var projA = _kit.MakeProjectDir("/aaa/project");
        var projZ = _kit.MakeProjectDir("/zzz/project");
        var sid = NewUuid();
        WriteText(Path.Combine(projA, $"{sid}.jsonl"), "");
        MakeSessionFile(projZ, sessionId: sid, firstPrompt: "real");

        Rename(sid, "New Title");

        // 空檔案沒動
        Assert.Equal("", ReadText(Path.Combine(projA, $"{sid}.jsonl")));
        // 真檔案有那一行
        Assert.Contains("\"customTitle\":\"New Title\"", ReadText(Path.Combine(projZ, $"{sid}.jsonl")), StringComparison.Ordinal);
    }

    // Python: test_session_mutations.py::TestRenameSession::test_compact_json_format
    [Fact]
    public void RenameCompactJsonFormat()
    {
        // 附加的 JSON 用緊湊分隔（沒有空白），跟 CLI 一樣
        var (projectPath, projectDir) = Project();
        var (sid, filePath) = MakeSessionFile(projectDir);

        Rename(sid, "Title", projectPath);

        Assert.Equal($$"""{"type":"custom-title","customTitle":"Title","sessionId":"{{sid}}"}""", Lines(filePath)[^1]);
    }

    // ---- TestTagSession ----

    // Python: test_session_mutations.py::TestTagSession::test_invalid_session_id_raises
    [Fact]
    public void TagInvalidSessionIdRaises()
    {
        Assert.Contains("Invalid session_id", Assert.Throws<ArgumentException>(() => Tag("not-a-uuid", "tag")).Message, StringComparison.Ordinal);
        Assert.Contains("Invalid session_id", Assert.Throws<ArgumentException>(() => Tag("", "tag")).Message, StringComparison.Ordinal);
    }

    // Python: test_session_mutations.py::TestTagSession::test_empty_tag_raises
    [Fact]
    public void TagEmptyTagRaises()
    {
        var (projectPath, projectDir) = Project();
        var (sid, _) = MakeSessionFile(projectDir);

        Assert.Contains("tag must be non-empty", Assert.Throws<ArgumentException>(() => Tag(sid, "", projectPath)).Message, StringComparison.Ordinal);
        Assert.Contains("tag must be non-empty", Assert.Throws<ArgumentException>(() => Tag(sid, "   ", projectPath)).Message, StringComparison.Ordinal);
    }

    // Python: test_session_mutations.py::TestTagSession::test_session_not_found_raises
    [Fact]
    public void TagSessionNotFoundRaises()
    {
        var (projectPath, _) = Project();
        Assert.Throws<FileNotFoundException>(() => Tag(NewUuid(), "tag", projectPath));
    }

    // Python: test_session_mutations.py::TestTagSession::test_appends_tag_entry
    [Fact]
    public void TagAppendsTagEntry()
    {
        var (projectPath, projectDir) = Project();
        var (sid, filePath) = MakeSessionFile(projectDir);

        Tag(sid, "experiment", projectPath);

        var entry = LastEntry(filePath);
        Assert.Equal("tag", (string?)entry["type"]);
        Assert.Equal("experiment", (string?)entry["tag"]);
        Assert.Equal(sid, (string?)entry["sessionId"]);
    }

    // Python: test_session_mutations.py::TestTagSession::test_tag_trimmed
    [Fact]
    public void TagTrimmed()
    {
        var (projectPath, projectDir) = Project();
        var (sid, filePath) = MakeSessionFile(projectDir);

        Tag(sid, "  my-tag  ", projectPath);

        Assert.Equal("my-tag", (string?)LastEntry(filePath)["tag"]);
    }

    // Python: test_session_mutations.py::TestTagSession::test_none_clears_tag
    [Fact]
    public void TagNoneClearsTag()
    {
        // null 附加一行空字串的 tag（清除）
        var (projectPath, projectDir) = Project();
        var (sid, filePath) = MakeSessionFile(projectDir);

        Tag(sid, "original-tag", projectPath);
        Tag(sid, null, projectPath);

        var entry = LastEntry(filePath);
        Assert.Equal("tag", (string?)entry["type"]);
        Assert.Equal("", (string?)entry["tag"]);
        Assert.Equal(sid, (string?)entry["sessionId"]);
    }

    // Python: test_session_mutations.py::TestTagSession::test_last_wins
    [Fact]
    public void TagLastWins()
    {
        var (projectPath, projectDir) = Project();
        var (sid, filePath) = MakeSessionFile(projectDir);

        Tag(sid, "first", projectPath);
        Tag(sid, "second", projectPath);
        Tag(sid, "third", projectPath);

        var lines = Lines(filePath);
        Assert.Equal("third", (string?)((JsonObject)JsonNode.Parse(lines[^1])!)["tag"]);
        // 三行 tag 都在
        Assert.Equal(3, lines.Count(line => (string?)JsonNode.Parse(line)!["type"] == "tag"));
    }

    // Python: test_session_mutations.py::TestTagSession::test_compact_json_format
    [Fact]
    public void TagCompactJsonFormat()
    {
        var (projectPath, projectDir) = Project();
        var (sid, filePath) = MakeSessionFile(projectDir);

        Tag(sid, "mytag", projectPath);

        Assert.Equal($$"""{"type":"tag","tag":"mytag","sessionId":"{{sid}}"}""", Lines(filePath)[^1]);
    }

    // Python: test_session_mutations.py::TestTagSession::test_unicode_sanitization
    [Fact]
    public void TagUnicodeSanitization()
    {
        // 去掉零寬字元與 BOM
        var (projectPath, projectDir) = Project();
        var (sid, filePath) = MakeSessionFile(projectDir);

        Tag(sid, "clean\x200btag\xfeff", projectPath);

        Assert.Equal("cleantag", (string?)LastEntry(filePath)["tag"]);
    }

    // Python: test_session_mutations.py::TestTagSession::test_sanitization_rejects_pure_invisible
    [Fact]
    public void TagSanitizationRejectsPureInvisible()
    {
        var (projectPath, projectDir) = Project();
        var (sid, _) = MakeSessionFile(projectDir);

        var e = Assert.Throws<ArgumentException>(() => Tag(sid, "\x200b\x200c\xfeff", projectPath));
        Assert.Contains("tag must be non-empty", e.Message, StringComparison.Ordinal);
    }

    // ---- TestSanitizeUnicode ----

    // Python: test_session_mutations.py::TestSanitizeUnicode::test_passthrough_clean_string
    [Fact]
    public void SanitizePassthroughCleanString()
    {
        Assert.Equal("hello", ClaudeSessions.SanitizeUnicode("hello"));
        Assert.Equal("tag-with-dashes_123", ClaudeSessions.SanitizeUnicode("tag-with-dashes_123"));
    }

    // Python: test_session_mutations.py::TestSanitizeUnicode::test_strips_zero_width
    [Fact]
    public void SanitizeStripsZeroWidth()
    {
        Assert.Equal("ab", ClaudeSessions.SanitizeUnicode("a\x200bb"));
        Assert.Equal("ab", ClaudeSessions.SanitizeUnicode("a\x200cb")); // zero-width non-joiner
        Assert.Equal("ab", ClaudeSessions.SanitizeUnicode("a\x200db")); // zero-width joiner
    }

    // Python: test_session_mutations.py::TestSanitizeUnicode::test_strips_bom
    [Fact]
    public void SanitizeStripsBom()
    {
        Assert.Equal("hello", ClaudeSessions.SanitizeUnicode("\xfeffhello"));
    }

    // Python: test_session_mutations.py::TestSanitizeUnicode::test_strips_directional_marks
    [Fact]
    public void SanitizeStripsDirectionalMarks()
    {
        Assert.Equal("abc", ClaudeSessions.SanitizeUnicode("a\x202ab\x202cc"));
        Assert.Equal("abc", ClaudeSessions.SanitizeUnicode("a\x2066b\x2069c"));
    }

    // Python: test_session_mutations.py::TestSanitizeUnicode::test_strips_private_use
    [Fact]
    public void SanitizeStripsPrivateUse()
    {
        Assert.Equal("ab", ClaudeSessions.SanitizeUnicode("a\xe000b"));
        Assert.Equal("ab", ClaudeSessions.SanitizeUnicode("a\xf8ffb"));
    }

    // Python: test_session_mutations.py::TestSanitizeUnicode::test_nfkc_normalization
    [Fact]
    public void SanitizeNfkcNormalization()
    {
        // 全形 'Ａ' → ASCII 'A'
        Assert.Equal("A", ClaudeSessions.SanitizeUnicode("\xff21"));
    }

    // Python: test_session_mutations.py::TestSanitizeUnicode::test_iterative_converges
    [Fact]
    public void SanitizeIterativeConverges()
    {
        Assert.Equal("ab", ClaudeSessions.SanitizeUnicode("a" + new string('\x200b', 20) + "b"));
    }

    // ---- TestDeleteSession ----

    // Python: test_session_mutations.py::TestDeleteSession::test_invalid_session_id_raises
    [Fact]
    public void DeleteInvalidSessionIdRaises()
    {
        Assert.Contains("Invalid session_id", Assert.Throws<ArgumentException>(() => Delete("not-a-uuid")).Message, StringComparison.Ordinal);
    }

    // Python: test_session_mutations.py::TestDeleteSession::test_session_not_found_raises
    [Fact]
    public void DeleteSessionNotFoundRaises()
    {
        Assert.Contains("not found", Assert.Throws<FileNotFoundException>(() => Delete(NewUuid())).Message, StringComparison.Ordinal);
    }

    // Python: test_session_mutations.py::TestDeleteSession::test_deletes_session_file
    [Fact]
    public void DeleteDeletesSessionFile()
    {
        var (projectPath, projectDir) = Project();
        var (sid, filePath) = MakeSessionFile(projectDir);
        Assert.True(File.Exists(filePath));
        Delete(sid, projectPath);
        Assert.False(File.Exists(filePath));
    }

    // Python: test_session_mutations.py::TestDeleteSession::test_removes_subagent_transcript_dir
    [Fact]
    public void DeleteRemovesSubagentTranscriptDir()
    {
        // 連旁邊的 {sid}/ subagent 資料夾一起刪
        var (projectPath, projectDir) = Project();
        var (sid, filePath) = MakeSessionFile(projectDir);
        var subagentDir = Directory.CreateDirectory(Path.Combine(projectDir, sid)).FullName;
        WriteText(Path.Combine(subagentDir, $"{NewUuid()}.jsonl"), "{}\n");

        Delete(sid, projectPath);

        Assert.False(File.Exists(filePath));
        Assert.False(Directory.Exists(subagentDir));
    }

    // Python: test_session_mutations.py::TestDeleteSession::test_deletes_without_directory
    [Fact]
    public void DeleteDeletesWithoutDirectory()
    {
        var (sid, filePath) = MakeSessionFile(_kit.MakeProjectDir("/any/project"));
        Assert.True(File.Exists(filePath));
        Delete(sid);
        Assert.False(File.Exists(filePath));
    }

    // Python: test_session_mutations.py::TestDeleteSession::test_no_longer_in_list_sessions
    [Fact]
    public void DeleteNoLongerInListSessions()
    {
        var (projectPath, projectDir) = Project();
        var (sid, _) = MakeSessionFile(projectDir);
        Assert.Contains(List(projectPath), s => s.SessionId == sid);
        Delete(sid, projectPath);
        Assert.DoesNotContain(List(projectPath), s => s.SessionId == sid);
    }

    // ---- TestForkSession ----

    // Python: test_session_mutations.py::TestForkSession::test_invalid_session_id_raises
    [Fact]
    public void ForkInvalidSessionIdRaises()
    {
        Assert.Contains("Invalid session_id", Assert.Throws<ArgumentException>(() => Fork("not-a-uuid")).Message, StringComparison.Ordinal);
    }

    // Python: test_session_mutations.py::TestForkSession::test_session_not_found_raises
    [Fact]
    public void ForkSessionNotFoundRaises()
    {
        Assert.Contains("not found", Assert.Throws<FileNotFoundException>(() => Fork(NewUuid())).Message, StringComparison.Ordinal);
    }

    // Python: test_session_mutations.py::TestForkSession::test_invalid_up_to_message_id_raises
    [Fact]
    public void ForkInvalidUpToMessageIdRaises()
    {
        var e = Assert.Throws<ArgumentException>(() => Fork(NewUuid(), upToMessageId: "not-valid"));
        Assert.Contains("Invalid up_to_message_id", e.Message, StringComparison.Ordinal);
    }

    // Python: test_session_mutations.py::TestForkSession::test_fork_creates_new_session
    [Fact]
    public void ForkCreatesNewSession()
    {
        var (projectPath, projectDir) = Project();
        var (sid, _, _) = MakeTranscriptSession(projectDir);

        var result = Fork(sid, projectPath);
        Assert.NotEqual(sid, result.SessionId);

        // 新檔案存在
        Assert.True(File.Exists(Path.Combine(projectDir, $"{result.SessionId}.jsonl")));
    }

    // Python: test_session_mutations.py::TestForkSession::test_fork_remaps_uuids
    [Fact]
    public void ForkRemapsUuids()
    {
        // uuid 與 parentUuid 都換掉；原本的只出現在 forkedFrom
        var (projectPath, projectDir) = Project();
        var (sid, _, originalUuids) = MakeTranscriptSession(projectDir);

        var result = Fork(sid, projectPath);
        foreach (var line in Lines(Path.Combine(projectDir, $"{result.SessionId}.jsonl")))
        {
            var entry = JsonNode.Parse(line)!;
            if ((string?)entry["type"] is "user" or "assistant")
            {
                Assert.DoesNotContain((string)entry["uuid"]!, originalUuids);
                if (entry["parentUuid"] is not null)
                {
                    Assert.DoesNotContain((string)entry["parentUuid"]!, originalUuids);
                }
            }
        }
    }

    // Python: test_session_mutations.py::TestForkSession::test_fork_preserves_message_count
    [Fact]
    public void ForkPreservesMessageCount()
    {
        var (projectPath, projectDir) = Project();
        var (sid, _, _) = MakeTranscriptSession(projectDir, numTurns: 3);

        var result = Fork(sid, projectPath);

        var originalMessages = ClaudeSessions.GetSessionMessages(sid, projectPath, claudeConfigDir: _kit.ConfigDir);
        var forkMessages = ClaudeSessions.GetSessionMessages(result.SessionId, projectPath, claudeConfigDir: _kit.ConfigDir);
        // 分支的看得見的訊息一樣多（多出的 custom-title 看不見）
        Assert.Equal(originalMessages.Count, forkMessages.Count);
    }

    // Python: test_session_mutations.py::TestForkSession::test_fork_up_to_message_id
    [Fact]
    public void ForkUpToMessageId()
    {
        var (projectPath, projectDir) = Project();
        var (sid, _, uuids) = MakeTranscriptSession(projectDir, numTurns: 3);

        // 到第一個助理回應為止（uuid 索引 1）
        var result = Fork(sid, projectPath, upToMessageId: uuids[1]);

        // 第一輪的 1 則使用者 + 1 則助理
        Assert.Equal(2, ClaudeSessions.GetSessionMessages(result.SessionId, projectPath, claudeConfigDir: _kit.ConfigDir).Count);
    }

    // Python: test_session_mutations.py::TestForkSession::test_fork_up_to_message_id_not_found_raises
    [Fact]
    public void ForkUpToMessageIdNotFoundRaises()
    {
        var (projectPath, projectDir) = Project();
        var (sid, _, _) = MakeTranscriptSession(projectDir);

        var e = Assert.Throws<ArgumentException>(() => Fork(sid, projectPath, upToMessageId: NewUuid()));
        Assert.Contains("not found in session", e.Message, StringComparison.Ordinal);
    }

    // Python: test_session_mutations.py::TestForkSession::test_fork_custom_title
    [Fact]
    public void ForkCustomTitle()
    {
        var (projectPath, projectDir) = Project();
        var (sid, _, _) = MakeTranscriptSession(projectDir);

        var result = Fork(sid, projectPath, title: "My Fork");

        Assert.Equal("My Fork", List(projectPath).First(s => s.SessionId == result.SessionId).CustomTitle);
    }

    // Python: test_session_mutations.py::TestForkSession::test_fork_default_title_has_suffix
    [Fact]
    public void ForkDefaultTitleHasSuffix()
    {
        // 預設標題是原本的加上「 (fork)」
        var (projectPath, projectDir) = Project();
        var (sid, _, _) = MakeTranscriptSession(projectDir);

        var result = Fork(sid, projectPath);

        var forkInfo = List(projectPath).First(s => s.SessionId == result.SessionId);
        Assert.NotNull(forkInfo.CustomTitle);
        Assert.EndsWith("(fork)", forkInfo.CustomTitle, StringComparison.Ordinal);
    }

    // Python: test_session_mutations.py::TestForkSession::test_fork_session_id_in_entries
    [Fact]
    public void ForkSessionIdInEntries()
    {
        // 分支裡每一行都是新的 session ID
        var (projectPath, projectDir) = Project();
        var (sid, _, _) = MakeTranscriptSession(projectDir);

        var result = Fork(sid, projectPath);
        foreach (var line in Lines(Path.Combine(projectDir, $"{result.SessionId}.jsonl")))
        {
            Assert.Equal(result.SessionId, (string?)JsonNode.Parse(line)!["sessionId"]);
        }
    }

    // Python: test_session_mutations.py::TestForkSession::test_fork_forked_from_field
    [Fact]
    public void ForkForkedFromField()
    {
        // 紀錄有 forkedFrom 指回來源
        var (projectPath, projectDir) = Project();
        var (sid, _, _) = MakeTranscriptSession(projectDir);

        var result = Fork(sid, projectPath);
        foreach (var line in Lines(Path.Combine(projectDir, $"{result.SessionId}.jsonl")))
        {
            var entry = JsonNode.Parse(line)!;
            if ((string?)entry["type"] is "user" or "assistant")
            {
                Assert.Equal(sid, (string?)entry["forkedFrom"]!["sessionId"]);
            }
        }
    }

    // Python: test_session_mutations.py::TestForkSession::test_fork_without_directory
    [Fact]
    public void ForkWithoutDirectory()
    {
        var projectDir = _kit.MakeProjectDir("/any/project");
        var (sid, _, _) = MakeTranscriptSession(projectDir);

        var result = Fork(sid);
        Assert.True(File.Exists(Path.Combine(projectDir, $"{result.SessionId}.jsonl")));
    }

    // Python: test_session_mutations.py::TestForkSession::test_fork_clears_stale_fields
    [Fact]
    public void ForkClearsStaleFields()
    {
        // teamName、agentName、slug 從分支的紀錄拿掉
        var (projectPath, projectDir) = Project();
        var sid = NewUuid();
        var entry = new JsonObject
        {
            ["type"] = "user",
            ["uuid"] = NewUuid(),
            ["parentUuid"] = null,
            ["sessionId"] = sid,
            ["timestamp"] = "2026-03-01T00:00:00Z",
            ["teamName"] = "test-team",
            ["agentName"] = "test-agent",
            ["slug"] = "test-slug",
            ["message"] = new JsonObject { ["role"] = "user", ["content"] = "Hello" },
        };
        WriteText(Path.Combine(projectDir, $"{sid}.jsonl"), Dumps(entry) + "\n");

        var result = Fork(sid, projectPath);
        foreach (var line in Lines(Path.Combine(projectDir, $"{result.SessionId}.jsonl")))
        {
            var e = (JsonObject)JsonNode.Parse(line)!;
            if ((string?)e["type"] == "user")
            {
                Assert.False(e.ContainsKey("teamName"));
                Assert.False(e.ContainsKey("agentName"));
                Assert.False(e.ContainsKey("slug"));
            }
        }
    }
}
