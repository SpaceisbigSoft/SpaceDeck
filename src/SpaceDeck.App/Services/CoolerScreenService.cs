using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SpaceDeck.App.ViewModels;
using SpaceDeck.Core.Dashboards;
using SpaceDeck.Core.Protocol;
using SpaceDeck.Core.Screens;

namespace SpaceDeck.App.Services;

/// <summary>
/// Cooler screens (<c>docs/SCREEN-PIPE.md</c>): one <see cref="CoolerScreenViewModel"/> per screen the collector reports,
/// each drawing its dashboard and sending it while it is on and the collector is connected. Settings → Cooler screen
/// shows them. UI thread.
/// </summary>
/// <remarks>
/// Nothing is sent unless the user turned a screen on. Frames stop when a screen is turned off, its dashboard is
/// deleted, the collector disconnects (or is too old) and when the app exits; nothing is ever re-sent on its own, so the
/// cooler shows its own screen again about 20 s later. A replay window never draws on a cooler.
/// </remarks>
public sealed class CoolerScreenService : ObservableObject, IDisposable
{
    private readonly SensorStore _store;
    private readonly DashboardViewModel _dashboards;
    private readonly AppSettings _settings;
    private readonly DialogService _dialogs;
    private bool _disposed;

    public CoolerScreenService(SensorStore store, DashboardViewModel dashboards, AppSettings settings, DialogService dialogs)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(dashboards);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(dialogs);
        _store = store;
        _dashboards = dashboards;
        _settings = settings;
        _dialogs = dialogs;
        if (AppMode.IsReplay)
        {
            return;
        }

        store.PropertyChanged += OnStoreChanged;
        store.CatalogApplied += Refresh;
        dashboards.Dashboards.CollectionChanged += OnDashboardsChanged;
        Refresh();
    }

    /// <summary>"Edit layout" in a cooler screen's row: the main window shows that dashboard in edit mode.</summary>
    public event Action<DashboardDocumentViewModel>? EditLayoutRequested;

    public ObservableCollection<CoolerScreenViewModel> Screens { get; } = [];

    public bool IsEmpty => Screens.Count == 0;

    public ObservableCollection<DashboardDocumentViewModel> Dashboards => _dashboards.Dashboards;

    /// <summary>The collector is connected and takes frames.</summary>
    internal bool CanSend => !_disposed && _store.SupportsScreens;

    /// <summary>Why nothing can be sent right now (for a screen that is on), or null.</summary>
    internal string? Blocked => _disposed ? "Off"
        : _store.ConnectionState != ConnectionState.Connected ? "Waiting for the collector…"
        : _store.CollectorProtocolVersion < ProtocolInfo.ScreenVersion ? "The collector is older than this app: update it to use cooler screens."
        : null;

    internal void Save() => _settings.Save();

    internal DashboardDocumentViewModel? FindDashboard(string? id) =>
        id is null ? null : _dashboards.Dashboards.FirstOrDefault(d => d.Id == id);

    /// <summary>Makes the built-in "Cooler screen" dashboard for this screen; null while no sensors are known yet.</summary>
    internal DashboardDocumentViewModel? CreateDefaultDashboard(ScreenTarget target)
    {
        if (_store.Sensors.Count == 0)
        {
            return null;
        }

        return _dashboards.AddDashboard(DefaultDashboard.CreateCoolerScreen(_store.Sensors, target.HardwareId, target.Width, target.Height));
    }

    /// <summary>
    /// After asking: replaces <paramref name="dashboard"/>'s tiles with the built-in layout for this screen (and makes it
    /// a screen layout). Its name, background and everything else stay. False when cancelled or there are no sensors yet.
    /// </summary>
    internal bool ResetLayout(DashboardDocumentViewModel dashboard, ScreenTarget target)
    {
        ArgumentNullException.ThrowIfNull(dashboard);
        if (_store.Sensors.Count == 0)
        {
            _dialogs.ShowError("The default layout is built from the detected sensors. Wait until the collector is connected, then try again.");
            return false;
        }

        if (!_dialogs.Confirm($"Replace every tile on \"{dashboard.Name}\" with the default cooler screen layout (large numbers filling the screen)?"))
        {
            return false;
        }

        dashboard.ApplyScreenLayout(DefaultDashboard.CreateCoolerScreen(_store.Sensors, target.HardwareId, target.Width, target.Height));
        return true;
    }

    internal void RequestEditLayout(DashboardDocumentViewModel dashboard) => EditLayoutRequested?.Invoke(dashboard);

    internal string? PickFile(string title, string filter) => _dialogs.PickOpenFile(title, filter);

    internal string? PickFolder(string title) => _dialogs.PickFolder(title);

    /// <summary>
    /// Tells each dashboard whether a cooler screen shows it (<see cref="DashboardDocumentViewModel.ScreenUse"/>): one
    /// that fits the screen's area is then edited in the square frame and drawn as that area. Once set it stays while the
    /// screen shows the dashboard, so dragging a tile out of the frame doesn't take the frame away.
    /// </summary>
    internal void UpdateScreenUse()
    {
        foreach (var dashboard in _dashboards.Dashboards)
        {
            var screen = Screens.FirstOrDefault(s => s.ShowsDashboard(dashboard));
            if (screen is null)
            {
                dashboard.ScreenUse = null;
                continue;
            }

            if (dashboard.ScreenUse is null)
            {
                var size = new DashboardScreen { Width = screen.Target.Width, Height = screen.Target.Height };
                if (dashboard.Document.Screen is not null || ScreenLayoutGrid.For(size).Contains(dashboard.Document.Tiles))
                {
                    dashboard.ScreenUse = size;
                }
            }
        }
    }

    internal Task SendAsync(string hardwareId, byte[] jpeg) => _store.Client.SendScreenFrameAsync(hardwareId, jpeg);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _store.PropertyChanged -= OnStoreChanged;
        _store.CatalogApplied -= Refresh;
        _dashboards.Dashboards.CollectionChanged -= OnDashboardsChanged;
        foreach (var screen in Screens)
        {
            screen.Dispose();
        }

    }

    private void OnStoreChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SensorStore.Screens) or nameof(SensorStore.ConnectionState)
            or nameof(SensorStore.CollectorProtocolVersion) or nameof(SensorStore.SupportsScreens))
        {
            Refresh();
        }
    }

    private void OnDashboardsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var screen in Screens)
        {
            screen.OnDashboardsChanged();
        }

        UpdateScreenUse();
    }

    /// <summary>One row per reported screen (settings are kept for screens that are away), then each starts or stops.</summary>
    private void Refresh()
    {
        if (_disposed)
        {
            return;
        }

        var reported = _store.Screens;
        foreach (var gone in Screens.Where(s => reported.All(r => r.HardwareId != s.HardwareId)).ToList())
        {
            gone.Dispose();
            Screens.Remove(gone);
        }

        foreach (var target in reported)
        {
            if (Screens.FirstOrDefault(s => s.HardwareId == target.HardwareId) is { } existing)
            {
                existing.Target = target;
                continue;
            }

            var settings = _settings.CoolerScreens.FirstOrDefault(s => s.HardwareId == target.HardwareId);
            if (settings is null)
            {
                settings = new CoolerScreenSettings { HardwareId = target.HardwareId };
                _settings.CoolerScreens.Add(settings); // saved with the first change
            }

            Screens.Add(new CoolerScreenViewModel(this, settings, target));
        }

        OnPropertyChanged(nameof(IsEmpty));
        foreach (var screen in Screens)
        {
            screen.UpdateRunning();
        }

        UpdateScreenUse();
    }
}
