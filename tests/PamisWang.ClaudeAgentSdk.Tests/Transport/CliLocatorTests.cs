using PamisWang.ClaudeAgentSdk.Transport;

namespace PamisWang.ClaudeAgentSdk.Tests.Transport;

public sealed class CliLocatorTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Find_ExplicitCliPath_ReturnsItUnchanged()
    {
        var locator = new CliLocator(pathVariable: null, _temp.Path, isWindows: true);

        Assert.Equal(@"D:\tools\claude.exe", locator.Find(@"D:\tools\claude.exe"));
    }

    [Fact]
    public void Find_OnWindows_PrefersNativeExeOverEarlierNpmShim()
    {
        var npm = Path.GetDirectoryName(_temp.CreateFile("npm", "claude.cmd"))!;
        var native = _temp.CreateFile("native", "claude.exe");
        var locator = new CliLocator(JoinPath(npm, Path.GetDirectoryName(native)!), Home(), isWindows: true);

        Assert.Equal(native, locator.Find(null));
    }

    [Fact]
    public void Find_OnWindows_FallsBackToLocalBin()
    {
        var home = Home();
        var native = _temp.CreateFile("home", ".local", "bin", "claude.exe");
        var locator = new CliLocator(pathVariable: "", home, isWindows: true);

        Assert.Equal(native, locator.Find(null));
    }

    [Fact]
    public void Find_OnWindows_OnlyNpmShim_ReturnsShimSoConnectExplainsRefusal()
    {
        var shim = _temp.CreateFile("npm", "claude.cmd");
        var locator = new CliLocator(Path.GetDirectoryName(shim), Home(), isWindows: true);

        Assert.Equal(shim, locator.Find(null));
        Assert.Throws<CliConnectionException>(() => locator.EnsureRunnable(shim));
    }

    [Fact]
    public void Find_OnWindows_IgnoresExtensionlessScript()
    {
        // git-bash 或 WSL 留下的 claude 腳本，CreateProcess 執行不了
        var script = _temp.CreateFile("bin", "claude");
        var locator = new CliLocator(Path.GetDirectoryName(script), Home(), isWindows: true);

        var error = Assert.Throws<CliNotFoundException>(() => locator.Find(null));
        Assert.Contains("install.ps1", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Find_OnUnix_FindsClaudeOnPath()
    {
        var empty = _temp.CreateDirectory("empty");
        var claude = _temp.CreateFile("bin", "claude");
        var locator = new CliLocator(JoinPath(empty, Path.GetDirectoryName(claude)!), Home(), isWindows: false);

        Assert.Equal(claude, locator.Find(null));
    }

    [Fact]
    public void Find_OnUnix_FallsBackToKnownLocations()
    {
        var home = Home();
        var claude = _temp.CreateFile("home", ".claude", "local", "claude");
        var locator = new CliLocator(pathVariable: null, home, isWindows: false);

        Assert.Equal(claude, locator.Find(null));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Find_NothingInstalled_ThrowsCliNotFound(bool isWindows)
    {
        // Unix 的候選位置有一個是固定的 /usr/local/bin/claude，這台機器真的裝在那裡就測不了
        if (!isWindows && File.Exists("/usr/local/bin/claude"))
        {
            return;
        }

        var locator = new CliLocator(_temp.CreateDirectory("empty"), Home(), isWindows);

        Assert.Throws<CliNotFoundException>(() => locator.Find(null));
    }

    [Theory]
    [InlineData("claude.cmd")]
    [InlineData(@"C:\npm\claude.CMD")]
    [InlineData("claude.bat")]
    [InlineData("claude.cmd.")]
    [InlineData("claude.cmd ")]
    [InlineData(".cmd")]
    [InlineData(@"C:\tools.cmd\claude.exe")]
    [InlineData(@"C:\npm\claude.cmd\..\..\claude.exe")]
    [InlineData("C:claude.cmd")]
    [InlineData("claude.cmd:stream")]
    [InlineData("claude:evil.cmd")]
    public void IsWindowsBatchPath_AnyBatchSegment_IsBatch(string path)
    {
        Assert.True(CliLocator.IsWindowsBatchPath(path));
    }

    [Theory]
    [InlineData("claude.exe")]
    [InlineData(@"C:\Users\me\.local\bin\claude.exe")]
    [InlineData("claude")]
    [InlineData(@"C:\cmdtools\claude.exe")]
    [InlineData("/usr/local/bin/claude")]
    public void IsWindowsBatchPath_NativeExecutable_IsNotBatch(string path)
    {
        Assert.False(CliLocator.IsWindowsBatchPath(path));
    }

    [Fact]
    public void EnsureRunnable_OffWindows_AllowsBatchLookingPath()
    {
        var locator = new CliLocator(pathVariable: null, Home(), isWindows: false);

        locator.EnsureRunnable("/opt/claude.cmd");
    }

    private string Home() => _temp.CreateDirectory("home");

    private static string JoinPath(params string[] directories) => string.Join(Path.PathSeparator, directories);
}
