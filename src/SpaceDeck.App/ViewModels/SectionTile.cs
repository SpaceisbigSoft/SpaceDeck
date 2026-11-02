using System.ComponentModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using SpaceDeck.App.Services;
using SpaceDeck.Core.Dashboards;
using SpaceDeck.Core.Model;

namespace SpaceDeck.App.ViewModels;

/// <summary>A row a section can show, switched on and off with a checkbox.</summary>
public sealed record SectionOption(string Key, string Label, bool Default);

/// <summary>A kind of section (CPU, GPU, RAM…) and the rows it offers.</summary>
public sealed record SectionKind(string Key, string Title, string Glyph, IReadOnlyList<SectionOption> Options)
{
    public IReadOnlyList<string> Defaults => [.. Options.Where(o => o.Default).Select(o => o.Key)];
}

/// <summary>One planned row: a label and up to three sensors (shown right-aligned), or a sub-heading.</summary>
/// <remarks>Values fill the rightmost columns; a null keeps its column empty so the others stay aligned.</remarks>
/// <param name="GapBefore">A little space above the row (where the E-cores start after the P-cores).</param>
/// <param name="DefaultLabel">The automatic name (what <see cref="TileSpec.Labels"/> renames are keyed on).</param>
public sealed record SectionRowPlan(string Label, IReadOnlyList<SensorItem?> Values, bool IsHeading = false, bool GapBefore = false, string? DefaultLabel = null)
{
    public string Key => DefaultLabel ?? Label;
}

/// <summary>What a section tile will show, worked out from the current sensors.</summary>
public sealed record SectionPlan(SectionKind Kind, string? HardwareName, IReadOnlyList<SectionRowPlan> Rows);

/// <summary>A section the builder can offer on this PC (one per CPU/GPU, one each for the rest).</summary>
public sealed record SectionCandidate(SectionKind Kind, string? HardwareId, string Label, bool Recommended);

/// <summary>
/// Hardware sections for the sidebar (<see cref="WidgetType.Section"/>): which exist, which rows each offers, and
/// which sensors fill those rows. Sensor names follow LibreHardwareMonitor (Intel, AMD, NVIDIA, Super I/O chips).
/// </summary>
public static class Sections
{
    private const int MaxValues = 3;

    public static SectionKind Cpu { get; } = new("cpu", "CPU", "",
    [
        new("name", "Processor name", true),
        new("core_clock", "Clock per core", true),
        new("core_temp", "Temperature per core", true),
        new("core_load", "Load per core", false),
        new("temp", "Package temperature", true),
        new("load", "Total load", true),
        new("power", "Package power", true),
        new("vcore", "Core voltage", false),
    ]);

    public static SectionKind Gpu { get; } = new("gpu", "GPU", "",
    [
        new("name", "Graphics card name", true),
        new("temp", "Temperature", true),
        new("hotspot", "Hot spot", false),
        new("load", "Load", true),
        new("core_clock", "Core clock", true),
        new("mem_clock", "Memory clock", true),
        new("vram", "Video memory used", true),
        new("power", "Power", true),
        new("fan", "Fan", true),
    ]);

    public static SectionKind Memory { get; } = new("memory", "RAM", "",
    [
        new("name", "Part number", true),
        new("load", "Load", true),
        new("used", "Used", true),
        new("free", "Available", true),
        new("temps", "Module temperatures", false),
    ]);

    public static SectionKind Storage { get; } = new("storage", "Drives", "",
    [
        new("temp", "Temperature", true),
        new("used", "Used space", true),
        new("activity", "Read / write rate", false),
    ]);

    public static SectionKind Network { get; } = new("network", "Network", "",
    [
        new("rate", "Download / upload rate", true),
        new("total", "Downloaded / uploaded today", false),
        new("utilization", "Utilization", false),
    ]);

    public static SectionKind Motherboard { get; } = new("motherboard", "Motherboard", "",
    [
        new("name", "Board name", true),
        new("temps", "Temperatures", true),
        new("fans", "Fans and pump", true),
        new("voltages", "Voltage rails", false),
    ]);

    public static SectionKind Battery { get; } = new("battery", "Battery", "",
    [
        new("charge", "Charge", true),
        new("rate", "Charge / discharge rate", true),
        new("time", "Time remaining", true),
        new("health", "Wear level", false),
    ]);

    public static IReadOnlyList<SectionKind> Kinds { get; } = [Cpu, Gpu, Memory, Storage, Network, Motherboard, Battery];

    public static SectionKind? Find(string? key) => Kinds.FirstOrDefault(k => k.Key == key);

    /// <summary>The sections this PC can show, in sidebar order, with sensible ones pre-selected.</summary>
    public static IReadOnlyList<SectionCandidate> Available(IReadOnlyList<SensorItem> sensors)
    {
        ArgumentNullException.ThrowIfNull(sensors);
        var result = new List<SectionCandidate>();
        foreach (var cpu in sensors.Where(s => s.HardwareKind == HardwareKind.Cpu).GroupBy(s => s.HardwareId))
        {
            result.Add(new SectionCandidate(Cpu, cpu.Key, $"CPU · {cpu.First().GroupName}", true));
        }

        var gpus = DefaultDashboard.GpusByImportance(sensors);
        for (var i = 0; i < gpus.Count; i++)
        {
            var hasTemp = gpus[i].Any(s => s.Kind == SensorKind.Temperature);
            result.Add(new SectionCandidate(Gpu, gpus[i][0].HardwareId, $"GPU · {gpus[i][0].GroupName}", i == 0 || hasTemp));
        }

        if (sensors.Any(s => s.HardwareKind == HardwareKind.Memory))
        {
            result.Add(new SectionCandidate(Memory, null, "RAM", true));
        }

        if (sensors.Any(s => s.HardwareKind == HardwareKind.Storage))
        {
            result.Add(new SectionCandidate(Storage, null, "Drives", true));
        }

        if (Adapters(sensors).Any())
        {
            result.Add(new SectionCandidate(Network, null, "Network", true));
        }

        if (sensors.Any(s => s.HardwareKind == HardwareKind.SuperIo))
        {
            result.Add(new SectionCandidate(Motherboard, null, "Motherboard (temperatures, fans)", false));
        }

        if (sensors.Any(s => s.HardwareKind == HardwareKind.Battery))
        {
            result.Add(new SectionCandidate(Battery, null, "Battery", true));
        }

        return result;
    }

    /// <summary>Works out the rows a section tile shows right now.</summary>
    /// <param name="includeHidden">Also list rows the user hid (the tile editor shows them so they can be shown again).</param>
    /// <param name="boardName">The motherboard's name from the catalog (its sensors sit on the Super I/O chip, so they don't carry it).</param>
    public static SectionPlan Plan(TileSpec spec, IReadOnlyList<SensorItem> sensors, bool includeHidden = false, string? boardName = null)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(sensors);
        var kind = Find(spec.Section) ?? Cpu;
        var on = (spec.Rows ?? kind.Defaults).ToHashSet(StringComparer.Ordinal);
        var rows = new List<SectionRowPlan>();
        string? name = null;

        void Row(string key, string label, params SensorItem?[] values)
        {
            if (on.Contains(key) && values.Any(v => v is not null))
            {
                rows.Add(new SectionRowPlan(label, [.. values.Take(MaxValues)]));
            }
        }

        switch (kind.Key)
        {
            case "cpu":
            {
                var cpu = Hardware(spec, sensors, HardwareKind.Cpu);
                name = cpu.FirstOrDefault()?.GroupName;
                // Fixed columns for the core rows (only the switched-on ones): clock · temperature · load.
                var columns = new[] { "core_clock", "core_temp", "core_load" }.Where(on.Contains).ToList();
                if (columns.Count > 0)
                {
                    string? previousType = null;
                    foreach (var core in CoreMap.Cores(cpu))
                    {
                        // Space between the P-cores and the E-cores.
                        var type = core.Label.StartsWith("E-", StringComparison.Ordinal) ? "E" : "P";
                        var gap = previousType is not null && previousType != type;
                        previousType = type;

                        SensorItem?[] values = [.. columns.Select(c => c switch
                        {
                            "core_clock" => core.Clock,
                            "core_temp" => core.Temperature,
                            _ => core.Load,
                        })];
                        if (values.Any(v => v is not null))
                        {
                            rows.Add(new SectionRowPlan(core.Label, values, GapBefore: gap));
                        }
                    }
                }

                // Package temperature and total load sit under the per-core temperature / load columns.
                SensorItem?[] Under(string column, SensorItem? value)
                {
                    var index = columns.IndexOf(column);
                    if (index < 0)
                    {
                        return [value];
                    }

                    var slots = new SensorItem?[columns.Count];
                    slots[index] = value;
                    return slots;
                }

                Row("temp", "Temperature", Under("core_temp", DefaultDashboard.Pick(cpu, HardwareKind.Cpu, SensorKind.Temperature, DefaultDashboard.CpuTemperatureNames, anyFallback: false)));
                Row("load", "Load", Under("core_load", DefaultDashboard.Pick(cpu, HardwareKind.Cpu, SensorKind.Load, ["CPU Total"], anyFallback: false)));
                Row("power", "Power", DefaultDashboard.Pick(cpu, HardwareKind.Cpu, SensorKind.Power, ["CPU Package", "Package"], anyFallback: false));
                Row("vcore", "Vcore",
                    DefaultDashboard.Pick(sensors, HardwareKind.SuperIo, SensorKind.Voltage, ["Vcore", "CPU Vcore", "CPU Core"], anyFallback: false)
                    ?? DefaultDashboard.Pick(cpu, HardwareKind.Cpu, SensorKind.Voltage, ["CPU Core", "Core (SVI2 TFN)"], anyFallback: false));
                break;
            }

            case "gpu":
            {
                var gpu = Hardware(spec, sensors, HardwareKind.Gpu);
                name = gpu.FirstOrDefault()?.GroupName;
                Row("temp", "Temperature", DefaultDashboard.Pick(gpu, HardwareKind.Gpu, SensorKind.Temperature, DefaultDashboard.GpuTemperatureNames));
                Row("hotspot", "Hot spot", DefaultDashboard.Pick(gpu, HardwareKind.Gpu, SensorKind.Temperature, ["GPU Hot Spot"], anyFallback: false));
                Row("load", "Load", DefaultDashboard.Pick(gpu, HardwareKind.Gpu, SensorKind.Load, ["GPU Core", "D3D 3D"]));
                Row("core_clock", "Core clock", DefaultDashboard.Pick(gpu, HardwareKind.Gpu, SensorKind.Clock, ["GPU Core"]));
                Row("mem_clock", "Memory clock", DefaultDashboard.Pick(gpu, HardwareKind.Gpu, SensorKind.Clock, ["GPU Memory"], anyFallback: false));
                Row("vram", "VRAM used",
                    DefaultDashboard.Pick(gpu, HardwareKind.Gpu, SensorKind.SmallData, ["GPU Memory Used", "D3D Dedicated Memory Used"], anyFallback: false));
                Row("power", "Power", DefaultDashboard.Pick(gpu, HardwareKind.Gpu, SensorKind.Power, ["GPU Package", "GPU Power", "GPU Core"]));
                Row("fan", "Fan",
                    gpu.FirstOrDefault(s => s.Kind == SensorKind.Fan)
                    ?? gpu.FirstOrDefault(s => s.Kind == SensorKind.Control));
                break;
            }

            case "memory":
            {
                var memory = sensors.Where(s => s.HardwareKind == HardwareKind.Memory).ToList();
                var physical = memory.Where(s => !s.GroupName.Contains("Virtual", StringComparison.OrdinalIgnoreCase)).ToList();
                // Part number and slots from Windows (the module names LibreHardwareMonitor reads can be garbled).
                var installed = MemoryModules.All;
                name = MemoryModules.PartNumbers(installed)
                    ?? memory.Select(s => s.GroupName).FirstOrDefault(n => IsModuleName(n) && !n.StartsWith("Memory module", StringComparison.Ordinal));
                Row("load", "Load", physical.FirstOrDefault(s => s.Kind == SensorKind.Load));
                Row("used", "Used", physical.FirstOrDefault(s => s.Kind == SensorKind.Data && s.OriginalName.Contains("Used", StringComparison.OrdinalIgnoreCase)));
                Row("free", "Available", physical.FirstOrDefault(s => s.Kind == SensorKind.Data && s.OriginalName.Contains("Available", StringComparison.OrdinalIgnoreCase)));
                if (on.Contains("temps"))
                {
                    // One reading per installed module. A DDR5 module's thermal sensor also reports its fixed
                    // low/high/critical thresholds as temperatures; those are skipped.
                    var modules = memory
                        .Where(s => s.Kind == SensorKind.Temperature && !s.IsHidden && !s.IsAdvanced && !IsThreshold(s.OriginalName))
                        .GroupBy(s => s.HardwareId)
                        .Select(g => g.OrderBy(s => s.OriginalName.Length).First())
                        .ToList();
                    // Modules are read in slot order, as Windows lists them; with the same count, pair them up by order.
                    IReadOnlyList<MemoryModule> slots = installed.Count == modules.Count ? installed : Array.Empty<MemoryModule>();
                    for (var i = 0; i < modules.Count; i++)
                    {
                        var label = slots.Count > 0 ? slots[i].SlotLabel : modules.Count == 1 ? "Temperature" : $"Module {i + 1}";
                        rows.Add(new SectionRowPlan(label, [modules[i]]));
                    }
                }

                break;
            }

            case "storage":
                foreach (var drive in sensors.Where(s => s.HardwareKind == HardwareKind.Storage).GroupBy(s => s.HardwareId))
                {
                    var list = drive.ToList();
                    var before = rows.Count;
                    rows.Add(new SectionRowPlan(list[0].GroupName, [], IsHeading: true));
                    Row("temp", "Temperature",
                        DefaultDashboard.Pick(list, HardwareKind.Storage, SensorKind.Temperature, ["Composite Temperature", "Temperature"]));
                    Row("used", "Used space", list.FirstOrDefault(s => s.Kind == SensorKind.Load && s.OriginalName.Contains("Used", StringComparison.OrdinalIgnoreCase)));
                    if (on.Contains("activity"))
                    {
                        Row("activity", "Read", list.FirstOrDefault(s => s.Kind == SensorKind.Throughput && s.OriginalName.Contains("Read", StringComparison.OrdinalIgnoreCase)));
                        Row("activity", "Write", list.FirstOrDefault(s => s.Kind == SensorKind.Throughput && s.OriginalName.Contains("Write", StringComparison.OrdinalIgnoreCase)));
                    }

                    DropEmptyHeading(rows, before);
                }

                break;

            case "network":
                foreach (var adapter in Adapters(sensors))
                {
                    var before = rows.Count;
                    rows.Add(new SectionRowPlan(adapter[0].GroupName, [], IsHeading: true));
                    Row("rate", "Down", adapter.FirstOrDefault(s => s.Kind == SensorKind.Throughput && s.OriginalName.Contains("Download", StringComparison.OrdinalIgnoreCase)));
                    Row("rate", "Up", adapter.FirstOrDefault(s => s.Kind == SensorKind.Throughput && s.OriginalName.Contains("Upload", StringComparison.OrdinalIgnoreCase)));
                    Row("total", "Downloaded", adapter.FirstOrDefault(s => s.Kind == SensorKind.Data && s.OriginalName.Contains("Download", StringComparison.OrdinalIgnoreCase)));
                    Row("total", "Uploaded", adapter.FirstOrDefault(s => s.Kind == SensorKind.Data && s.OriginalName.Contains("Upload", StringComparison.OrdinalIgnoreCase)));
                    Row("utilization", "Utilization", adapter.FirstOrDefault(s => s.Kind == SensorKind.Load));
                    DropEmptyHeading(rows, before);
                }

                break;

            case "motherboard":
            {
                name = boardName ?? sensors.FirstOrDefault(s => s.HardwareKind == HardwareKind.Motherboard)?.GroupName;
                var board = sensors.Where(s => s.HardwareKind == HardwareKind.SuperIo && !s.IsHidden && !s.IsAdvanced).ToList();
                if (on.Contains("temps"))
                {
                    rows.AddRange(board.Where(s => s.Kind == SensorKind.Temperature).Select(s => new SectionRowPlan(s.Name, [s])));
                }

                if (on.Contains("fans"))
                {
                    rows.AddRange(board.Where(s => s.Kind == SensorKind.Fan).Select(s => new SectionRowPlan(s.Name, [s])));
                }

                if (on.Contains("voltages"))
                {
                    rows.AddRange(board.Where(s => s is { Kind: SensorKind.Voltage, HasNominal: true }).Select(s => new SectionRowPlan(s.Name, [s])));
                }

                break;
            }

            case "battery":
            {
                var battery = sensors.Where(s => s.HardwareKind == HardwareKind.Battery).ToList();
                Row("charge", "Charge", battery.FirstOrDefault(s => s.Kind == SensorKind.Level && s.OriginalName.Contains("Charge", StringComparison.OrdinalIgnoreCase)));
                Row("rate", "Rate", battery.FirstOrDefault(s => s.Kind == SensorKind.Power));
                Row("time", "Time left", battery.FirstOrDefault(s => s.Kind == SensorKind.TimeSpan));
                Row("health", "Wear", battery.FirstOrDefault(s => s.Kind == SensorKind.Level && s.OriginalName.Contains("Degradation", StringComparison.OrdinalIgnoreCase)));
                break;
            }
        }

        // The user's names for rows ("Slot 2" → "Top stick"), keyed on the automatic name.
        var hidden = includeHidden || spec.HiddenRows is not { Count: > 0 } ? null : spec.HiddenRows.ToHashSet(StringComparer.Ordinal);
        var renamed = rows.Where(r => hidden is null || !hidden.Contains(r.Label)).Select(r => r with
        {
            DefaultLabel = r.Label,
            Label = spec.Labels is { } labels && labels.TryGetValue(r.Label, out var custom) && !string.IsNullOrWhiteSpace(custom) ? custom : r.Label,
        }).ToList();
        return new SectionPlan(kind, on.Contains("name") ? name : null, renamed);
    }

    /// <summary>Grid rows a section needs to show every line (estimated from the skin's normal text size).</summary>
    public static int FitRowSpan(SectionPlan plan, bool showTitle)
    {
        ArgumentNullException.ThrowIfNull(plan);
        const double Line = 20;
        var height = 14.0 // tile padding (measured: rows are 20 px, the frame adds little)
            + (showTitle ? 28 : 0) // the heading carries the hardware name
            + plan.Rows.Sum(r => r.IsHeading || r.GapBefore ? Line + 8 : Line)
            + (plan.Rows.Count == 0 ? Line : 0); // "nothing to show" line
        var span = (int)Math.Ceiling((height + GridLayout.GapPx) / GridLayout.RowPitchPx);
        return Math.Clamp(span, TileSpec.MinimumSize(WidgetType.Section).RowSpan, 40);
    }

    /// <summary>The sensors of the chosen (or main) CPU/GPU.</summary>
    private static List<SensorItem> Hardware(TileSpec spec, IReadOnlyList<SensorItem> sensors, HardwareKind kind)
    {
        if (spec.Hardware is { } id && sensors.Any(s => s.HardwareId == id))
        {
            return [.. sensors.Where(s => s.HardwareId == id)];
        }

        return kind == HardwareKind.Gpu
            ? DefaultDashboard.GpusByImportance(sensors).FirstOrDefault() ?? new List<SensorItem>()
            : sensors.Where(s => s.HardwareKind == kind).GroupBy(s => s.HardwareId).Select(g => g.ToList()).FirstOrDefault() ?? new List<SensorItem>();
    }

    /// <summary>Real network adapters (virtual, loopback and tunnel adapters left out).</summary>
    private static IEnumerable<List<SensorItem>> Adapters(IReadOnlyList<SensorItem> sensors) =>
        sensors.Where(s => s.HardwareKind == HardwareKind.Network)
            .GroupBy(s => s.HardwareId)
            .Select(g => g.ToList())
            .Where(g => !IsVirtualAdapter(g[0].GroupName) && g.Any(s => s.Kind == SensorKind.Throughput));

    private static readonly string[] VirtualAdapterWords =
        ["vEthernet", "Loopback", "Bluetooth", "Teredo", "isatap", "Virtual", "VPN", "TAP-", "WAN Miniport", "Hyper-V"];

    private static bool IsVirtualAdapter(string name) =>
        VirtualAdapterWords.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase));

    private static readonly string[] ThresholdWords = ["Limit", "Threshold", "Low", "High", "Critical", "Resolution", "Max", "Min"];

    /// <summary>A fixed threshold reported as a temperature (e.g. a DDR5 module's "Critical High"), not a reading.</summary>
    private static bool IsThreshold(string name) =>
        ThresholdWords.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase));

    /// <summary>A memory module's name (from SPD), not LibreHardwareMonitor's "Generic/Total/Virtual Memory".</summary>
    private static bool IsModuleName(string name) =>
        !name.Contains("Generic", StringComparison.OrdinalIgnoreCase)
        && !name.Contains("Total", StringComparison.OrdinalIgnoreCase)
        && !name.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
        && !name.Contains("Physical", StringComparison.OrdinalIgnoreCase);

    private static void DropEmptyHeading(List<SectionRowPlan> rows, int headingIndex)
    {
        if (rows.Count == headingIndex + 1)
        {
            rows.RemoveAt(headingIndex);
        }
    }
}

/// <summary>How fan speeds show in a section (<see cref="TileSpec.Metric"/>).</summary>
public static class FanDisplay
{
    public const string Rpm = "rpm";
    public const string Percent = "percent";
    public const string Bar = "bar";
    public const string BarRpm = "bar_rpm";
    public const string BarPercent = "bar_percent";

    public static IReadOnlyList<Option<string>> Choices { get; } =
    [
        new("RPM (1,031 RPM)", Rpm),
        new("Percent of maximum (45 %)", Percent),
        new("Bar", Bar),
        new("Bar + RPM", BarRpm),
        new("Bar + percent", BarPercent),
    ];

    public static string Normalize(string? mode) => Choices.Any(c => c.Value == mode) ? mode! : Rpm;
}

/// <summary>
/// One value in a section row. Fans can show as RPM, a percentage of their maximum (FanControl's calibration, or the
/// highest RPM seen), a bar, or a bar with either number.
/// </summary>
public sealed class SectionValue : ObservableObject, IDisposable
{
    private readonly string _mode;

    public SectionValue(TileReading reading, string fanMode)
    {
        ArgumentNullException.ThrowIfNull(reading);
        Reading = reading;
        _mode = IsFan(reading) ? FanDisplay.Normalize(fanMode) : FanDisplay.Rpm;
        reading.PropertyChanged += OnReadingChanged;
        if (IsFan(reading))
        {
            FanScale.Observe(reading.Sensor);
        }
    }

    public TileReading Reading { get; }

    public Brush Brush => Reading.Brush;

    public bool ShowBar => _mode is FanDisplay.Bar or FanDisplay.BarRpm or FanDisplay.BarPercent;

    public string DisplayValue => _mode switch
    {
        FanDisplay.Bar => "",
        FanDisplay.Percent or FanDisplay.BarPercent => Percent(),
        _ => Reading.DisplayValue,
    };

    /// <summary>Speed as a share of the fan's maximum (0–1), for the bar.</summary>
    public double Fraction
    {
        get
        {
            var (max, _) = FanScale.MaxFor(Reading.Sensor);
            return Reading.Value is { } rpm && float.IsFinite(rpm) && max is > 0 ? Math.Clamp(rpm / max.Value, 0, 1) : 0;
        }
    }

    public string ToolTip
    {
        get
        {
            var (max, calibrated) = FanScale.MaxFor(Reading.Sensor);
            return max is null
                ? Reading.DisplayValue
                : $"{Reading.DisplayValue} of {max.Value:N0} RPM max ({(calibrated ? "FanControl calibration" : "highest seen")})";
        }
    }

    public void Dispose() => Reading.PropertyChanged -= OnReadingChanged;

    private static bool IsFan(TileReading reading) => reading.Sensor.Kind == SensorKind.Fan;

    private string Percent()
    {
        var (max, _) = FanScale.MaxFor(Reading.Sensor);
        return Reading.Value is not { } rpm || !float.IsFinite(rpm) ? "–"
            : max is > 0 ? string.Create(System.Globalization.CultureInfo.CurrentCulture, $"{Math.Clamp(rpm / max.Value * 100, 0, 100):0} %")
            : rpm <= 0 ? "0 %" // stopped (or nothing connected): 0 % whatever the maximum
            : Reading.DisplayValue; // spinning but no maximum known yet: RPM rather than a made-up percentage
    }

    private void OnReadingChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(TileReading.DisplayValue):
                if (IsFan(Reading))
                {
                    FanScale.Observe(Reading.Sensor);
                }

                OnPropertyChanged(nameof(DisplayValue));
                OnPropertyChanged(nameof(Fraction));
                OnPropertyChanged(nameof(ToolTip));
                break;
            case nameof(TileReading.Brush):
                OnPropertyChanged(nameof(Brush));
                break;
        }
    }
}

/// <summary>A section row on screen: a label and up to three values in aligned columns (filled from the right).</summary>
public sealed class SectionRow : IDisposable
{
    public SectionRow(string label, bool isHeading, IReadOnlyList<SectionValue?> values, bool gapBefore = false, string? key = null)
    {
        Label = label;
        Key = key ?? label;
        IsHeading = isHeading;
        Margin = new System.Windows.Thickness(0, gapBefore ? 8 : 0, 0, 0);
        // Right-aligned: one value sits in the last column, two in the last two (so temperatures line up).
        var slots = new SectionValue?[3];
        var count = Math.Min(values.Count, slots.Length);
        for (var i = 0; i < count; i++)
        {
            slots[slots.Length - count + i] = values[i];
        }

        V1 = slots[0];
        V2 = slots[1];
        V3 = slots[2];
        Values = values;
    }

    public string Label { get; }

    /// <summary>The row's automatic name, which hiding and renaming are keyed on.</summary>
    public string Key { get; }

    public bool IsHeading { get; }

    /// <summary>Extra space above the row (between P-cores and E-cores).</summary>
    public System.Windows.Thickness Margin { get; }

    public SectionValue? V1 { get; }

    public SectionValue? V2 { get; }

    public SectionValue? V3 { get; }

    private IReadOnlyList<SectionValue?> Values { get; }

    public void Dispose()
    {
        foreach (var value in Values)
        {
            value?.Reading.Dispose();
            value?.Dispose();
        }
    }
}

/// <summary>The live content of a section tile.</summary>
public sealed class SectionTile : IDisposable
{
    // Sidebar numbers read better without decimals for these kinds ("5,677 MHz", "62 °C", "14 %").
    private static readonly TileSpec Whole = new() { Decimals = 0 };
    private static readonly TileSpec Automatic = new();

    public SectionTile(TileSpec spec, SensorStore store)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(store);
        var plan = Sections.Plan(spec, store.Sensors, boardName: store.MotherboardName);
        var fanMode = FanDisplay.Normalize(spec.Metric);
        Glyph = plan.Kind.Glyph;
        HardwareName = plan.HardwareName;
        Rows = [.. plan.Rows.Select(r => new SectionRow(r.Label, r.IsHeading,
            [.. r.Values.Select(s => s is null ? null : new SectionValue(new TileReading(s, SpecFor(s.Kind), store.Display), fanMode))], r.GapBefore, r.Key))];
        IsEmpty = Rows.Count == 0;
    }

    public string Glyph { get; }

    /// <summary>The CPU/GPU/board name shown as the tile's heading (null when unknown or switched off).</summary>
    public string? HardwareName { get; }

    public IReadOnlyList<SectionRow> Rows { get; }

    /// <summary>No rows switched on, or this PC doesn't report them.</summary>
    public bool IsEmpty { get; }

    public void Dispose()
    {
        foreach (var row in Rows)
        {
            row.Dispose();
        }
    }

    private static TileSpec SpecFor(SensorKind kind) =>
        kind is SensorKind.Temperature or SensorKind.Clock or SensorKind.Load or SensorKind.Power or SensorKind.Fan
            or SensorKind.Control or SensorKind.Level
            ? Whole
            : Automatic;
}
