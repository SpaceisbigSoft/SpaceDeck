using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using SensorDeck.Core.Alerts;
using SensorDeck.Core.Automation;
using SensorDeck.Integrations.FanControl;

namespace SensorDeck.App.Services;

/// <summary>
/// Runs the automation rules while the app is open: once a second it asks <see cref="AutomationEngine"/> which
/// FanControl config should be active (programs running, sensor readings, time, active alerts, manual hold) and
/// switches when needed. Also plays alert sounds. FanControl still does all the fan control.
/// </summary>
public sealed partial class AutomationService : ObservableObject
{
    private static readonly TimeSpan ProcessPollInterval = TimeSpan.FromSeconds(3);
    private static readonly IReadOnlySet<string> NoProcesses = new HashSet<string>();

    private readonly AppSettings _settings;
    private readonly FanProfileService _fans;
    private readonly SensorStore _store;
    private readonly AlertService _alerts;
    private readonly RecordingService _recorder;
    private readonly AutomationEngine _engine;
    private readonly DispatcherTimer _timer;
    private IReadOnlySet<string> _processes = NoProcesses;
    private DateTime _lastProcessPoll = DateTime.MinValue;
    private bool _processPollRunning;
    private string? _lastFailure;
    private bool _ticking;
    private HashSet<string> _loggedActive = new(StringComparer.Ordinal);
    private bool _loggedHolding;
    private string? _lastRecordError;

    /// <summary>When readings for the current catalog last arrived (UTC); MinValue after a new catalog, until they do.</summary>
    private DateTime _lastValuesUtc = DateTime.MinValue;

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private bool _isHolding;

    [ObservableProperty]
    private string? _lastSwitchText;

    public AutomationService(AppSettings settings, FanProfileService fans, SensorStore store, AlertService alerts, RecordingService recorder)
    {
        _recorder = recorder;
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(fans);
        ArgumentNullException.ThrowIfNull(alerts);
        _settings = settings;
        _fans = fans;
        _store = store;
        _alerts = alerts;
        _engine = new AutomationEngine(settings.LastFanProfile, settings.Automation.BaselineProfile, settings.Automation.HoldUntil);

        var dispatcher = Dispatcher.CurrentDispatcher;
        fans.ManuallySwitched += name =>
        {
            _engine.ManualSwitch(name, DateTimeOffset.Now, Settings);
            Log(_engine.HoldUntil == DateTimeOffset.MaxValue
                ? $"You picked {name}: rules paused until resumed"
                : $"You picked {name}: rules paused until {_engine.HoldUntil?.LocalDateTime:t}");
            _lastFailure = null;
            // Not from inside FanProfileService.Switch (and the picker's binding update): check again afterwards.
            dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Tick));
        };
        alerts.AlertRaised += PlayAlertSound;
        store.ValuesApplied += _ => _lastValuesUtc = DateTime.UtcNow;
        store.CatalogApplied += () => _lastValuesUtc = DateTime.MinValue; // a (re)connect or new hardware: wait for its readings

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Tick();
    }

    public AutomationSettings Settings => _settings.Automation;

    /// <summary>Ids of rules whose condition holds right now.</summary>
    public IReadOnlySet<string> ActiveRules => _engine.ActiveRules;

    /// <summary>Raised after every check (the Automation page refreshes its "active" marks).</summary>
    public event Action? Evaluated;

    public void Start()
    {
        // Know which programs run before the first decision, or a game already running would switch twice.
        if (HasProcessRules)
        {
            try
            {
                _processes = ListProcesses();
                _lastProcessPoll = DateTime.UtcNow;
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                // The background poll tries again.
            }
        }

        _timer.Start();
        Tick();
    }

    private bool HasProcessRules => Settings.Enabled && Settings.Rules.Any(r => r.Enabled && r.Trigger == TriggerKind.Process);

    public void Stop() => _timer.Stop();

    /// <summary>Saves changed settings and applies them straight away.</summary>
    public void SaveSettings()
    {
        _settings.Save();
        Tick();
    }

    /// <summary>What automation did and why: %AppData%\SensorDeck\automation.log.</summary>
    public static string LogFilePath => Path.Combine(AppSettings.Directory, "automation.log");

    /// <summary>Ends a manual hold so the rules take over again.</summary>
    public void Resume()
    {
        Log("Rules resumed");
        _engine.Resume();
        _lastFailure = null;
        Tick();
    }

    /// <summary>Names of programs running now (for the rule editor), without ".exe", sorted.</summary>
    public static IReadOnlyList<string> RunningProgramNames()
    {
        var processes = Process.GetProcesses();
        try
        {
            return [.. processes.Select(SafeName).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];
        }
        finally
        {
            foreach (var p in processes)
            {
                p.Dispose();
            }
        }
    }

    private void Tick()
    {
        if (_ticking)
        {
            return;
        }

        _ticking = true;
        try
        {
            Evaluate();
        }
        finally
        {
            _ticking = false;
        }
    }

    private void Evaluate()
    {
        PollProcessesIfNeeded();
        var now = DateTimeOffset.Now;
        var connected = _store.ConnectionState == ConnectionState.Connected;

        // Live readings: connected, and some arrived lately (a few poll intervals; none come while the collector
        // restarts or the PC sleeps). Without them sensor rules keep their last state (see AutomationInputs).
        var staleAfter = TimeSpan.FromMilliseconds(Math.Max(5_000, 3 * _store.PollIntervalMs));
        var sensorData = connected && DateTime.UtcNow - _lastValuesUtc < staleAfter;
        var input = new AutomationInputs(
            now,
            _processes,
            id => sensorData && _store.SensorsById.TryGetValue(id, out var sensor) ? (double?)sensor.Value : null, // no stale readings
            _alerts.HighestLevel,
            sensorData);

        var status = _engine.Evaluate(Settings, input);
        if (status.ShouldSwitch && status.Target is { } target)
        {
            var result = _fans.Switch(target, automatic: true);
            if (result.Outcome is SwitchOutcome.Sent or SwitchOutcome.StartedWithConfig)
            {
                _engine.Switched(target, now);
                _lastFailure = null;
                LastSwitchText = $"Switched to {target} at {now.LocalDateTime:t} — {status.Reason}";
                Log($"Switched to {target} ({status.Reason})");
            }
            else if (result.Outcome == SwitchOutcome.ElevationCancelled)
            {
                // Don't ask again every cool-down: pause until the user picks a profile or resumes.
                _engine.Pause(now, Settings);
                _lastFailure = $"Paused: the administrator prompt for switching to {target} was cancelled. Click Resume rules to try again.";
                Log(_lastFailure);
            }
            else
            {
                _engine.SwitchFailed(target, now, Settings);
                _lastFailure = $"Couldn't switch to {target}: {result.Message}";
                Log(_lastFailure);
            }

            status = status with { IsHolding = _engine.HoldUntil is { } h && now < h };
        }
        else if (string.Equals(status.Target, _engine.Applied, StringComparison.OrdinalIgnoreCase))
        {
            _lastFailure = null; // nothing pending any more
        }

        SaveEngineState();
        LogChanges(status);
        UpdateRecording();
        IsHolding = status.IsHolding;
        var profile = _engine.Applied is { } p ? $"Using {p}" : "No profile chosen yet";
        StatusText = _lastFailure ?? $"{profile} · {status.Reason}";
        Evaluated?.Invoke();
    }

    /// <summary>Records while a rule with "record" is active; stops only recordings automation started.</summary>
    private void UpdateRecording()
    {
        var rule = Settings.Enabled
            ? Settings.Rules.FirstOrDefault(r => r.Enabled && r.Record && _engine.ActiveRules.Contains(r.Id))
            : null;
        if (rule is null)
        {
            _recorder.AutomaticStoppedByUser = false; // the rule ended: next time it may record again
            _lastRecordError = null;
        }

        if (rule is not null && !_recorder.IsRecording && !_recorder.AutomaticStoppedByUser)
        {
            // Retried every check (e.g. "no sensors yet" clears once connected), but each distinct failure is logged once.
            var name = AutomationEngine.DisplayName(rule);
            var error = _recorder.Start(name, automatic: true);
            if (error is null)
            {
                _lastRecordError = null;
                Log($"Started recording for rule \"{name}\"");
            }
            else if (error != _lastRecordError)
            {
                _lastRecordError = error;
                Log($"Couldn't record for rule \"{name}\": {error}");
            }
        }
        else if (rule is null && _recorder.IsRecording && _recorder.IsAutomatic)
        {
            _recorder.Stop();
            Log("Stopped automatic recording");
        }
    }

    /// <summary>Logs rules turning on/off and pauses ending, so "why didn't it switch?" can be answered later.</summary>
    private void LogChanges(AutomationStatus status)
    {
        var active = _engine.ActiveRules;
        foreach (var rule in Settings.Rules)
        {
            var on = active.Contains(rule.Id);
            if (on != _loggedActive.Contains(rule.Id))
            {
                Log($"Rule \"{AutomationEngine.DisplayName(rule)}\" is now {(on ? "on" : "off")}");
            }
        }

        _loggedActive = [.. active];
        if (_loggedHolding && !status.IsHolding)
        {
            Log("Pause ended");
        }

        _loggedHolding = status.IsHolding;
    }

    /// <summary>One line per entry: rule names and error texts (e.g. from a restored backup) can't forge entries.</summary>
    private static void Log(string message)
    {
        LogFiles.Append(LogFilePath, string.Create(CultureInfo.InvariantCulture, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message.ReplaceLineEndings(" ")}{Environment.NewLine}"), 1_000_000);
    }

    /// <summary>Keeps the profile to return to and any pause across restarts (saved only when they change).</summary>
    private void SaveEngineState()
    {
        if (Settings.BaselineProfile == _engine.Baseline && Settings.HoldUntil == _engine.HoldUntil)
        {
            return;
        }

        Settings.BaselineProfile = _engine.Baseline;
        Settings.HoldUntil = _engine.HoldUntil;
        _settings.Save();
    }

    /// <summary>Lists running programs in the background every few seconds, only while a program rule exists.</summary>
    private void PollProcessesIfNeeded()
    {
        if (!HasProcessRules)
        {
            _processes = NoProcesses; // no allocation every tick
            return;
        }

        if (_processPollRunning || DateTime.UtcNow - _lastProcessPoll < ProcessPollInterval)
        {
            return;
        }

        _processPollRunning = true;
        _lastProcessPoll = DateTime.UtcNow;
        _ = PollProcessesAsync();
    }

    private async Task PollProcessesAsync()
    {
        try
        {
            var names = await Task.Run(ListProcesses).ConfigureAwait(true);
            _processes = names;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            // Try again next poll.
        }
        finally
        {
            _processPollRunning = false;
        }
    }

    private static HashSet<string> ListProcesses()
    {
        var processes = Process.GetProcesses();
        try
        {
            return processes.Select(SafeName).OfType<string>().Select(ProcessNames.Normalize).ToHashSet(StringComparer.Ordinal);
        }
        finally
        {
            foreach (var p in processes)
            {
                p.Dispose();
            }
        }
    }

    private static string? SafeName(Process process)
    {
        try
        {
            return process.ProcessName;
        }
        catch (InvalidOperationException)
        {
            return null; // exited while listing
        }
    }

    private void PlayAlertSound(AlertLevel level)
    {
        if (AppMode.IsReplay || !Settings.Alerts.SoundFor(level) || _settings.IsQuietNow(level == AlertLevel.Critical))
        {
            return;
        }

        (level == AlertLevel.Critical ? SystemSounds.Hand : SystemSounds.Exclamation).Play();
    }
}
