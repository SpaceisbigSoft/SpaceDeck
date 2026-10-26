using SensorDeck.Core.Model;

namespace SensorDeck.Core.Replay;

/// <summary>
/// A fake sensor source that plays back a <see cref="SensorRecording"/> in real time, looping at the end. Sensor ids
/// are the recorded ones, so dashboards and alert rules behave exactly as on the recorded machine.
/// </summary>
/// <remarks>A one-frame recording (a hardware report) is held with gentle movement so charts and gauges look alive.</remarks>
public sealed class ReplaySource : ISensorSource
{
    /// <summary>Pause at the end of a recording before it starts over.</summary>
    private const long LoopGapMs = 1000;

    private readonly string? _path;
    private readonly TimeProvider _time;
    private SensorRecording? _recording;
    private long _startTimestamp;
    private int _frameIndex;
    private int _catalogIndex;
    private float[] _min = [];
    private float[] _max = [];
    private long _elapsedMs;

    /// <summary>Plays the file at <paramref name="path"/> (loaded in <see cref="Open"/>).</summary>
    public ReplaySource(string path, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Plays an already loaded recording.</summary>
    public ReplaySource(SensorRecording recording, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(recording);
        _recording = recording;
        _time = time ?? TimeProvider.System;
    }

    public string Name => "replay";

    public SourceStatus Status { get; private set; } = SourceStatus.NotOpened;

    public string? StatusMessage { get; private set; }

    public SensorCatalog Catalog { get; private set; } = SensorCatalog.Empty;

    public void Open()
    {
        if (_recording is null && _path is not null)
        {
            try
            {
                _recording = SensorRecording.Load(_path);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                Status = SourceStatus.Error;
                StatusMessage = $"Couldn't load the replay file {Path.GetFileName(_path)}: {ex.Message}";
                return;
            }
        }

        if (_recording is null)
        {
            Status = SourceStatus.Error;
            StatusMessage = "No replay file.";
            return;
        }

        _startTimestamp = _time.GetTimestamp();
        _frameIndex = 0;
        SwitchCatalog(_recording.Frames[0].CatalogIndex);
        Status = SourceStatus.Ok;
        var name = _path is not null ? Path.GetFileName(_path) : _recording.Description ?? "recording";
        StatusMessage = _recording.IsSnapshot
            ? $"Replaying {name} (a snapshot; values move a little so charts look alive)."
            : $"Replaying {name} ({TimeSpan.FromMilliseconds(_recording.DurationMs):h\\:mm\\:ss}, loops).";
    }

    public void Update()
    {
        if (_recording is null || Status != SourceStatus.Ok)
        {
            return;
        }

        _elapsedMs = (long)_time.GetElapsedTime(_startTimestamp).TotalMilliseconds;
        var frames = _recording.Frames;
        if (frames.Count == 1)
        {
            _frameIndex = 0;
            return;
        }

        var position = _elapsedMs % (_recording.DurationMs + LoopGapMs);
        _frameIndex = FindFrame(frames, position);
    }

    public bool TryRefreshCatalog()
    {
        if (_recording is null || Status != SourceStatus.Ok)
        {
            return false;
        }

        var wanted = _recording.Frames[_frameIndex].CatalogIndex;
        if (wanted == _catalogIndex)
        {
            return false;
        }

        SwitchCatalog(wanted);
        return true;
    }

    public void Read(Span<SensorReading> into)
    {
        if (_recording is null || Status != SourceStatus.Ok)
        {
            into.Clear();
            return;
        }

        var frame = _recording.Frames[_frameIndex];
        var sensors = Catalog.Sensors;
        // A frame recorded under another catalog (only possible mid-switch): no values rather than wrong ones.
        var usable = frame.CatalogIndex == _catalogIndex;
        for (var i = 0; i < into.Length; i++)
        {
            float? value = usable && i < frame.Values.Length ? frame.Values[i] : null;
            if (value is { } v && _recording.IsSnapshot && i < sensors.Count)
            {
                value = Wobble(sensors[i].Kind, v, i, _elapsedMs);
            }

            if (value is { } x && i < _min.Length)
            {
                _min[i] = float.IsNaN(_min[i]) ? x : Math.Min(_min[i], x);
                _max[i] = float.IsNaN(_max[i]) ? x : Math.Max(_max[i], x);
            }

            into[i] = new SensorReading(
                value,
                i < _min.Length && !float.IsNaN(_min[i]) ? _min[i] : null,
                i < _max.Length && !float.IsNaN(_max[i]) ? _max[i] : null);
        }
    }

    public void Dispose()
    {
        // Nothing to release; the recording is in memory.
    }

    /// <summary>Index of the last frame at or before <paramref name="positionMs"/>.</summary>
    internal static int FindFrame(IReadOnlyList<RecordingFrame> frames, long positionMs)
    {
        int lo = 0, hi = frames.Count - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (frames[mid].OffsetMs <= positionMs)
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return lo;
    }

    /// <summary>Slow, sensor-specific movement around a snapshot value (a few percent at most).</summary>
    internal static float Wobble(SensorKind kind, float value, int index, long elapsedMs)
    {
        var amount = kind switch
        {
            SensorKind.Temperature => 0.03,
            SensorKind.Load or SensorKind.Power or SensorKind.Clock or SensorKind.Throughput => 0.08,
            SensorKind.Fan or SensorKind.Control or SensorKind.Flow => 0.02,
            SensorKind.Voltage or SensorKind.Current => 0.003,
            _ => 0,
        };

        if (amount == 0 || value == 0)
        {
            return value; // stopped fans stay stopped, counters stay put
        }

        var phase = (elapsedMs / 20_000.0 * 2 * Math.PI) + (index * 0.7);
        var result = value * (1 + (amount * Math.Sin(phase)));
        if (kind is SensorKind.Load or SensorKind.Control)
        {
            result = Math.Clamp(result, 0, 100);
        }

        return (float)result;
    }

    private void SwitchCatalog(int index)
    {
        _catalogIndex = index;
        Catalog = _recording!.Catalogs[index];
        _min = new float[Catalog.Sensors.Count];
        _max = new float[Catalog.Sensors.Count];
        Array.Fill(_min, float.NaN);
        Array.Fill(_max, float.NaN);
    }
}
