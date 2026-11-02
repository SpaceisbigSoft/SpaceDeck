using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using SpaceDeck.App.Services;
using SpaceDeck.App.Themes;
using SpaceDeck.Core.Formatting;
using SpaceDeck.Core.Model;

namespace SpaceDeck.App.ViewModels;

/// <summary>One shared once-a-second tick for clock-like tiles (UI thread only). Runs only while someone listens.</summary>
public static class UiClock
{
    private static DispatcherTimer? _timer;
    private static event Action? TickHandlers;

    public static event Action? Tick
    {
        add
        {
            TickHandlers += value;
            if (_timer is null)
            {
                _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _timer.Tick += (_, _) => TickHandlers?.Invoke();
            }

            if (!_timer.IsEnabled)
            {
                _timer.Start(); // Start() on a running timer would restart its countdown
            }
        }

        remove
        {
            TickHandlers -= value;
            if (TickHandlers is null)
            {
                _timer?.Stop(); // no clock tiles left: don't wake the UI thread every second for nothing
            }
        }
    }
}

/// <summary>Clock, date and Windows uptime for the Clock and Uptime tiles.</summary>
public sealed partial class ClockTile : ObservableObject, IDisposable
{
    [ObservableProperty]
    private string _time = "";

    [ObservableProperty]
    private string _seconds = "";

    [ObservableProperty]
    private string _date = "";

    /// <summary>AM/PM in 12-hour mode (shown after the seconds), else empty.</summary>
    [ObservableProperty]
    private string _designator = "";

    [ObservableProperty]
    private string _uptime = "";

    [ObservableProperty]
    private string _since = "";

    public ClockTile()
    {
        Update();
        UiClock.Tick += Update;
    }

    public void Dispose() => UiClock.Tick -= Update;

    private void Update()
    {
        var now = DateTime.Now;
        var text = Appearance.Time(now);
        var designator = now.ToString("tt", CultureInfo.CurrentCulture);
        if (designator.Length > 0 && text.EndsWith(designator, StringComparison.Ordinal))
        {
            Time = text[..^designator.Length].TrimEnd();
            Designator = designator;
        }
        else
        {
            Time = text;
            Designator = "";
        }
        Seconds = now.ToString("ss", CultureInfo.CurrentCulture);
        Date = now.ToString("dddd, MMMM d", CultureInfo.CurrentCulture);
        var up = TimeSpan.FromMilliseconds(Environment.TickCount64);
        Uptime = up.TotalDays >= 1
            ? $"{(int)up.TotalDays} d {up.Hours} h {up.Minutes:00} min"
            : $"{up.Hours} h {up.Minutes:00} min";
        Since = $"since {(now - up).ToString("d", CultureInfo.CurrentCulture)} {Appearance.Time(now - up)}";
    }
}

/// <summary>One cell of the core map: a core (or thread) colored by its reading.</summary>
public sealed partial class CoreCell : ObservableObject, IDisposable
{
    private readonly SensorItem _sensor;
    private readonly double _low;
    private readonly double _high;
    private readonly DisplayOptions _display;

    [ObservableProperty]
    private string _text = "";

    [ObservableProperty]
    private Brush _fill = Brushes.Transparent;

    [ObservableProperty]
    private Brush _foreground = Brushes.Transparent;

    public CoreCell(SensorItem sensor, string label, double low, double high, DisplayOptions display)
    {
        _sensor = sensor;
        _low = low;
        _high = high;
        _display = display;
        Label = label;
        sensor.PropertyChanged += OnSensorChanged;
        Update();
    }

    public string Label { get; }

    public string ToolTip => $"{_sensor.Name} · {_sensor.GroupName}";

    public void Dispose() => _sensor.PropertyChanged -= OnSensorChanged;

    private void OnSensorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SensorItem.Value) or nameof(SensorItem.DisplayValue))
        {
            Update();
        }
    }

    private void Update()
    {
        var value = _sensor.Value;
        Text = value is { } v && !float.IsNaN(v) ? Short(_sensor.Kind, v) : "–";
        var t = value is { } x && !float.IsNaN(x) ? Math.Clamp((x - _low) / Math.Max(1e-6, _high - _low), 0, 1) : 0;
        // Fixed core-temperature colors (°C): the cells are tiny, so they don't follow per-tile thresholds.
        var hot = _sensor.Kind == SensorKind.Temperature && value >= 95 ? "Skin.Critical"
            : _sensor.Kind == SensorKind.Temperature && value >= 85 ? "Skin.Warning"
            : "Skin.Accent";
        Fill = Blend(SkinManager.Brush("Skin.Track"), SkinManager.Brush(hot), 0.15 + (0.85 * t));
        Foreground = t > 0.55 ? SkinManager.Brush("Skin.OnAccent") : SkinManager.Brush("Skin.Text");
    }

    /// <summary>"57", "4.9" (GHz), "88°" — no units, the cell is small.</summary>
    private string Short(SensorKind kind, float v) => kind switch
    {
        SensorKind.Clock => (v / 1000f).ToString("0.0", CultureInfo.CurrentCulture),
        SensorKind.Temperature when _display.TemperatureUnit == TemperatureUnit.Fahrenheit =>
            UnitFormatter.CelsiusToFahrenheit(v).ToString("0", CultureInfo.CurrentCulture) + "°",
        SensorKind.Temperature => v.ToString("0", CultureInfo.CurrentCulture) + "°",
        _ => v.ToString("0", CultureInfo.CurrentCulture),
    };

    private static SolidColorBrush Blend(Brush from, Brush to, double amount)
    {
        var a = (from as SolidColorBrush)?.Color ?? Colors.Gray;
        var b = (to as SolidColorBrush)?.Color ?? Colors.White;
        byte Mix(byte x, byte y) => (byte)Math.Round(x + ((y - x) * amount));
        var brush = new SolidColorBrush(Color.FromArgb(Mix(a.A, b.A), Mix(a.R, b.R), Mix(a.G, b.G), Mix(a.B, b.B)));
        brush.Freeze();
        return brush;
    }
}

/// <summary>A labelled block of core cells (P-cores / E-cores).</summary>
public sealed record CoreGroup(string Label, IReadOnlyList<CoreCell> Cells)
{
    public bool HasLabel => Label.Length > 0;
}

/// <summary>Finds the per-core sensors of the CPU for the core map tile.</summary>
public static partial class CoreMap
{
    public static readonly IReadOnlyList<(string Key, string Label)> Metrics =
    [
        ("load", "Load per thread"),
        ("temperature", "Temperature per core"),
        ("clock", "Clock per core"),
    ];

    // ASCII digits, at most 5, so int.Parse of a sensor name's numbers can't overflow or meet non-ASCII digits.
    [GeneratedRegex(@"^(?:CPU )?(?:(?<type>[PE])-)?Core #(?<core>[0-9]{1,5})(?: Thread #(?<thread>[0-9]{1,5}))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CoreName();

    public static string MetricLabel(string? metric) =>
        Metrics.FirstOrDefault(m => m.Key == metric).Label ?? Metrics[0].Label;

    /// <summary>A core map's layout without live cells (also used by the network dashboard).</summary>
    public sealed record Layout(IReadOnlyList<(string Label, IReadOnlyList<(SensorItem Sensor, string Label)> Cells)> Groups, double Low, double High);

    public static List<CoreGroup> Build(IEnumerable<SensorItem> sensors, string? metric, DisplayOptions display)
    {
        var layout = Describe(sensors, metric);
        return [.. layout.Groups.Select(g => new CoreGroup(g.Label, [.. g.Cells.Select(c => new CoreCell(c.Sensor, c.Label, layout.Low, layout.High, display))]))];
    }

    /// <summary>One physical core with its clock, temperature and load sensors (any may be missing).</summary>
    public sealed record CoreReadings(string Label, SensorItem? Clock, SensorItem? Temperature, SensorItem? Load);

    /// <summary>
    /// The CPU's physical cores in order (P-cores, then E-cores on hybrid Intel CPUs), each with its per-core clock,
    /// temperature and load. Labels are "P-Core 1…", "E-Core 1…" on hybrid CPUs, else "Core 1…".
    /// </summary>
    public static IReadOnlyList<CoreReadings> Cores(IEnumerable<SensorItem> cpuSensors)
    {
        var parsed = cpuSensors
            .Select(s => (Sensor: s, Match: CoreName().Match(s.OriginalName)))
            .Where(x => x.Match.Success)
            .Select(x => (
                x.Sensor,
                Type: x.Match.Groups["type"].Success ? x.Match.Groups["type"].Value.ToUpperInvariant() : "",
                Core: int.Parse(x.Match.Groups["core"].Value, CultureInfo.InvariantCulture),
                Thread: x.Match.Groups["thread"].Success ? int.Parse(x.Match.Groups["thread"].Value, CultureInfo.InvariantCulture) : 0))
            .ToList();

        SensorItem? Pick(IEnumerable<(SensorItem Sensor, string Type, int Core, int Thread)> group, SensorKind kind) =>
            group.Where(x => x.Sensor.Kind == kind && !x.Sensor.IsHidden).OrderBy(x => x.Thread).Select(x => x.Sensor).FirstOrDefault();

        var result = new List<CoreReadings>();
        var typed = parsed.Where(x => x.Type.Length > 0).ToList();
        if (typed.Count > 0)
        {
            // Hybrid Intel (LibreHardwareMonitor): clocks and temperatures are named "P-Core #k" / "E-Core #k", while
            // loads use the overall core number "CPU Core #n [Thread #t]", P-cores first. Map n onto P/E k.
            var pCount = typed.Where(x => x.Type == "P").Select(x => x.Core).DefaultIfEmpty(0).Max();
            var merged = typed.Concat(parsed
                    .Where(x => x.Type.Length == 0)
                    .Select(x => x.Core <= pCount ? x with { Type = "P" } : x with { Type = "E", Core = x.Core - pCount }))
                .ToList();
            foreach (var g in merged.GroupBy(x => (x.Type, x.Core)).OrderBy(g => g.Key.Type == "P" ? 0 : 1).ThenBy(g => g.Key.Core))
            {
                var clock = Pick(g, SensorKind.Clock);
                var temp = Pick(g, SensorKind.Temperature);
                if (clock is null && temp is null)
                {
                    continue; // a load-only number past the last real core
                }

                result.Add(new CoreReadings($"{(g.Key.Type == "P" ? "P" : "E")}-Core {g.Key.Core}", clock, temp, Pick(g, SensorKind.Load)));
            }

            return result;
        }

        // Older names: one "CPU Core #n" per core; on hybrid CPUs the cores with a second thread are P-cores.
        var twoThreads = parsed.Where(x => x.Thread == 2).Select(x => x.Core).ToHashSet();
        var cores = parsed
            .GroupBy(x => x.Core)
            .Select(g => (Core: g.Key, P: twoThreads.Contains(g.Key),
                          Clock: Pick(g, SensorKind.Clock), Temp: Pick(g, SensorKind.Temperature), Load: Pick(g, SensorKind.Load)))
            .Where(c => c.Clock is not null || c.Temp is not null || c.Load is not null)
            .OrderByDescending(c => c.P)
            .ThenBy(c => c.Core)
            .ToList();

        var hybrid = cores.Any(c => c.P) && cores.Any(c => !c.P);
        int p = 0, e = 0;
        foreach (var c in cores)
        {
            var label = !hybrid ? $"Core {result.Count + 1}" : c.P ? $"P-Core {++p}" : $"E-Core {++e}";
            result.Add(new CoreReadings(label, c.Clock, c.Temp, c.Load));
        }

        return result;
    }

    public static Layout Describe(IEnumerable<SensorItem> sensors, string? metric)
    {
        var kind = metric switch
        {
            "temperature" => SensorKind.Temperature,
            "clock" => SensorKind.Clock,
            _ => SensorKind.Load,
        };

        var cpu = sensors.Where(s => s.HardwareKind == HardwareKind.Cpu).ToList();

        // Hybrid Intel CPUs: cores with a second thread are P-cores (E-cores have one).
        var parsed = cpu.Select(s => (Sensor: s, Match: CoreName().Match(s.OriginalName))).Where(x => x.Match.Success).ToList();
        // Keyed by (P/E prefix, number): "P-Core #1" and "E-Core #1" are different cores.
        static (string, int) Key(Match m) =>
            (m.Groups["type"].Success ? m.Groups["type"].Value.ToUpperInvariant() : "",
             int.Parse(m.Groups["core"].Value, CultureInfo.InvariantCulture));

        var twoThreads = parsed
            .Where(x => x.Match.Groups["thread"].Success && x.Match.Groups["thread"].Value == "2")
            .Select(x => Key(x.Match))
            .ToHashSet();
        var withThreads = parsed
            .Where(x => x.Sensor.Kind == kind && x.Match.Groups["thread"].Success)
            .Select(x => Key(x.Match))
            .ToHashSet();

        var cells = parsed
            .Where(x => x.Sensor.Kind == kind)
            // A core-level reading next to per-thread ones would show the core twice.
            .Where(x => x.Match.Groups["thread"].Success || !withThreads.Contains(Key(x.Match)))
            .Select(x => (
                x.Sensor,
                Core: int.Parse(x.Match.Groups["core"].Value, CultureInfo.InvariantCulture),
                Thread: x.Match.Groups["thread"].Success ? int.Parse(x.Match.Groups["thread"].Value, CultureInfo.InvariantCulture) : 0,
                Type: x.Match.Groups["type"].Success ? x.Match.Groups["type"].Value.ToUpperInvariant() : null))
            .OrderBy(x => x.Type ?? "")
            .ThenBy(x => x.Core)
            .ThenBy(x => x.Thread)
            .ToList();

        var (low, high) = kind switch
        {
            SensorKind.Temperature => (30.0, 100.0),
            SensorKind.Clock => (0.0, Math.Max(1000.0, cells.Select(c => (double)(c.Sensor.Max ?? c.Sensor.Value ?? 0)).DefaultIfEmpty(0).Max())),
            _ => (0.0, 100.0),
        };

        (SensorItem Sensor, string Label) Cell((SensorItem Sensor, int Core, int Thread, string? Type) c) =>
            (c.Sensor, c.Thread > 0 && kind == SensorKind.Load && twoThreads.Contains((c.Type ?? "", c.Core)) ? $"{c.Core}.{c.Thread}" : $"{c.Core}");

        bool IsP((SensorItem Sensor, int Core, int Thread, string? Type) c) =>
            c.Type is { } t ? t == "P" : twoThreads.Contains(("", c.Core));

        var p = cells.Where(IsP).ToList();
        var e = cells.Where(c => !IsP(c)).ToList();
        IReadOnlyList<(string, IReadOnlyList<(SensorItem, string)>)> groups = p.Count > 0 && e.Count > 0
            ? [("P-cores", [.. p.Select(Cell)]), ("E-cores", [.. e.Select(Cell)])]
            : [("", [.. cells.Select(Cell)])];
        return new Layout(groups, low, high);
    }
}
