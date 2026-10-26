using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SensorDeck.App.Services;
using SensorDeck.Core.Model;
using SensorDeck.Core.Protocol;

namespace SensorDeck.App.ViewModels;

public enum DiagnosticState
{
    Ok,
    Info,
    Warning,
    Error,
}

/// <summary>One line on the Diagnostics page: what was checked, the result, and a fix when there is one.</summary>
public sealed record DiagnosticCheck(string Title, DiagnosticState State, string Detail, string? ActionText = null, IRelayCommand? Action = null)
{
    /// <summary>Same visible content (the command instance differs on every refresh).</summary>
    public bool Looks(DiagnosticCheck other) =>
        other is not null && Title == other.Title && State == other.State && Detail == other.Detail && ActionText == other.ActionText;

    public bool HasAction => ActionText is not null && Action is not null;

    public string StateText => State switch
    {
        DiagnosticState.Ok => "OK",
        DiagnosticState.Info => "Note",
        DiagnosticState.Warning => "Check",
        _ => "Problem",
    };
}

/// <summary>
/// Diagnostics and first-run setup: checks PawnIO, the collector service, permissions, versions, sensors and
/// FanControl, offers fixes, and exports a hardware report for supporting other boards.
/// </summary>
public sealed partial class DiagnosticsViewModel : ObservableObject
{
    private const string PawnIoUrl = "https://pawnio.eu";

    /// <summary>ERROR_CANCELLED: the user declined the UAC prompt.</summary>
    private const int ErrorCancelled = 1223;

    private static readonly JsonSerializerOptions ReportOptions = new()
    {
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals, // a sensor can read NaN
    };

    private readonly DispatcherTimer _refreshTimer;
    private bool _isPageVisible;

    private readonly SensorStore _store;
    private readonly FanProfileService _fans;
    private readonly AppSettings _settings;
    private readonly DialogService _dialogs;
    private readonly ReplayRecorder _recorder;
    private readonly DispatcherTimer _recordTimer;

    [ObservableProperty]
    private string? _message;

    [ObservableProperty]
    private string? _replayMessage;

    [ObservableProperty]
    private string _recordButtonText = "Record replay file";

    [ObservableProperty]
    private string? _reportMessage;

    [ObservableProperty]
    private bool _isFirstRun;

    public DiagnosticsViewModel(SensorStore store, FanProfileService fans, AppSettings settings, DialogService dialogs, ReplayRecorder recorder)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(recorder);
        _store = store;
        _fans = fans;
        _settings = settings;
        _dialogs = dialogs;
        _recorder = recorder;
        _recordTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _recordTimer.Tick += (_, _) => ReplayMessage = $"Recording… {_recorder.Elapsed:m\\:ss} (stops by itself after {ReplayRecorder.MaxDuration.TotalMinutes:0} minutes).";
        var dispatcher = Dispatcher.CurrentDispatcher;
        recorder.AutoStopped += path => dispatcher.BeginInvoke(() => RecordingStopped(path, null));
        IsFirstRun = !settings.SetupCompleted;

        // Store changes come in bursts (a connect changes five properties); checks query Windows services, so batch
        // them and only run while the page is on screen.
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _refreshTimer.Tick += (_, _) =>
        {
            _refreshTimer.Stop();
            Refresh();
        };
        store.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SensorStore.ConnectionState) or nameof(SensorStore.SourceStatuses)
                or nameof(SensorStore.CollectorStatus) or nameof(SensorStore.ServerVersion) or nameof(SensorStore.HardwareCount)
                or nameof(SensorStore.ConnectionProblem))
            {
                ScheduleRefresh();
            }
        };
        store.CatalogApplied += ScheduleRefresh;
    }

    /// <summary>Set by the main window when this page is shown or left; checks run only while it's visible.</summary>
    public bool IsPageVisible
    {
        get => _isPageVisible;
        set
        {
            _isPageVisible = value;
            if (value)
            {
                Refresh();
            }
            else
            {
                _refreshTimer.Stop();
            }
        }
    }

    private void ScheduleRefresh()
    {
        if (_isPageVisible)
        {
            _refreshTimer.Stop();
            _refreshTimer.Start();
        }
    }

    public ObservableCollection<DiagnosticCheck> Checks { get; } = [];

    public static string AppVersion => typeof(DiagnosticsViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>Instance wrapper so XAML can bind it.</summary>
    public string AppVersionText => AppVersion;

    /// <summary>"All set" when nothing is a warning or problem.</summary>
    public string Summary => Checks.Any(c => c.State == DiagnosticState.Error)
        ? "Something needs fixing before SensorDeck can show everything."
        : Checks.Any(c => c.State == DiagnosticState.Warning)
            ? "SensorDeck works; a few things could be better."
            : "Everything is set up.";

    /// <summary>Raised when the user finishes first-run setup (the window navigates to the dashboard).</summary>
    public event Action? SetupFinished;

    [RelayCommand]
    public void Refresh()
    {
        List<DiagnosticCheck> fresh = [CheckPawnIo(), CheckCollectorService(), CheckConnection(), .. CheckSources(), CheckSensors(), CheckFanControl()];

        // Update rows in place and only when they changed, so a button isn't replaced under the mouse.
        for (var i = 0; i < fresh.Count; i++)
        {
            if (i >= Checks.Count)
            {
                Checks.Add(fresh[i]);
            }
            else if (!Checks[i].Looks(fresh[i]))
            {
                Checks[i] = fresh[i];
            }
        }

        while (Checks.Count > fresh.Count)
        {
            Checks.RemoveAt(Checks.Count - 1);
        }

        OnPropertyChanged(nameof(Summary));
    }

    [RelayCommand]
    private void FinishSetup()
    {
        _settings.SetupCompleted = true;
        _settings.Save();
        IsFirstRun = false;
        SetupFinished?.Invoke();
    }

    [RelayCommand]
    private void CopyDiagnostics()
    {
        try
        {
            Clipboard.SetText(BuildDiagnosticsText());
            ReportMessage = "Diagnostics copied to the clipboard.";
        }
        catch (COMException)
        {
            ReportMessage = "The clipboard is busy; try again.";
        }
    }

    [RelayCommand]
    private void SaveHardwareReport()
    {
        var board = _store.MotherboardName ?? "hardware";
        var path = _dialogs.PickSaveFile("Save hardware report", $"SensorDeck report - {board}", "SensorDeck hardware report (*.json)|*.json");
        if (path is null)
        {
            return;
        }

        try
        {
            File.WriteAllText(path, BuildHardwareReport());
            ReportMessage = $"Saved {Path.GetFileName(path)}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ReportMessage = $"Couldn't save: {ex.Message}";
        }
    }

    /// <summary>This window is a replay (the replay section explains that instead of offering to record).</summary>
    public bool IsReplay => AppMode.IsReplay;

    public string ReplayDescription => AppMode.IsReplay
        ? $"This window replays {AppMode.ReplayName}. Its dashboards and settings are kept separately from your real ones; notifications, automation, hotkeys and FanControl switching are off."
        : "Record what your sensors do into a replay file, or open a recording (or a hardware report someone sent you) in a separate SensorDeck window — handy for testing dashboards on other hardware and for screenshots. Nothing in your real setup changes.";

    [RelayCommand]
    private void ToggleRecording()
    {
        if (_recorder.IsRecording)
        {
            var result = _recorder.Stop();
            RecordingStopped(result?.Path, result?.Frames);
            return;
        }

        if (_store.ConnectionState != ConnectionState.Connected)
        {
            ReplayMessage = "Not connected to the collector, so there's nothing to record.";
            return;
        }

        var path = Path.Combine(ReplayRecorder.Directory, $"Recording {DateTime.Now:yyyy-MM-dd HHmmss}{Core.Replay.SensorRecording.FileExtension}");
        try
        {
            _recorder.Start(path, $"{_store.MotherboardName ?? "Unknown board"} · SensorDeck {AppVersion}");
            RecordButtonText = "Stop recording";
            ReplayMessage = "Recording…";
            _recordTimer.Start();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ReplayMessage = $"Couldn't start recording: {ex.Message}";
        }
    }

    private void RecordingStopped(string? path, int? frames)
    {
        _recordTimer.Stop();
        RecordButtonText = "Record replay file";
        ReplayMessage = path is null ? null
            : frames is { } n ? $"Saved {Path.GetFileName(path)} ({n} samples)."
            : $"Recording stopped (the {ReplayRecorder.MaxDuration.TotalMinutes:0}-minute limit, or the disk is full); saved {Path.GetFileName(path)}.";
    }

    [RelayCommand]
    private void OpenReplay()
    {
        var file = _dialogs.PickOpenFile("Open a recording or hardware report",
            "Recordings and hardware reports (*.sdrec;*.json)|*.sdrec;*.json|All files (*.*)|*.*");
        if (file is null)
        {
            return;
        }

        ReplayMessage = ReplayHost.Launch(file, out var error)
            ? $"Opening {Path.GetFileName(file)} in a new window…"
            : $"Couldn't open the replay: {error}";
    }

    [RelayCommand]
    private void OpenReplaysFolder() => OpenFolder(ReplayRecorder.Directory, "No recordings yet.", m => ReplayMessage = m);

    [RelayCommand]
    private void OpenCollectorLogs() =>
        OpenFolder(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SensorDeck", "logs"),
            "No collector logs yet (the collector writes them once it has run).");

    [RelayCommand]
    private void OpenAppData() => OpenFolder(AppSettings.Directory, "The settings folder doesn't exist yet.");

    private void OpenFolder(string path, string missing, Action<string?>? show = null)
    {
        var message = !Directory.Exists(path) ? missing
            : CollectorServiceStatus.OpenUrl(path) ? null
            : $"Couldn't open {path}.";
        (show ?? (m => ReportMessage = m))(message);
    }

    // ── Checks ──────────────────────────────────────────────────────────

    private DiagnosticCheck CheckPawnIo()
    {
        var driverMissing = _store.SourceStatuses.Any(s => s.Status == SourceStatus.DriverMissing);
        var (state, detail) = QueryPawnIo();
        if (driverMissing || state == "missing")
        {
            return new DiagnosticCheck("PawnIO driver", DiagnosticState.Error,
                "PawnIO isn't installed. It's the signed driver LibreHardwareMonitor uses to read motherboard and CPU sensors. Install it, then restart the collector.",
                "Get PawnIO", new RelayCommand(() => CollectorServiceStatus.OpenUrl(PawnIoUrl)));
        }

        return state == "running"
            ? new DiagnosticCheck("PawnIO driver", DiagnosticState.Ok, detail)
            : new DiagnosticCheck("PawnIO driver", DiagnosticState.Info, detail);
    }

    /// <summary>Looks for the PawnIO driver service ("running" / "stopped" / "missing" / "unknown").</summary>
    private static (string State, string Detail) QueryPawnIo()
    {
        try
        {
            var drivers = ServiceController.GetDevices();
            try
            {
                var pawn = drivers.FirstOrDefault(d => d.ServiceName.Contains("PawnIO", StringComparison.OrdinalIgnoreCase)
                                                       || d.DisplayName.Contains("PawnIO", StringComparison.OrdinalIgnoreCase));
                return pawn is null
                    ? ("missing", "Not installed.")
                    : pawn.Status == ServiceControllerStatus.Running
                        ? ("running", "Installed and running.")
                        : ("stopped", "Installed. Windows starts it when the collector needs it.");
            }
            finally
            {
                foreach (var d in drivers)
                {
                    d.Dispose();
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return ("unknown", "Couldn't check (not critical if sensors show up).");
        }
    }

    private DiagnosticCheck CheckCollectorService()
    {
        var connected = _store.ConnectionState == ConnectionState.Connected;
        var asService = _store.CollectorStatus?.IsService;
        switch (CollectorServiceStatus.Query())
        {
            case CollectorInstallState.Running:
                return new DiagnosticCheck("Collector service", DiagnosticState.Ok, "Installed and running; it starts with Windows.");

            case CollectorInstallState.Stopped:
                return new DiagnosticCheck("Collector service", connected ? DiagnosticState.Warning : DiagnosticState.Error,
                    connected
                        ? "The service is installed but stopped; a collector started from a terminal is running instead."
                        : "The service is installed but stopped, so there's no sensor data.",
                    "Start service", new RelayCommand(StartService));

            case CollectorInstallState.NotInstalled:
                var script = FindInstallScript();
                return new DiagnosticCheck("Collector service", connected ? DiagnosticState.Warning : DiagnosticState.Error,
                    (connected && asService != true
                        ? "Not installed as a service: the collector is running from a terminal and stops when you close it. "
                        : "The collector service isn't installed. ")
                    + (script is not null ? "Install it once (asks for administrator rights)." : "Run scripts\\Install-Collector.ps1 as administrator."),
                    script is not null ? "Install service" : null,
                    script is not null ? new RelayCommand(() => RunInstallScript(script)) : null);

            default:
                return new DiagnosticCheck("Collector service", DiagnosticState.Info, "Couldn't query Windows services.");
        }
    }

    private DiagnosticCheck CheckConnection()
    {
        if (_store.ConnectionState != ConnectionState.Connected)
        {
            return new DiagnosticCheck("Connection", DiagnosticState.Error,
                _store.ConnectionProblem
                ?? (_store.ConnectionState == ConnectionState.Connecting ? "Connecting to the collector…" : "Not connected to the collector."));
        }

        var version = _store.ServerVersion ?? "?";
        if (_store.CollectorProtocolVersion != ProtocolInfo.Version)
        {
            var fix = _store.CollectorProtocolVersion < ProtocolInfo.Version
                ? "Update (rebuild/reinstall) the collector and restart it."
                : "Update this app to match the collector.";
            return new DiagnosticCheck("Connection", DiagnosticState.Warning,
                $"Connected, but the collector speaks protocol {_store.CollectorProtocolVersion} and this app {ProtocolInfo.Version}. {fix}");
        }

        return version == AppVersion
            ? new DiagnosticCheck("Connection", DiagnosticState.Ok, $"Connected to collector {version}.")
            : new DiagnosticCheck("Connection", DiagnosticState.Info, $"Connected to collector {version} (app {AppVersion}).");
    }

    private IEnumerable<DiagnosticCheck> CheckSources()
    {
        foreach (var source in _store.SourceStatuses)
        {
            switch (source.Status)
            {
                case SourceStatus.Ok:
                    yield return new DiagnosticCheck($"Sensor source: {source.Source}", DiagnosticState.Ok, "Reading hardware normally.");
                    break;
                case SourceStatus.NotElevated:
                    yield return new DiagnosticCheck($"Sensor source: {source.Source}", DiagnosticState.Error,
                        source.Message ?? "The collector isn't running as administrator, so many sensors are missing. Install it as a service, or start it from an elevated terminal.");
                    break;
                case SourceStatus.DriverMissing:
                    break; // covered by the PawnIO check
                default:
                    yield return new DiagnosticCheck($"Sensor source: {source.Source}", DiagnosticState.Warning, source.Message ?? source.Status.ToString());
                    break;
            }
        }
    }

    private DiagnosticCheck CheckSensors()
    {
        var count = _store.Sensors.Count;
        if (count == 0)
        {
            return new DiagnosticCheck("Sensors", _store.ConnectionState == ConnectionState.Connected ? DiagnosticState.Error : DiagnosticState.Info,
                "No sensors yet.");
        }

        var board = _store.MotherboardName is { } b ? $" · {b}" : "";
        return new DiagnosticCheck("Sensors", DiagnosticState.Ok, $"{count} sensors on {_store.HardwareCount} devices{board}.");
    }

    private DiagnosticCheck CheckFanControl()
    {
        if (_fans.Install is null)
        {
            _fans.Refresh();
        }

        return _fans.Install is { } install
            ? new DiagnosticCheck("FanControl", DiagnosticState.Ok, $"Found {install.ExePath} with {_fans.Configs.Count} saved configs.")
            : _fans.Rejected is { } rejected
                ? new DiagnosticCheck("FanControl", DiagnosticState.Warning,
                    $"Found but not used automatically: {rejected}. Set its location in Settings to use it anyway.")
                : new DiagnosticCheck("FanControl", DiagnosticState.Info,
                    "Not found. Optional: SensorDeck can switch FanControl configs if it's installed (set its location in Settings).");
    }

    // ── Actions ─────────────────────────────────────────────────────────

    private void StartService()
    {
        Message = CollectorServiceStatus.TryStart(out var error) ? "Starting the collector service…" : error;
    }

    /// <summary>
    /// scripts\Install-Collector.ps1 in the source tree, only when the app runs from that tree's build output. It is
    /// run as administrator, so it is never searched for above an installed app (any user can create C:\scripts).
    /// </summary>
    private static string? FindInstallScript()
    {
        var candidate = CollectorServiceStatus.SourceTreeRoot() is { } root ? Path.Combine(root, "scripts", "Install-Collector.ps1") : null;
        return candidate is not null && File.Exists(candidate) ? candidate : null;
    }

    private void RunInstallScript(string script)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                // Full path: this runs as administrator, so don't let the search path pick the program.
                FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -NoExit -File \"{script}\"",
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(Path.GetDirectoryName(script)) ?? "",
            })?.Dispose();
            Message = "The installer is running in an administrator window. Stop any collector running in a terminal first.";
        }
        catch (Win32Exception ex)
        {
            Message = ex.NativeErrorCode == ErrorCancelled ? "The administrator prompt was cancelled." : ex.Message;
        }
    }

    // ── Reports ─────────────────────────────────────────────────────────

    private string BuildDiagnosticsText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"SensorDeck {AppVersion} diagnostics — {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine($"Windows {Environment.OSVersion.Version} · .NET {Environment.Version}");
        sb.AppendLine($"Collector {_store.ServerVersion ?? "not connected"} (protocol {_store.CollectorProtocolVersion}) · {_store.ConnectionState}");
        if (_store.CollectorStatus is { } s)
        {
            sb.AppendLine($"Collector: {(s.IsService ? "service" : "terminal")}, {(s.IsElevated ? "admin" : "not admin")}, every {s.PollIntervalMs} ms, storage {s.StorageEnabled}, network {s.NetworkEnabled}, controllers {s.ControllersEnabled}");
        }

        foreach (var check in Checks)
        {
            sb.AppendLine($"[{check.StateText}] {check.Title}: {check.Detail}");
        }

        return sb.ToString();
    }

    private string BuildHardwareReport()
    {
        var report = new
        {
            Generated = DateTimeOffset.Now,
            App = AppVersion,
            Collector = _store.ServerVersion,
            Windows = Environment.OSVersion.Version.ToString(),
            Board = _store.MotherboardName,
            // Calculated sensors are this app's own (recreated from settings), so they're left out.
            Sensors = _store.Sensors.Where(s => !s.Id.StartsWith("calc:", StringComparison.Ordinal)).Select(s => new
            {
                s.Id,
                Name = s.OriginalName,
                s.HardwareId,
                Hardware = s.GroupName,
                HardwareKind = s.HardwareKind.ToString(),
                Kind = s.Kind.ToString(),
                s.Unit,
                Value = s.RawValue,
                Min = s.ToRaw(s.Min),
                Max = s.ToRaw(s.Max),
                s.Nominal,
            }),
        };
        return JsonSerializer.Serialize(report, ReportOptions);
    }
}
