using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SpaceDeck.App.Services;
using SpaceDeck.Core.Model;
using SpaceDeck.Core.Protocol;
using SpaceDeck.Integrations.FanControl;

namespace SpaceDeck.App.ViewModels;

/// <summary>An entry in the left navigation.</summary>
/// <param name="Glyph">Segoe Fluent Icons / MDL2 code point.</param>
public sealed record NavItem(string Key, string Title, string Glyph, object Page);

public sealed partial class MainViewModel : ObservableObject
{
    private const string PawnIoUrl = "https://pawnio.eu";

    private readonly SensorStore _store;
    private readonly AppSettings _settings;
    private readonly FanProfileService _fans;
    private Action? _bannerAction;
    private bool _suppressProfileSwitch;

    /// <summary>False while the main window is hidden (see <see cref="SetWindowShown"/>).</summary>
    private bool _windowShown = true;

    [ObservableProperty]
    private NavItem? _selectedNav;

    [ObservableProperty]
    private bool _showBanner;

    [ObservableProperty]
    private string _bannerText = "";

    [ObservableProperty]
    private string? _bannerActionText;

    [ObservableProperty]
    private bool _bannerIsError;

    [ObservableProperty]
    private string _connectionText = "Connecting…";

    [ObservableProperty]
    private string? _selectedFanProfile;

    [ObservableProperty]
    private string? _fanProfileStatus;

    [ObservableProperty]
    private bool _fanControlAvailable;

    public MainViewModel(
        SensorStore store,
        AppSettings settings,
        FanProfileService fans,
        DashboardViewModel dashboard,
        SensorsViewModel sensors,
        VoltagesViewModel voltages,
        FansViewModel fansPage,
        HistoryViewModel history,
        RecordingsViewModel recordings,
        AlertsViewModel alerts,
        AutomationViewModel automation,
        SettingsViewModel settingsPage,
        DiagnosticsViewModel diagnostics,
        AboutViewModel about,
        TrayService tray)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(settings);
        _store = store;
        _settings = settings;
        _fans = fans;

        NavItems =
        [
            new NavItem("overview", "Dashboard", "\uE80F", dashboard),
            new NavItem("sensors", "All sensors", "\uE8FD", sensors),
            new NavItem("voltages", "Voltages", "\uE945", voltages),
            new NavItem("fans", "Fans & pump", "\uE9CA", fansPage),
            new NavItem("history", "History", "\uE81C", history),
            new NavItem("recordings", "Recordings", "\uE7C8", recordings),
            new NavItem("alerts", "Alerts", "\uE7BA", alerts),
            new NavItem("automation", "Automation", "\uE916", automation),
            new NavItem("settings", "Settings", "\uE713", settingsPage),
            new NavItem("diagnostics", "Diagnostics", "\uE9D9", diagnostics),
            new NavItem("about", "About", "\uE946", about),
        ];

        // First run starts on the setup checklist; "Looks good" moves on to the dashboard.
        SelectedNav = !settings.SetupCompleted
            ? NavItems.First(n => n.Key == "diagnostics")
            : NavItems.FirstOrDefault(n => n.Key == settings.LastPage) ?? NavItems[0];
        diagnostics.SetupFinished += () => SelectedNav = NavItems[0];
        diagnostics.IsPageVisible = SelectedNav?.Page == diagnostics;
        history.IsPageVisible = SelectedNav?.Page == history;

        store.PropertyChanged += OnStorePropertyChanged;
        store.CatalogApplied += UpdateConnectionText;
        fans.ProfileSwitched += OnProfileSwitched;
        tray.NotificationClicked += () => SelectedNav = NavItems.First(n => n.Key == "alerts");
        RefreshFanProfiles();
        UpdateBanner();
    }

    /// <summary>Keeps the header picker in step when a dashboard tile, automation or FanControl itself switches the profile.</summary>
    private void OnProfileSwitched(string name)
    {
        if (string.Equals(SelectedFanProfile, name, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _suppressProfileSwitch = true;
        try
        {
            SelectedFanProfile = FanProfiles.FirstOrDefault(p => p.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? SelectedFanProfile;
            FanProfileStatus = $"FanControl: {name}";
        }
        finally
        {
            _suppressProfileSwitch = false;
        }
    }

    public IReadOnlyList<NavItem> NavItems { get; }

    /// <summary>Shows the dashboard page with the n-th (0-based) dashboard tab. False if there is no such tab.</summary>
    public bool ShowDashboard(int index)
    {
        if (NavItems[0].Page is not DashboardViewModel dashboards || index < 0 || index >= dashboards.Dashboards.Count)
        {
            return false;
        }

        SelectedNav = NavItems[0];
        dashboards.Selected = dashboards.Dashboards[index];
        return true;
    }

    public SensorStore Store => _store;

    public ObservableCollection<string> FanProfiles { get; } = [];

    partial void OnSelectedNavChanged(NavItem? value)
    {
        if (value is null)
        {
            return;
        }

        if (value.Page is RecordingsViewModel recordingsPage)
        {
            recordingsPage.Refresh();
        }

        if (value.Page is AutomationViewModel automationPage)
        {
            automationPage.LoadProfiles(); // FanControl configs may have changed
        }

        UpdatePageVisibility();
        _settings.LastPage = value.Key;
        _settings.Save();
    }

    /// <summary>
    /// The main window was shown or hidden (in the tray, or never shown: started with Windows). Pages that poll
    /// (History's long range runs a disk-history query each minute; Diagnostics) run only while the window shows them.
    /// </summary>
    public void SetWindowShown(bool shown)
    {
        _windowShown = shown;
        UpdatePageVisibility();
    }

    /// <summary>Tells the polling pages whether they are on screen: the selected page of a shown window.</summary>
    private void UpdatePageVisibility()
    {
        foreach (var item in NavItems)
        {
            var visible = _windowShown && item == SelectedNav;
            if (item.Page is DiagnosticsViewModel diagnostics)
            {
                if (diagnostics.IsPageVisible != visible)
                {
                    diagnostics.IsPageVisible = visible; // true re-runs the checks, so only on a change
                }
            }
            else if (item.Page is HistoryViewModel historyPage)
            {
                if (historyPage.IsPageVisible != visible)
                {
                    historyPage.IsPageVisible = visible;
                }
            }
        }
    }

    partial void OnSelectedFanProfileChanged(string? value)
    {
        if (_suppressProfileSwitch || string.IsNullOrEmpty(value))
        {
            return;
        }

        var result = _fans.Switch(value);
        FanProfileStatus = result.Outcome switch
        {
            SwitchOutcome.Sent or SwitchOutcome.StartedWithConfig => $"FanControl: {_fans.LastProfile ?? value}",
            _ => result.Message,
        };
    }

    /// <summary>Re-reads FanControl's configs (called when the picker opens) without switching anything.</summary>
    public void RefreshFanProfiles()
    {
        _suppressProfileSwitch = true;
        try
        {
            _fans.Refresh();
            // LastProfile follows FanControl's own record when it has one (a profile picked in FanControl itself).
            var current = _fans.LastProfile ?? SelectedFanProfile;
            FanProfiles.Clear();
            foreach (var config in _fans.Configs)
            {
                FanProfiles.Add(config.Name);
            }

            FanControlAvailable = _fans.Install is not null && !AppMode.IsReplay;
            SelectedFanProfile = current is not null && FanProfiles.Contains(current) ? current : null;
            if (!FanControlAvailable)
            {
                FanProfileStatus = AppMode.IsReplay ? "Replay" : "FanControl not found";
            }
        }
        finally
        {
            _suppressProfileSwitch = false;
        }
    }

    [RelayCommand]
    private void BannerAction() => _bannerAction?.Invoke();

    private void OnStorePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SensorStore.ConnectionState) or nameof(SensorStore.SourceStatuses) or nameof(SensorStore.CollectorProtocolVersion)
            or nameof(SensorStore.ConnectionProblem))
        {
            UpdateBanner();
            UpdateConnectionText();
        }
    }

    private void UpdateConnectionText()
    {
        ConnectionText = _store.ConnectionState switch
        {
            ConnectionState.Connected when AppMode.IsReplay => $"Replay · {_store.Sensors.Count} sensors",
            ConnectionState.Connected => $"Live · {_store.Sensors.Count} sensors",
            ConnectionState.Connecting => "Connecting…",
            _ => "Collector offline",
        };
    }

    private void UpdateBanner()
    {
        // Another program answered on the collector's pipe: say so instead of "connecting" (its data isn't used).
        if (_store.ConnectionState != ConnectionState.Connected && _store.ConnectionProblem is { } refused)
        {
            SetBanner(refused, null, null, isError: true);
            return;
        }

        if (AppMode.IsReplay)
        {
            var failed = _store.SourceStatuses.FirstOrDefault(s => s.Status == SourceStatus.Error);
            if (failed is not null)
            {
                SetBanner(failed.Message ?? "The replay couldn't be loaded.", null, null, isError: true);
            }
            else if (_store.ConnectionState != ConnectionState.Connected)
            {
                SetBanner("Starting the replay…", null, null, isError: false);
            }
            else
            {
                SetBanner($"Replay of {AppMode.ReplayName}: recorded values, not this PC. Dashboards and settings here are separate from your real ones.", null, null, isError: false);
            }

            return;
        }

        if (_store.ConnectionState != ConnectionState.Connected)
        {
            switch (CollectorServiceStatus.Query())
            {
                case CollectorInstallState.NotInstalled:
                    SetBanner(
                        "The SpaceDeck collector service isn't installed. Run scripts\\Install-Collector.ps1 as administrator, or run the collector from an elevated terminal while developing.",
                        null,
                        null,
                        isError: true);
                    return;

                case CollectorInstallState.Stopped:
                    SetBanner("The SpaceDeck collector service is stopped.", "Start service", StartService, isError: true);
                    return;

                default:
                    SetBanner(_store.ConnectionState == ConnectionState.Connecting ? "Connecting to the collector…" : "Waiting for the collector…", null, null, isError: false);
                    return;
            }
        }

        // Version handshake: the app and collector must speak the same protocol for every feature to work.
        if (_store.CollectorProtocolVersion is > 0 and var collectorProtocol && collectorProtocol != ProtocolInfo.Version)
        {
            SetBanner(
                collectorProtocol < ProtocolInfo.Version
                    ? $"The collector service is older than this app (protocol {collectorProtocol}, app {ProtocolInfo.Version}), so some features are off. Update the collector: rebuild it and run scripts\\Install-Collector.ps1 again (or reinstall SpaceDeck)."
                    : $"The collector service is newer than this app (protocol {collectorProtocol}, app {ProtocolInfo.Version}). Update SpaceDeck so everything works.",
                "Details",
                () => SelectedNav = NavItems.First(n => n.Key == "diagnostics"),
                isError: false);
            return;
        }

        var problem = _store.SourceStatuses.FirstOrDefault(s => s.Status != SourceStatus.Ok);
        if (problem is null)
        {
            ShowBanner = false;
            return;
        }

        switch (problem.Status)
        {
            case SourceStatus.DriverMissing:
                SetBanner(problem.Message ?? "The PawnIO driver isn't installed, so motherboard and CPU sensors are missing.", "Get PawnIO", () => CollectorServiceStatus.OpenUrl(PawnIoUrl), isError: true);
                break;
            default:
                SetBanner(problem.Message ?? $"Sensor source '{problem.Source}': {problem.Status}.", null, null, isError: problem.Status == SourceStatus.Error);
                break;
        }
    }

    private void SetBanner(string text, string? actionText, Action? action, bool isError)
    {
        BannerText = text;
        BannerActionText = actionText;
        _bannerAction = action;
        BannerIsError = isError;
        ShowBanner = true;
    }

    private void StartService()
    {
        if (!CollectorServiceStatus.TryStart(out var error))
        {
            BannerText = $"Couldn't start the collector: {error}";
        }
    }
}
