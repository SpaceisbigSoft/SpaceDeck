using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SpaceDeck.App.Services;
using SpaceDeck.Core.Dashboards;

namespace SpaceDeck.App.ViewModels;

/// <summary>
/// The Dashboard page: one or more user-built dashboards shown as tabs, each a grid of tiles. The first one is
/// generated from the detected hardware; after that everything is the user's: add, move, resize, restyle, and
/// keep several dashboards ("Main", "Gaming", "Second monitor").
/// </summary>
public sealed partial class DashboardViewModel : ObservableObject
{
    private readonly DashboardServices _services;
    private readonly AppSettings _settings;

    [ObservableProperty]
    private DashboardDocumentViewModel? _selected;

    [ObservableProperty]
    private bool _isEditing;

    public DashboardViewModel(SensorStore store, FanProfileService fanProfiles, DashboardRepository repository, DialogService dialogs, AppSettings settings, SensorLinker linker)
    {
        ArgumentNullException.ThrowIfNull(store);
        _services = new DashboardServices(store, fanProfiles, repository, dialogs, linker);
        _settings = settings;

        foreach (var doc in repository.LoadAll())
        {
            Dashboards.Add(new DashboardDocumentViewModel(doc, _services));
        }

        Selected = Dashboards.FirstOrDefault(d => d.Id == settings.LastDashboard) ?? Dashboards.FirstOrDefault();
        _services.Dialogs.AllDashboards = () => Dashboards;
        _services.Dialogs.SelectedDashboard = () => Selected;
        store.CatalogApplied += OnCatalogApplied;

        // Renamed/corrected sensors: rebuild tiles so titles and chart labels follow.
        store.CustomizationApplied += _ =>
        {
            foreach (var dashboard in Dashboards)
            {
                dashboard.Rebuild();
            }
        };

        linker.Relinked += (oldId, newId) =>
        {
            foreach (var dashboard in Dashboards)
            {
                dashboard.ReplaceSensor(oldId, newId);
            }
        };

        // Chart colors and number sizes are taken from the skin when tiles are built.
        Themes.SkinManager.SkinChanged += () =>
        {
            foreach (var dashboard in Dashboards)
            {
                dashboard.Rebuild();
            }
        };
    }

    public ObservableCollection<DashboardDocumentViewModel> Dashboards { get; } = [];

    public SensorStore Store => _services.Store;

    /// <summary>True until the first catalog arrives when there's no saved dashboard to show yet.</summary>
    public bool IsWaiting => Dashboards.Count == 0;

    partial void OnSelectedChanged(DashboardDocumentViewModel? oldValue, DashboardDocumentViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsEditing = false;
        }

        if (newValue is not null)
        {
            newValue.IsEditing = IsEditing;
            _settings.LastDashboard = newValue.Id;
            _settings.Save();
        }
    }

    partial void OnIsEditingChanged(bool value)
    {
        if (Selected is not null)
        {
            Selected.IsEditing = value;
        }
    }

    private void OnCatalogApplied()
    {
        if (Dashboards.Count == 0 && _services.Store.Sensors.Count > 0)
        {
            var doc = DefaultDashboard.Create(_services.Store.Sensors);
            var vm = new DashboardDocumentViewModel(doc, _services);
            vm.Save();
            Dashboards.Add(vm);
            Selected = vm;
            OnPropertyChanged(nameof(IsWaiting));
            return;
        }

        foreach (var dashboard in Dashboards)
        {
            dashboard.Rebuild();
        }
    }

    /// <summary>Reopens the dashboard windows that were open when the app last exited (e.g. on a second monitor).</summary>
    public void RestoreWindows()
    {
        foreach (var dashboard in Dashboards.ToList())
        {
            if (_settings.DashboardWindows.TryGetValue(dashboard.Id, out var state) && state.Open)
            {
                _services.Dialogs.OpenDashboardWindow(dashboard);
            }
        }
    }

    [RelayCommand]
    private void ToggleEdit() => IsEditing = !IsEditing;

    [RelayCommand]
    private void AddTile()
    {
        if (Selected is null)
        {
            return;
        }

        IsEditing = true;
        Selected.AddTile();
    }

    [RelayCommand]
    private void NewDashboard()
    {
        var name = _services.Dialogs.Prompt("New dashboard", "Name", UniqueName("Dashboard"));
        if (name is null)
        {
            return;
        }

        var vm = Add(new DashboardDocument { Name = name });
        Selected = vm;
        IsEditing = true;
    }

    [RelayCommand]
    private void RenameDashboard()
    {
        if (Selected is null)
        {
            return;
        }

        var name = _services.Dialogs.Prompt("Rename dashboard", "Name", Selected.Name);
        if (name is not null)
        {
            Selected.Name = name;
        }
    }

    [RelayCommand]
    private void DuplicateDashboard()
    {
        if (Selected is null)
        {
            return;
        }

        Selected.Save();
        Selected = Add(Selected.Document.Clone(UniqueName(Selected.Name + " copy")));
    }

    [RelayCommand]
    private void DeleteDashboard()
    {
        if (Selected is not { } doomed
            || !_services.Dialogs.Confirm($"Delete the dashboard \"{doomed.Name}\"? This can't be undone."))
        {
            return;
        }

        var index = Dashboards.IndexOf(doomed);
        try
        {
            _services.Repository.Delete(doomed.Id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _services.Dialogs.ShowError($"Couldn't delete the dashboard: {ex.Message}");
            return;
        }

        _services.Dialogs.CloseDashboardWindow(doomed);
        Dashboards.Remove(doomed);
        doomed.Dispose();

        if (Dashboards.Count == 0)
        {
            // Never leave the page empty: start over from the detected hardware.
            Selected = Add(DefaultDashboard.Create(_services.Store.Sensors));
        }
        else
        {
            Selected = Dashboards[Math.Min(index, Dashboards.Count - 1)];
        }

        SaveOrder();
    }

    [RelayCommand]
    private void ResetDashboard()
    {
        if (Selected is null)
        {
            return;
        }

        if (_services.Store.Sensors.Count == 0)
        {
            _services.Dialogs.ShowError("The default layout is built from the detected sensors. Wait until the collector is connected, then try again.");
            return;
        }

        if (!_services.Dialogs.Confirm($"Replace every tile on \"{Selected.Name}\" with the default layout for this PC?"))
        {
            return;
        }

        Selected.Document.Tiles = DefaultDashboard.Create(_services.Store.Sensors).Tiles;
        Selected.Rebuild();
        Selected.Save();
    }

    [RelayCommand]
    private void ExportDashboard()
    {
        if (Selected is null)
        {
            return;
        }

        var path = _services.Dialogs.PickSaveFile(Selected.Name);
        if (path is null)
        {
            return;
        }

        Selected.Save();
        try
        {
            DashboardRepository.Export(Selected.Document, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _services.Dialogs.ShowError($"Couldn't export: {ex.Message}");
        }
    }

    [RelayCommand]
    private void ImportDashboard()
    {
        var path = _services.Dialogs.PickOpenFile();
        if (path is null)
        {
            return;
        }

        try
        {
            var doc = DashboardRepository.Import(path);
            doc.Name = UniqueName(doc.Name);
            Selected = Add(doc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _services.Dialogs.ShowError($"Couldn't import: {ex.Message}");
        }
    }

    /// <summary>Restarts min/max for the sensors on this dashboard.</summary>
    [RelayCommand]
    private async Task ResetMinMaxAsync()
    {
        if (Selected is null)
        {
            return;
        }

        if (!_services.Store.SupportsMinMaxReset)
        {
            _services.Dialogs.ShowError("Resetting min/max needs the updated collector: rebuild and restart it.");
            return;
        }

        var ids = Selected.Document.Tiles.SelectMany(t => t.Sensors).Distinct(StringComparer.Ordinal).ToList();
        if (ids.Count > 0 && !await _services.Store.ResetMinMaxAsync(ids))
        {
            _services.Dialogs.ShowError("Couldn't reach the collector.");
        }
    }

    [RelayCommand]
    private void OpenWindow()
    {
        if (Selected is not null)
        {
            _services.Dialogs.OpenDashboardWindow(Selected);
        }
    }

    [RelayCommand]
    private void MoveLeft() => Move(-1);

    [RelayCommand]
    private void MoveRight() => Move(1);

    private void Move(int delta)
    {
        if (Selected is null)
        {
            return;
        }

        var from = Dashboards.IndexOf(Selected);
        var to = Math.Clamp(from + delta, 0, Dashboards.Count - 1);
        if (from != to)
        {
            Dashboards.Move(from, to);
            SaveOrder();
        }
    }

    /// <summary>Adds a ready-made dashboard (e.g. from the sidebar builder) under a unique name.</summary>
    public DashboardDocumentViewModel AddDashboard(DashboardDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        doc.Name = UniqueName(doc.Name);
        return Add(doc);
    }

    private DashboardDocumentViewModel Add(DashboardDocument doc)
    {
        doc.Order = Dashboards.Count;
        var vm = new DashboardDocumentViewModel(doc, _services);
        vm.Save();
        Dashboards.Add(vm);
        OnPropertyChanged(nameof(IsWaiting));
        return vm;
    }

    private void SaveOrder()
    {
        for (var i = 0; i < Dashboards.Count; i++)
        {
            if (Dashboards[i].Document.Order != i)
            {
                Dashboards[i].Document.Order = i;
                Dashboards[i].Save();
            }
        }
    }

    private string UniqueName(string name)
    {
        var candidate = name;
        for (var n = 2; Dashboards.Any(d => d.Name.Equals(candidate, StringComparison.CurrentCultureIgnoreCase)); n++)
        {
            candidate = $"{name} {n}";
        }

        return candidate;
    }
}
