using System.Collections.Specialized;
using SpaceDeck.App.ViewModels;
using SpaceDeck.App.Views;

namespace SpaceDeck.App.Services;

/// <summary>Opens, closes and updates the screen-edge sidebar from <see cref="AppSettings.Sidebar"/>.</summary>
public sealed class SidebarService
{
    private readonly AppSettings _settings;
    private readonly DashboardViewModel _dashboards;
    private SidebarWindow? _window;

    public SidebarService(AppSettings settings, DashboardViewModel dashboards)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(dashboards);
        _settings = settings;
        _dashboards = dashboards;

        // A deleted dashboard, or the first one arriving on a fresh install.
        dashboards.Dashboards.CollectionChanged += (_, e) =>
        {
            if (e.Action != NotifyCollectionChangedAction.Move)
            {
                _window?.ShowDashboard(Resolve());
            }
        };
    }

    public SidebarSettings Settings => _settings.Sidebar;

    public bool IsOpen => _window is not null;

    /// <summary>Raised when the sidebar was turned on or off, or its settings changed (for the Settings page).</summary>
    public event Action? Changed;

    /// <summary>Shows, updates or closes the sidebar to match the settings.</summary>
    public void Apply()
    {
        if (!Settings.Enabled)
        {
            Close();
            Changed?.Invoke();
            return;
        }

        if (_window is null)
        {
            var window = new SidebarWindow(Settings, SaveAndNotify, () => _dashboards.Dashboards, () => SetEnabled(false));
            window.Closed += (_, _) =>
            {
                if (!ReferenceEquals(_window, window))
                {
                    return; // closed by this service
                }

                _window = null;
                if (!AppLifetime.IsExiting)
                {
                    // Closed some other way (Alt+F4): treat it as turning the sidebar off, so everything agrees.
                    Settings.Enabled = false;
                    SaveAndNotify();
                }
            };
            _window = window;
            window.ShowDashboard(Resolve());
            window.Show();
        }
        else
        {
            _window.ShowDashboard(Resolve());
            _window.Change(() => { });
        }

        Changed?.Invoke();
    }

    public void SetEnabled(bool enabled)
    {
        Settings.Enabled = enabled;
        Save();
        Apply();
    }

    /// <summary>Changes a setting, saves and re-docks.</summary>
    public void Update(Action<SidebarSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        change(Settings);
        Save();
        Apply();
    }

    public DashboardDocumentViewModel? Resolve() =>
        _dashboards.Dashboards.FirstOrDefault(d => d.Id == Settings.DashboardId) ?? _dashboards.Dashboards.FirstOrDefault();

    private void Close()
    {
        var window = _window;
        _window = null;
        window?.Close();
    }

    private void Save() => _settings.Save();

    /// <summary>Saves and tells the Settings page (changes made from the sidebar's own menu).</summary>
    private void SaveAndNotify()
    {
        Save();
        Changed?.Invoke();
    }
}
