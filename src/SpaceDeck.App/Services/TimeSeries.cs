namespace SpaceDeck.App.Services;

/// <summary>
/// A sensor's recent history: a fixed-size ring of (timestamp, value) samples. Missing values are NaN; so are
/// non-finite readings (±Infinity), so charts never get infinite coordinates.
/// Lives on the UI thread; <see cref="Changed"/> tells sparklines and charts to redraw.
/// </summary>
public sealed class TimeSeries
{
    /// <summary>A sample this much older than the newest one means time went backwards, not a late frame.</summary>
    private const long ClockJumpBackMs = 5 * 60 * 1000;

    private readonly long[] _times;
    private readonly float[] _values;
    private int _head;
    private int _count;

    public TimeSeries(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 2);
        _times = new long[capacity];
        _values = new float[capacity];
    }

    public int Count => _count;

    /// <summary>Timestamp (Unix ms) of the oldest sample, or 0 when empty.</summary>
    public long FirstTime => _count == 0 ? 0 : _times[(_head - _count + _times.Length) % _times.Length];

    /// <summary>Timestamp (Unix ms) of the newest sample, or 0 when empty.</summary>
    public long LastTime => _count == 0 ? 0 : _times[(_head - 1 + _times.Length) % _times.Length];

    public event Action? Changed;

    public void Add(long timeUnixMs, float? value)
    {
        // Ignore duplicates and out-of-order samples (e.g. live frames overlapping a backfill). A big jump back is
        // the clock being set back or a replay starting over: start the history again, or it would freeze until
        // time caught up with the newest sample.
        if (_count > 0 && timeUnixMs <= LastTime)
        {
            if (timeUnixMs >= LastTime - ClockJumpBackMs)
            {
                return;
            }

            _head = 0;
            _count = 0;
        }

        _times[_head] = timeUnixMs;
        _values[_head] = Sanitize(value);
        _head = (_head + 1) % _times.Length;
        if (_count < _times.Length)
        {
            _count++;
        }

        Changed?.Invoke();
    }

    /// <summary>Replaces the contents with history from the collector (oldest first), keeping any newer live samples.</summary>
    public void Backfill(long[] times, float?[] values)
    {
        ArgumentNullException.ThrowIfNull(times);
        ArgumentNullException.ThrowIfNull(values);

        // Keep live samples newer than the backfill so nothing is lost while the request was in flight.
        var newestBackfill = times.Length > 0 ? times[^1] : long.MinValue;
        var live = new List<(long, float)>();
        ForEach((t, v) =>
        {
            if (t > newestBackfill)
            {
                live.Add((t, v));
            }
        });

        _head = 0;
        _count = 0;
        // Keep the newest samples that fit (a reply with fewer values than timestamps is cut to the pairs it has).
        var available = Math.Min(times.Length, values.Length);
        var start = Math.Max(0, available - _times.Length);
        for (var i = start; i < available; i++)
        {
            _times[_head] = times[i];
            _values[_head] = Sanitize(values[i]);
            _head = (_head + 1) % _times.Length;
            _count = Math.Min(_count + 1, _times.Length);
        }

        foreach (var (t, v) in live)
        {
            _times[_head] = t;
            _values[_head] = v;
            _head = (_head + 1) % _times.Length;
            _count = Math.Min(_count + 1, _times.Length);
        }

        Changed?.Invoke();
    }

    /// <summary>Re-maps every stored value in place (a sensor's scale/offset correction changed).</summary>
    public void Map(Func<float, float> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        var start = (_head - _count + _values.Length) % _values.Length;
        for (var k = 0; k < _count; k++)
        {
            var i = (start + k) % _values.Length;
            _values[i] = Sanitize(map(_values[i]));
        }

        Changed?.Invoke();
    }

    /// <summary>Visits samples oldest → newest.</summary>
    public void ForEach(Action<long, float> visit)
    {
        ArgumentNullException.ThrowIfNull(visit);
        var start = (_head - _count + _times.Length) % _times.Length;
        for (var k = 0; k < _count; k++)
        {
            var i = (start + k) % _times.Length;
            visit(_times[i], _values[i]);
        }
    }

    /// <summary>Min and max of real (non-NaN) values newer than <paramref name="sinceUnixMs"/>; NaN when none.</summary>
    public (float Min, float Max) Range(long sinceUnixMs)
    {
        var min = float.NaN;
        var max = float.NaN;
        ForEach((t, v) =>
        {
            if (t < sinceUnixMs || float.IsNaN(v))
            {
                return;
            }

            min = float.IsNaN(min) ? v : Math.Min(min, v);
            max = float.IsNaN(max) ? v : Math.Max(max, v);
        });
        return (min, max);
    }

    private static float Sanitize(float? value) => value is { } v && float.IsFinite(v) ? v : float.NaN;
}
