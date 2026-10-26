using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SensorDeck.App.Services;
using SensorDeck.Core.Dashboards;

namespace SensorDeck.App.ViewModels;

/// <summary>Settings → Sidebar: a dashboard docked to a monitor edge.</summary>
public sealed partial class SidebarSettingsViewModel : ObservableObject
{
    private readonly SidebarService _sidebar;
    private readonly DashboardViewModel _dashboards;
    private readonly DialogService _dialogs;

    [ObservableProperty]
    private IReadOnlyList<MonitorInfo> _monitorList = [];

    [ObservableProperty]
    private string? _status;

    public SidebarSettingsViewModel(SidebarService sidebar, DashboardViewModel dashboards, DialogService dialogs)
    {
        ArgumentNullException.ThrowIfNull(sidebar);
        ArgumentNullException.ThrowIfNull(dashboards);
        _sidebar = sidebar;
        _dashboards = dashboards;
        _dialogs = dialogs;
        RefreshMonitors();
        sidebar.Changed += () => OnPropertyChanged(string.Empty);
    }

    private SidebarSettings S => _sidebar.Settings;

    public bool Enabled
    {
        get => S.Enabled;
        set => _sidebar.SetEnabled(value);
    }

    public ObservableCollection<DashboardDocumentViewModel> Dashboards => _dashboards.Dashboards;

    public DashboardDocumentViewModel? Dashboard
    {
        get => _sidebar.Resolve();
        set
        {
            if (value is not null && value.Id != S.DashboardId)
            {
                _sidebar.Update(s => s.DashboardId = value.Id);
            }
        }
    }

    public MonitorInfo? Monitor
    {
        get
        {
            var device = Monitors.Find(S.Monitor).DeviceName;
            return MonitorList.FirstOrDefault(m => m.DeviceName == device);
        }
        set
        {
            if (value is not null && value.DeviceName != S.Monitor)
            {
                _sidebar.Update(s => s.Monitor = value.DeviceName);
            }
        }
    }

    public IReadOnlyList<Option<SidebarEdge>> Edges { get; } = [new("Right edge", SidebarEdge.Right), new("Left edge", SidebarEdge.Left)];

    public Option<SidebarEdge>? Edge
    {
        get => Edges.FirstOrDefault(e => e.Value == S.Edge);
        set
        {
            if (value is not null && value.Value != S.Edge)
            {
                _sidebar.Update(s => s.Edge = value.Value);
            }
        }
    }

    public IReadOnlyList<Option<int>> Widths { get; } =
        [new("Narrow (2 columns)", 2), new("Normal (3 columns)", 3), new("Wide (4 columns)", 4), new("Extra wide (6 columns)", 6)];

    public Option<int>? Width
    {
        get => Widths.FirstOrDefault(w => w.Value == S.Columns) ?? Widths[1];
        set
        {
            if (value is not null && value.Value != S.Columns)
            {
                _sidebar.Update(s => s.Columns = value.Value);
            }
        }
    }

    /// <summary>"Fit to screen height" (value 0), then every manual size 70–150 % in 5 % steps.</summary>
    public IReadOnlyList<Option<double>> SizeOptions { get; } =
    [
        new("Fit to screen height", 0),
        .. Enumerable.Range(0, (int)Math.Round((ContentSize.Maximum - ContentSize.Minimum) / ContentSize.Step) + 1)
            .Select(i => ContentSize.Clamp(ContentSize.Minimum + (i * ContentSize.Step)))
            .Select(scale => new Option<double>(ContentSize.Percent(scale), scale)),
    ];

    public Option<double>? SizeOption
    {
        get
        {
            if (S.FitToHeight)
            {
                return SizeOptions[0];
            }

            var scale = ContentSize.Clamp(S.ContentScale);
            return SizeOptions.FirstOrDefault(o => o.Value > 0 && Math.Abs(o.Value - scale) < 0.001);
        }
        set
        {
            if (value is null || ReferenceEquals(value, SizeOption))
            {
                return;
            }

            var fit = value.Value <= 0;
            _sidebar.Update(s =>
            {
                s.FitToHeight = fit;
                if (!fit)
                {
                    s.ContentScale = value.Value;
                }
            });
        }
    }

    public bool ReserveSpace
    {
        get => S.ReserveSpace;
        set => _sidebar.Update(s => s.ReserveSpace = value);
    }

    public bool HideWhenFullscreen
    {
        get => S.HideWhenFullscreen;
        set => _sidebar.Update(s => s.HideWhenFullscreen = value);
    }

    [RelayCommand]
    private void RefreshMonitors()
    {
        MonitorList = Monitors.All();
        OnPropertyChanged(nameof(Monitor));
    }

    /// <summary>Opens the checklist, makes the dashboard and shows it in the sidebar (turning the sidebar on).</summary>
    [RelayCommand]
    private void Build()
    {
        // The sidebar's current dashboard is updated in place when it's a built one (only sections and the clock).
        var current = _sidebar.Resolve();
        var existing = current is not null && S.DashboardId == current.Id
            && current.Document.Tiles.Any(t => t.Type == WidgetType.Section)
            && current.Document.Tiles.All(t => t.Type is WidgetType.Section or WidgetType.Clock)
            ? current
            : null;
        if (_dialogs.BuildSidebar(S.Columns, existing?.Document) is not { } built)
        {
            return;
        }

        var (doc, replace) = built;
        DashboardDocumentViewModel dashboard;
        if (replace && existing is not null)
        {
            existing.ReplaceLayout(doc.Tiles, doc.Name);
            dashboard = existing;
        }
        else
        {
            dashboard = _dashboards.AddDashboard(doc);
        }

        var columns = doc.Tiles.Count > 0 ? doc.Tiles.Max(t => t.Col + t.ColSpan) : S.Columns;
        _sidebar.Update(s =>
        {
            s.DashboardId = dashboard.Id;
            s.Columns = Math.Clamp(columns, 2, 6);
            s.Enabled = true;
        });
        Status = replace && existing is not null
            ? $"Updated \"{dashboard.Name}\"."
            : $"Created \"{dashboard.Name}\" and showed it in the sidebar. Edit it on the Dashboard page like any other.";
    }
}
