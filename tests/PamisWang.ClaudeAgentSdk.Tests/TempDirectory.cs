namespace PamisWang.ClaudeAgentSdk.Tests;

/// <summary>測試用的暫存資料夾，Dispose 時整個刪掉。</summary>
internal sealed class TempDirectory : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("claude-agent-sdk-tests-").FullName;

    public string CreateDirectory(params string[] parts) =>
        Directory.CreateDirectory(System.IO.Path.Combine([Path, .. parts])).FullName;

    /// <summary>建立空檔案（需要的資料夾一併建立），回傳完整路徑。</summary>
    public string CreateFile(params string[] parts)
    {
        var path = System.IO.Path.Combine([Path, .. parts]);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, []);
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // 被強制結束的程序可能還沒放開資料夾；留在暫存區不影響測試結果
        }
    }
}
