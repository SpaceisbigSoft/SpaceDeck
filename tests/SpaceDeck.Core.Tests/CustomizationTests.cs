using SpaceDeck.Core.Customization;
using SpaceDeck.Core.Model;

namespace SpaceDeck.Core.Tests;

public sealed class CustomizationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "spacedeck-cust-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void Override_AppliesScaleThenOffset()
    {
        var o = new SensorOverride { Scale = 2, Offset = -0.5 };
        Assert.Equal(5.5, o.Apply(3));
        Assert.True(o.HasCorrection);
        Assert.False(new SensorOverride().HasCorrection);
        Assert.True(new SensorOverride().IsEmpty);
    }

    [Fact]
    public void SaveThenLoad_RoundTrips_AndDropsEmptyOrBrokenOverrides()
    {
        var path = Path.Combine(_dir, "sensors.json");
        var c = new SensorCustomizations();
        c.Overrides["/lpc/nct6687d/0/fan/1"] = new SensorOverride { Name = "Front intake" };
        c.Overrides["/lpc/nct6687d/0/voltage/3"] = new SensorOverride { Scale = 0 }; // broken → no correction → empty → dropped
        c.Overrides["/x/0/load/0"] = new SensorOverride { Hidden = true };
        c.Known["/gpu-nvidia/0/temperature/0"] = new KnownSensor { Name = "GPU Core", Kind = SensorKind.Temperature, Hardware = "RTX 3060 Ti" };
        CustomizationStore.Save(path, c);

        var loaded = CustomizationStore.Load(path);

        Assert.Equal("Front intake", loaded.Overrides["/lpc/nct6687d/0/fan/1"].Name);
        Assert.False(loaded.Overrides.ContainsKey("/lpc/nct6687d/0/voltage/3"));
        Assert.True(loaded.Overrides["/x/0/load/0"].Hidden);
        Assert.Equal(1, loaded.Overrides["/x/0/load/0"].Scale);
        Assert.Equal(SensorKind.Temperature, loaded.Known["/gpu-nvidia/0/temperature/0"].Kind);
    }

    [Fact]
    public void Load_MissingOrCorruptFile_GivesEmpty()
    {
        Assert.Empty(CustomizationStore.Load(Path.Combine(_dir, "nope.json")).Overrides);
        Directory.CreateDirectory(_dir);
        var bad = Path.Combine(_dir, "bad.json");
        File.WriteAllText(bad, "{ nope");
        Assert.Empty(CustomizationStore.Load(bad).Overrides);
    }

    [Fact]
    public void BoardProfile_ExportImport_RoundTrips_AndRejectsOtherFiles()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "board.json");
        var profile = new BoardProfile { Board = "MSI PRO Z790-A WIFI (MS-7E07)" };
        profile.Overrides["/lpc/nct6687d/0/voltage/0"] = new SensorOverride { Name = "+12V", Scale = 12 };
        CustomizationStore.ExportProfile(path, profile);

        var imported = CustomizationStore.ImportProfile(path);
        Assert.Equal("MSI PRO Z790-A WIFI (MS-7E07)", imported.Board);
        Assert.Equal(12, imported.Overrides["/lpc/nct6687d/0/voltage/0"].Scale);

        var other = Path.Combine(_dir, "other.json");
        File.WriteAllText(other, "{ \"name\": \"Main\", \"tiles\": [] }");
        Assert.Throws<InvalidDataException>(() => CustomizationStore.ImportProfile(other));
    }

    [Fact]
    public void Matcher_PrefersSameNameInSameFamily_AndOnlySameKind()
    {
        var known = new KnownSensor { Name = "GPU Core", Kind = SensorKind.Temperature, Hardware = "RTX 3060 Ti" };
        var candidates = new[]
        {
            new SensorCandidate("/gpu-amd/0/temperature/0", "GPU Core", SensorKind.Temperature, "RX 7800 XT"),
            new SensorCandidate("/gpu-amd/0/temperature/1", "GPU Hot Spot", SensorKind.Temperature, "RX 7800 XT"),
            new SensorCandidate("/intelcpu/0/temperature/0", "CPU Package", SensorKind.Temperature, "i9-14900K"),
            new SensorCandidate("/gpu-amd/0/load/0", "GPU Core", SensorKind.Load, "RX 7800 XT"),
        };

        var suggestions = SensorMatcher.Suggest("/gpu-nvidia/0/temperature/0", known, candidates);

        Assert.Equal("/gpu-amd/0/temperature/0", suggestions[0].Id);
        Assert.DoesNotContain(suggestions, s => s.Kind != SensorKind.Temperature);
        Assert.Equal("/gpu-amd/0/temperature/1", suggestions[1].Id); // same family beats the CPU
    }

    [Fact]
    public void Matcher_WithoutKnownName_UsesKindFromId()
    {
        var candidates = new[] { new SensorCandidate("/nvme/1/temperature/0", "Composite Temperature", SensorKind.Temperature, "Samsung 990") };
        var suggestions = SensorMatcher.Suggest("/nvme/0/temperature/0", null, candidates);
        Assert.Single(suggestions);
        Assert.Empty(SensorMatcher.Suggest("garbage", null, candidates));
    }

    [Theory]
    [InlineData("lhm:/gpu-nvidia/0/temperature/0", "gpu")]
    [InlineData("/intelcpu/0/load/0", "cpu")]
    [InlineData("lhm:/nvme/1/temperature/0", "storage")]
    [InlineData("lhm:/lpc/nct6687d/0/fan/1", "board")]
    public void Family_IgnoresSourcePrefix(string id, string expected) => Assert.Equal(expected, SensorMatcher.Family(id));
}
