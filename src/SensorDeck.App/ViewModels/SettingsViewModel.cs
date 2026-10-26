using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using SensorDeck.App.Services;
using SensorDeck.App.Themes;
using SensorDeck.Core.Formatting;
using SensorDeck.Core.Protocol;

namespace SensorDeck.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly SensorStore _store;
    private readonly CollectorClient _client;
    private readonly AppSettings _settings;
    private readonly FanProfileService _fans;

    [ObservableProperty]
    private int _pollIntervalMs = 1000;

    [ObservableProperty]
    private string? _pollStatus;

    [ObservableProperty]
    private string _fanControlLocation = "Not found";

    private readonly DialogService _dialogs;

    public SettingsViewModel(SensorStore store, CollectorClient client, AppSettings settings, FanProfileService fans, DialogService dialogs, GlobalHotkeyService hotkeys, RemoteSettingsViewModel remote, SidebarSettingsViewModel sidebar)
    {
        Remote = remote;
        Sidebar = sidebar;
        _dialogs = dialogs;
        _hotkeys = hotkeys;
        ClickThroughHotkey = settings.ClickThroughHotkey;
        hotkeys.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(GlobalHotkeyService.Status))
            {
                OnPropertyChanged(nameof(HotkeyStatus));
            }
            else if (e.PropertyName == nameof(GlobalHotkeyService.ClickThroughStatus))
            {
                OnPropertyChanged(nameof(ClickThroughHotkeyStatus));
            }
        };
        OverlayHotkey = settings.OverlayHotkey;
        store.CustomizationApplied += _ => OnPropertyChanged(nameof(CustomizationSummary));
        _store = store;
        _client = client;
        _settings = settings;
        _fans = fans;
        store.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SensorStore.PollIntervalMs))
            {
                PollIntervalMs = store.PollIntervalMs;
            }
            else if (e.PropertyName == nameof(SensorStore.CollectorStatus))
            {
                OnCollectorStatus(store.CollectorStatus);
            }
            else if (e.PropertyName == nameof(SensorStore.ConnectionState))
            {
                OnPropertyChanged(nameof(CollectorModeText));
            }
        };
        UpdateFanControlLocation();
    }

    public IReadOnlyList<TemperatureUnit> TemperatureUnits { get; } = [TemperatureUnit.Celsius, TemperatureUnit.Fahrenheit];

    public IReadOnlyList<SkinChoice> Skins => SkinManager.Available;

    /// <summary>The look. Applies immediately (no restart) and is remembered.</summary>
    public SkinChoice SelectedSkin
    {
        get => SkinManager.Available.FirstOrDefault(s => s.Id == SkinManager.Current) ?? SkinManager.Available[0];
        set
        {
            if (value is null || value.Id == SkinManager.Current)
            {
                return;
            }

            SkinManager.Apply(value.Id);
            _settings.Skin = value.Id;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    public bool CloseToTray
    {
        get => _settings.CloseToTray;
        set
        {
            _settings.CloseToTray = value;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    public bool MinimizeToTray
    {
        get => _settings.MinimizeToTray;
        set
        {
            _settings.MinimizeToTray = value;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    /// <summary>Starts SensorDeck in the tray when you sign in to Windows.</summary>
    public bool StartWithWindows
    {
        get => AppLifetime.StartsWithWindows;
        set
        {
            if (AppMode.IsReplay)
            {
                StartupStatus = "Not available in a replay window.";
                OnPropertyChanged();
                return;
            }

            StartupStatus = AppLifetime.SetStartWithWindows(value) ? null : "Couldn't change the Windows startup entry.";
            OnPropertyChanged();
        }
    }

    [ObservableProperty]
    private string? _startupStatus;

    [ObservableProperty]
    private string? _backupStatus;

    /// <summary>A replay window has its own throwaway profile, so backups are only offered in the real one.</summary>
    public bool CanBackup => !AppMode.IsReplay;

    [RelayCommand]
    private void SaveBackup()
    {
        var path = _dialogs.PickSaveFile("Save backup", $"SensorDeck backup {DateTime.Now:yyyy-MM-dd}", "SensorDeck backup (*.zip)|*.zip");
        if (path is null)
        {
            return;
        }

        try
        {
            var files = BackupService.Create(path, _settings);
            BackupStatus = $"Saved {Path.GetFileName(path)} ({files} files).";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            BackupStatus = $"Couldn't save the backup: {ex.Message}";
        }
    }

    [RelayCommand]
    private void RestoreBackup()
    {
        var path = _dialogs.PickOpenFile("Restore from backup", "SensorDeck backup (*.zip)|*.zip|All files (*.*)|*.*");
        if (path is null)
        {
            return;
        }

        BackupInfo info;
        try
        {
            info = BackupService.Inspect(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            BackupStatus = ex.Message;
            return;
        }

        var from = info.Created is { } created ? $" from {created.LocalDateTime:g}" : "";
        var machine = string.IsNullOrEmpty(info.Machine) ? "" : $" ({info.Machine})";
        if (!_dialogs.Confirm(
                $"Restore the backup{from}{machine}?\n\nIt replaces your dashboards, alert rules, sensor names and settings ({info.Files} files). " +
                "Your current files are kept in a \"before-restore\" folder, paired network devices stay paired, and SensorDeck restarts."))
        {
            return;
        }

        try
        {
            BackupService.Stage(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            BackupStatus = $"Couldn't restore: {ex.Message}";
            return;
        }

        if (!BackupService.Restart())
        {
            BackupStatus = "Ready to restore: close SensorDeck (tray → Exit) and start it again to finish.";
            return;
        }

        _dialogs.SaveDashboardWindowsForExit();
        AppLifetime.IsExiting = true;
        System.Windows.Application.Current?.Shutdown();
    }

    [ObservableProperty]
    private string? _profileStatus;

    /// <summary>"3 sensors renamed, hidden or corrected".</summary>
    public string CustomizationSummary => _store.Customizations.Count switch
    {
        0 => "No sensors customized yet. Double-click a sensor on All sensors to rename, hide or correct it.",
        1 => "1 sensor renamed, hidden or corrected.",
        var n => $"{n} sensors renamed, hidden or corrected.",
    };

    private const string ProfileFilter = "SensorDeck board profile (*.json)|*.json|All files (*.*)|*.*";

    [RelayCommand]
    private void ExportBoardProfile()
    {
        var board = _store.MotherboardName ?? "My board";
        var path = _dialogs.PickSaveFile("Export board profile", board, ProfileFilter);
        if (path is null)
        {
            return;
        }

        try
        {
            SensorDeck.Core.Customization.CustomizationStore.ExportProfile(path, _store.Customizations.ExportProfile(board));
            ProfileStatus = $"Saved {_store.Customizations.Count} sensor settings for {board}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ProfileStatus = $"Couldn't save: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ImportBoardProfile()
    {
        var path = _dialogs.PickOpenFile("Import board profile", ProfileFilter);
        if (path is null)
        {
            return;
        }

        try
        {
            var profile = SensorDeck.Core.Customization.CustomizationStore.ImportProfile(path);
            var board = _store.MotherboardName;
            if (board is not null && !string.Equals(profile.Board, board, StringComparison.OrdinalIgnoreCase)
                && !_dialogs.Confirm($"This profile was made for \"{profile.Board}\", but this PC has \"{board}\". Sensor ids may not match. Import anyway?"))
            {
                return;
            }

            var count = _store.Customizations.Import(profile);
            ProfileStatus = $"Imported {count} sensor settings from {profile.Board}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ProfileStatus = $"Couldn't import: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ResetCustomizations()
    {
        if (_dialogs.Confirm("Remove every sensor rename, hidden flag and correction?"))
        {
            _store.Customizations.ResetAll();
            ProfileStatus = "All sensor customizations removed.";
        }
    }

    public TemperatureUnit TemperatureUnit
    {
        get => _store.TemperatureUnit;
        set
        {
            _store.TemperatureUnit = value;
            OnPropertyChanged();
        }
    }

    public SensorStore Store => _store;

    // ── Appearance (accent, size, clock) ──────────────────────────────

    public IReadOnlyList<AccentChoice> Accents { get; } =
    [
        new("Skin default", null),
        new("Orange", "#C2410C"),
        new("Red", "#DC2626"),
        new("Amber", "#D97706"),
        new("Green", "#16A34A"),
        new("Teal", "#0D9488"),
        new("Blue", "#2563EB"),
        new("Indigo", "#4F46E5"),
        new("Purple", "#9333EA"),
        new("Pink", "#DB2777"),
    ];

    public AccentChoice Accent
    {
        get => Accents.FirstOrDefault(a => string.Equals(a.Hex, _settings.AccentColor, StringComparison.OrdinalIgnoreCase)) ?? Accents[0];
        set
        {
            _settings.AccentColor = value?.Hex;
            _settings.Save();
            SkinManager.SetAccent(Appearance.TryParseColor(value?.Hex, out var c) ? c : null);
            OnPropertyChanged();
        }
    }

    public IReadOnlyList<Option<double>> Scales { get; } =
        [new("80 %", 0.8), new("90 %", 0.9), new("100 %", 1.0), new("110 %", 1.1), new("125 %", 1.25), new("150 %", 1.5)];

    public Option<double> Scale
    {
        get => Scales.OrderBy(s => Math.Abs(s.Value - _settings.UiScale)).First();
        set
        {
            _settings.UiScale = value?.Value ?? 1.0;
            _settings.Save();
            Appearance.Set(_settings.UiScale, _settings.ClockFormat);
            OnPropertyChanged();
        }
    }

    public IReadOnlyList<Option<string>> ClockFormats { get; } =
        [new("Windows setting", "system"), new("12-hour", "12"), new("24-hour", "24")];

    public Option<string> ClockFormat
    {
        get => ClockFormats.FirstOrDefault(c => c.Value == _settings.ClockFormat) ?? ClockFormats[0];
        set
        {
            _settings.ClockFormat = value?.Value ?? "system";
            _settings.Save();
            Appearance.Set(_settings.UiScale, _settings.ClockFormat);
            OnPropertyChanged();
        }
    }

    private readonly GlobalHotkeyService _hotkeys;

    /// <summary>Network dashboard + phone notifications section.</summary>
    public RemoteSettingsViewModel Remote { get; }

    /// <summary>Screen-edge sidebar section.</summary>
    public SidebarSettingsViewModel Sidebar { get; }

    [ObservableProperty]
    private string _overlayHotkey = "";

    /// <summary>Whether the shortcut is registered (or why not).</summary>
    public string HotkeyStatus => _hotkeys.Status;

    [RelayCommand]
    private void ApplyHotkey()
    {
        _hotkeys.Apply(OverlayHotkey);
        _hotkeys.ApplyClickThrough(ClickThroughHotkey);
        OverlayHotkey = _settings.OverlayHotkey;
        ClickThroughHotkey = _settings.ClickThroughHotkey;
    }

    [ObservableProperty]
    private string _clickThroughHotkey = "";

    public string ClickThroughHotkeyStatus => _hotkeys.ClickThroughStatus;

    private bool _syncingGroups;
    private bool _storageEnabled;
    private bool _networkEnabled;
    private bool _controllersEnabled;

    /// <summary>The connected collector reports and saves its settings (protocol v2+).</summary>
    public bool CollectorSupportsSettings => _store.CollectorStatus is not null;

    /// <summary>"Running as a Windows service · settings in C:\ProgramData\…" or a hint when the collector is older.</summary>
    public string CollectorModeText => _store.CollectorStatus is { } s
        ? $"{(s.IsService ? "Running as a Windows service" : "Running in a terminal (not as a service)")}{(s.IsElevated ? "" : " · not as administrator")} · settings saved to {s.ConfigPath}"
        : _store.ConnectionState == ConnectionState.Connected && _store.CollectorProtocolVersion >= ProtocolInfo.SettingsVersion
            ? "Reading collector settings…"
            : _store.ConnectionState == ConnectionState.Connected
            ? "This collector is an older version: rebuild and restart it to change sensor groups here."
            : "Not connected to the collector.";

    /// <summary>Drives (SMART). Can wake sleeping hard drives.</summary>
    public bool StorageEnabled
    {
        get => _storageEnabled;
        set => SetGroup(ref _storageEnabled, value, new SetConfigRequest(null, value, null, Persist: true));
    }

    public bool NetworkEnabled
    {
        get => _networkEnabled;
        set => SetGroup(ref _networkEnabled, value, new SetConfigRequest(null, null, value, Persist: true));
    }

    /// <summary>USB fan/AIO controllers LHM supports. Off when FanControl (or its plugins) owns them.</summary>
    public bool ControllersEnabled
    {
        get => _controllersEnabled;
        set => SetGroup(ref _controllersEnabled, value, new SetConfigRequest(null, null, null, ControllersEnabled: value, Persist: true));
    }

    private void SetGroup(ref bool field, bool value, SetConfigRequest request, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (field == value)
        {
            return;
        }

        field = value;
        OnPropertyChanged(name);
        if (!_syncingGroups)
        {
            _ = SendGroupChangeAsync(request);
        }
    }

    private async Task SendGroupChangeAsync(SetConfigRequest request)
    {
        try
        {
            await _client.SetConfigAsync(request);
            PollStatus = "Saved. The collector reopens the hardware, so the sensor list refreshes in a moment.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or ObjectDisposedException)
        {
            PollStatus = "Couldn't reach the collector service.";
            OnCollectorStatus(_store.CollectorStatus); // put the switches back
        }
    }

    private void OnCollectorStatus(CollectorStatusMessage? status)
    {
        _syncingGroups = true;
        try
        {
            StorageEnabled = status?.StorageEnabled ?? false;
            NetworkEnabled = status?.NetworkEnabled ?? false;
            ControllersEnabled = status?.ControllersEnabled ?? false;
        }
        finally
        {
            _syncingGroups = false;
        }

        if (status?.ConfigError is { } error)
        {
            PollStatus = error;
        }

        OnPropertyChanged(nameof(CollectorSupportsSettings));
        OnPropertyChanged(nameof(CollectorModeText));
    }

    [RelayCommand]
    private async Task ApplyPollIntervalAsync()
    {
        try
        {
            // Persist: the collector saves it to collector.json, so it survives restarts (older collectors ignore the flag).
            await _client.SetConfigAsync(new SetConfigRequest(PollIntervalMs, null, null, Persist: true));
            PollStatus = CollectorSupportsSettings
                ? $"Saved: the collector reads sensors every {PollIntervalMs} ms."
                : $"The collector now reads sensors every {PollIntervalMs} ms until it restarts (update the collector to save it).";
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or ObjectDisposedException)
        {
            PollStatus = "Couldn't reach the collector service.";
        }
    }

    [RelayCommand]
    private void BrowseFanControl()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Locate FanControl.exe",
            Filter = "FanControl|FanControl.exe|Programs (*.exe)|*.exe",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() == true)
        {
            _settings.FanControlExePath = dialog.FileName;
            _settings.Save();
            UpdateFanControlLocation();
        }
    }

    [RelayCommand]
    private void OpenLogs()
    {
        var logs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SensorDeck", "logs");
        if (!Directory.Exists(logs))
        {
            PollStatus = "No collector logs yet. They appear once the collector has run.";
            return;
        }

        CollectorServiceStatus.OpenUrl(logs);
    }

    private void UpdateFanControlLocation()
    {
        _fans.Refresh();
        FanControlLocation = _fans.Install is { } install
            ? $"{install.ExePath} ({_fans.Configs.Count} configs)"
            : _fans.Rejected is { } rejected
                ? $"Found but not used automatically: {rejected}. Browse to FanControl.exe to use it anyway."
                : "Not found. Start FanControl, or browse to FanControl.exe.";
    }
}

/// <summary>An accent color choice (null hex = the skin's own).</summary>
public sealed record AccentChoice(string Name, string? Hex)
{
    public System.Windows.Media.Brush Brush => Appearance.TryParseColor(Hex, out var c)
        ? new System.Windows.Media.SolidColorBrush(c)
        : SensorDeck.App.Themes.SkinManager.SkinAccent;
}
