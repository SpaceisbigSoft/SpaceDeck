using SpaceDeck.Collector.Plugins;
using SpaceDeck.Collector.Polling;
using SpaceDeck.Core.Model;
using SpaceDeck.Core.PluginPipe;
using SpaceDeck.Sources.Lhm;

namespace SpaceDeck.Collector.Tests;

public class PluginBridgeSourceTests
{
    private static readonly PluginCatalog Galahad = new(
        [
            new PluginHardware("LianLi/0", "Lian Li Galahad II LCD", PluginHardwareKind.Cooler),
            new PluginHardware("LianLi/w1", "Lian Li TL Wireless (3 fans)", PluginHardwareKind.Fans),
        ],
        [
            new PluginSensor("LianLi/0/coolant/temp", "LianLi/0", "Coolant", PluginSensorKind.Temperature),
            new PluginSensor("LianLi/0/ch1/fan", "LianLi/0", "Pump", PluginSensorKind.Fan),
            new PluginSensor("LianLi/w1/f0/control", "LianLi/w1", "Fan 1 duty", PluginSensorKind.Control),
        ]);

    private static (PluginBridgeSource Source, ManualTimeProvider Time) Opened()
    {
        var time = new ManualTimeProvider();
        var source = new PluginBridgeSource(time);
        source.Open();
        return (source, time);
    }

    private static SensorReading[] Read(PluginBridgeSource source)
    {
        var readings = new SensorReading[source.Catalog.Sensors.Count];
        source.Read(readings);
        return readings;
    }

    [Fact]
    public void BeforeAnyPlugin_DeviceNotFound()
    {
        var source = new PluginBridgeSource(new ManualTimeProvider());
        Assert.Equal("lianli", source.Name);
        Assert.Equal(SourceStatus.NotOpened, source.Status);

        source.Open();
        Assert.Equal(SourceStatus.DeviceNotFound, source.Status);
        Assert.Equal("FanControl's Lian Li plugin isn't connected", source.StatusMessage);
        Assert.Empty(source.Catalog.Sensors);
        Assert.False(source.TryRefreshCatalog());
        source.Read([]);
    }

    [Fact]
    public void Catalog_IsPublishedWithPrefixedIdsKindsAndUnits()
    {
        var (source, _) = Opened();
        source.ApplyCatalog(1, Galahad);

        Assert.Equal(SourceStatus.Ok, source.Status);
        Assert.Null(source.StatusMessage);

        // The poll loop's catalog changes only when it asks.
        Assert.Empty(source.Catalog.Sensors);
        Assert.True(source.TryRefreshCatalog());
        Assert.False(source.TryRefreshCatalog());

        var catalog = source.Catalog;
        Assert.Equal(2, catalog.Hardware.Count);
        Assert.Equal(new HardwareInfo("lianli:/LianLi/0", "Lian Li Galahad II LCD", HardwareKind.Cooler, null, "lianli"), catalog.Hardware[0]);
        Assert.Equal(new HardwareInfo("lianli:/LianLi/w1", "Lian Li TL Wireless (3 fans)", HardwareKind.Other, null, "lianli"), catalog.Hardware[1]);
        Assert.Equal(new SensorInfo("lianli:/LianLi/0/coolant/temp", "lianli:/LianLi/0", "Coolant", SensorKind.Temperature, "°C", "lianli", null),
            catalog.Sensors[0]);
        Assert.Equal((SensorKind.Fan, "RPM"), (catalog.Sensors[1].Kind, catalog.Sensors[1].Unit));
        Assert.Equal((SensorKind.Control, "%"), (catalog.Sensors[2].Kind, catalog.Sensors[2].Unit));
        Assert.Equal("lianli:/LianLi/w1", catalog.Sensors[2].HardwareId);
    }

    [Fact]
    public void Values_AreReadUntilThreeSecondsOld()
    {
        var (source, time) = Opened();
        source.ApplyCatalog(1, Galahad);
        source.TryRefreshCatalog();

        // Blank until the first values line.
        Assert.All(Read(source), r => Assert.Null(r.Value));

        source.ApplyValues(1, [31.5, 2900, null]);
        var readings = Read(source);
        Assert.Equal(31.5f, readings[0].Value);
        Assert.Equal(2900f, readings[1].Value);
        Assert.Null(readings[2].Value);

        time.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(31.5f, Read(source)[0].Value);

        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.All(Read(source), r => Assert.Null(r.Value));
        Assert.Equal(SourceStatus.Ok, source.Status); // still connected, just quiet

        source.ApplyValues(1, [32, 2950, 40]);
        Assert.Equal(32f, Read(source)[0].Value);
    }

    [Fact]
    public void MinMax_AreKeptPerSensor()
    {
        var (source, _) = Opened();
        source.ApplyCatalog(1, Galahad);
        source.TryRefreshCatalog();

        source.ApplyValues(1, [30, 2900, 40]);
        Read(source);
        source.ApplyValues(1, [34, 2800, null]);
        var readings = Read(source);

        Assert.Equal(new SensorReading(34, 30, 34), readings[0]);
        Assert.Equal(new SensorReading(2800, 2800, 2900), readings[1]);
        Assert.Equal(new SensorReading(null, 40, 40), readings[2]);
    }

    [Fact]
    public void Disconnect_BlanksValuesAndKeepsTheCatalog()
    {
        var (source, _) = Opened();
        source.ApplyCatalog(1, Galahad);
        source.TryRefreshCatalog();
        source.ApplyValues(1, [30, 2900, 40]);

        source.Disconnect(1);

        Assert.Equal(SourceStatus.DeviceNotFound, source.Status);
        Assert.Equal(PluginBridgeSource.NotConnectedMessage, source.StatusMessage);
        Assert.False(source.TryRefreshCatalog());
        Assert.Equal(3, source.Catalog.Sensors.Count);
        Assert.All(Read(source), r => Assert.Null(r.Value));

        // Values from the gone connection are ignored.
        source.ApplyValues(1, [30, 2900, 40]);
        Assert.All(Read(source), r => Assert.Null(r.Value));
    }

    [Fact]
    public void NewCatalog_ExtendsButNeverShrinks()
    {
        var (source, _) = Opened();
        source.ApplyCatalog(1, Galahad);
        source.TryRefreshCatalog();
        var first = source.Catalog;

        // A plugin restart (new connection) with the pump gone and a new fan: ids stay, the new one is appended.
        var next = new PluginCatalog(
            [new PluginHardware("LianLi/0", "Lian Li Galahad II LCD", PluginHardwareKind.Cooler),
             new PluginHardware("LianLi/w2", "Lian Li TL Wireless (1 fan)", PluginHardwareKind.Fans)],
            [new PluginSensor("LianLi/w2/f0/fan", "LianLi/w2", "Fan A", PluginSensorKind.Fan),
             new PluginSensor("LianLi/0/coolant/temp", "LianLi/0", "Coolant", PluginSensorKind.Temperature)]);
        source.ApplyCatalog(2, next);

        Assert.True(source.TryRefreshCatalog());
        var catalog = source.Catalog;
        Assert.True(catalog.Version > first.Version);
        Assert.Equal(first.Sensors, catalog.Sensors.Take(3));
        Assert.Equal("lianli:/LianLi/w2/f0/fan", catalog.Sensors[3].Id);
        Assert.Equal(3, catalog.Hardware.Count);

        // Values follow the new connection's order and land on the kept ids; the pump reads blank.
        source.ApplyValues(2, [1200, 29]);
        var readings = Read(source);
        Assert.Equal(29f, readings[0].Value);
        Assert.Null(readings[1].Value);
        Assert.Null(readings[2].Value);
        Assert.Equal(1200f, readings[3].Value);

        // The replaced connection no longer feeds, and its disconnect doesn't blank the new one.
        source.ApplyValues(1, [50, 3000, 60]);
        source.Disconnect(1);
        Assert.Equal(SourceStatus.Ok, source.Status);
        Assert.Equal(29f, Read(source)[0].Value);
    }

    [Fact]
    public void SameCatalogAgain_DoesNotChangeTheCatalog()
    {
        var (source, _) = Opened();
        source.ApplyCatalog(1, Galahad);
        Assert.True(source.TryRefreshCatalog());

        source.Disconnect(1);
        source.ApplyCatalog(2, Galahad);
        Assert.False(source.TryRefreshCatalog());
        Assert.Equal(SourceStatus.Ok, source.Status);
    }

    [Fact]
    public void Catalog_IsCappedOverTheCollectorsLifetime()
    {
        var (source, _) = Opened();
        for (var round = 0; round < 6; round++)
        {
            var hardwareId = $"h{round}";
            var sensors = Enumerable.Range(0, PluginPipeProtocol.MaxSensors)
                .Select(i => new PluginSensor($"r{round}/s{i}", hardwareId, "S", PluginSensorKind.Fan))
                .ToArray();
            source.ApplyCatalog(round + 1, new PluginCatalog([new PluginHardware(hardwareId, "H", PluginHardwareKind.Fans)], sensors));
        }

        source.TryRefreshCatalog();
        Assert.Equal(PluginBridgeSource.MaxSensors, source.Catalog.Sensors.Count);

        // The latest connection's sensors are over the cap: its values are accepted but show nowhere.
        source.ApplyValues(6, Enumerable.Repeat<double?>(1000, PluginPipeProtocol.MaxSensors).ToArray());
        Assert.All(Read(source), r => Assert.Null(r.Value));
    }

    [Fact]
    public void Reopen_KeepsCatalogAndConnection()
    {
        var (source, _) = Opened();
        source.ApplyCatalog(1, Galahad);
        source.TryRefreshCatalog();
        source.ApplyValues(1, [30, 2900, 40]);

        // What PollingService does when sensor groups change: dispose every source, then open the new set.
        source.Dispose();
        source.Open();

        Assert.Equal(SourceStatus.Ok, source.Status);
        Assert.Equal(3, source.Catalog.Sensors.Count);
        Assert.Equal(30f, Read(source)[0].Value);
    }

    [Fact]
    public void Read_RejectsAWrongSpanLength()
    {
        var (source, _) = Opened();
        source.ApplyCatalog(1, Galahad);
        source.TryRefreshCatalog();
        Assert.Throws<ArgumentException>(() => source.Read(new SensorReading[2]));
    }

    [Fact]
    public void Factory_AddsTheSameBridgeAfterLhm_EveryTime()
    {
        var bridge = new PluginBridgeSource();
        var factory = new DefaultSensorSourceFactory(bridge);

        var first = factory.Create(new CollectorOptions());
        var second = factory.Create(new CollectorOptions { StorageEnabled = true });

        Assert.Collection(first, s => Assert.IsType<LhmSource>(s), s => Assert.Same(bridge, s));
        Assert.Same(bridge, second[1]);
        foreach (var source in first.Concat(second))
        {
            source.Dispose();
        }
    }

    [Fact]
    public void Factory_ReplayHasNoBridge()
    {
        var sources = new DefaultSensorSourceFactory(new PluginBridgeSource()).Create(new CollectorOptions { ReplayFile = @"C:\demo\laptop.sdrec" });
        Assert.Equal("replay", Assert.Single(sources).Name);
    }

    [Theory]
    [InlineData(@"C:\Program Files\FanControl\FanControl.exe", true)]
    [InlineData(@"C:\Program Files\FanControl\fancontrol.service.exe", true)]
    [InlineData(@"C:\Program Files\FanControl\FanControl.Evil.exe", false)]
    [InlineData(@"C:\Tools\NotFanControl.exe", false)]
    [InlineData(@"\\server\share\FanControl\FanControl.exe", false)]
    public void Verifier_AcceptsOnlyFanControlImageNames(string path, bool expected)
    {
        Assert.Equal(expected, FanControlClientVerifier.IsFanControlImage(path));
    }

    [Fact]
    public void Verifier_RejectsUnknownOrOtherPrograms()
    {
        var verifier = new FanControlClientVerifier();
        Assert.NotNull(verifier.Check(1234, null));
        Assert.NotNull(verifier.Check(1234, Environment.ProcessPath));
    }
}

/// <summary>A clock tests move by hand (Microsoft.Extensions.TimeProvider.Testing isn't referenced).</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private long _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Interlocked.Read(ref _ticks);

    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
}
