using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SpaceDeck.App.Services;
using SpaceDeck.App.Services.Media;
using SpaceDeck.Core.Dashboards;
using SpaceDeck.Core.Screens;

namespace SpaceDeck.App.ViewModels;

/// <summary>What every dashboard needs: live sensors, FanControl, storage and dialogs.</summary>
public sealed record DashboardServices(SensorStore Store, FanProfileService FanProfiles, DashboardRepository Repository, DialogService Dialogs, SensorLinker Linker);

/// <summary>One dashboard (a tab): its tiles and the edit actions on them. Every change is saved straight away.</summary>
public sealed partial class DashboardDocumentViewModel : ObservableObject, IDisposable
{
    private readonly DashboardServices _services;

    /// <summary>Opacity of a dimmed background (Dashboard menu → Dim the background).</summary>
    private const double DimmedOpacity = 0.45;

    [ObservableProperty]
    private bool _isEditing;

    /// <summary>
    /// The last frame a cooler screen drew from this dashboard, upright: the square editor's live preview. Set by the
    /// cooler screen while it shows this dashboard.
    /// </summary>
    [ObservableProperty]
    private BitmapSource? _screenPreview;

    private DashboardScreen? _screenUse;

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

    /// <summary>
    /// The screen of a cooler that shows this dashboard, set by the cooler screen while it does when the layout fits
    /// that screen's area (a 6-column layout on the GA II LCD): it is then edited in the square frame and drawn as that
    /// area, as if marked (<see cref="IsMarkedScreenLayout"/>), without changing the file. Null otherwise.
    /// </summary>
    public DashboardScreen? ScreenUse
    {
        get => _screenUse;
        set
        {
            if (value?.Width == _screenUse?.Width && value?.Height == _screenUse?.Height)
            {
                return;
            }

            var wasLayout = IsScreenLayout;
            _screenUse = value;
            OnScreenChanged(rebuild: wasLayout != IsScreenLayout);
        }
    }

    /// <summary>The screen this layout is for: its own (<see cref="DashboardDocument.Screen"/>) or the cooler's using it.</summary>
    public DashboardScreen? EffectiveScreen => Document.Screen ?? _screenUse;

    /// <summary>Edited in a frame of the screen's shape and drawn on the cooler as exactly that area.</summary>
    public bool IsScreenLayout => EffectiveScreen is not null;

    /// <summary>The screen layout's area on the grid, or null for an ordinary dashboard.</summary>
    public ScreenLayoutGrid? ScreenGrid => EffectiveScreen is { } screen ? ScreenLayoutGrid.For(screen) : null;

    /// <summary>Under the square editor's frame: "Cooler screen · 480 × 480 · tiles inside the frame are shown".</summary>
    public string ScreenCaption => EffectiveScreen is { } screen
        ? string.Create(CultureInfo.CurrentCulture, $"Cooler screen · {screen.Width} × {screen.Height} · only tiles inside the frame are shown")
        : "";

    /// <summary>Dashboard menu → "Square cooler screen layout": marks (or unmarks) this dashboard as a screen layout.</summary>
    public bool IsMarkedScreenLayout
    {
        get => Document.Screen is not null;
        set
        {
            if (value == IsMarkedScreenLayout)
            {
                return;
            }

            var wasLayout = IsScreenLayout;
            Document.Screen = value ? _screenUse?.Clone() ?? new DashboardScreen() : null;
            OnScreenChanged(rebuild: wasLayout != IsScreenLayout);
            Save();
        }
    }

    public bool HasBackground => Document.Background is not null;

    public string? BackgroundPath => Document.Background?.Path;

    public ImageFit BackgroundFit => Document.Background?.Fit ?? ImageFit.Fill;

    public double BackgroundOpacity => Document.Background?.Opacity ?? 1;

    /// <summary>Dashboard menu → "Fit the background inside": all of the picture instead of filling (cropping).</summary>
    public bool BackgroundFits
    {
        get => BackgroundFit == ImageFit.Fit;
        set
        {
            if (Document.Background is { } background && value != BackgroundFits)
            {
                background.Fit = value ? ImageFit.Fit : ImageFit.Fill;
                OnBackgroundChanged();
                Save();
            }
        }
    }

    /// <summary>Dashboard menu → "Dim the background", so readings stand out.</summary>
    public bool BackgroundDimmed
    {
        get => BackgroundOpacity < 1;
        set
        {
            if (Document.Background is { } background && value != BackgroundDimmed)
            {
                background.Opacity = value ? DimmedOpacity : 1;
                OnBackgroundChanged();
                Save();
            }
        }
    }

    /// <summary>
    /// A GIF that animates: the background or a picture tile. A cooler screen showing this dashboard draws it at its max
    /// frames a second instead of every few seconds.
    /// </summary>
    public bool HasAnimation =>
        IsAnimatedFile(Document.Background?.Path) || Document.Tiles.Any(t => t.Type == WidgetType.Image && IsAnimatedFile(t.Media));

    /// <summary>Replaces the tiles and the screen with a ready-made screen layout (the cooler screen's "Reset to default layout").</summary>
    public void ApplyScreenLayout(DashboardDocument layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        Document.Tiles = [.. layout.Tiles];
        Document.Screen = layout.Screen?.Clone();
        OnScreenChanged(rebuild: false);
        Rebuild();
        Save();
    }

    [RelayCommand]
    private void ChooseBackground()
    {
        var path = _services.Dialogs.PickOpenFile("Background picture or GIF", MediaFiles.PictureFilter);
        if (path is null)
        {
            return;
        }

        if (!MediaFiles.IsImage(path))
        {
            _services.Dialogs.ShowError("Choose a picture (PNG, JPEG, BMP, TIFF) or a GIF.");
            return;
        }

        try
        {
            var stored = MediaLibrary.Import(path);
            var old = Document.Background;
            Document.Background = new DashboardBackground { Path = stored, Fit = old?.Fit ?? ImageFit.Fill, Opacity = old?.Opacity ?? 1 };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            _services.Dialogs.ShowError($"Couldn't use that picture: {ex.Message}");
            return;
        }

        OnBackgroundChanged();
        Save();
    }

    [RelayCommand]
    private void RemoveBackground()
    {
        if (Document.Background is null)
        {
            return;
        }

        Document.Background = null;
        OnBackgroundChanged();
        Save();
    }

    private static bool IsAnimatedFile(string? path) =>
        path is not null && MediaFiles.KindOf(path) == MediaKind.Gif && (MediaCache.TryGetLoaded(path)?.IsAnimated ?? true);

    private void OnBackgroundChanged()
    {
        OnPropertyChanged(nameof(HasBackground));
        OnPropertyChanged(nameof(BackgroundPath));
        OnPropertyChanged(nameof(BackgroundFit));
        OnPropertyChanged(nameof(BackgroundOpacity));
        OnPropertyChanged(nameof(BackgroundFits));
        OnPropertyChanged(nameof(BackgroundDimmed));
    }

    /// <summary>Numbers on a screen layout fill their tiles, so the tiles are rebuilt when that changes.</summary>
    private void OnScreenChanged(bool rebuild)
    {
        OnPropertyChanged(nameof(EffectiveScreen));
        OnPropertyChanged(nameof(IsScreenLayout));
        OnPropertyChanged(nameof(ScreenGrid));
        OnPropertyChanged(nameof(ScreenCaption));
        OnPropertyChanged(nameof(IsMarkedScreenLayout));
        if (rebuild)
        {
            Rebuild();
        }
    }

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
            Tiles.Add(NewTile(spec));
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
        var columns = ScreenGrid?.Columns ?? GridLayout.Columns; // a screen layout's new tiles go inside its frame
        spec.ColSpan = Math.Min(colSpan, columns);
        spec.RowSpan = spec.Type == WidgetType.Section ? FitSection(spec) : rowSpan;
        (spec.Col, spec.Row) = GridLayout.FindFreeSpot(Document.Tiles, spec.ColSpan, spec.RowSpan, columns);
        GridLayout.Clamp(spec);
        Document.Tiles.Add(spec);
        Tiles.Add(NewTile(spec));
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

    private TileViewModel NewTile(TileSpec spec) => new(spec, _services.Store, _services.FanProfiles, fillNumbers: IsScreenLayout);

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
        (copy.Col, copy.Row) = GridLayout.FindFreeSpot(Document.Tiles, copy.ColSpan, copy.RowSpan, ScreenGrid?.Columns ?? GridLayout.Columns);
        Document.Tiles.Add(copy);
        Tiles.Add(NewTile(copy));
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
