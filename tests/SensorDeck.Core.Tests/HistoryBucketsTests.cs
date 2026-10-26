using SensorDeck.Core.History;

namespace SensorDeck.Core.Tests;

public class HistoryBucketsTests
{
    private static HistoryTable Ramp(int rows, int start = 0)
    {
        var table = new HistoryTable(2);
        for (var i = 0; i < rows; i++)
        {
            table.Add(i * 1000L, [start + i, i % 2 == 0 ? float.NaN : 10]);
        }

        return table;
    }

    [Fact]
    public void Bucket_AveragesAndKeepsRange()
    {
        var result = HistoryBuckets.Bucket(Ramp(100), 0, 99_000, 10);

        Assert.Equal(10, result.Count);
        Assert.Equal(4.5f, result.Values[0][0]);
        Assert.Equal(0f, result.Min![0][0]);
        Assert.Equal(9f, result.Max![0][0]);
        Assert.Equal(10f, result.Values[0][1]); // NaN samples are skipped, not averaged as 0
        Assert.Equal(4500, result.Times[0]);
    }

    [Fact]
    public void Bucket_SmallTable_IsReturnedWithRange()
    {
        var result = HistoryBuckets.Bucket(Ramp(5), 0, 4000, 10);
        Assert.Equal(5, result.Count);
        Assert.NotNull(result.Min);
    }

    [Fact]
    public void Bucket_ZeroMaxPoints_ReturnsEverySample()
    {
        var table = Ramp(50);
        Assert.Same(table, HistoryBuckets.Bucket(table, 0, 49_000, 0));
    }

    [Fact]
    public void Bucket_CombinesExistingMinMax()
    {
        var table = new HistoryTable(1, withRange: true);
        table.Add(0, [5], [1], [9]);
        table.Add(1000, [7], [6], [20]);
        table.Add(5000, [3], [3], [3]);

        var result = HistoryBuckets.Bucket(table, 0, 5000, 2);
        Assert.Equal(2, result.Count);
        Assert.Equal(6f, result.Values[0][0]);
        Assert.Equal(1f, result.Min![0][0]);
        Assert.Equal(20f, result.Max![0][0]);
    }

    [Fact]
    public void SortByTime_KeepsRowsTogether()
    {
        var table = new HistoryTable(1);
        table.Add(3, [3]);
        table.Add(1, [1]);
        table.Add(2, [2]);
        table.SortByTime();
        Assert.Equal(new long[] { 1, 2, 3 }, table.Times);
        Assert.Equal(new[] { 1f, 2f, 3f }, table.Values.Select(v => v[0]));
    }

    [Fact]
    public void ToColumns_TurnsNaNIntoNull()
    {
        var columns = HistoryTable.ToColumns([[1, float.NaN], [2, 4]], 2);
        Assert.Equal(new float?[] { 1, 2 }, columns[0]);
        Assert.Equal(new float?[] { null, 4 }, columns[1]);
    }
}
