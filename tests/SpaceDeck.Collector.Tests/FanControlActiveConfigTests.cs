using System.Text;
using SpaceDeck.Integrations.FanControl;

namespace SpaceDeck.Collector.Tests;

/// <summary>Reading the loaded config from FanControl's Configurations\CACHE file.</summary>
public class FanControlActiveConfigTests
{
    private static string? Parse(string json) => FanControlConfigs.ParseActive(new MemoryStream(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public void ReadsCurrentConfigFileName()
    {
        Assert.Equal("Basic Ramps", Parse("""{ "SomethingElse": 1, "CurrentConfigFileName": "Basic Ramps.json" }"""));
    }

    [Fact]
    public void AcceptsAFullPathAndAnyCase()
    {
        Assert.Equal("Quiet", Parse("""{ "currentconfigfilename": "C:\\Program Files (x86)\\FanControl\\Configurations\\Quiet.JSON" }"""));
    }

    [Theory]
    [InlineData("""{ "CurrentConfigFileName": "" }""")]
    [InlineData("""{ "CurrentConfigFileName": null }""")]
    [InlineData("""{ "CurrentConfigFileName": 5 }""")]
    [InlineData("""{ "Other": "Quiet.json" }""")]
    [InlineData("""[ "Quiet.json" ]""")]
    [InlineData("""{ "CurrentConfigFileName": "Qui""")] // half-written
    [InlineData("")]
    public void MissingOrOddValues_GiveNull(string json)
    {
        Assert.Null(Parse(json));
    }

    [Fact]
    public void ReadActive_UsesTheConfigurationsFolder()
    {
        var dir = Directory.CreateTempSubdirectory("sd-fc-");
        try
        {
            var exe = Path.Combine(dir.FullName, "FanControl.exe");
            File.WriteAllBytes(exe, []);
            var install = new FanControlInstall(exe, false, "test");
            Assert.Null(FanControlConfigs.ReadActive(install)); // no Configurations folder yet

            Directory.CreateDirectory(Path.Combine(dir.FullName, "Configurations"));
            File.WriteAllText(Path.Combine(dir.FullName, "Configurations", "CACHE"), """{ "CurrentConfigFileName": "50 Percent.json" }""");
            Assert.Equal("50 Percent", FanControlConfigs.ReadActive(install));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
