using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SpaceDeck.App.Services;
using SpaceDeck.Core.Automation;
using SpaceDeck.Core.Formatting;

namespace SpaceDeck.App.ViewModels;

/// <summary>One rule in the Automation list.</summary>
public sealed partial class AutomationRuleRow : ObservableObject
{
    private readonly Action _save;

    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    private string _summary = "";

    public AutomationRuleRow(AutomationRule rule, SensorStore store, Action save)
    {
        Rule = rule;
        _save = save;
        Refresh(store);
    }

    public AutomationRule Rule { get; }

    public string Title => AutomationEngine.DisplayName(Rule);

    public string Profile => Rule.Profile;

    public bool Enabled
    {
        get => Rule.Enabled;
        set
        {
            if (Rule.Enabled != value)
            {
                Rule.Enabled = value;
                OnPropertyChanged();
                _save();
            }
        }
    }

    public void Refresh(SensorStore store)
    {
        Summary = Describe(Rule, store) + (Rule.Record ? " · records a session" : "");
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Profile));
    }

    /// <summary>"When eldenring is running", "When CPU Package ≥ 80.0 °C for 10 s", "Every day 22:00–07:00".</summary>
    public static string Describe(AutomationRule rule, SensorStore store)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(store);
        switch (rule.Trigger)
        {
            case TriggerKind.Process:
                return rule.Processes.Count == 0 ? "No programs chosen" : $"While {string.Join(" or ", rule.Processes)} is running";
            case TriggerKind.TimeOfDay:
                return $"Every day {rule.From}–{rule.To}";
            default:
                var sign = rule.Trigger == TriggerKind.SensorAbove ? "≥" : "≤";
                if (rule.SensorId is null || !store.SensorsById.TryGetValue(rule.SensorId, out var sensor))
                {
                    return $"When a missing sensor {sign} {rule.Threshold.ToString("0.##", CultureInfo.CurrentCulture)}";
                }

                var threshold = UnitFormatter.Format(sensor.Kind, (float)rule.Threshold, store.TemperatureUnit);
                return $"When {sensor.Name} {sign} {threshold} for {rule.ForSeconds} s";
        }
    }
}

/// <summary>The Automation page: automatic fan profiles, alert actions, and the current state.</summary>
public sealed partial class AutomationViewModel : ObservableObject
{
    public const string KeepPrevious = "Keep the previous profile";
    public const string DontSwitch = "Don't switch";
    public const string SameAsWarning = "Same as warning";

    private readonly AutomationService _automation;
    private readonly FanProfileService _fans;
    private readonly SensorStore _store;
    private readonly DialogService _dialogs;
    private bool _loading;

    [ObservableProperty]
    private string? _defaultChoice;

    [ObservableProperty]
    private string? _warningChoice;

    [ObservableProperty]
    private string? _criticalChoice;

    [ObservableProperty]
    private string? _message;

    public AutomationViewModel(AutomationService automation, FanProfileService fans, SensorStore store, DialogService dialogs)
    {
        ArgumentNullException.ThrowIfNull(automation);
        ArgumentNullException.ThrowIfNull(store);
        _automation = automation;
        _fans = fans;
        _store = store;
        _dialogs = dialogs;
        automation.Evaluated += UpdateActive;
        store.CatalogApplied += RefreshSummaries;
        store.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SensorStore.TemperatureUnit))
            {
                RefreshSummaries();
            }
        };
        LoadProfiles();
        RebuildRows();
    }

    public AutomationService Automation => _automation;

    private AutomationSettings Settings => _automation.Settings;

    public ObservableCollection<AutomationRuleRow> Rules { get; } = [];

    public bool HasNoRules => Rules.Count == 0;

    /// <summary>"Keep the previous profile" + FanControl configs.</summary>
    public ObservableCollection<string> DefaultChoices { get; } = [];

    /// <summary>"Don't switch" + FanControl configs.</summary>
    public ObservableCollection<string> AlertChoices { get; } = [];

    /// <summary>"Same as warning" + FanControl configs (a critical alert falls back to the warning profile).</summary>
    public ObservableCollection<string> CriticalChoices { get; } = [];

    public bool Enabled
    {
        get => Settings.Enabled;
        set => Set(Settings.Enabled, value, v => Settings.Enabled = v);
    }

    public int CooldownSeconds
    {
        get => Settings.CooldownSeconds;
        set => Set(Settings.CooldownSeconds, Math.Clamp(value, 0, 3600), v => Settings.CooldownSeconds = v);
    }

    public int HoldMinutes
    {
        get => Settings.HoldMinutes;
        set => Set(Settings.HoldMinutes, Math.Clamp(value, 0, 24 * 60), v => Settings.HoldMinutes = v);
    }

    public bool WarningSound
    {
        get => Settings.Alerts.WarningSound;
        set => Set(Settings.Alerts.WarningSound, value, v => Settings.Alerts.WarningSound = v);
    }

    public bool CriticalSound
    {
        get => Settings.Alerts.CriticalSound;
        set => Set(Settings.Alerts.CriticalSound, value, v => Settings.Alerts.CriticalSound = v);
    }

    public bool RevertOnClear
    {
        get => Settings.Alerts.RevertOnClear;
        set => Set(Settings.Alerts.RevertOnClear, value, v => Settings.Alerts.RevertOnClear = v);
    }

    public bool FanControlMissing => _fans.Install is null;

    /// <summary>Re-reads FanControl's configs (called each time the Automation page is shown).</summary>
    public void LoadProfiles()
    {
        _loading = true;
        try
        {
            _fans.Refresh();
            var names = _fans.Configs.Select(c => c.Name).ToList();
            Fill(DefaultChoices, KeepPrevious, names, Settings.DefaultProfile);
            Fill(AlertChoices, DontSwitch, names, Settings.Alerts.WarningProfile);
            Fill(CriticalChoices, SameAsWarning, names, Settings.Alerts.CriticalProfile);
            DefaultChoice = WarningChoice = CriticalChoice = null; // so the combos re-select after the lists were refilled
            DefaultChoice = Pick(DefaultChoices, Settings.DefaultProfile);
            WarningChoice = Pick(AlertChoices, Settings.Alerts.WarningProfile);
            CriticalChoice = Pick(CriticalChoices, Settings.Alerts.CriticalProfile);
            OnPropertyChanged(nameof(FanControlMissing));
        }
        finally
        {
            _loading = false;
        }
    }

    partial void OnDefaultChoiceChanged(string? value) => SaveChoice(value, KeepPrevious, v => Settings.DefaultProfile = v);

    partial void OnWarningChoiceChanged(string? value) => SaveChoice(value, DontSwitch, v => Settings.Alerts.WarningProfile = v);

    partial void OnCriticalChoiceChanged(string? value) => SaveChoice(value, SameAsWarning, v => Settings.Alerts.CriticalProfile = v);

    /// <summary>The list's own entry for a saved name (any case), or the "none" entry.</summary>
    private static string Pick(ObservableCollection<string> list, string? saved) =>
        (saved is null ? null : list.Skip(1).FirstOrDefault(n => n.Equals(saved, StringComparison.OrdinalIgnoreCase))) ?? list[0];

    [RelayCommand]
    private void Resume() => _automation.Resume();

    [RelayCommand]
    private void OpenLog()
    {
        if (!System.IO.File.Exists(AutomationService.LogFilePath))
        {
            Message = "Nothing logged yet.";
            return;
        }

        Message = CollectorServiceStatus.OpenUrl(AutomationService.LogFilePath) ? null : "Couldn't open the log.";
    }

    [RelayCommand]
    private void AddRule()
    {
        if (_dialogs.EditAutomationRule(null) is { } rule)
        {
            Settings.Rules.Add(rule);
            Save();
            RebuildRows();
        }
    }

    [RelayCommand]
    private void EditRule(AutomationRuleRow? row)
    {
        if (row is null || _dialogs.EditAutomationRule(row.Rule) is not { } edited)
        {
            return;
        }

        var index = Settings.Rules.IndexOf(row.Rule);
        if (index >= 0)
        {
            Settings.Rules[index] = edited;
            Save();
            RebuildRows();
        }
    }

    [RelayCommand]
    private void DeleteRule(AutomationRuleRow? row)
    {
        if (row is null || !_dialogs.Confirm($"Delete the rule \"{row.Title}\"?"))
        {
            return;
        }

        Settings.Rules.Remove(row.Rule);
        Save();
        RebuildRows();
    }

    [RelayCommand]
    private void MoveUp(AutomationRuleRow? row) => Move(row, -1);

    [RelayCommand]
    private void MoveDown(AutomationRuleRow? row) => Move(row, +1);

    private void Move(AutomationRuleRow? row, int delta)
    {
        if (row is null)
        {
            return;
        }

        var index = Settings.Rules.IndexOf(row.Rule);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= Settings.Rules.Count)
        {
            return;
        }

        (Settings.Rules[index], Settings.Rules[target]) = (Settings.Rules[target], Settings.Rules[index]);
        Rules.Move(index, target);
        Save();
    }

    private void RebuildRows()
    {
        Rules.Clear();
        foreach (var rule in Settings.Rules)
        {
            Rules.Add(new AutomationRuleRow(rule, _store, Save));
        }

        OnPropertyChanged(nameof(HasNoRules));
        UpdateActive();
    }

    private void RefreshSummaries()
    {
        foreach (var row in Rules)
        {
            row.Refresh(_store);
        }
    }

    private void UpdateActive()
    {
        var active = _automation.ActiveRules;
        foreach (var row in Rules)
        {
            row.IsActive = active.Contains(row.Rule.Id);
        }
    }

    private void Save() => _automation.SaveSettings();

    private void Set<T>(T current, T value, Action<T> apply, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(current, value))
        {
            return;
        }

        apply(value);
        OnPropertyChanged(name);
        Save();
    }

    private void SaveChoice(string? value, string none, Action<string?> apply)
    {
        if (_loading || value is null)
        {
            return;
        }

        apply(value == none ? null : value);
        Save();
    }

    /// <summary>The "none" entry, the configs, and any saved choice FanControl no longer lists (so it isn't lost).</summary>
    private static void Fill(ObservableCollection<string> target, string none, List<string> names, params string?[] keep)
    {
        target.Clear();
        target.Add(none);
        foreach (var name in names.Concat(keep.OfType<string>()).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            target.Add(name);
        }
    }
}
