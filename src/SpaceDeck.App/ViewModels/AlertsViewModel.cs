using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SpaceDeck.App.Services;
using SpaceDeck.Core.Alerts;
using SpaceDeck.Core.Formatting;
using SpaceDeck.Core.Model;

namespace SpaceDeck.App.ViewModels;

/// <summary>
/// One sensor's limits on the Alerts page. Limits are shown and typed in the unit the app displays (°F when chosen)
/// and saved in the sensor's base unit. Editing a default rule turns it into the user's own.
/// </summary>
public sealed partial class AlertRuleRow : ObservableObject
{
    private readonly AlertsViewModel _owner;
    private readonly bool _fahrenheit;
    private bool _loading;

    [ObservableProperty]
    private bool _enabled;

    [ObservableProperty]
    private string _warningAbove = "";

    [ObservableProperty]
    private string _criticalAbove = "";

    [ObservableProperty]
    private string _warningBelow = "";

    [ObservableProperty]
    private string _criticalBelow = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Source), nameof(CanReset))]
    private bool _isCustom;

    public AlertRuleRow(AlertsViewModel owner, SensorItem sensor, AlertRule rule, bool isCustom, bool hasDefault, bool fahrenheit)
    {
        _owner = owner;
        Sensor = sensor;
        IsCustom = isCustom;
        HasDefault = hasDefault;
        _fahrenheit = fahrenheit && sensor.Kind == SensorKind.Temperature;
        _loading = true;
        Enabled = rule.Enabled;
        WarningAbove = Show(rule.WarningAbove);
        CriticalAbove = Show(rule.CriticalAbove);
        WarningBelow = Show(rule.WarningBelow);
        CriticalBelow = Show(rule.CriticalBelow);
        _loading = false;
    }

    public SensorItem Sensor { get; }

    public bool HasDefault { get; }

    /// <summary>"Default" (built in), "Changed" (built-in default edited) or "Custom" (added by the user).</summary>
    public string Source => !IsCustom ? "Default" : HasDefault ? "Changed" : "Custom";

    /// <summary>Reset puts the default back; for a sensor without a default it removes the rule.</summary>
    public string ResetText => HasDefault ? "Reset" : "Remove";

    public bool CanReset => IsCustom;

    public string Unit
    {
        get
        {
            var text = UnitFormatter.Format(Sensor.Kind, 0f, _fahrenheit ? TemperatureUnit.Fahrenheit : TemperatureUnit.Celsius);
            var space = text.IndexOf(' ', StringComparison.Ordinal);
            return space < 0 ? "" : text[(space + 1)..];
        }
    }

    partial void OnEnabledChanged(bool value) => Commit();

    partial void OnWarningAboveChanged(string value) => Commit();

    partial void OnCriticalAboveChanged(string value) => Commit();

    partial void OnWarningBelowChanged(string value) => Commit();

    partial void OnCriticalBelowChanged(string value) => Commit();

    private void Commit()
    {
        if (_loading)
        {
            return;
        }

        if (!TryParse(WarningAbove, out var wa) || !TryParse(CriticalAbove, out var ca)
            || !TryParse(WarningBelow, out var wb) || !TryParse(CriticalBelow, out var cb))
        {
            _owner.SetError(this, $"{Sensor.Name}: limits must be numbers (or blank for none). Not saved.");
            return;
        }

        // A rule that is off never fires, so it may keep inverted limits (e.g. saved by an older version): the user
        // must always be able to switch an alert off.
        if (Enabled
            && ((wa is { } warnAbove && ca is { } critAbove && critAbove < warnAbove)
                || (wb is { } warnBelow && cb is { } critBelow && critBelow > warnBelow)))
        {
            _owner.SetError(this, $"{Sensor.Name}: the critical limit must be past the warning one (higher for ≥, lower for ≤). Not saved.");
            return;
        }

        _owner.SetError(this, null);
        _owner.Save(new AlertRule
        {
            SensorId = Sensor.Id,
            Enabled = Enabled,
            WarningAbove = wa,
            CriticalAbove = ca,
            WarningBelow = wb,
            CriticalBelow = cb,
        });
        IsCustom = true;
    }

    [RelayCommand]
    private void Reset() => _owner.Reset(this);

    private string Show(double? value)
    {
        if (value is not { } v)
        {
            return "";
        }

        var shown = _fahrenheit ? (v * 9 / 5) + 32 : v;
        return shown.ToString("0.###", CultureInfo.CurrentCulture);
    }

    private bool TryParse(string text, out double? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        if (!double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out var parsed) || !double.IsFinite(parsed))
        {
            return false;
        }

        value = _fahrenheit ? (parsed - 32) * 5 / 9 : parsed;
        return true;
    }
}

/// <summary>The Alerts page: notification settings, what's wrong right now, per-sensor limits and the history.</summary>
public sealed partial class AlertsViewModel : ObservableObject
{
    private readonly AlertService _alerts;
    private readonly SensorStore _store;
    private readonly AppSettings _settings;
    private bool _saving;
    private readonly Dictionary<AlertRuleRow, string> _errors = new();

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    private string _searchText = "";

    public AlertsViewModel(AlertService alerts, SensorStore store, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(alerts);
        ArgumentNullException.ThrowIfNull(store);
        _alerts = alerts;
        _store = store;
        _settings = settings;
        store.CatalogApplied += RebuildRows;
        store.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SensorStore.TemperatureUnit))
            {
                RebuildRows();
            }
        };
        alerts.RulesChanged += () =>
        {
            if (!_saving)
            {
                RebuildRows();
            }
        };
        RebuildRows();
    }

    public AlertService Alerts => _alerts;

    public ObservableCollection<AlertRuleRow> Rows { get; } = [];

    /// <summary>Sensors matching the search that don't have a rule yet.</summary>
    public ObservableCollection<SensorItem> Candidates { get; } = [];

    public bool NotificationsEnabled
    {
        get => _settings.AlertsEnabled;
        set
        {
            _settings.AlertsEnabled = value;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    public bool NotifyWarnings
    {
        get => _settings.AlertNotifyWarnings;
        set
        {
            _settings.AlertNotifyWarnings = value;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    public bool NotifyCleared
    {
        get => _settings.AlertNotifyCleared;
        set
        {
            _settings.AlertNotifyCleared = value;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    public bool QuietHoursEnabled
    {
        get => _settings.QuietHoursEnabled;
        set
        {
            _settings.QuietHoursEnabled = value;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    public string QuietFrom
    {
        get => _settings.QuietFrom;
        set => SetQuietTime(value, v => _settings.QuietFrom = v);
    }

    public string QuietTo
    {
        get => _settings.QuietTo;
        set => SetQuietTime(value, v => _settings.QuietTo = v);
    }

    public bool QuietAllowCritical
    {
        get => _settings.QuietAllowCritical;
        set
        {
            _settings.QuietAllowCritical = value;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    /// <summary>Saves "HH:mm" times; anything else shows an error and isn't saved.</summary>
    private void SetQuietTime(string value, Action<string> apply, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (SpaceDeck.Core.Automation.TimeWindow.TryParse(value, out var time))
        {
            apply(time.ToString("HH:mm", CultureInfo.InvariantCulture));
            _settings.Save();
            QuietError = null;
        }
        else
        {
            QuietError = "Use 24-hour times like 22:30.";
        }

        OnPropertyChanged(name);
    }

    [ObservableProperty]
    private string? _quietError;

    public string LimitsHint => _store.TemperatureUnit == TemperatureUnit.Fahrenheit
        ? "Limits are in the sensor's unit (temperatures in °F). Blank = no limit."
        : "Limits are in the sensor's unit. Blank = no limit.";

    /// <summary>Tracks errors per row, so fixing one row doesn't hide another row's problem.</summary>
    internal void SetError(AlertRuleRow row, string? message)
    {
        if (message is null)
        {
            _errors.Remove(row);
        }
        else
        {
            _errors[row] = message;
        }

        Error = _errors.Count == 0 ? null : string.Join(Environment.NewLine, _errors.Values);
    }

    internal void Save(AlertRule rule)
    {
        // The row already shows what was typed; don't rebuild the list under the user's cursor.
        _saving = true;
        try
        {
            _alerts.SetCustomRule(rule);
        }
        finally
        {
            _saving = false;
        }
    }

    internal void Reset(AlertRuleRow row)
    {
        _alerts.ResetRule(row.Sensor.Id);
        RebuildRows();
    }

    partial void OnSearchTextChanged(string value) => RefreshCandidates();

    [RelayCommand]
    private void Add(SensorItem? sensor)
    {
        if (sensor is null)
        {
            return;
        }

        _alerts.SetCustomRule(new AlertRule { SensorId = sensor.Id });
        SearchText = "";
    }

    [RelayCommand]
    private void SendTest() => _alerts.SendTestNotification();

    [RelayCommand]
    private void ClearLog() => _alerts.ClearLog();

    [RelayCommand]
    private void OpenLogFile()
    {
        var path = AlertService.LogFilePath;
        try
        {
            if (File.Exists(path))
            {
                // Full path: a bare "explorer.exe" is looked up through the search path (current directory included).
                var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
                Process.Start(new ProcessStartInfo(explorer, $"/select,\"{path}\"") { UseShellExecute = true })?.Dispose();
            }
            else
            {
                Directory.CreateDirectory(AppSettings.Directory);
                Process.Start(new ProcessStartInfo(AppSettings.Directory) { UseShellExecute = true })?.Dispose();
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            // Nothing useful to do; the history is also shown on the page.
        }
    }

    private void RebuildRows()
    {
        _errors.Clear();
        Error = null;
        Rows.Clear();
        var fahrenheit = _store.TemperatureUnit == TemperatureUnit.Fahrenheit;
        foreach (var sensor in _store.Sensors)
        {
            var custom = _alerts.CustomRuleFor(sensor.Id);
            var fallback = AlertService.DefaultRuleFor(sensor);
            if ((custom ?? fallback) is { } rule)
            {
                Rows.Add(new AlertRuleRow(this, sensor, rule, custom is not null, fallback is not null, fahrenheit));
            }
        }

        OnPropertyChanged(nameof(LimitsHint));
        RefreshCandidates();
    }

    private void RefreshCandidates()
    {
        Candidates.Clear();
        if (string.IsNullOrWhiteSpace(SearchText))
        {
            return;
        }

        var taken = Rows.Select(r => r.Sensor.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var sensor in _store.Sensors
                     .Where(s => !taken.Contains(s.Id))
                     .Where(s => !s.IsHidden)
                     .Where(s => SensorsViewModel.MatchesSearch(s, SearchText))
                     .Take(12))
        {
            Candidates.Add(sensor);
        }
    }
}
