using System.Text.Json.Serialization;

namespace SpaceDeck.Core.Dashboards;

/// <summary>What a tile draws. Stored by name in JSON, so members can be reordered but not renamed.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<WidgetType>))]
public enum WidgetType
{
    /// <summary>Label, big number + unit, scale bar, min/max.</summary>
    BigNumber,

    /// <summary>270° ring with the number in the middle.</summary>
    Gauge,

    /// <summary>Label + number on one line, a bar underneath.</summary>
    Bar,

    /// <summary>Number with a trend line filling the rest of the tile.</summary>
    Sparkline,

    /// <summary>Time chart of one or more sensors.</summary>
    Chart,

    /// <summary>Voltage rails with the ATX tolerance band.</summary>
    Rails,

    /// <summary>Fans and pump: RPM plus FanControl duty.</summary>
    Fans,

    /// <summary>Any sensors as name · value rows with thin bars.</summary>
    List,

    /// <summary>Text only: a section header or note.</summary>
    Label,

    /// <summary>Buttons that switch FanControl configs.</summary>
    FanProfiles,

    /// <summary>One cell per CPU core/thread, colored by load, temperature or clock (<see cref="TileSpec.Metric"/>).</summary>
    CoreMap,

    /// <summary>Time and date.</summary>
    Clock,

    /// <summary>How long Windows has been running.</summary>
    Uptime,

    /// <summary>Network adapter and drive transfer rates (found automatically).</summary>
    Throughput,

    /// <summary>Several values in one line of text, from a template like "CPU {1} · GPU {2}".</summary>
    Text,

    /// <summary>
    /// A hardware section (CPU, GPU, RAM, drives, network, motherboard, battery): an icon heading, the hardware's
    /// name and aligned rows of readings, chosen with <see cref="TileSpec.Section"/> and <see cref="TileSpec.Rows"/>.
    /// Made for the sidebar.
    /// </summary>
    Section,
}

/// <summary>One dashboard: a name and its tiles. Saved as one JSON file.</summary>
public sealed class DashboardDocument
{
    public const int CurrentVersion = 1;

    /// <summary>File-format version, for future migrations.</summary>
    public int Version { get; set; } = CurrentVersion;

    /// <summary>Stable id; also the file name.</summary>
    public string Id { get; set; } = NewId();

    public string Name { get; set; } = "Dashboard";

    /// <summary>Position among the dashboard tabs.</summary>
    public int Order { get; set; }

    public List<TileSpec> Tiles { get; set; } = [];

    public static string NewId() => Guid.NewGuid().ToString("N")[..12];

    /// <summary>A deep copy with a new id (and new tile ids).</summary>
    public DashboardDocument Clone(string name) => new()
    {
        Name = name,
        Order = Order,
        Tiles = [.. Tiles.Select(t => t.Clone())],
    };
}

/// <summary>
/// One tile: where it sits on the 12-column grid, what it shows and how. Sensors are referenced by their stable
/// collector ids, so a dashboard survives restarts; a sensor that disappears shows "not found" instead of failing.
/// </summary>
public sealed class TileSpec
{
    public string Id { get; set; } = DashboardDocument.NewId();

    public WidgetType Type { get; set; } = WidgetType.BigNumber;

    /// <summary>Left column, 0-based (0–11).</summary>
    public int Col { get; set; }

    /// <summary>Top row, 0-based. Rows have a fixed height; the grid grows downward.</summary>
    public int Row { get; set; }

    public int ColSpan { get; set; } = 3;

    public int RowSpan { get; set; } = 4;

    /// <summary>Overrides the default title (the sensor's name). Empty string hides the title.</summary>
    public string? Title { get; set; }

    /// <summary>Sensor ids. One for number widgets; any number for charts and lists.</summary>
    public List<string> Sensors { get; set; } = [];

    /// <summary>Body text for <see cref="WidgetType.Label"/> tiles.</summary>
    public string? Text { get; set; }

    /// <summary>Time span for charts and sparklines.</summary>
    public int WindowSeconds { get; set; } = 300;

    /// <summary>Fixed scale bottom; null = 0 / automatic.</summary>
    public double? ScaleMin { get; set; }

    /// <summary>Fixed scale top; null = automatic.</summary>
    public double? ScaleMax { get; set; }

    /// <summary>Digits after the decimal point; null = the default for the sensor kind.</summary>
    public int? Decimals { get; set; }

    /// <summary>Reading at or above which the tile turns to the warning color (in the sensor's base unit, e.g. °C).</summary>
    public double? Warning { get; set; }

    /// <summary>Reading at or above which the tile turns to the critical color.</summary>
    public double? Critical { get; set; }

    /// <summary>Skin series color index (0–5) for the normal state; null = the skin's ink.</summary>
    public int? Accent { get; set; }

    public bool ShowMinMax { get; set; } = true;

    /// <summary>What a <see cref="WidgetType.CoreMap"/> shows: "load", "temperature" or "clock".</summary>
    public string? Metric { get; set; }

    /// <summary>Which <see cref="WidgetType.Section"/>: "cpu", "gpu", "memory", "storage", "network", "motherboard", "battery".</summary>
    public string? Section { get; set; }

    /// <summary>Hardware id a section shows (a particular CPU or GPU); null = the main one.</summary>
    public string? Hardware { get; set; }

    /// <summary>A section's rows that are switched on (option keys); null = the section's defaults.</summary>
    public List<string>? Rows { get; set; }

    /// <summary>A section's renamed rows: automatic name (e.g. "Slot 2") → the user's name (e.g. "Top stick").</summary>
    public Dictionary<string, string>? Labels { get; set; }

    /// <summary>A section's hidden rows, by automatic name (e.g. "System Fan #4").</summary>
    public List<string>? HiddenRows { get; set; }

    public TileSpec Clone() => new()
    {
        Type = Type,
        Col = Col,
        Row = Row,
        ColSpan = ColSpan,
        RowSpan = RowSpan,
        Title = Title,
        Sensors = [.. Sensors],
        Text = Text,
        WindowSeconds = WindowSeconds,
        ScaleMin = ScaleMin,
        ScaleMax = ScaleMax,
        Decimals = Decimals,
        Warning = Warning,
        Critical = Critical,
        Accent = Accent,
        ShowMinMax = ShowMinMax,
        Metric = Metric,
        Section = Section,
        Hardware = Hardware,
        Rows = Rows?.ToList(),
        Labels = Labels is null ? null : new Dictionary<string, string>(Labels, StringComparer.Ordinal),
        HiddenRows = HiddenRows?.ToList(),
    };

    /// <summary>Smallest sensible size for each widget, in cells.</summary>
    public static (int ColSpan, int RowSpan) MinimumSize(WidgetType type) => type switch
    {
        WidgetType.BigNumber => (2, 3),
        WidgetType.Gauge => (2, 3),
        WidgetType.Bar => (2, 2),
        WidgetType.Sparkline => (2, 3),
        WidgetType.Chart => (3, 3),
        WidgetType.Rails or WidgetType.Fans or WidgetType.List => (3, 2),
        WidgetType.Label => (1, 1),
        WidgetType.FanProfiles => (2, 2),
        WidgetType.CoreMap => (3, 3),
        WidgetType.Clock or WidgetType.Uptime => (2, 2),
        WidgetType.Throughput => (3, 2),
        WidgetType.Text => (2, 1),
        WidgetType.Section => (2, 2),
        _ => (1, 1),
    };

    /// <summary>Size a newly added tile starts at.</summary>
    public static (int ColSpan, int RowSpan) DefaultSize(WidgetType type, int sensorCount) => type switch
    {
        WidgetType.BigNumber => (3, 4),
        WidgetType.Gauge => (3, 5),
        WidgetType.Bar => (3, 2),
        WidgetType.Sparkline => (2, 4),
        WidgetType.Chart => (6, 6),
        WidgetType.Rails or WidgetType.Fans or WidgetType.List => (4, Math.Clamp((int)Math.Ceiling(((sensorCount * 30) + 52) / (double)GridLayout.RowPitchPx), 2, 14)),
        WidgetType.Label => (12, 1),
        WidgetType.FanProfiles => (4, 2),
        WidgetType.CoreMap => (6, 5),
        WidgetType.Clock => (3, 3),
        WidgetType.Uptime => (3, 2),
        WidgetType.Throughput => (4, 5),
        WidgetType.Text => (6, 1),
        WidgetType.Section => (3, 6),
        _ => (3, 3),
    };

    /// <summary>Widgets that take any number of sensors (the rest use the first).</summary>
    public static bool IsMultiSensor(WidgetType type) =>
        type is WidgetType.Chart or WidgetType.Rails or WidgetType.Fans or WidgetType.List or WidgetType.Text;

    /// <summary>Widgets that need no sensor at all.</summary>
    public static bool NeedsNoSensor(WidgetType type) =>
        type is WidgetType.Label or WidgetType.FanProfiles or WidgetType.CoreMap or WidgetType.Clock or WidgetType.Uptime or WidgetType.Throughput
            or WidgetType.Section;
}
