using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using SensorDeck.Core.Model;

namespace SensorDeck.App.Services;

/// <summary>Per-sensor statistics of one recording, in the sensor's base unit.</summary>
public sealed record RecordingSensorStats(
    string Id,
    string Name,
    string Group,
    SensorKind Kind,
    string Unit,
    double? Min,
    double? Avg,
    double? Max,
    double WarningSeconds,
    double CriticalSeconds);

/// <summary>The summary saved next to a recording's CSV (<c>name.summary.json</c>).</summary>
public sealed class RecordingSummary
{
    public string Name { get; set; } = "";

    public DateTimeOffset Start { get; set; }

    public DateTimeOffset End { get; set; }

    public int Samples { get; set; }

    /// <summary>Started by an automation rule rather than by hand.</summary>
    public bool Automatic { get; set; }

    /// <summary>The CSV's file name (same folder). Read back from disk, so only its file-name part is ever used.</summary>
    public string CsvFile { get; set; } = "";

    public List<RecordingSensorStats> Sensors { get; set; } = [];

    [JsonIgnore]
    public string SummaryPath { get; set; } = "";

    [JsonIgnore]
    public string CsvPath => Path.Combine(Path.GetDirectoryName(SummaryPath) ?? "", Path.GetFileName(CsvFile));

    [JsonIgnore]
    public TimeSpan Duration => End - Start;
}

/// <summary>
/// Records sessions while the app runs: every reading of every visible sensor goes to a CSV in
/// <c>%AppData%\SensorDeck\recordings</c>, and stopping writes a summary (min/avg/max, time spent in warning/critical).
/// Started by hand on the Recordings page or automatically by an automation rule.
/// </summary>
public sealed partial class RecordingService : ObservableObject, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    /// <summary>Longer gaps (app suspended, collector restarted) don't count as time in a level.</summary>
    private const long MaxStepMs = 10_000;

    private readonly SensorStore _store;
    private StreamWriter? _writer;
    private string[] _ids = [];
    private Stats[] _stats = [];
    private RecordingSummary? _current;
    private long _lastTime;
    private DateTime _lastFlush;
    private readonly StringBuilder _line = new();

    [ObservableProperty]
    private bool _isRecording;

    [ObservableProperty]
    private string _statusText = "";

    public RecordingService(SensorStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        store.ValuesApplied += OnValues;
    }

    public static string Directory => Path.Combine(AppSettings.Directory, "recordings");

    /// <summary>The current recording was started by automation (and automation may stop it).</summary>
    public bool IsAutomatic => _current?.Automatic == true;

    /// <summary>The user stopped an automatic recording: automation shouldn't start another until its rule ends.</summary>
    public bool AutomaticStoppedByUser { get; set; }

    /// <summary>Raised after a recording is finished or deleted.</summary>
    public event Action? RecordingsChanged;

    /// <summary>Starts recording; does nothing if one is running. Returns an error message, or null.</summary>
    public string? Start(string? name, bool automatic = false)
    {
        if (IsRecording)
        {
            return null;
        }

        var sensors = _store.Sensors.Where(s => !s.IsHidden).ToList();
        if (sensors.Count == 0)
        {
            return "No sensors yet: wait until SensorDeck is connected.";
        }

        var start = DateTimeOffset.Now;
        var title = string.IsNullOrWhiteSpace(name) ? "Recording" : name.Trim();
        var baseName = $"{start:yyyy-MM-dd HHmmss} {Safe(title)}";
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var csv = Path.Combine(Directory, baseName + ".csv");
            _writer = new StreamWriter(csv, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            _writer.WriteLine(string.Join(",", new[] { "Time" }.Concat(sensors.Select(s => HistoryExport.Escape(HistoryExport.ColumnName(s))))));
            _current = new RecordingSummary
            {
                Name = title,
                Start = start,
                End = start,
                Automatic = automatic,
                CsvFile = Path.GetFileName(csv),
                SummaryPath = Path.Combine(Directory, baseName + ".summary.json"),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _writer?.Dispose();
            _writer = null;
            return $"Couldn't start recording: {ex.Message}";
        }

        _ids = [.. sensors.Select(s => s.Id)];
        _stats = [.. sensors.Select(s => new Stats(s))];
        _lastTime = 0;
        _lastFlush = DateTime.UtcNow;
        IsRecording = true;
        StatusText = $"Recording “{title}”…";
        return null;
    }

    /// <summary>Stops and saves the summary. Returns it (null if nothing was recording).</summary>
    public RecordingSummary? Stop()
    {
        if (!IsRecording || _current is not { } summary)
        {
            return null;
        }

        IsRecording = false;
        try
        {
            _writer?.Dispose(); // flushes
        }
        catch (IOException)
        {
            // Disk full or gone: keep what was written.
        }

        _writer = null;
        summary.End = DateTimeOffset.Now;
        summary.Sensors = [.. _stats.Select(s => s.ToRecord())];
        _current = null;
        try
        {
            File.WriteAllText(summary.SummaryPath, JsonSerializer.Serialize(summary, JsonOptions));
            if (!StatusText.StartsWith("Recording stopped", StringComparison.Ordinal))
            {
                StatusText = $"Saved “{summary.Name}” ({Format(summary.Duration)}).";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = $"Couldn't save the summary: {ex.Message}";
        }
        RecordingsChanged?.Invoke();
        return summary;
    }

    /// <summary>All saved recordings, newest first.</summary>
    public static List<RecordingSummary> List()
    {
        var result = new List<RecordingSummary>();
        if (!System.IO.Directory.Exists(Directory))
        {
            return result;
        }

        foreach (var path in System.IO.Directory.EnumerateFiles(Directory, "*.summary.json"))
        {
            try
            {
                if (JsonSerializer.Deserialize<RecordingSummary>(File.ReadAllText(path), JsonOptions) is { } summary)
                {
                    summary.SummaryPath = path;
                    summary.Sensors ??= [];
                    summary.CsvFile ??= ""; // "CsvFile": null would make CsvPath throw (Delete)
                    result.Add(summary);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // Skip damaged summaries.
            }
        }

        return [.. result.OrderByDescending(r => r.Start)];
    }

    public void Delete(RecordingSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        foreach (var (path, extension) in new[] { (summary.CsvPath, ".csv"), (summary.SummaryPath, ".json") })
        {
            // The CSV name comes from the summary JSON (CsvPath keeps it in this folder): only ever delete the
            // recording's own file types, so an edited summary can't make Delete remove some other file.
            if (!path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Leave it; the list shows what's left.
            }
        }

        RecordingsChanged?.Invoke();
    }

    public void Dispose()
    {
        Stop();
        _store.ValuesApplied -= OnValues;
    }

    public static string Format(TimeSpan duration) =>
        duration.TotalHours >= 1 ? $"{(int)duration.TotalHours} h {duration.Minutes} min"
        : duration.TotalMinutes >= 1 ? $"{duration.Minutes} min {duration.Seconds} s"
        : $"{duration.Seconds} s";

    private void OnValues(long timestampUnixMs)
    {
        if (!IsRecording || _writer is null || _current is null)
        {
            return;
        }

        var step = _lastTime == 0 ? 0 : Math.Clamp(timestampUnixMs - _lastTime, 0, MaxStepMs);
        _lastTime = timestampUnixMs;
        _line.Clear().Append(HistoryExport.Time(timestampUnixMs));
        for (var i = 0; i < _ids.Length; i++)
        {
            // Look up each time: a new catalog replaces the SensorItem objects.
            var sensor = _store.SensorsById.TryGetValue(_ids[i], out var s) ? s : null;
            var value = sensor?.Value;
            _line.Append(',').Append(HistoryExport.Cell(value));
            _stats[i].Add(value, sensor?.Level ?? ValueLevel.Normal, step);
        }

        try
        {
            _writer.WriteLine(_line.ToString());
            if (DateTime.UtcNow - _lastFlush > TimeSpan.FromSeconds(30))
            {
                _writer.Flush();
                _lastFlush = DateTime.UtcNow;
            }
        }
        catch (IOException ex)
        {
            StatusText = $"Recording stopped: {ex.Message}";
            Stop();
            return;
        }

        _current.Samples++;
        _current.End = DateTimeOffset.Now;
        StatusText = $"Recording “{_current.Name}” · {Format(_current.Duration)}";
    }

    private static string Safe(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return cleaned.Length > 60 ? cleaned[..60] : cleaned;
    }

    /// <summary>Running statistics for one sensor.</summary>
    private sealed class Stats(SensorItem sensor)
    {
        private readonly string _id = sensor.Id;
        private readonly string _name = sensor.Name;
        private readonly string _group = sensor.GroupName;
        private readonly SensorKind _kind = sensor.Kind;
        private readonly string _unit = sensor.Unit;
        private double _min = double.NaN;
        private double _max = double.NaN;
        private double _sum;
        private int _count;
        private long _warningMs;
        private long _criticalMs;

        public void Add(float? value, ValueLevel level, long stepMs)
        {
            if (value is { } v && !float.IsNaN(v))
            {
                _min = double.IsNaN(_min) ? v : Math.Min(_min, v);
                _max = double.IsNaN(_max) ? v : Math.Max(_max, v);
                _sum += v;
                _count++;
            }

            if (level == ValueLevel.Critical)
            {
                _criticalMs += stepMs;
            }
            else if (level == ValueLevel.Warning)
            {
                _warningMs += stepMs;
            }
        }

        public RecordingSensorStats ToRecord() => new(
            _id,
            _name,
            _group,
            _kind,
            _unit,
            _count == 0 ? null : _min,
            _count == 0 ? null : _sum / _count,
            _count == 0 ? null : _max,
            _warningMs / 1000.0,
            _criticalMs / 1000.0);
    }
}
