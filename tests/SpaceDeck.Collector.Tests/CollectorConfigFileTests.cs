using System.Text.Json.Nodes;

namespace SpaceDeck.Collector.Tests;

public sealed class CollectorConfigFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "spacedeck-cfg-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void Save_CreatesFileWithCollectorSection()
    {
        var path = Path.Combine(_dir, "collector.json");
        CollectorConfigFile.Save(path, new CollectorOptions { PollIntervalMs = 500, StorageEnabled = true });

        var section = JsonNode.Parse(File.ReadAllText(path))!["Collector"]!;
        Assert.Equal(500, section["PollIntervalMs"]!.GetValue<int>());
        Assert.True(section["StorageEnabled"]!.GetValue<bool>());
        Assert.False(section["ControllersEnabled"]!.GetValue<bool>());
    }

    [Fact]
    public void Save_KeepsOtherSettings()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "collector.json");
        File.WriteAllText(path, """{ "Collector": { "HistorySeconds": 7200, "PipeName": "Test" }, "Logging": { "LogLevel": { "Default": "Debug" } } }""");

        CollectorConfigFile.Save(path, new CollectorOptions { PollIntervalMs = 2000 });

        var root = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Equal(2000, root["Collector"]!["PollIntervalMs"]!.GetValue<int>());
        Assert.Equal(7200, root["Collector"]!["HistorySeconds"]!.GetValue<int>());
        Assert.Equal("Test", root["Collector"]!["PipeName"]!.GetValue<string>());
        Assert.Equal("Debug", root["Logging"]!["LogLevel"]!["Default"]!.GetValue<string>());
    }

    [Fact]
    public void Save_ReplacesCorruptFile()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "collector.json");
        File.WriteAllText(path, "{ not json");

        CollectorConfigFile.Save(path, new CollectorOptions { NetworkEnabled = true });

        Assert.True(JsonNode.Parse(File.ReadAllText(path))!["Collector"]!["NetworkEnabled"]!.GetValue<bool>());
        Assert.Equal("{ not json", File.ReadAllText(path + ".bak"));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Save_AcceptsCommentsAndReusesKeyCasing()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "collector.json");
        File.WriteAllText(path, """
            {
              // hand-edited
              "collector": { "pollIntervalMs": 1000, "HistorySeconds": 600, },
              "Logging": { "LogLevel": { "Default": "Debug" } },
            }
            """);

        CollectorConfigFile.Save(path, new CollectorOptions { PollIntervalMs = 250 });

        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.Equal(new[] { "collector", "Logging" }, root.Select(kv => kv.Key));
        var section = root["collector"]!.AsObject();
        Assert.Equal(250, section["pollIntervalMs"]!.GetValue<int>());
        Assert.DoesNotContain(section, kv => kv.Key == "PollIntervalMs");
        Assert.Equal(600, section["HistorySeconds"]!.GetValue<int>());
        Assert.Equal("Debug", root["Logging"]!["LogLevel"]!["Default"]!.GetValue<string>());
    }
}
