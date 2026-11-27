using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SpaceDeck.App.ViewModels;
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
    private bool _disposed;

    public CoolerScreenService(SensorStore store, DashboardViewModel dashboards, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(dashboards);
        ArgumentNullException.ThrowIfNull(settings);
        _store = store;
        _dashboards = dashboards;
        _settings = settings;
        if (AppMode.IsReplay)
        {
            return;
        }

        store.PropertyChanged += OnStoreChanged;
        store.CatalogApplied += Refresh;
        dashboards.Dashboards.CollectionChanged += OnDashboardsChanged;
        Refresh();
    }

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
    internal DashboardDocumentViewModel? CreateDefaultDashboard(string hardwareId)
    {
        if (_store.Sensors.Count == 0)
        {
            return null;
        }

        return _dashboards.AddDashboard(DefaultDashboard.CreateCoolerScreen(_store.Sensors, hardwareId));
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
    }
}
