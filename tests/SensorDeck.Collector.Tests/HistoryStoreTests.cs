using SensorDeck.Collector.Polling;

namespace SensorDeck.Collector.Tests;

public class HistoryStoreTests
{
    [Fact]
    public void Query_ReturnsSamplesOldestFirst()
    {
        var store = new HistoryStore(10);
        store.SetSensors(["a", "b"]);
        store.Append(1000, [1f, 10f]);
        store.Append(2000, [2f, null]);
        store.Append(3000, [3f, 30f]);

        var slice = store.Query(["a", "b"], 60, 3000);

        Assert.Equal(new long[] { 1000, 2000, 3000 }, slice.TimestampsUnixMs);
        Assert.Equal(new float?[] { 1f, 2f, 3f }, slice.Values[0]);
        Assert.Equal(new float?[] { 10f, null, 30f }, slice.Values[1]);
    }

    [Fact]
    public void Append_WrapsAtCapacity()
    {
        var store = new HistoryStore(3);
        store.SetSensors(["a"]);
        for (var i = 1; i <= 5; i++)
        {
            store.Append(i * 1000, [i]);
        }

        var slice = store.Query(["a"], 60, 5000);

        Assert.Equal(3, store.Count);
        Assert.Equal(new long[] { 3000, 4000, 5000 }, slice.TimestampsUnixMs);
        Assert.Equal(new float?[] { 3f, 4f, 5f }, slice.Values[0]);
    }

    [Fact]
    public void Query_FiltersByWindow()
    {
        var store = new HistoryStore(10);
        store.SetSensors(["a"]);
        store.Append(1_000, [1f]);
        store.Append(50_000, [2f]);
        store.Append(60_000, [3f]);

        var slice = store.Query(["a"], 15, 60_000);

        Assert.Equal(new long[] { 50_000, 60_000 }, slice.TimestampsUnixMs);
    }

    [Fact]
    public void SetSensors_KeepsHistoryForSameIdsAndStartsNewOnesEmpty()
    {
        var store = new HistoryStore(10);
        store.SetSensors(["a", "b"]);
        store.Append(1000, [1f, 10f]);

        store.SetSensors(["c", "a"]); // b removed, c added, a moved
        store.Append(2000, [100f, 2f]);

        var slice = store.Query(["a", "b", "c", "missing"], 60, 2000);

        Assert.Equal(new float?[] { 1f, 2f }, slice.Values[0]);
        Assert.Equal(new float?[] { null, null }, slice.Values[1]);
        Assert.Equal(new float?[] { null, 100f }, slice.Values[2]);
        Assert.Equal(new float?[] { null, null }, slice.Values[3]);
    }

    [Fact]
    public void Append_RejectsWrongLength()
    {
        var store = new HistoryStore(10);
        store.SetSensors(["a", "b"]);

        Assert.Throws<ArgumentException>(() => store.Append(1000, [1f]));
    }

    [Fact]
    public void ResetRequests_AreMergedAndBounded()
    {
        var hub = new SensorHub(10, 1000);
        hub.RequestResetMinMax(["a", "b"]);
        hub.RequestResetMinMax(["b", "c"]);
        var (all, ids) = hub.TakeResetRequests();
        Assert.False(all);
        Assert.Equal(new[] { "a", "b", "c" }, ids.Order());

        hub.RequestResetMinMax(["a"]);
        hub.RequestResetMinMax(null);
        Assert.True(hub.TakeResetRequests().All);

        var empty = hub.TakeResetRequests();
        Assert.False(empty.All);
        Assert.Empty(empty.Ids);
    }

    [Fact]
    public void CollectorOptions_HistoryCapacityCoversWindow()
    {
        Assert.Equal(3600, new CollectorOptions { PollIntervalMs = 1000, HistorySeconds = 3600 }.HistoryCapacity());
        Assert.Equal(7200, new CollectorOptions { PollIntervalMs = 500, HistorySeconds = 3600 }.HistoryCapacity());
        Assert.Equal(60, new CollectorOptions { PollIntervalMs = 5000, HistorySeconds = 60 }.HistoryCapacity());
        Assert.Equal(250, CollectorOptions.ClampInterval(10));
    }
}
