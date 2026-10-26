namespace SensorDeck.Core.History;

/// <summary>
/// History rows for a fixed list of sensors, oldest first: one timestamp and one value per sensor per row (NaN = no
/// reading). <see cref="Min"/>/<see cref="Max"/> are present when rows already summarize several samples.
/// </summary>
public sealed class HistoryTable
{
    public HistoryTable(int columns, bool withRange = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(columns);
        Columns = columns;
        if (withRange)
        {
            Min = [];
            Max = [];
        }
    }

    public int Columns { get; }

    public List<long> Times { get; } = [];

    /// <summary>Per row: the value (or average) of each column.</summary>
    public List<float[]> Values { get; } = [];

    public List<float[]>? Min { get; }

    public List<float[]>? Max { get; }

    public int Count => Times.Count;

    public void Add(long time, float[] values, float[]? min = null, float[]? max = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        Times.Add(time);
        Values.Add(values);
        Min?.Add(min ?? values);
        Max?.Add(max ?? values);
    }

    /// <summary>Sorts rows by time (rows from several files arrive interleaved).</summary>
    public void SortByTime()
    {
        if (IsSorted())
        {
            return;
        }

        var order = Enumerable.Range(0, Count).OrderBy(i => Times[i]).ToArray();
        Reorder(Times, order);
        Reorder(Values, order);
        if (Min is not null)
        {
            Reorder(Min, order);
        }

        if (Max is not null)
        {
            Reorder(Max, order);
        }
    }

    /// <summary>Adds another table's rows (its values only when this table has no range).</summary>
    public HistoryTable Append(HistoryTable other)
    {
        ArgumentNullException.ThrowIfNull(other);
        for (var i = 0; i < other.Count; i++)
        {
            Add(other.Times[i], other.Values[i], other.Min?[i], other.Max?[i]);
        }

        return this;
    }

    /// <summary>Keeps rows [start, start + count).</summary>
    public HistoryTable Slice(int start, int count)
    {
        var slice = new HistoryTable(Columns, Min is not null);
        for (var i = start; i < start + count && i < Count; i++)
        {
            slice.Add(Times[i], Values[i], Min?[i], Max?[i]);
        }

        return slice;
    }

    /// <summary>Column-major nullable arrays for the wire format.</summary>
    public static float?[][] ToColumns(List<float[]> rows, int columns)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var result = new float?[columns][];
        for (var c = 0; c < columns; c++)
        {
            var column = new float?[rows.Count];
            for (var r = 0; r < rows.Count; r++)
            {
                var v = rows[r][c];
                column[r] = float.IsNaN(v) ? null : v;
            }

            result[c] = column;
        }

        return result;
    }

    private bool IsSorted()
    {
        for (var i = 1; i < Times.Count; i++)
        {
            if (Times[i] < Times[i - 1])
            {
                return false;
            }
        }

        return true;
    }

    private static void Reorder<T>(List<T> list, int[] order)
    {
        var copy = order.Select(i => list[i]).ToList();
        list.Clear();
        list.AddRange(copy);
    }
}

/// <summary>Averages history rows into at most N evenly spaced points, keeping each point's min and max.</summary>
public static class HistoryBuckets
{
    public static HistoryTable Bucket(HistoryTable table, long fromUnixMs, long toUnixMs, int maxPoints)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (maxPoints <= 0 || table.Count <= maxPoints)
        {
            if (table.Min is not null || maxPoints <= 0)
            {
                return table;
            }

            // Callers asked for points: always include a range, even when nothing had to be combined.
            var withRange = new HistoryTable(table.Columns, withRange: true);
            for (var i = 0; i < table.Count; i++)
            {
                withRange.Add(table.Times[i], table.Values[i]);
            }

            return withRange;
        }

        var span = Math.Max(1, toUnixMs - fromUnixMs + 1);
        var width = Math.Max(1, (span + maxPoints - 1) / maxPoints);
        var result = new HistoryTable(table.Columns, withRange: true);
        var columns = table.Columns;
        var sum = new double[columns];
        var count = new int[columns];
        var min = new float[columns];
        var max = new float[columns];
        long timeSum = 0;
        var rows = 0;
        var bucket = long.MinValue;

        void Reset()
        {
            Array.Clear(sum);
            Array.Clear(count);
            Array.Fill(min, float.NaN);
            Array.Fill(max, float.NaN);
            timeSum = 0;
            rows = 0;
        }

        void Flush()
        {
            if (rows == 0)
            {
                return;
            }

            var avg = new float[columns];
            for (var c = 0; c < columns; c++)
            {
                avg[c] = count[c] == 0 ? float.NaN : (float)(sum[c] / count[c]);
            }

            result.Add(timeSum / rows, avg, (float[])min.Clone(), (float[])max.Clone());
        }

        Reset();
        for (var r = 0; r < table.Count; r++)
        {
            var t = table.Times[r];
            var b = Math.Max(0, (t - fromUnixMs) / width);
            if (b != bucket)
            {
                Flush();
                Reset();

                // Skipped buckets mean a gap (collector off): a NaN point stops charts drawing a line across it.
                if (bucket != long.MinValue && b > bucket + 1)
                {
                    var gap = new float[columns];
                    Array.Fill(gap, float.NaN);
                    result.Add(fromUnixMs + ((bucket + 1) * width), gap, gap, gap);
                }

                bucket = b;
            }

            timeSum += t;
            rows++;
            var values = table.Values[r];
            var lows = table.Min?[r] ?? values;
            var highs = table.Max?[r] ?? values;
            for (var c = 0; c < columns; c++)
            {
                var v = values[c];
                if (!float.IsNaN(v))
                {
                    sum[c] += v;
                    count[c]++;
                }

                min[c] = Lower(min[c], lows[c]);
                max[c] = Higher(max[c], highs[c]);
            }
        }

        Flush();
        return result;
    }

    private static float Lower(float a, float b) => float.IsNaN(a) ? b : float.IsNaN(b) ? a : Math.Min(a, b);

    private static float Higher(float a, float b) => float.IsNaN(a) ? b : float.IsNaN(b) ? a : Math.Max(a, b);
}
