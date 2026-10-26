using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SensorDeck.App.Services;
using SensorDeck.Core.Dashboards;

namespace SensorDeck.App.ViewModels;

/// <summary>What every dashboard needs: live sensors, FanControl, storage and dialogs.</summary>
public sealed record DashboardServices(SensorStore Store, FanProfileService FanProfiles, DashboardRepository Repository, DialogService Dialogs, SensorLinker Linker);

/// <summary>One dashboard (a tab): its tiles and the edit actions on them. Every change is saved straight away.</summary>
public sealed partial class DashboardDocumentViewModel : ObservableObject, IDisposable
{
    private readonly DashboardServices _services;

    [ObservableProperty]
    private bool _isEditing;

    public DashboardDocumentViewModel(DashboardDocument document, DashboardServices services)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(services);
        Document = document;
        _services = services;
        Rebuild();
    }

    public DashboardDocument Document { get; }

    public string Id => Document.Id;

    public string Name
    {
        get => Document.Name;
        set
        {
            var name = string.IsNullOrWhiteSpace(value) ? Document.Name : value.Trim();
            if (name != Document.Name)
            {
                Document.Name = name;
                OnPropertyChanged();
                Save();
            }
        }
    }

    public ObservableCollection<TileViewModel> Tiles { get; } = [];

    public bool IsEmpty => Tiles.Count == 0;

    /// <summary>Asks for a new heading for a tile (blank = back to the automatic one, e.g. the hardware's name).</summary>
    public void RenameTitle(TileViewModel tile)
    {
        ArgumentNullException.ThrowIfNull(tile);
        var name = _services.Dialogs.Prompt("Rename heading", "Heading (leave blank for the automatic name)", tile.Spec.Title ?? tile.Title);
        if (name is null)
        {
            return; // cancelled
        }

        tile.SyncSpec();
        tile.Spec.Title = string.IsNullOrWhiteSpace(name) || name.Trim() == tile.Title && tile.Spec.Title is null ? null : name.Trim();
        Rebuild();
        Save();
    }

    /// <summary>Hides or shows one row of a section tile (right-click → Hide), then saves.</summary>
    public void SetRowHidden(TileViewModel tile, string key, bool hidden)
    {
        ArgumentNullException.ThrowIfNull(tile);
        var rows = tile.Spec.HiddenRows ?? new List<string>();
        rows.RemoveAll(k => k == key);
        if (hidden)
        {
            rows.Add(key);
        }

        tile.Spec.HiddenRows = rows.Count > 0 ? rows : null;
        if (tile.Spec.Type == WidgetType.Section)
        {
            tile.SyncSpec();
            tile.Spec.RowSpan = FitSection(tile.Spec); // on a grid dashboard the tile shrinks/grows with its rows
            GridLayout.ResolveCollisions(Document.Tiles, tile.Spec);
        }

        Rebuild();
        Save();
    }

    /// <summary>Swaps in a whole new layout (the sidebar builder updating this dashboard) and saves it.</summary>
    public void ReplaceLayout(IEnumerable<TileSpec> tiles, string name)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        Document.Tiles = [.. tiles];
        if (!string.IsNullOrWhiteSpace(name) && name.Trim() != Document.Name)
        {
            Document.Name = name.Trim();
            OnPropertyChanged(nameof(Name));
        }

        Rebuild();
        Save();
    }

    /// <summary>Re-resolves every tile against the current sensors (new catalog, or after settings changed).</summary>
    public void Rebuild()
    {
        foreach (var tile in Tiles)
        {
            tile.Dispose();
        }

        Tiles.Clear();
        foreach (var spec in Document.Tiles)
        {
            Tiles.Add(new TileViewModel(spec, _services.Store, _services.FanProfiles));
        }

        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>
    /// Puts a tile where the user dropped it (or at its new size) and pushes anything it now overlaps further down.
    /// </summary>
    public void CommitLayout(TileViewModel tile, CellRect target)
    {
        ArgumentNullException.ThrowIfNull(tile);
        foreach (var t in Tiles)
        {
            t.SyncSpec();
        }

        tile.Spec.Col = target.Col;
        tile.Spec.Row = target.Row;
        tile.Spec.ColSpan = target.ColSpan;
        tile.Spec.RowSpan = target.RowSpan;
        GridLayout.ResolveCollisions(Document.Tiles, tile.Spec);
        foreach (var t in Tiles)
        {
            t.LoadPosition();
        }

        Save();
    }

    [RelayCommand]
    public void AddTile()
    {
        var spec = _services.Dialogs.EditTile(null);
        if (spec is null)
        {
            return;
        }

        var (colSpan, rowSpan) = TileSpec.DefaultSize(spec.Type, spec.Sensors.Count);
        spec.ColSpan = colSpan;
        spec.RowSpan = spec.Type == WidgetType.Section ? FitSection(spec) : rowSpan;
        (spec.Col, spec.Row) = GridLayout.FindFreeSpot(Document.Tiles, spec.ColSpan, spec.RowSpan);
        GridLayout.Clamp(spec);
        Document.Tiles.Add(spec);
        Tiles.Add(new TileViewModel(spec, _services.Store, _services.FanProfiles));
        OnPropertyChanged(nameof(IsEmpty));
        Save();
    }

    [RelayCommand]
    private void EditTile(TileViewModel? tile)
    {
        if (tile is null)
        {
            return;
        }

        tile.SyncSpec();
        var edited = _services.Dialogs.EditTile(tile.Spec);
        if (edited is null)
        {
            return;
        }

        // Keep id and position; a different widget may need a bigger minimum size.
        edited.Id = tile.Spec.Id;
        edited.Col = tile.Spec.Col;
        edited.Row = tile.Spec.Row;
        edited.ColSpan = tile.Spec.ColSpan;
        edited.RowSpan = edited.Type == WidgetType.Section ? FitSection(edited) : tile.Spec.RowSpan;
        GridLayout.Clamp(edited);

        var index = Document.Tiles.IndexOf(tile.Spec);
        if (index < 0)
        {
            return; // the tile went away while the dialog was open (the sidebar builder replaced the layout)
        }

        Document.Tiles[index] = edited;
        GridLayout.ResolveCollisions(Document.Tiles, edited);
        Rebuild();
        Save();
    }

    /// <summary>A section's height follows its rows (24 cores need more room than 3 RAM lines).</summary>
    private int FitSection(TileSpec spec) => _services.Store.Sensors.Count == 0
        ? Math.Max(spec.RowSpan, TileSpec.DefaultSize(WidgetType.Section, 0).RowSpan) // no sensors yet: don't shrink it to nothing
        : Sections.FitRowSpan(Sections.Plan(spec, _services.Store.Sensors, boardName: _services.Store.MotherboardName), showTitle: spec.Title != "");

    [RelayCommand]
    private void DuplicateTile(TileViewModel? tile)
    {
        if (tile is null)
        {
            return;
        }

        tile.SyncSpec();
        var copy = tile.Spec.Clone();
        (copy.Col, copy.Row) = GridLayout.FindFreeSpot(Document.Tiles, copy.ColSpan, copy.RowSpan);
        Document.Tiles.Add(copy);
        Tiles.Add(new TileViewModel(copy, _services.Store, _services.FanProfiles));
        OnPropertyChanged(nameof(IsEmpty));
        Save();
    }

    [RelayCommand]
    private void DeleteTile(TileViewModel? tile)
    {
        if (tile is null)
        {
            return;
        }

        Document.Tiles.Remove(tile.Spec);
        Tiles.Remove(tile);
        tile.Dispose();
        OnPropertyChanged(nameof(IsEmpty));
        Save();
    }

    /// <summary>
    /// For each of the tile's sensors that no longer exists, asks which sensor replaces it and re-links it everywhere
    /// (every tile on every dashboard, alerts, History).
    /// </summary>
    [RelayCommand]
    private void FixSensors(TileViewModel? tile)
    {
        if (tile is null)
        {
            return;
        }

        var missing = tile.Spec.Sensors.Where(id => !_services.Store.SensorsById.ContainsKey(id)).Distinct().ToList();
        foreach (var id in missing)
        {
            if (_services.Dialogs.PickReplacement(id) is { } replacement)
            {
                _services.Linker.Relink(id, replacement);
            }
        }
    }

    /// <summary>Replaces a sensor id in every tile of this dashboard. Returns true if anything changed.</summary>
    public bool ReplaceSensor(string oldId, string newId)
    {
        var changed = false;
        foreach (var spec in Document.Tiles)
        {
            var index = spec.Sensors.IndexOf(oldId);
            if (index < 0)
            {
                continue;
            }

            if (spec.Sensors.Contains(newId))
            {
                spec.Sensors.RemoveAt(index);
            }
            else
            {
                spec.Sensors[index] = newId;
            }

            changed = true;
        }

        if (changed)
        {
            foreach (var tile in Tiles)
            {
                tile.SyncSpec();
            }

            Rebuild();
            Save();
        }

        return changed;
    }

    public void Save()
    {
        foreach (var tile in Tiles)
        {
            tile.SyncSpec();
        }

        try
        {
            _services.Repository.Save(Document);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _services.Dialogs.ShowError($"Couldn't save the dashboard: {ex.Message}");
        }
    }

    public void Dispose()
    {
        foreach (var tile in Tiles)
        {
            tile.Dispose();
        }

        Tiles.Clear();
    }
}
