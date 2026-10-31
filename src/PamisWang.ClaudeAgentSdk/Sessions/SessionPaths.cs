using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace PamisWang.ClaudeAgentSdk.Sessions;

/// <summary>
/// session 檔案的位置：設定資料夾（<c>CLAUDE_CONFIG_DIR</c>）、專案資料夾的命名（跟 CLI 一樣的 sanitize 與雜湊）、
/// 路徑正規化（realpath + NFC）與 git worktree。對應 Python <c>_internal/sessions.py</c> 的同名函式。
/// </summary>
internal static partial class SessionPaths
{
    /// <summary>單一路徑元件的上限；大部分檔案系統是 255 位元組，留空間給雜湊後綴。對應 <c>MAX_SANITIZED_LENGTH</c>。</summary>
    public const int MaxSanitizedLength = 200;

    [GeneratedRegex("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UuidRegex();

    /// <summary>是 UUID 時回傳原字串，否則 <see langword="null"/>。對應 <c>_validate_uuid</c>（Python 的 <c>$</c> 也接受結尾一個 \n）。</summary>
    public static string? ValidateUuid(string maybeUuid) => UuidRegex().IsMatch(maybeUuid) ? maybeUuid : null;

    /// <summary>32 位元整數雜湊轉 36 進位，跟 CLI 的資料夾命名一樣。對應 <c>_simple_hash</c>（逐個 code point）。</summary>
    public static string SimpleHash(string value)
    {
        var hash = 0;
        foreach (var rune in EnumerateCodePoints(value))
        {
            hash = unchecked((hash << 5) - hash + rune);
        }

        var n = Math.Abs((long)hash);
        if (n == 0)
        {
            return "0";
        }

        const string Digits = "0123456789abcdefghijklmnopqrstuvwxyz";
        var builder = new StringBuilder();
        while (n > 0)
        {
            builder.Insert(0, Digits[(int)(n % 36)]);
            n /= 36;
        }

        return builder.ToString();
    }

    /// <summary>
    /// 讓字串可以當資料夾名稱：每個非英數字的 code point 換成 <c>-</c>；超過 200 字元時截斷並加上雜湊後綴。
    /// 對應 <c>_sanitize_path</c>。
    /// </summary>
    public static string SanitizePath(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var codePoint in EnumerateCodePoints(name))
        {
            builder.Append(codePoint is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') ? (char)codePoint : '-');
        }

        var sanitized = builder.ToString();
        if (sanitized.Length <= MaxSanitizedLength)
        {
            return sanitized;
        }

        return $"{sanitized[..MaxSanitizedLength]}-{SimpleHash(name)}";
    }

    private static IEnumerable<int> EnumerateCodePoints(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                yield return char.ConvertToUtf32(c, value[i + 1]);
                i++;
            }
            else
            {
                yield return c;
            }
        }
    }

    /// <summary>
    /// 專案資料夾所在的 <c>projects</c>。<paramref name="claudeConfigDir"/> 有值時優先（等同 Python 的 <c>env_override</c>），
    /// 否則是 <c>CLAUDE_CONFIG_DIR</c>，再來是 <c>~/.claude</c>；都經過 NFC。對應 <c>_get_projects_dir</c>。
    /// </summary>
    public static string ProjectsDir(string? claudeConfigDir)
    {
        if (!string.IsNullOrEmpty(claudeConfigDir))
        {
            return Path.Combine(claudeConfigDir.Normalize(NormalizationForm.FormC), "projects");
        }

        var configDir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        if (!string.IsNullOrEmpty(configDir))
        {
            return Path.Combine(configDir.Normalize(NormalizationForm.FormC), "projects");
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(Path.Combine(home, ".claude").Normalize(NormalizationForm.FormC), "projects");
    }

    /// <summary>realpath + NFC。對應 <c>_canonicalize_path</c>。</summary>
    public static string CanonicalizePath(string directory)
    {
        try
        {
            return RealPath(directory).Normalize(NormalizationForm.FormC);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return directory.Normalize(NormalizationForm.FormC);
        }
    }

    /// <summary>
    /// Python 的 <c>os.path.realpath</c>（非 strict）：逐段解開符號連結、處理 <c>..</c>，不存在的部分照字面保留，最後轉成絕對路徑。
    /// Windows 上用 <see cref="Path.GetFullPath(string)"/> 再逐段解開連結，不像 Python 那樣改成檔案系統上的大小寫。
    /// </summary>
    public static string RealPath(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return WindowsRealPath(path);
        }

        var seen = new Dictionary<string, string?>(StringComparer.Ordinal);
        var (resolved, _) = JoinRealPath("", path, seen);
        return PosixAbsPath(resolved);
    }

    private static (string Path, bool Ok) JoinRealPath(string path, string rest, Dictionary<string, string?> seen)
    {
        if (rest.StartsWith('/'))
        {
            rest = rest[1..];
            path = "/";
        }

        while (rest.Length > 0)
        {
            var slash = rest.IndexOf('/', StringComparison.Ordinal);
            var name = slash >= 0 ? rest[..slash] : rest;
            rest = slash >= 0 ? rest[(slash + 1)..] : "";
            if (name.Length == 0 || name == ".")
            {
                continue;
            }

            if (name == "..")
            {
                if (path.Length > 0)
                {
                    var (head, tail) = PosixSplit(path);
                    path = tail == ".." ? PosixJoin(PosixJoin(head, ".."), "..") : head;
                }
                else
                {
                    path = "..";
                }

                continue;
            }

            var newPath = PosixJoin(path, name);
            var target = ReadLinkOrNull(newPath);
            if (target is null)
            {
                path = newPath;
                continue;
            }

            if (seen.TryGetValue(newPath, out var known))
            {
                if (known is not null)
                {
                    path = known;
                    continue;
                }

                // 連結還沒解開又遇到：符號連結迴圈
                return (PosixJoin(newPath, rest), false);
            }

            seen[newPath] = null;
            var (inner, ok) = JoinRealPath(path, target, seen);
            path = inner;
            if (!ok)
            {
                return (PosixJoin(path, rest), false);
            }

            seen[newPath] = path;
        }

        return (path, true);
    }

    private static string? ReadLinkOrNull(string path)
    {
        try
        {
            // 跟 os.lstat 一樣不跟著連結走；不存在或不是連結都是 null
            return new FileInfo(path).LinkTarget;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static string PosixJoin(string a, string b)
    {
        if (b.StartsWith('/'))
        {
            return b;
        }

        if (a.Length == 0 || a.EndsWith('/'))
        {
            return a + b;
        }

        return a + "/" + b;
    }

    private static (string Head, string Tail) PosixSplit(string path)
    {
        var i = path.LastIndexOf('/') + 1;
        var head = path[..i];
        var tail = path[i..];
        if (head.Length > 0 && head != new string('/', head.Length))
        {
            head = head.TrimEnd('/');
        }

        return (head, tail);
    }

    private static string PosixAbsPath(string path)
    {
        if (!path.StartsWith('/'))
        {
            path = PosixJoin(Directory.GetCurrentDirectory(), path);
        }

        return PosixNormPath(path);
    }

    /// <summary>Python 的 <c>posixpath.normpath</c>。</summary>
    private static string PosixNormPath(string path)
    {
        if (path.Length == 0)
        {
            return ".";
        }

        var initialSlashes = path.StartsWith('/') ? (path.StartsWith("//", StringComparison.Ordinal) && !path.StartsWith("///", StringComparison.Ordinal) ? 2 : 1) : 0;
        var parts = new List<string>();
        foreach (var part in path.Split('/'))
        {
            if (part.Length == 0 || part == ".")
            {
                continue;
            }

            if (part != ".." || (initialSlashes == 0 && parts.Count == 0) || (parts.Count > 0 && parts[^1] == ".."))
            {
                parts.Add(part);
            }
            else if (parts.Count > 0)
            {
                parts.RemoveAt(parts.Count - 1);
            }
        }

        var result = new string('/', initialSlashes) + string.Join('/', parts);
        return result.Length == 0 ? "." : result;
    }

    private static string WindowsRealPath(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? "";
        var current = root;
        var parts = full[root.Length..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            current = Path.Combine(current, parts[i]);
            try
            {
                var info = new FileInfo(current);
                if (info.LinkTarget is not null && info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
                {
                    current = target.FullName;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // 解不開就照字面留著，跟 Python 的非 strict realpath 一樣
            }
        }

        return current;
    }

    /// <summary>專案路徑對應的資料夾（不檢查是否存在）。</summary>
    public static string ProjectDir(string projectsDir, string projectPath) => Path.Combine(projectsDir, SanitizePath(projectPath));

    /// <summary>
    /// 找專案資料夾。長路徑（超過 200 字元）的雜湊後綴可能跟 CLI（Bun.hash）不同，精確比對失敗時改用前綴找。
    /// 對應 <c>_find_project_dir</c>。
    /// </summary>
    public static string? FindProjectDir(string projectsDir, string projectPath)
    {
        var exact = ProjectDir(projectsDir, projectPath);
        if (Directory.Exists(exact))
        {
            return exact;
        }

        var sanitized = SanitizePath(projectPath);
        if (sanitized.Length <= MaxSanitizedLength)
        {
            return null;
        }

        var prefix = sanitized[..MaxSanitizedLength] + "-";
        foreach (var entry in ListDirectory(projectsDir) ?? [])
        {
            if (Directory.Exists(entry) && Path.GetFileName(entry).StartsWith(prefix, StringComparison.Ordinal))
            {
                return entry;
            }
        }

        return null;
    }

    /// <summary>
    /// 資料夾裡的所有項目（檔案與資料夾，順序跟 Python 的 <c>iterdir()</c> 一樣是檔案系統的順序）；
    /// 讀不到時回傳 <see langword="null"/>（Python 的 <c>OSError</c>）。
    /// </summary>
    public static List<string>? ListDirectory(string directory)
    {
        try
        {
            return [.. Directory.EnumerateFileSystemEntries(directory)];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>資料夾裡的子資料夾（跟著符號連結判斷，同 Python 的 <c>is_dir()</c>）；讀不到時回傳 <see langword="null"/>。</summary>
    public static List<string>? ListSubdirectories(string directory) =>
        ListDirectory(directory)?.Where(Directory.Exists).ToList();

    /// <summary>測試用：取代 <see cref="GetWorktreePaths"/> 的結果，只影響同一個非同步流程（不會干擾同時跑的其他測試）。</summary>
    internal static readonly AsyncLocal<Func<string, List<string>>?> WorktreePathsOverride = new();

    /// <summary>
    /// <paramref name="cwd"/> 所在 git repo 的所有 worktree 路徑（<c>git worktree list --porcelain</c>，最多等 5 秒）。
    /// 沒有 git、不在 repo 裡或任何錯誤都回傳空的清單。對應 <c>_get_worktree_paths</c>。
    /// </summary>
    public static List<string> GetWorktreePaths(string cwd)
    {
        if (WorktreePathsOverride.Value is { } provider)
        {
            return provider(cwd);
        }

        string stdout;
        try
        {
            var startInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            startInfo.ArgumentList.Add("worktree");
            startInfo.ArgumentList.Add("list");
            startInfo.ArgumentList.Add("--porcelain");

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return [];
            }

            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(TimeSpan.FromSeconds(5)))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // 已經結束
                }

                return [];
            }

            process.WaitForExit();
            stdout = output.GetAwaiter().GetResult();
            _ = error.GetAwaiter().GetResult();
            if (process.ExitCode != 0 || stdout.Length == 0)
            {
                return [];
            }
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException
                                      or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException)
        {
            return [];
        }

        var paths = new List<string>();
        foreach (var line in PyText.TranslateNewlines(stdout).Split('\n'))
        {
            if (line.StartsWith("worktree ", StringComparison.Ordinal))
            {
                paths.Add(line["worktree ".Length..].Normalize(NormalizationForm.FormC));
            }
        }

        return paths;
    }
}
