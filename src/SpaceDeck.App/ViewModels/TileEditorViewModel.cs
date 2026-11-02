using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SpaceDeck.App.Services;
using SpaceDeck.Core.Dashboards;
using SpaceDeck.Core.Formatting;
using SpaceDeck.Core.Model;

namespace SpaceDeck.App.ViewModels;

/// <summary>A widget the user can pick in the tile editor.</summary>
public sealed record WidgetChoice(WidgetType Type, string Name, string Description);

/// <summary>A labelled option for combo boxes (null value = "automatic").</summary>
public sealed record Option<T>(string Label, T Value);

/// <summary>A section row's name in the tile editor: the automatic name and the user's (blank = automatic).</summary>
public sealed partial class RowNameChoice : ObservableObject
{
    [ObservableProperty]
    private string _custom = "";

    [ObservableProperty]
    private bool _isShown = true;

    public RowNameChoice(string automatic, string custom, bool isShown)
    {
        Automatic = automatic;
        Custom = custom;
        IsShown = isShown;
    }

    public string Automatic { get; }
}

/// <summary>A sensor row in the tile editor's picker.</summary>
public sealed partial class SensorPickRow : ObservableObject
{
    [ObservableProperty]
    private bool _isChosen;

    public SensorPickRow(SensorItem sensor) => Sensor = sensor;

    public SensorItem Sensor { get; }
}

/// <summary>
/// Backs the add/edit tile dialog: choose a widget, choose its sensor(s), then optional settings
/// (title, scale, decimals, thresholds, color, time window).
/// </summary>
public sealed partial class TileEditorViewModel : ObservableObject
{
    private readonly SensorStore _store;
    private readonly TileSpec? _existing;
    private readonly Dictionary<string, SensorPickRow> _rows;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMultiSensor), nameof(NeedsSensors), nameof(IsLabel), nameof(HasTimeWindow), nameof(HasScale), nameof(HasNumberSettings), nameof(SensorHint), nameof(HasTemplate), nameof(HasMetric), nameof(HasSection), nameof(HasFanMode))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private WidgetChoice _widget = null!;

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private string _category = "All";

    [ObservableProperty]
    private bool _showTitle = true;

    [ObservableProperty]
    private string _titleText = "";

    [ObservableProperty]
    private string _labelText = "";

    [ObservableProperty]
    private Option<int> _window = null!;

    [ObservableProperty]
    private string _scaleMinText = "";

    [ObservableProperty]
    private string _scaleMaxText = "";

    [ObservableProperty]
    private Option<int?> _decimals = null!;

    [ObservableProperty]
    private string _warningText = "";

    [ObservableProperty]
    private string _criticalText = "";

    [ObservableProperty]
    private Option<int?> _accent = null!;

    [ObservableProperty]
    private bool _showMinMax = true;

    [ObservableProperty]
    private Option<string> _metric = null!;

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    private Option<string> _sectionKind = null!;

    [ObservableProperty]
    private IReadOnlyList<Option<string?>> _sectionHardwareOptions = [];

    [ObservableProperty]
    private Option<string?>? _sectionHardware;

    [ObservableProperty]
    private ObservableCollection<SectionOptionChoice> _sectionOptions = [];

    [ObservableProperty]
    private IReadOnlyList<RowNameChoice> _rowNames = [];

    [ObservableProperty]
    private Option<string> _fanMode = FanDisplay.Choices[0];

    public IReadOnlyList<Option<string>> FanModes => FanDisplay.Choices;

    /// <summary>Sections with fan rows (GPU, motherboard) offer RPM / percent / bar.</summary>
    public bool HasFanMode => HasSection && SectionKind?.Value is "gpu" or "motherboard";

    /// <summary>Row renames typed so far, kept while options change (automatic name → custom name).</summary>
    private readonly Dictionary<string, string> _labelEdits = new(StringComparer.Ordinal);

    /// <summary>Rows switched off (hidden), kept while options change.</summary>
    private readonly HashSet<string> _hiddenEdits = new(StringComparer.Ordinal);

    public TileEditorViewModel(SensorStore store, TileSpec? existing)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _existing = existing;

        _rows = store.Sensors.GroupBy(s => s.Id).ToDictionary(g => g.Key, g => new SensorPickRow(g.First()));
        Rows = [.. store.Sensors.Select(s => _rows[s.Id])];
        var view = new ListCollectionView(Rows);
        view.GroupDescriptions.Add(new PropertyGroupDescription("Sensor.GroupName"));
        view.Filter = Matches;
        View = view;

        Widget = Widgets[0];
        Window = Windows[1];
        Decimals = DecimalOptions[0];
        Accent = AccentOptions[0];
        Metric = Metrics[0];
        SectionKind = SectionKinds[0];

        if (existing is not null)
        {
            Load(existing);
            View.Refresh(); // chosen rows stay visible even if they're normally hidden
        }
    }

    public bool IsEdit => _existing is not null;

    public string WindowTitle => IsEdit ? "Tile settings" : "Add tile";

    public string SaveText => IsEdit ? "Save" : "Add tile";

    public IReadOnlyList<WidgetChoice> Widgets { get; } =
    [
        new(WidgetType.BigNumber, "Big number", "One reading, large, with a scale bar and min/max."),
        new(WidgetType.Gauge, "Gauge", "A ring gauge with the reading in the middle."),
        new(WidgetType.Bar, "Bar", "Compact: name, reading and a bar on two lines."),
        new(WidgetType.Sparkline, "Number + trend", "A reading with its recent trend line."),
        new(WidgetType.Chart, "Chart", "History of one or more sensors over time."),
        new(WidgetType.List, "Sensor list", "Several sensors as rows with values and thin bars."),
        new(WidgetType.Rails, "Voltage rails", "Rails with their ATX ±5 % tolerance band."),
        new(WidgetType.Fans, "Fans & pump", "RPM with the duty FanControl is applying."),
        new(WidgetType.Label, "Label", "A section header or note."),
        new(WidgetType.FanProfiles, "Fan profiles", "Buttons that switch FanControl configs."),
        new(WidgetType.CoreMap, "Core map", "Every CPU core/thread as a colored cell: load, temperature or clock."),
        new(WidgetType.Text, "Text line", "Several values in one line, e.g. \"CPU 54 °C · GPU 61 °C\"."),
        new(WidgetType.Throughput, "Network & disk", "Transfer rates of network adapters and drives."),
        new(WidgetType.Clock, "Clock", "Time and date."),
        new(WidgetType.Uptime, "Uptime", "How long Windows has been running."),
        new(WidgetType.Section, "Hardware section", "CPU, GPU, RAM, drives… as aligned rows of readings, sidebar style (per-core clocks and temperatures)."),
    ];

    public bool HasSection => Widget.Type == WidgetType.Section;

    public IReadOnlyList<Option<string>> SectionKinds { get; } = [.. Sections.Kinds.Select(k => new Option<string>(k.Title, k.Key))];

    partial void OnSectionKindChanged(Option<string> value)
    {
        var kind = Sections.Find(value?.Value) ?? Sections.Cpu;
        SectionOptions = SectionOptionChoice.For(kind, null);
        var hardware = kind == Sections.Cpu ? HardwareKind.Cpu : kind == Sections.Gpu ? HardwareKind.Gpu : (HardwareKind?)null;
        List<Option<string?>> options = [new("Main (automatic)", null)];
        if (hardware is { } wanted)
        {
            options.AddRange(_store.Sensors.Where(s => s.HardwareKind == wanted)
                .GroupBy(s => s.HardwareId)
                .Select(g => new Option<string?>(g.First().GroupName, g.Key)));
        }

        SectionHardwareOptions = options;
        SectionHardware = options[0];
        OnPropertyChanged(nameof(HasFanMode));
    }

    partial void OnSectionHardwareChanged(Option<string?>? value) => RefreshRowNames();

    partial void OnSectionOptionsChanged(ObservableCollection<SectionOptionChoice> value)
    {
        foreach (var option in value)
        {
            option.PropertyChanged += (_, _) => RefreshRowNames();
        }

        RefreshRowNames();
    }

    /// <summary>Lists the rows the section would show now, so each can be renamed.</summary>
    private void RefreshRowNames()
    {
        RememberRowEdits();

        if (!HasSection)
        {
            RowNames = [];
            return;
        }

        var preview = new TileSpec
        {
            Type = WidgetType.Section,
            Section = SectionKind?.Value,
            Hardware = SectionHardware?.Value,
            Rows = SectionOptions.Where(o => o.IsChecked).Select(o => o.Key).ToList(),
        };
        RowNames = [.. Sections.Plan(preview, _store.Sensors, includeHidden: true).Rows
            .Select(r => r.Key)
            .Distinct(StringComparer.Ordinal)
            .Select(key => new RowNameChoice(key, _labelEdits.GetValueOrDefault(key, ""), !_hiddenEdits.Contains(key)))];
    }

    private void RememberRowEdits()
    {
        foreach (var row in RowNames)
        {
            _labelEdits[row.Automatic] = row.Custom;
            if (row.IsShown)
            {
                _hiddenEdits.Remove(row.Automatic);
            }
            else
            {
                _hiddenEdits.Add(row.Automatic);
            }
        }
    }

    private List<string>? CollectHidden()
    {
        RememberRowEdits();
        return _hiddenEdits.Count > 0 ? _hiddenEdits.Order(StringComparer.Ordinal).ToList() : null;
    }

    private Dictionary<string, string>? CollectLabels()
    {
        RememberRowEdits();

        var labels = _labelEdits
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Value) && kv.Value.Trim() != kv.Key)
            .ToDictionary(kv => kv.Key, kv => kv.Value.Trim(), StringComparer.Ordinal);
        return labels.Count > 0 ? labels : null;
    }

    public IReadOnlyList<string> Categories { get; } = ["All", "Temperatures", "Voltages", "Fans", "Power", "Clocks", "Load", "Data"];

    public IReadOnlyList<Option<int>> Windows { get; } =
        [new("1 min", 60), new("5 min", 300), new("15 min", 900), new("1 hour", 3600)];

    public IReadOnlyList<Option<int?>> DecimalOptions { get; } =
        [new("Automatic", null), new("0", 0), new("1", 1), new("2", 2), new("3", 3)];

    public IReadOnlyList<Option<int?>> AccentOptions { get; } =
        [new("Ink (default)", null), new("Color 1", 0), new("Color 2", 1), new("Color 3", 2), new("Color 4", 3), new("Color 5", 4), new("Color 6", 5)];

    public ObservableCollection<SensorPickRow> Rows { get; }

    public ICollectionView View { get; }

    /// <summary>Chosen sensors, in the order they were picked.</summary>
    public ObservableCollection<SensorItem> Chosen { get; } = [];

    public bool IsMultiSensor => TileSpec.IsMultiSensor(Widget.Type);

    public bool NeedsSensors => !TileSpec.NeedsNoSensor(Widget.Type);

    public bool IsLabel => Widget.Type == WidgetType.Label;

    public bool HasTemplate => Widget.Type == WidgetType.Text;

    public bool HasMetric => Widget.Type == WidgetType.CoreMap;

    public IReadOnlyList<Option<string>> Metrics { get; } = [.. CoreMap.Metrics.Select(m => new Option<string>(m.Label, m.Key))];

    public bool HasTimeWindow => Widget.Type is WidgetType.Chart or WidgetType.Sparkline;

    public bool HasScale => Widget.Type is WidgetType.BigNumber or WidgetType.Gauge or WidgetType.Bar or WidgetType.Sparkline or WidgetType.List;

    public bool HasNumberSettings => Widget.Type is WidgetType.BigNumber or WidgetType.Gauge or WidgetType.Bar or WidgetType.Sparkline or WidgetType.List;

    public string SensorHint => IsMultiSensor ? "Click sensors to add or remove them." : "Click a sensor to choose it.";

    /// <summary>Thresholds and scale limits are typed in the unit shown (°F when the app shows °F).</summary>
    public string ThresholdUnit => Chosen.FirstOrDefault() is { } s ? UnitOf(s) : "";

    private bool ConvertsToFahrenheit =>
        Chosen.FirstOrDefault()?.Kind == SensorKind.Temperature && _store.Display.TemperatureUnit == TemperatureUnit.Fahrenheit;

    /// <summary>Set by the dialog when Save succeeds.</summary>
    public TileSpec? Result { get; private set; }

    public event Action? RequestClose;

    partial void OnSearchTextChanged(string value) => View.Refresh();

    partial void OnCategoryChanged(string value) => View.Refresh();

    partial void OnWidgetChanged(WidgetChoice value)
    {
        RefreshRowNames();

        // Single-sensor widgets keep only the first choice.
        while (!IsMultiSensor && Chosen.Count > 1)
        {
            Unchoose(Chosen[^1]);
        }
    }

    [RelayCommand]
    private void Pick(SensorPickRow? row)
    {
        if (row is null)
        {
            return;
        }

        if (row.IsChosen)
        {
            Unchoose(row.Sensor);
            return;
        }

        if (!IsMultiSensor)
        {
            foreach (var other in Chosen.ToList())
            {
                Unchoose(other);
            }
        }

        row.IsChosen = true;
        Chosen.Add(row.Sensor);
        AfterChosenChanged();
    }

    [RelayCommand]
    private void Remove(SensorItem? sensor)
    {
        if (sensor is not null)
        {
            Unchoose(sensor);
        }
    }

    // Sensors that exist in the saved tile but aren't present right now still count (they're kept on save).
    private bool CanSave() => !NeedsSensors || Chosen.Count > 0 || (_existing?.Sensors.Any(id => !_rows.ContainsKey(id)) ?? false);

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save()
    {
        if (!TryNumber(ScaleMinText, "Scale minimum", out var scaleMin)
            || !TryNumber(ScaleMaxText, "Scale maximum", out var scaleMax)
            || !TryNumber(WarningText, "Warning", out var warning)
            || !TryNumber(CriticalText, "Critical", out var critical))
        {
            return;
        }

        if (scaleMin is { } lo && scaleMax is { } hi && hi <= lo)
        {
            Error = "Scale maximum must be above the minimum.";
            return;
        }

        // Both are "at or above" limits; a critical below the warning would skip the warning color entirely.
        if (warning is { } warn && critical is { } crit && crit < warn)
        {
            Error = "Critical must be at or above the warning value.";
            return;
        }

        var spec = new TileSpec
        {
            Type = Widget.Type,
            Title = !ShowTitle ? "" : string.IsNullOrWhiteSpace(TitleText) ? null : TitleText.Trim(),
            Text = (IsLabel || HasTemplate) && !string.IsNullOrWhiteSpace(LabelText) ? LabelText.Trim() : null,
            Metric = HasMetric ? Metric.Value : HasSection ? FanMode.Value : null,
            Section = HasSection ? SectionKind.Value : null,
            Hardware = HasSection ? SectionHardware?.Value : null,
            Rows = HasSection ? SectionOptions.Where(o => o.IsChecked).Select(o => o.Key).ToList() : null,
            Labels = HasSection ? CollectLabels() : null,
            HiddenRows = HasSection ? CollectHidden() : null,
            WindowSeconds = Window.Value,
            ScaleMin = ToCelsius(scaleMin),
            ScaleMax = ToCelsius(scaleMax),
            Decimals = Decimals.Value,
            Warning = ToCelsius(warning),
            Critical = ToCelsius(critical),
            Accent = Accent.Value,
            ShowMinMax = ShowMinMax,
        };

        if (NeedsSensors)
        {
            // Keep ids of sensors that aren't present right now (e.g. editing while a device is unplugged).
            var missing = _existing?.Sensors.Where(id => !_rows.ContainsKey(id)) ?? [];
            spec.Sensors = [.. Chosen.Select(s => s.Id).Concat(missing)];
            if (!IsMultiSensor && spec.Sensors.Count > 1)
            {
                spec.Sensors = [spec.Sensors[0]];
            }
        }

        if (spec.Type == WidgetType.Label && spec.Title is null && spec.Text is null)
        {
            spec.Title = "Section";
        }

        Result = spec;
        RequestClose?.Invoke();
    }

    private void Load(TileSpec spec)
    {
        Widget = Widgets.FirstOrDefault(w => w.Type == spec.Type) ?? Widgets[0];
        foreach (var id in spec.Sensors)
        {
            if (_rows.TryGetValue(id, out var row) && !row.IsChosen)
            {
                row.IsChosen = true;
                Chosen.Add(row.Sensor);
            }
        }

        ShowTitle = spec.Title != "";
        TitleText = spec.Title ?? "";
        LabelText = spec.Text ?? "";
        Window = Windows.FirstOrDefault(w => w.Value == spec.WindowSeconds) ?? Windows[1];
        ScaleMinText = Format(FromCelsius(spec.ScaleMin));
        ScaleMaxText = Format(FromCelsius(spec.ScaleMax));
        Decimals = DecimalOptions.FirstOrDefault(d => d.Value == spec.Decimals) ?? DecimalOptions[0];
        WarningText = Format(FromCelsius(spec.Warning));
        CriticalText = Format(FromCelsius(spec.Critical));
        Accent = AccentOptions.FirstOrDefault(a => a.Value == spec.Accent) ?? AccentOptions[0];
        ShowMinMax = spec.ShowMinMax;
        Metric = Metrics.FirstOrDefault(m => m.Value == spec.Metric) ?? Metrics[0];
        if (spec.Type == WidgetType.Section)
        {
            foreach (var key in spec.HiddenRows ?? Enumerable.Empty<string>())
            {
                _hiddenEdits.Add(key);
            }

            if (spec.Labels is { } savedLabels)
            {
                foreach (var (automatic, custom) in savedLabels)
                {
                    _labelEdits[automatic] = custom;
                }
            }

            SectionKind = SectionKinds.FirstOrDefault(k => k.Value == spec.Section) ?? SectionKinds[0];
            FanMode = FanDisplay.Choices.FirstOrDefault(c => c.Value == spec.Metric) ?? FanDisplay.Choices[0];
            SectionOptions = SectionOptionChoice.For(Sections.Find(spec.Section) ?? Sections.Cpu, spec.Rows);
            if (spec.Hardware is { } saved && SectionHardwareOptions.All(h => h.Value != saved))
            {
                // Not connected right now: keep it rather than silently switching to the main one.
                SectionHardwareOptions = [.. SectionHardwareOptions, new Option<string?>("Saved hardware (not found right now)", saved)];
            }

            SectionHardware = SectionHardwareOptions.FirstOrDefault(h => h.Value == spec.Hardware) ?? SectionHardwareOptions[0];
        }

        OnPropertyChanged(nameof(ThresholdUnit));
    }

    private void Unchoose(SensorItem sensor)
    {
        if (_rows.TryGetValue(sensor.Id, out var row))
        {
            row.IsChosen = false;
        }

        Chosen.Remove(sensor);
        AfterChosenChanged();
    }

    private void AfterChosenChanged()
    {
        Error = null;
        OnPropertyChanged(nameof(ThresholdUnit));
        SaveCommand.NotifyCanExecuteChanged();
    }

    private bool Matches(object obj)
    {
        if (obj is not SensorPickRow row)
        {
            return false;
        }

        var item = row.Sensor;
        if (row.IsChosen)
        {
            return true; // chosen sensors stay visible so they can be removed
        }

        if (item.IsHidden)
        {
            return false;
        }

        if (!SensorsViewModel.InCategory(item.Kind, Category))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(SearchText))
        {
            return !item.IsAdvanced;
        }

        return SensorsViewModel.MatchesSearch(item, SearchText);
    }

    private bool TryNumber(string text, string what, out double? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        if (double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out var parsed) && double.IsFinite(parsed))
        {
            value = parsed;
            return true;
        }

        Error = $"{what} isn't a number.";
        return false;
    }

    private double? ToCelsius(double? shown) => shown is { } v && ConvertsToFahrenheit ? (v - 32) * 5 / 9 : shown;

    private double? FromCelsius(double? celsius) => celsius is { } v && ConvertsToFahrenheit ? (v * 9 / 5) + 32 : celsius;

    private static string Format(double? value) => value?.ToString("0.###", CultureInfo.CurrentCulture) ?? "";

    private string UnitOf(SensorItem sensor)
    {
        var text = UnitFormatter.Format(sensor.Kind, 0f, _store.Display.TemperatureUnit);
        var space = text.IndexOf(' ', StringComparison.Ordinal);
        return space < 0 ? "" : text[(space + 1)..];
    }
}
