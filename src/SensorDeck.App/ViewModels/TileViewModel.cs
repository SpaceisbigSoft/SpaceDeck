using System.ComponentModel;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using SensorDeck.App.Controls;
using SensorDeck.App.Services;
using SensorDeck.App.Themes;
using SensorDeck.Core.Dashboards;

namespace SensorDeck.App.ViewModels;

/// <summary>
/// One tile on a dashboard: its <see cref="TileSpec"/> (what gets saved) plus the live sensors it resolved to.
/// Rebuilt whenever the collector sends a new catalog or the tile's settings change.
/// </summary>
public sealed partial class TileViewModel : ObservableObject, IDisposable
{
    [ObservableProperty]
    private int _col;

    [ObservableProperty]
    private int _row;

    [ObservableProperty]
    private int _colSpan;

    [ObservableProperty]
    private int _rowSpan;

    public TileViewModel(TileSpec spec, SensorStore store, FanProfileService fanProfiles)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(store);
        Spec = spec;
        Col = spec.Col;
        Row = spec.Row;
        ColSpan = spec.ColSpan;
        RowSpan = spec.RowSpan;

        var byId = store.SensorsById;
        List<SensorItem> sensors = spec.Type == WidgetType.Throughput
            ? [.. store.Sensors.Where(s => s.Kind == Core.Model.SensorKind.Throughput && !s.IsHidden
                                            && s.HardwareKind is Core.Model.HardwareKind.Network or Core.Model.HardwareKind.Storage)]
            : spec.Sensors.Select(id => byId.TryGetValue(id, out var s) ? s : null).OfType<SensorItem>().ToList();
        Readings = [.. sensors.Select(s => new TileReading(s, spec, store.Display))];
        MissingCount = Math.Max(0, spec.Sensors.Count - sensors.Count);
        IsWaiting = store.Sensors.Count == 0;

        switch (spec.Type)
        {
            case WidgetType.Chart:
                Series = [.. Readings.Select((r, i) => new ChartSeries(r.Name, r.History, SkinManager.Brush($"Skin.Series.{((spec.Accent ?? 0) + i) % 6}"), r.Sensor.Kind, store.Display))];
                break;
            case WidgetType.Fans:
                var all = FanRow.Build(store.Sensors).ToList();
                FanRows = [.. sensors.Select(s => all.FirstOrDefault(r => r.Fan == s) ?? new FanRow(s, null))];
                break;
        }

        switch (spec.Type)
        {
            case WidgetType.FanProfiles:
                Profiles = new FanProfilesTile(fanProfiles);
                break;
            case WidgetType.Clock or WidgetType.Uptime:
                Clock = new ClockTile();
                break;
            case WidgetType.CoreMap:
                CoreGroups = CoreMap.Build(store.Sensors, spec.Metric, store.Display);
                break;
            case WidgetType.Section:
                Section = new SectionTile(spec, store);
                break;
            case WidgetType.Text:
                foreach (var reading in Readings)
                {
                    reading.PropertyChanged += OnReadingChanged;
                }

                break;
        }
    }

    public ClockTile? Clock { get; }

    /// <summary>Section tile content (heading, hardware name, rows).</summary>
    public SectionTile? Section { get; }

    /// <summary>In a stacked layout (the sidebar) this tile is exactly as tall as its content.</summary>
    public bool FitsContent => Spec.Type == WidgetType.Section;

    public bool HasReadings => Readings.Count > 0;

    public IReadOnlyList<CoreGroup> CoreGroups { get; } = [];

    /// <summary>Core map: nothing matched (sensors not loaded, or a CPU LibreHardwareMonitor names differently).</summary>
    public bool HasNoCores => CoreGroups.All(g => g.Cells.Count == 0);

    /// <summary>Text tile: the template with {1}, {2}… replaced by readings ({1:name} = the sensor's name).</summary>
    public string TextValue
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Spec.Text))
            {
                return string.Join("   ·   ", Readings.Select(r => $"{r.Name} {r.DisplayValue}"));
            }

            return Placeholder().Replace(Spec.Text, m =>
            {
                var index = int.Parse(m.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture) - 1;
                if (index < 0)
                {
                    return "?";
                }

                // {n} is the n-th chosen sensor, even when an earlier one is missing right now.
                if (index >= Spec.Sensors.Count || Readings.FirstOrDefault(r => r.Sensor.Id == Spec.Sensors[index]) is not { } reading)
                {
                    return "–";
                }

                return m.Groups["name"].Success ? reading.Name : reading.DisplayValue;
            });
        }
    }

    // ASCII digits, at most 4: the template can come from an imported dashboard, and int.Parse must not throw on
    // "{99999999999}" (overflow) or non-ASCII digits (\d also matches those).
    [GeneratedRegex(@"\{(?<n>[0-9]{1,4})(?<name>:name)?\}", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();

    private void OnReadingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TileReading.DisplayValue))
        {
            OnPropertyChanged(nameof(TextValue));
        }
    }

    public TileSpec Spec { get; }

    public string Id => Spec.Id;

    public WidgetType Type => Spec.Type;

    /// <summary>The tile's heading: the override if set (empty hides it), else the first sensor's name.</summary>
    public string Title => Spec.Title ?? Spec.Type switch
    {
        WidgetType.Rails => "Voltage rails",
        WidgetType.Fans => "Fans & pump",
        WidgetType.Chart => Readings.Count == 1 ? Readings[0].Name : "History",
        WidgetType.List => "Sensors",
        WidgetType.FanProfiles => "Fan profile",
        WidgetType.Label => "",
        WidgetType.CoreMap => CoreMap.MetricLabel(Spec.Metric),
        WidgetType.Clock => "",
        WidgetType.Uptime => "Windows uptime",
        WidgetType.Throughput => "Network & disk",
        WidgetType.Text => "",
        WidgetType.Section => Section?.HardwareName ?? Sections.Find(Spec.Section)?.Title ?? "Section", // "Intel Core i9-14900K" beats "CPU"
        _ => Reading?.Name ?? "Sensor",
    };

    public bool HasTitle => Title.Length > 0;

    /// <summary>The (first) sensor, for single-sensor widgets.</summary>
    public TileReading? Reading => Readings.Count > 0 ? Readings[0] : null;

    public IReadOnlyList<TileReading> Readings { get; }

    /// <summary>Raw sensors (rail rows use the sensor's own nominal/tolerance logic).</summary>
    public IReadOnlyList<SensorItem> Sensors => [.. Readings.Select(r => r.Sensor)];

    public IReadOnlyList<ChartSeries> Series { get; } = [];

    public IReadOnlyList<FanRow> FanRows { get; } = [];

    public FanProfilesTile? Profiles { get; }

    public int MissingCount { get; }

    /// <summary>True when the tile needs sensors and none of them exist (e.g. hardware removed).</summary>
    public bool IsMissing => !TileSpec.NeedsNoSensor(Spec.Type) && Readings.Count == 0;

    /// <summary>No catalog yet (collector not connected): sensors aren't "missing", just not loaded.</summary>
    public bool IsWaiting { get; }

    /// <summary>The tile's sensors are gone (not just never chosen), so "Replace sensor…" makes sense.</summary>
    public bool CanReplace => !IsWaiting && Spec.Sensors.Count > 0;

    public string MissingText => IsWaiting ? "Waiting for sensors…" : Spec.Sensors.Count == 0 ? "No sensor chosen" : "Sensor not found";

    public string? Text => Spec.Text;

    public int WindowSeconds => Spec.WindowSeconds;

    public bool ShowMinMax => Spec.ShowMinMax;

    /// <summary>Size of the main number, scaled to the tile's height.</summary>
    public double HeroSize => RowSpan switch
    {
        >= 5 => SkinManager.Double("Skin.Size.Hero", 52) * 1.25,
        4 => SkinManager.Double("Skin.Size.Hero", 52),
        3 => SkinManager.Double("Skin.Size.Large", 28) * 1.25,
        _ => SkinManager.Double("Skin.Size.Large", 28),
    };

    /// <summary>Number size for trend, bar and compact tiles.</summary>
    public double CompactNumberSize => RowSpan >= 6 && ColSpan >= 3
        ? SkinManager.Double("Skin.Size.Hero", 52)
        : SkinManager.Double("Skin.Size.Large", 28);

    /// <summary>Gauge number size, scaled to the smaller tile side.</summary>
    public double GaugeNumberSize => Math.Clamp(Math.Min(ColSpan * 18, RowSpan * 9), 18, 52);

    public CellRect Cells => new(Col, Row, ColSpan, RowSpan);

    /// <summary>Copies the on-screen position back into the spec (before saving).</summary>
    public void SyncSpec()
    {
        Spec.Col = Col;
        Spec.Row = Row;
        Spec.ColSpan = ColSpan;
        Spec.RowSpan = RowSpan;
    }

    /// <summary>Takes the spec's position (after collision resolution moved it).</summary>
    public void LoadPosition()
    {
        Col = Spec.Col;
        Row = Spec.Row;
        ColSpan = Spec.ColSpan;
        RowSpan = Spec.RowSpan;
    }

    partial void OnRowSpanChanged(int value)
    {
        OnPropertyChanged(nameof(HeroSize));
        OnPropertyChanged(nameof(GaugeNumberSize));
        OnPropertyChanged(nameof(CompactNumberSize));
    }

    partial void OnColSpanChanged(int value)
    {
        OnPropertyChanged(nameof(GaugeNumberSize));
        OnPropertyChanged(nameof(CompactNumberSize));
    }

    public void Dispose()
    {
        foreach (var reading in Readings)
        {
            reading.PropertyChanged -= OnReadingChanged;
            reading.Dispose();
        }

        Profiles?.Dispose();
        Clock?.Dispose();
        Section?.Dispose();
        foreach (var cell in CoreGroups.SelectMany(g => g.Cells))
        {
            cell.Dispose();
        }
    }
}
