using SensorDeck.Collector.Polling;
using SensorDeck.Collector.Storage;
using SensorDeck.Core.Protocol;

namespace SensorDeck.Collector.Tests;

public sealed class DiskHistoryTests : IDisposable
{
    // 2026-10-22 10:00:00 UTC
    private const long T0 = 1_792_663_200_000;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sensordeck-hist-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private DiskHistory Create(int rawHours = 24, int minuteDays = 7, long maxBytes = 1L << 30) =>
        new(new DiskHistoryOptions { Directory = _dir, RawHours = rawHours, MinuteDays = minuteDays, MaxBytes = maxBytes });

    [Fact]
    public void Raw_RoundTripsEverySample()
    {
        using var disk = Create();
        disk.SetSensors(["a", "b"]);
        for (var i = 0; i < 10; i++)
        {
            disk.Append(T0 + (i * 1000), [i, i % 2 == 0 ? null : 100f + i]);
        }

        var table = disk.Read(HistoryTier.Raw, ["b", "missing", "a"], T0 + 2000, T0 + 5000);
        Assert.Equal(new[] { T0 + 2000, T0 + 3000, T0 + 4000, T0 + 5000 }, table.Times);
        Assert.True(float.IsNaN(table.Values[0][0])); // b at i=2 was null
        Assert.Equal(103f, table.Values[1][0]);
        Assert.True(float.IsNaN(table.Values[1][1])); // unknown id
        Assert.Equal(5f, table.Values[3][2]);
    }

    [Fact]
    public void Minute_KeepsMinAvgMax()
    {
        using var disk = Create();
        disk.SetSensors(["a"]);
        for (var i = 0; i < 120; i++)
        {
            disk.Append(T0 + (i * 1000), [i]);
        }

        disk.Append(T0 + 130_000, [0]); // closes the second minute

        var table = disk.Read(HistoryTier.Minute, ["a"], T0, T0 + 200_000);
        Assert.Equal(new[] { T0, T0 + 60_000 }, table.Times);
        Assert.Equal(0f, table.Min![0][0]);
        Assert.Equal(29.5f, table.Values[0][0]);
        Assert.Equal(59f, table.Max![0][0]);
        Assert.Equal(119f, table.Max![1][0]);
    }

    [Fact]
    public void SurvivesReopen_AndCatalogChanges()
    {
        using (var disk = Create())
        {
            disk.SetSensors(["a"]);
            disk.Append(T0, [1]);
        }

        using (var disk = Create())
        {
            disk.SetSensors(["a"]);
            disk.Append(T0 + 1000, [2]);
            disk.SetSensors(["b", "a"]);
            disk.Append(T0 + 2000, [9, 3]);
            var table = disk.Read(HistoryTier.Raw, ["a"], T0, T0 + 10_000);
            Assert.Equal(new[] { 1f, 2f, 3f }, table.Values.Select(r => r[0]));
        }
    }

    [Fact]
    public void Retention_DeletesOldRawFiles()
    {
        using var disk = Create(rawHours: 2);
        disk.SetSensors(["a"]);
        for (var h = 0; h < 5; h++)
        {
            disk.Append(T0 + (h * 3_600_000L), [h]);
        }

        var table = disk.Read(HistoryTier.Raw, ["a"], T0, T0 + (10 * 3_600_000L));
        Assert.Equal(new[] { 2f, 3f, 4f }, table.Values.Select(r => r[0]));
    }

    [Fact]
    public void Query_UsesDiskForOlderRanges_AndAveragesPoints()
    {
        var disk = Create();
        var hub = new SensorHub(10, 1000, disk);
        hub.PublishCatalog(new Core.Model.SensorCatalog(1, [], [new Core.Model.SensorInfo("a", "h", "A", Core.Model.SensorKind.Load, "%", "fake", null)]));
        for (var i = 0; i < 100; i++)
        {
            hub.PublishValues(new ValuesMessage(1, i, T0 + (i * 1000), [i], [i], [i]));
        }

        // Memory keeps only the last 10 samples; this range is older, so it comes from disk.
        var response = HistoryQuery.Answer(hub, new HistoryRequest(7, ["a"], 0, T0, T0 + 49_000, MaxPoints: 5), T0 + 100_000);
        Assert.Equal(7, response.RequestId);
        Assert.Equal(5, response.TimestampsUnixMs.Length);
        Assert.Equal(4.5f, response.Values[0][0]);
        Assert.Equal(0f, response.Min![0][0]);
        Assert.Equal(9f, response.Max![0][0]);
        disk.Dispose();
    }

    [Fact]
    public void Query_PagesLargeAnswers()
    {
        var disk = Create();
        var hub = new SensorHub(10, 1000, disk);
        string[] ids = [.. Enumerable.Range(0, 400).Select(i => $"s{i}")];
        hub.PublishCatalog(new Core.Model.SensorCatalog(1, [], [.. ids.Select(id => new Core.Model.SensorInfo(id, "h", id, Core.Model.SensorKind.Load, "%", "fake", null))]));
        var row = new float?[ids.Length];
        for (var i = 0; i < 3000; i++)
        {
            hub.PublishValues(new ValuesMessage(1, i, T0 + (i * 1000), row, row, row));
        }

        var first = HistoryQuery.Answer(hub, new HistoryRequest(1, ids, 0, T0, T0 + 3_000_000), T0 + 3_000_000);
        Assert.Equal(2500, first.TimestampsUnixMs.Length); // 1,000,000 values / 400 sensors
        Assert.Equal(T0 + 2_500_000, first.NextFromUnixMs);

        var rest = HistoryQuery.Answer(hub, new HistoryRequest(2, ids, 0, first.NextFromUnixMs, T0 + 3_000_000), T0 + 3_000_000);
        Assert.Equal(500, rest.TimestampsUnixMs.Length);
        Assert.Null(rest.NextFromUnixMs);
        disk.Dispose();
    }

    [Fact]
    public void Query_ExtremeRange_DoesNotOverflow()
    {
        var disk = Create();
        var hub = new SensorHub(10, 1000, disk);
        hub.PublishCatalog(new Core.Model.SensorCatalog(1, [], [new Core.Model.SensorInfo("a", "h", "A", Core.Model.SensorKind.Load, "%", "fake", null)]));
        hub.PublishValues(new ValuesMessage(1, 0, T0, [1], [1], [1]));

        var chart = HistoryQuery.Answer(hub, new HistoryRequest(3, ["a"], 0, long.MinValue, long.MaxValue, MaxPoints: HistoryQuery.MaxPoints), T0 + 1000);
        var export = HistoryQuery.Answer(hub, new HistoryRequest(4, ["a"], 0, long.MinValue, long.MinValue), T0 + 1000);
        Assert.Equal(3, chart.RequestId);
        Assert.Empty(export.TimestampsUnixMs);
        disk.Dispose();
    }

    [Fact]
    public void Query_ExportAtRawHorizon_ReadsEverySample()
    {
        // A 24 h export starts a few milliseconds before the raw horizon (the collector's clock is a little later than
        // the app's); it must read raw rows, not a few milliseconds of per-minute rows and a continuation.
        var disk = Create(rawHours: 24);
        var hub = new SensorHub(10, 1000, disk);
        hub.PublishCatalog(new Core.Model.SensorCatalog(1, [], [new Core.Model.SensorInfo("a", "h", "A", Core.Model.SensorKind.Load, "%", "fake", null)]));
        for (var i = 0; i <= 120; i++)
        {
            hub.PublishValues(new ValuesMessage(1, i, T0 + (i * 1000), [i], [i], [i]));
        }

        var response = HistoryQuery.Answer(hub, new HistoryRequest(5, ["a"], 0, T0, T0 + 120_000), T0 + 86_400_000 + 3_000);
        Assert.Equal(121, response.TimestampsUnixMs.Length);
        Assert.Null(response.NextFromUnixMs);
        disk.Dispose();
    }

    [Fact]
    public void Hash_IsStable() => Assert.Equal(DiskHistory.Hash(["a", "b"]), DiskHistory.Hash(["a", "b"]));
}
