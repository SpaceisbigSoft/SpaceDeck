using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SpaceDeck.App.Services;
using SpaceDeck.Core.Automation;
using SpaceDeck.Core.Formatting;
using SpaceDeck.Core.Model;

namespace SpaceDeck.App.ViewModels;

public sealed record TriggerChoice(TriggerKind Kind, string Label);

/// <summary>Add or edit one automation rule.</summary>
public sealed partial class RuleEditorViewModel : ObservableObject
{
    private readonly SensorStore _store;
    private readonly AutomationRule _rule;

    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private TriggerChoice _selectedTrigger = null!;

    [ObservableProperty]
    private string _processText = "";

    [ObservableProperty]
    private string? _selectedRunningApp;

    [ObservableProperty]
    private SensorItem? _selectedSensor;

    [ObservableProperty]
    private string _thresholdText = "";

    [ObservableProperty]
    private string _forSecondsText = "10";

    [ObservableProperty]
    private string _fromText = "22:00";

    [ObservableProperty]
    private string _toText = "07:00";

    [ObservableProperty]
    private string? _selectedProfile;

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    private bool _record;

    public RuleEditorViewModel(SensorStore store, IEnumerable<string> profiles, IReadOnlyList<string> runningApps, AutomationRule? existing)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _rule = existing?.Clone() ?? new AutomationRule();
        IsNew = existing is null;

        foreach (var p in profiles)
        {
            Profiles.Add(p);
        }

        if (!string.IsNullOrEmpty(_rule.Profile) && !Profiles.Contains(_rule.Profile, StringComparer.OrdinalIgnoreCase))
        {
            Profiles.Add(_rule.Profile); // FanControl no longer lists it: keep it selectable
        }

        RunningApps = runningApps;
        Sensors = [.. store.Sensors.Where(s => !s.IsHidden || s.Id == _rule.SensorId)
            .OrderBy(s => s.GroupName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)];

        Name = _rule.Name;
        Record = _rule.Record;
        // An unknown trigger (settings from a newer version, or hand-edited) opens as the first choice instead of throwing.
        SelectedTrigger = Triggers.FirstOrDefault(t => t.Kind == _rule.Trigger) ?? Triggers[0];
        ProcessText = string.Join(", ", _rule.Processes);
        SelectedSensor = _rule.SensorId is { } id && store.SensorsById.TryGetValue(id, out var sensor) ? sensor : null;
        ThresholdText = IsNew ? "" : ToShown(_rule.Threshold).ToString("0.##", CultureInfo.CurrentCulture);
        ForSecondsText = _rule.ForSeconds.ToString(CultureInfo.CurrentCulture);
        FromText = _rule.From ?? "22:00";
        ToText = _rule.To ?? "07:00";
        SelectedProfile = Profiles.FirstOrDefault(p => p.Equals(_rule.Profile, StringComparison.OrdinalIgnoreCase)) ?? Profiles.FirstOrDefault();
    }

    public bool IsNew { get; }

    public string Title => IsNew ? "New automation rule" : "Edit automation rule";

    public IReadOnlyList<TriggerChoice> Triggers { get; } =
    [
        new(TriggerKind.Process, "A program is running"),
        new(TriggerKind.SensorAbove, "A sensor is at or above a value"),
        new(TriggerKind.SensorBelow, "A sensor is at or below a value"),
        new(TriggerKind.TimeOfDay, "Time of day"),
    ];

    public ObservableCollection<string> Profiles { get; } = [];

    public IReadOnlyList<string> RunningApps { get; }

    public IReadOnlyList<SensorItem> Sensors { get; }

    public bool IsProcess => SelectedTrigger.Kind == TriggerKind.Process;

    public bool IsSensor => SelectedTrigger.Kind is TriggerKind.SensorAbove or TriggerKind.SensorBelow;

    public bool IsTime => SelectedTrigger.Kind == TriggerKind.TimeOfDay;

    /// <summary>"°C", "RPM"… for the threshold box (throughput is entered in MB/s; the display scales automatically).</summary>
    public string UnitText => SelectedSensor is null ? ""
        : SelectedSensor.Kind == SensorKind.Temperature ? (_store.TemperatureUnit == TemperatureUnit.Fahrenheit ? "°F" : "°C")
        : SelectedSensor.Kind == SensorKind.Throughput ? "MB/s"
        : SelectedSensor.DisplayUnit;

    public string CurrentValueText => SelectedSensor is null ? "" : $"Now: {SelectedSensor.DisplayValue}";

    public AutomationRule? Result { get; private set; }

    public event Action? RequestClose;

    partial void OnSelectedTriggerChanged(TriggerChoice value)
    {
        OnPropertyChanged(nameof(IsProcess));
        OnPropertyChanged(nameof(IsSensor));
        OnPropertyChanged(nameof(IsTime));
    }

    partial void OnSelectedSensorChanged(SensorItem? oldValue, SensorItem? newValue)
    {
        // A different kind of value (temperature / MB/s / other): the old number no longer means the same thing.
        if (oldValue is not null && newValue is not null && Factor(oldValue) != Factor(newValue))
        {
            ThresholdText = "";
        }

        OnPropertyChanged(nameof(UnitText));
        OnPropertyChanged(nameof(CurrentValueText));
    }

    /// <summary>Picking a running program adds it to the list.</summary>
    partial void OnSelectedRunningAppChanged(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        var names = ProcessNames.Split(ProcessText);
        if (!names.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            names.Add(value);
            ProcessText = string.Join(", ", names);
        }
    }

    [RelayCommand]
    private void Save()
    {
        var rule = _rule.Clone();
        rule.Name = Name.Trim();
        rule.Trigger = SelectedTrigger.Kind;
        rule.Profile = SelectedProfile ?? "";
        rule.Record = Record;
        if (string.IsNullOrWhiteSpace(rule.Profile))
        {
            Error = "Choose the FanControl config to switch to.";
            return;
        }

        switch (rule.Trigger)
        {
            case TriggerKind.Process:
                rule.Processes = ProcessNames.Split(ProcessText);
                if (rule.Processes.Count == 0)
                {
                    Error = "Enter at least one program name (e.g. eldenring.exe), or pick one that's running.";
                    return;
                }

                break;

            case TriggerKind.TimeOfDay:
                if (!TimeWindow.TryParse(FromText, out var from) || !TimeWindow.TryParse(ToText, out var to))
                {
                    Error = "Use 24-hour times like 22:30.";
                    return;
                }

                if (from == to)
                {
                    Error = "Start and end are the same time.";
                    return;
                }

                rule.From = from.ToString("HH:mm", CultureInfo.InvariantCulture);
                rule.To = to.ToString("HH:mm", CultureInfo.InvariantCulture);
                break;

            default:
                if (SelectedSensor is null)
                {
                    Error = "Choose a sensor.";
                    return;
                }

                if (!double.TryParse(ThresholdText.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out var shown) || !double.IsFinite(shown))
                {
                    Error = "Enter the value as a number.";
                    return;
                }

                if (!int.TryParse(ForSecondsText.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var seconds) || seconds < 0 || seconds > 3600)
                {
                    Error = "Seconds must be between 0 and 3600.";
                    return;
                }

                rule.SensorId = SelectedSensor.Id;
                rule.Threshold = ToBase(shown);
                rule.ForSeconds = seconds;
                break;
        }

        Result = rule;
        RequestClose?.Invoke();
    }

    private bool IsFahrenheit => (SelectedSensor?.Kind ?? KindOfRule()) == SensorKind.Temperature && _store.TemperatureUnit == TemperatureUnit.Fahrenheit;

    private SensorKind? KindOfRule() =>
        _rule.SensorId is { } id && _store.SensorsById.TryGetValue(id, out var s) ? s.Kind : null;

    private const double BytesPerMegabyte = 1024 * 1024;

    private bool IsThroughput => (SelectedSensor?.Kind ?? KindOfRule()) == SensorKind.Throughput;

    private static string Factor(SensorItem sensor) =>
        sensor.Kind == SensorKind.Throughput ? "MB/s" : sensor.Kind == SensorKind.Temperature ? "temp" : "1";

    private double ToShown(double value) =>
        IsFahrenheit ? UnitFormatter.CelsiusToFahrenheit((float)value)
        : IsThroughput ? value / BytesPerMegabyte
        : value;

    private double ToBase(double shown) =>
        IsFahrenheit ? (shown - 32) * 5 / 9
        : IsThroughput ? shown * BytesPerMegabyte
        : shown;
}
