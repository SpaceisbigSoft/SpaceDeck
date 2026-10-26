using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using SensorDeck.Core.Alerts;
using SensorDeck.Core.Formatting;
using SensorDeck.Core.Model;

namespace SensorDeck.App.Services;

/// <summary>One line in the alert history.</summary>
public sealed record AlertEvent(DateTime Time, string SensorName, string GroupName, AlertLevel From, AlertLevel To, string Message)
{
    public bool IsRaise => To > From;

    /// <summary>Color to show: the new level for a raise, "ok" for back to normal.</summary>
    public AlertLevel ShownLevel => To;

    public string LevelText => To == AlertLevel.Normal ? "Back to normal" : IsRaise ? To.ToString() : $"Down to {To}";
}

/// <summary>A sensor that is in warning or critical right now.</summary>
public sealed record ActiveAlert(SensorItem Sensor, AlertLevel Level, DateTime Since);

/// <summary>
/// Watches every sensor that has an alert rule (built-in default or the user's), confirms level changes with
/// debounce/hysteresis (<see cref="AlertTracker"/>), keeps an active list and a history, and raises Windows
/// notifications through the tray icon. Runs while the app is open (including minimized).
/// </summary>
public sealed class AlertService
{
    private const int MaxLogEntries = 500;

    private readonly SensorStore _store;
    private readonly AppSettings _settings;
    private readonly TrayService _tray;
    private readonly NtfyService _ntfy;
    private readonly Dictionary<string, AlertTracker> _trackers = new(StringComparer.Ordinal);
    private readonly List<(SensorItem Sensor, AlertRule Rule, AlertTracker Tracker)> _watched = [];
    private readonly List<(string Title, string Text, bool Critical)> _pending = [];

    public AlertService(SensorStore store, AppSettings settings, TrayService tray, SensorLinker linker, NtfyService ntfy)
    {
        _ntfy = ntfy;
        ArgumentNullException.ThrowIfNull(linker);
        linker.Relinked += (_, _) =>
        {
            Rebuild();
            RulesChanged?.Invoke();
        };
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _settings = settings;
        _tray = tray;
        store.CatalogApplied += Rebuild;
        store.ValuesApplied += Evaluate;

        // Level colors come from the skin through a converter; replacing the items makes the lists re-render.
        Themes.SkinManager.SkinChanged += () =>
        {
            for (var i = 0; i < Active.Count; i++)
            {
                Active[i] = Active[i] with { };
            }

            for (var i = 0; i < Log.Count; i++)
            {
                Log[i] = Log[i] with { };
            }
        };
    }

    /// <summary>Newest first.</summary>
    public ObservableCollection<AlertEvent> Log { get; } = [];

    public ObservableCollection<ActiveAlert> Active { get; } = [];

    /// <summary>Raised after rules change (so the Alerts page can refresh).</summary>
    public event Action? RulesChanged;

    /// <summary>Raised when a sensor goes up to warning or critical (for alert sounds).</summary>
    public event Action<AlertLevel>? AlertRaised;

    /// <summary>The worst level any sensor is at right now.</summary>
    public AlertLevel HighestLevel => Active.Count == 0 ? AlertLevel.Normal : Active.Max(a => a.Level);

    public static string LogFilePath => Path.Combine(AppSettings.Directory, "alerts.log");

    public AlertRule? CustomRuleFor(string sensorId) => _settings.AlertRules.FirstOrDefault(r => r.SensorId == sensorId);

    public static AlertRule? DefaultRuleFor(SensorItem sensor) => DefaultAlertRules.For(sensor);

    public AlertRule? EffectiveRuleFor(SensorItem sensor) => CustomRuleFor(sensor.Id) ?? DefaultRuleFor(sensor);

    /// <summary>Saves a user rule for a sensor (replacing any earlier one) and applies it.</summary>
    public void SetCustomRule(AlertRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        _settings.AlertRules.RemoveAll(r => r.SensorId == rule.SensorId);
        _settings.AlertRules.Add(rule);
        _settings.Save();
        Rebuild();
        RulesChanged?.Invoke();
    }

    /// <summary>Drops the user's rule so the built-in default (if any) applies again.</summary>
    public void ResetRule(string sensorId)
    {
        if (_settings.AlertRules.RemoveAll(r => r.SensorId == sensorId) > 0)
        {
            _settings.Save();
            Rebuild();
            RulesChanged?.Invoke();
        }
    }

    public void SendTestNotification() =>
        _tray.Notify("SensorDeck", "Notifications are working. Alerts will appear like this.", critical: false);

    public void ClearLog() => Log.Clear();

    private void Rebuild()
    {
        _watched.Clear();
        DefaultAlertRules.IsLaptop = _store.Sensors.Any(s => s.HardwareKind == HardwareKind.Battery);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var stillWatched = new HashSet<string>(StringComparer.Ordinal);

        foreach (var sensor in _store.Sensors)
        {
            var rule = EffectiveRuleFor(sensor);
            sensor.Rule = rule;
            if (rule is not { Enabled: true, HasLimits: true })
            {
                continue;
            }

            if (!_trackers.TryGetValue(sensor.Id, out var tracker))
            {
                tracker = new AlertTracker(sensor.Id);
                _trackers[sensor.Id] = tracker;
            }

            _watched.Add((sensor, rule, tracker));
            stillWatched.Add(sensor.Id);
        }

        // A new catalog brings new sensor objects: point active alerts at them.
        for (var i = 0; i < Active.Count; i++)
        {
            if (_store.SensorsById.TryGetValue(Active[i].Sensor.Id, out var current) && !ReferenceEquals(current, Active[i].Sensor))
            {
                Active[i] = Active[i] with { Sensor = current };
            }
        }

        // Rules removed/disabled (or sensors gone): end their alerts.
        foreach (var (id, tracker) in _trackers.ToList())
        {
            if (!stillWatched.Contains(id))
            {
                var sensor = _store.SensorsById.TryGetValue(id, out var s) ? s : null;
                if (tracker.Update(null, sensor?.Value, now) is { } transition && sensor is not null)
                {
                    Handle(transition, sensor, null, notify: false);
                }

                _trackers.Remove(id);
                RemoveActive(id);
            }
        }
    }

    private void Evaluate(long timestampUnixMs)
    {
        foreach (var (sensor, rule, tracker) in _watched)
        {
            // Below-limits on fans only count once the fan has been seen spinning: an unused header reads 0 RPM.
            var value = sensor.IsRuleArmed ? sensor.Value : null;
            if (tracker.Update(rule, value, timestampUnixMs) is { } transition)
            {
                Handle(transition, sensor, rule, notify: true);
            }
        }

        FlushNotifications();
    }

    /// <summary>
    /// Notifications from one update are combined: Windows shows one tray notification at a time, so several
    /// separate ones would replace each other.
    /// </summary>
    private void FlushNotifications()
    {
        if (_pending.Count == 0)
        {
            return;
        }

        // Quiet hours: drop what may not notify now (still in the history and alerts.log). A replay never notifies:
        // its alerts are about recorded values, not this PC.
        _pending.RemoveAll(p => AppMode.IsReplay || _settings.IsQuietNow(p.Critical));
        if (_pending.Count == 0)
        {
            return;
        }

        var critical = _pending.Any(p => p.Critical);
        var (title, text) = _pending.Count == 1
            ? (_pending[0].Title, _pending[0].Text)
            : ($"{_pending.Count} sensor alerts", string.Join(Environment.NewLine, _pending.Select(p => $"{p.Title} — {p.Text.Split('\n')[0]}")));
        _tray.Notify(title, text, critical);
        _ = _ntfy.PublishAsync(title, text, critical); // phone (if turned on); never throws

        _pending.Clear();
    }

    private void Handle(AlertTransition transition, SensorItem sensor, AlertRule? rule, bool notify)
    {
        var value = Format(sensor, transition.Value);
        var message = transition.IsRaise && rule is not null
            ? $"{value} ({LimitText(sensor, rule, transition.To, transition.Value)})"
            : $"{value}";

        var entry = new AlertEvent(DateTime.Now, sensor.Name, sensor.GroupName, transition.From, transition.To, message);
        Log.Insert(0, entry);
        while (Log.Count > MaxLogEntries)
        {
            Log.RemoveAt(Log.Count - 1);
        }

        AppendToFile(entry);

        RemoveActive(sensor.Id);
        if (transition.To != AlertLevel.Normal)
        {
            Active.Insert(0, new ActiveAlert(sensor, transition.To, entry.Time));
        }

        if (notify && transition.IsRaise)
        {
            AlertRaised?.Invoke(transition.To);
        }

        if (!notify || !_settings.AlertsEnabled)
        {
            return;
        }

        if (transition.IsRaise && (transition.To == AlertLevel.Critical || _settings.AlertNotifyWarnings))
        {
            _pending.Add(($"{transition.To}: {sensor.Name}", $"{message}\n{sensor.GroupName}", transition.To == AlertLevel.Critical));
        }
        else if (!transition.IsRaise && transition.To == AlertLevel.Normal && _settings.AlertNotifyCleared)
        {
            _pending.Add(($"Back to normal: {sensor.Name}", $"{value}\n{sensor.GroupName}", false));
        }
    }

    private void RemoveActive(string sensorId)
    {
        for (var i = Active.Count - 1; i >= 0; i--)
        {
            if (Active[i].Sensor.Id == sensorId)
            {
                Active.RemoveAt(i);
            }
        }
    }

    /// <summary>"limit 95.0 °C" / "below 2.600 V": which limit was crossed.</summary>
    private string LimitText(SensorItem sensor, AlertRule rule, AlertLevel level, double value)
    {
        var (above, below) = level == AlertLevel.Critical
            ? (rule.CriticalAbove, rule.CriticalBelow)
            : (rule.WarningAbove, rule.WarningBelow);

        if (above is { } a && value >= a)
        {
            return $"limit {Format(sensor, a)}";
        }

        return below is { } b ? $"below {Format(sensor, b)}" : "limit crossed";
    }

    private string Format(SensorItem sensor, double value) =>
        UnitFormatter.Format(sensor.Kind, (float)value, _store.Display.TemperatureUnit);

    private static void AppendToFile(AlertEvent entry)
    {
        // Names come from the hardware or the user's rename: keep each entry on one line.
        LogFiles.Append(
            LogFilePath,
            string.Create(CultureInfo.InvariantCulture, $"{entry.Time:yyyy-MM-dd HH:mm:ss}  {entry.LevelText,-14}  {OneLine(entry.SensorName)} ({OneLine(entry.GroupName)})  {OneLine(entry.Message)}{Environment.NewLine}"));
    }

    private static string OneLine(string text) => text.ReplaceLineEndings(" ");
}
