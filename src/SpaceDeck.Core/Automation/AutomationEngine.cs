using SpaceDeck.Core.Alerts;

namespace SpaceDeck.Core.Automation;

/// <summary>What the engine sees on each check.</summary>
/// <param name="RunningProcesses">Normalized names (<see cref="ProcessNames.Normalize"/>).</param>
/// <param name="SensorValue">A sensor's current reading; null when it has none (then a sensor rule's condition is false).</param>
/// <param name="SensorDataAvailable">
/// False while there is no live sensor data at all (the app is disconnected from the collector, or readings stopped
/// coming, e.g. a collector restart, reinstall or sleep/resume): sensor rules then keep their last state instead of
/// reading as off, so such a gap doesn't switch profiles. Program and time-of-day rules are not affected.
/// </param>
public sealed record AutomationInputs(
    DateTimeOffset Now,
    IReadOnlySet<string> RunningProcesses,
    Func<string, double?> SensorValue,
    AlertLevel AlertLevel,
    bool SensorDataAvailable = true);

/// <summary>The engine's verdict: which profile should be active, why, and whether to switch now.</summary>
public sealed record AutomationStatus(string? Target, bool ShouldSwitch, string Reason, string? ActiveRuleId, bool IsHolding, DateTimeOffset? HoldUntil);

/// <summary>
/// Decides which FanControl config should be active. Priority: an active alert's profile → a manual hold → the first
/// active rule → the default profile → the profile in use before automation switched. Switches are rate-limited by a
/// cool-down (alerts excepted), and sensor rules must hold for N seconds both to turn on and to turn off, so a value
/// sitting on the threshold doesn't flap. Without live sensor data (<see cref="AutomationInputs.SensorDataAvailable"/>)
/// sensor rules keep their last state. Pure logic: the app does the switching and reports back.
/// </summary>
public sealed class AutomationEngine
{
    private readonly Dictionary<string, SensorRuleState> _sensorStates = new(StringComparer.Ordinal);
    private string? _applied;
    private string? _baseline;
    private string? _alertProfile;
    private DateTimeOffset _lastSwitch = DateTimeOffset.MinValue;
    private DateTimeOffset? _holdUntil;
    private string? _failedTarget;
    private DateTimeOffset _retryAfter = DateTimeOffset.MinValue;

    /// <param name="currentProfile">The config in use at start (FanControl doesn't report it; SpaceDeck's last switch).</param>
    /// <param name="baseline">The profile to return to when nothing is active (saved from last time); defaults to the current one.</param>
    /// <param name="holdUntil">A manual pause still running from last time.</param>
    public AutomationEngine(string? currentProfile, string? baseline = null, DateTimeOffset? holdUntil = null)
    {
        _applied = currentProfile;
        _baseline = baseline ?? currentProfile;
        _holdUntil = holdUntil;
    }

    /// <summary>The config SpaceDeck believes is active.</summary>
    public string? Applied => _applied;

    /// <summary>The profile to return to (save it to survive restarts).</summary>
    public string? Baseline => _baseline;

    /// <summary>The current manual pause, if any (save it to survive restarts).</summary>
    public DateTimeOffset? HoldUntil => _holdUntil;

    /// <summary>Rule ids whose condition currently holds (for showing which rules are "on").</summary>
    public IReadOnlySet<string> ActiveRules => _activeRules;

    private readonly HashSet<string> _activeRules = new(StringComparer.Ordinal);

    /// <summary>The user picked a profile: it becomes the one to return to, and rules pause for the hold time.</summary>
    public void ManualSwitch(string profile, DateTimeOffset now, AutomationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _applied = profile;
        _baseline = profile;
        _lastSwitch = now;
        _failedTarget = null;
        Hold(now, settings);
    }

    /// <summary>Pause rules on the current profile (e.g. after the user cancelled an administrator prompt).</summary>
    public void Pause(DateTimeOffset now, AutomationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _baseline = _applied;
        Hold(now, settings);
    }

    /// <summary>End a manual hold now.</summary>
    public void Resume()
    {
        _holdUntil = null;
        _failedTarget = null;
    }

    /// <summary>The app switched as told.</summary>
    public void Switched(string profile, DateTimeOffset now)
    {
        _applied = profile;
        _lastSwitch = now;
        _failedTarget = null;
    }

    /// <summary>The switch failed: don't retry that profile for a while (on any path, alerts included).</summary>
    public void SwitchFailed(string target, DateTimeOffset now, AutomationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _failedTarget = target;
        _retryAfter = now + TimeSpan.FromSeconds(Math.Max(30, settings.CooldownSeconds));
    }

    private void Hold(DateTimeOffset now, AutomationSettings settings) =>
        _holdUntil = settings.HoldMinutes <= 0 ? DateTimeOffset.MaxValue : now.AddMinutes(settings.HoldMinutes);

    public AutomationStatus Evaluate(AutomationSettings settings, AutomationInputs input)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(input);
        UpdateRuleStates(settings, input);

        var holding = _holdUntil is { } until && input.Now < until;
        if (!holding)
        {
            _holdUntil = null;
        }

        // 1. An alert wins over everything, including a hold, and ignores the cool-down.
        if (settings.Alerts.ProfileFor(input.AlertLevel) is { } alertProfile)
        {
            _alertProfile = alertProfile;
            return Decide(alertProfile, $"{input.AlertLevel} alert", null, input.Now, settings, useCooldown: false)
                with { IsHolding = holding, HoldUntil = holding ? _holdUntil : null };
        }

        var alertJustEnded = _alertProfile is not null;
        if (alertJustEnded)
        {
            if (!settings.Alerts.RevertOnClear)
            {
                // Stay on the alert's profile: treat it like the user's own pick, so rules don't switch away at once.
                _baseline = _alertProfile;
                Hold(input.Now, settings);
                holding = true;
            }

            _alertProfile = null;
        }

        // 2. Manual hold: keep (or go back to) the profile the user picked.
        if (holding)
        {
            var reason = _holdUntil == DateTimeOffset.MaxValue
                ? "Rules paused until you click Resume rules"
                : $"Rules paused until {_holdUntil!.Value.LocalDateTime:t}";
            return Decide(_baseline, reason, null, input.Now, settings, useCooldown: false) with { IsHolding = true, HoldUntil = _holdUntil };
        }

        // 3. First active rule.
        if (settings.Enabled && settings.Rules.FirstOrDefault(r => r.Enabled && !string.IsNullOrWhiteSpace(r.Profile) && _activeRules.Contains(r.Id)) is { } rule)
        {
            return Decide(rule.Profile, $"Rule: {DisplayName(rule)}", rule.Id, input.Now, settings, useCooldown: !alertJustEnded);
        }

        // 4. Nothing active: the default, or back to where we started.
        var fallback = settings.Enabled && !string.IsNullOrWhiteSpace(settings.DefaultProfile) ? settings.DefaultProfile : _baseline;
        var why = !settings.Enabled ? "Rules off"
            : fallback == settings.DefaultProfile ? "No rule active: default profile"
            : "No rule active";
        return Decide(fallback, why, null, input.Now, settings, useCooldown: !alertJustEnded);
    }

    public static string DisplayName(AutomationRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return string.IsNullOrWhiteSpace(rule.Name) ? rule.Profile : rule.Name;
    }

    private AutomationStatus Decide(string? target, string reason, string? ruleId, DateTimeOffset now, AutomationSettings settings, bool useCooldown)
    {
        if (target is null || string.Equals(target, _applied, StringComparison.OrdinalIgnoreCase))
        {
            return new AutomationStatus(target ?? _applied, false, reason, ruleId, false, null);
        }

        if (string.Equals(target, _failedTarget, StringComparison.OrdinalIgnoreCase) && now < _retryAfter)
        {
            return new AutomationStatus(target, false, $"{reason} (retrying at {_retryAfter.LocalDateTime:t})", ruleId, false, null);
        }

        var wait = TimeSpan.FromSeconds(Math.Max(0, settings.CooldownSeconds)) - (now - _lastSwitch);
        if (useCooldown && wait > TimeSpan.Zero)
        {
            return new AutomationStatus(target, false, $"{reason} (switching in {Math.Ceiling(wait.TotalSeconds):0} s)", ruleId, false, null);
        }

        return new AutomationStatus(target, true, reason, ruleId, false, null);
    }

    private void UpdateRuleStates(AutomationSettings settings, AutomationInputs input)
    {
        _activeRules.Clear();
        var time = TimeOnly.FromDateTime(input.Now.DateTime);
        foreach (var rule in settings.Rules)
        {
            var active = rule.Trigger switch
            {
                TriggerKind.Process => rule.Processes.Any(p => input.RunningProcesses.Contains(ProcessNames.Normalize(p))),
                TriggerKind.TimeOfDay => TimeWindow.Contains(rule.From, rule.To, time),
                _ => SensorRuleActive(rule, input),
            };

            if (active && rule.Enabled)
            {
                _activeRules.Add(rule.Id);
            }
        }

        // Forget timers of rules that were deleted.
        foreach (var id in _sensorStates.Keys.Where(id => !settings.Rules.Any(r => r.Id == id && r.IsSensorRule)).ToList())
        {
            _sensorStates.Remove(id);
        }
    }

    /// <summary>The condition must hold for ForSeconds to switch on, and fail for ForSeconds to switch off.</summary>
    private bool SensorRuleActive(AutomationRule rule, AutomationInputs input)
    {
        // An edited rule keeps its id: start its timers over when what it watches changed.
        var signature = (rule.SensorId, rule.Trigger, rule.Threshold);
        if (!_sensorStates.TryGetValue(rule.Id, out var state) || state.Signature != signature)
        {
            state = new SensorRuleState { Signature = signature };
            _sensorStates[rule.Id] = state;
        }

        if (!input.SensorDataAvailable)
        {
            // No readings at all (disconnected, or none arriving): keep the last state rather than read as off. A change
            // that was under way starts its ForSeconds over once readings are back (the gap isn't counted as held).
            state.ChangingSince = null;
            return state.Active;
        }

        var value = rule.SensorId is null ? null : input.SensorValue(rule.SensorId);
        var condition = value is { } v && !double.IsNaN(v) && (rule.Trigger == TriggerKind.SensorAbove ? v >= rule.Threshold : v <= rule.Threshold);
        if (condition == state.Active)
        {
            state.ChangingSince = null;
        }
        else
        {
            state.ChangingSince ??= input.Now;
            if (input.Now - state.ChangingSince.Value >= TimeSpan.FromSeconds(Math.Max(0, rule.ForSeconds)))
            {
                state.Active = condition;
                state.ChangingSince = null;
            }
        }

        return state.Active;
    }

    private sealed class SensorRuleState
    {
        public (string? SensorId, TriggerKind Trigger, double Threshold) Signature { get; init; }

        public bool Active { get; set; }

        public DateTimeOffset? ChangingSince { get; set; }
    }
}
