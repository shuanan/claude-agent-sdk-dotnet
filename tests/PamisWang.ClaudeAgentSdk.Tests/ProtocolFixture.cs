using System.Text.Json;

namespace PamisWang.ClaudeAgentSdk.Tests;

/// <summary>錄下來的協定樣本（Fixtures/*.jsonl），格式見 Fixtures/README.md。</summary>
internal sealed class ProtocolFixture
{
    private static readonly string FixturesDirectory = Path.Combine(AppContext.BaseDirectory, "Fixtures");

    private ProtocolFixture(JsonElement meta, IReadOnlyList<(string Direction, JsonElement Data)> entries)
    {
        Meta = meta;
        Entries = entries;
    }

    /// <summary>第一行：情境、版本、CLI 參數。</summary>
    public JsonElement Meta { get; }

    /// <summary>依收發順序的 send 與 recv；note 是錄製腳本的註記，不在這裡。</summary>
    public IReadOnlyList<(string Direction, JsonElement Data)> Entries { get; }

    /// <summary>從 CLI 讀到的訊息。</summary>
    public IReadOnlyList<JsonElement> Received => [.. Entries.Where(e => e.Direction == "recv").Select(e => e.Data)];

    public static TheoryData<string> Names()
    {
        var names = new TheoryData<string>();
        foreach (var path in Directory.GetFiles(FixturesDirectory, "*.jsonl").Order(StringComparer.Ordinal))
        {
            names.Add(Path.GetFileNameWithoutExtension(path));
        }

        return names;
    }

    public static ProtocolFixture Load(string name)
    {
        var lines = File.ReadAllLines(Path.Combine(FixturesDirectory, name + ".jsonl"))
            .Where(line => line.Length > 0)
            .Select(line => JsonSerializer.Deserialize<JsonElement>(line))
            .ToList();
        var entries = lines
            .Where(line => line.GetProperty("dir").GetString() is "send" or "recv")
            .Select(line => (line.GetProperty("dir").GetString()!, line.GetProperty("data")))
            .ToList();
        return new ProtocolFixture(lines[0], entries);
    }
}
