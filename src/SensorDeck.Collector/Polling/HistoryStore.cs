namespace SensorDeck.Collector.Polling;

/// <summary>Result of a history query. <see cref="Values"/> is parallel to the requested ids; each inner array is parallel to <see cref="TimestampsUnixMs"/>.</summary>
public sealed record HistorySlice(long[] TimestampsUnixMs, float?[][] Values);

/// <summary>
/// Fixed-size ring buffer of samples for every sensor, sharing one timeline.
/// </summary>
/// <remarks>
/// Thread-safe: the poll thread appends while pipe clients query.
/// Sensors keep their history across catalog changes as long as their id stays the same;
/// newly appearing sensors start with empty (NaN) history.
/// </remarks>
public sealed class HistoryStore
{
    private readonly Lock _lock = new();
    private readonly int _capacity;
    private readonly long[] _times;
    private Dictionary<string, float[]> _series = new(StringComparer.Ordinal);
    private float[][] _ordered = [];
    private int _head;
    private int _count;

    public HistoryStore(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 2);
        _capacity = capacity;
        _times = new long[capacity];
    }

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _count;
            }
        }
    }

    /// <summary>Sets the sensor ids, in the order future <see cref="Append"/> calls will supply values.</summary>
    public void SetSensors(IReadOnlyList<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        lock (_lock)
        {
            var next = new Dictionary<string, float[]>(ids.Count, StringComparer.Ordinal);
            var ordered = new float[ids.Count][];
            for (var i = 0; i < ids.Count; i++)
            {
                var id = ids[i];
                if (!next.TryGetValue(id, out var buffer))
                {
                    buffer = _series.TryGetValue(id, out var existing) ? existing : NewBuffer();
                    next[id] = buffer;
                }

                ordered[i] = buffer;
            }

            _series = next;
            _ordered = ordered;
        }
    }

    /// <summary>Appends one sample per sensor, in the order given to <see cref="SetSensors"/>.</summary>
    public void Append(long timestampUnixMs, ReadOnlySpan<float?> values)
    {
        lock (_lock)
        {
            if (values.Length != _ordered.Length)
            {
                throw new ArgumentException($"Expected {_ordered.Length} values, got {values.Length}.", nameof(values));
            }

            _times[_head] = timestampUnixMs;
            for (var i = 0; i < values.Length; i++)
            {
                _ordered[i][_head] = values[i] ?? float.NaN;
            }

            _head = (_head + 1) % _capacity;
            if (_count < _capacity)
            {
                _count++;
            }
        }
    }

    /// <summary>Timestamp of the oldest sample in memory, or null when empty.</summary>
    public long? OldestTimestamp
    {
        get
        {
            lock (_lock)
            {
                return _count == 0 ? null : _times[(_head - _count + _capacity) % _capacity];
            }
        }
    }

    /// <summary>Returns samples newer than <paramref name="windowSeconds"/> before <paramref name="nowUnixMs"/>, oldest first.</summary>
    public HistorySlice Query(IReadOnlyList<string> ids, int windowSeconds, long nowUnixMs) =>
        Query(ids, nowUnixMs - (Math.Max(windowSeconds, 0) * 1000L), long.MaxValue);

    /// <summary>Returns samples with timestamps in [<paramref name="fromUnixMs"/>, <paramref name="toUnixMs"/>], oldest first.</summary>
    public HistorySlice Query(IReadOnlyList<string> ids, long fromUnixMs, long toUnixMs)
    {
        ArgumentNullException.ThrowIfNull(ids);
        lock (_lock)
        {
            var start = (_head - _count + _capacity) % _capacity;
            var slots = new List<int>(_count);
            for (var k = 0; k < _count; k++)
            {
                var slot = (start + k) % _capacity;
                if (_times[slot] >= fromUnixMs && _times[slot] <= toUnixMs)
                {
                    slots.Add(slot);
                }
            }

            var times = new long[slots.Count];
            for (var j = 0; j < slots.Count; j++)
            {
                times[j] = _times[slots[j]];
            }

            var values = new float?[ids.Count][];
            for (var i = 0; i < ids.Count; i++)
            {
                var series = new float?[slots.Count];
                if (_series.TryGetValue(ids[i], out var buffer))
                {
                    for (var j = 0; j < slots.Count; j++)
                    {
                        var v = buffer[slots[j]];
                        series[j] = float.IsNaN(v) ? null : v;
                    }
                }

                values[i] = series;
            }

            return new HistorySlice(times, values);
        }
    }

    private float[] NewBuffer()
    {
        var buffer = new float[_capacity];
        Array.Fill(buffer, float.NaN);
        return buffer;
    }
}
