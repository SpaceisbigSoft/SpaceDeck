using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Threading;
using System.Windows.Data;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SensorDeck.App.Controls;
using SensorDeck.App.Services;
using SensorDeck.App.Themes;
using SensorDeck.Core.Model;
using SensorDeck.Core.Protocol;

namespace SensorDeck.App.ViewModels;

/// <summary>A time window choice on the History page.</summary>
public sealed record HistoryWindow(int Seconds, string Label);

/// <summary>A plotted sensor with its line color, for the legend.</summary>
public sealed record HistoryLegendEntry(SensorItem Sensor, Brush Brush);

/// <summary>
/// History page: pick up to six sensors and a window and see them on one chart. Up to an hour comes from each
/// sensor's live <see cref="TimeSeries"/>; longer ranges (6 h – 7 d) are loaded from the collector's disk history,
/// averaged to a few hundred points, and refreshed every minute.
/// </summary>
public sealed partial class HistoryViewModel : ObservableObject
{
    public const int MaxSeries = 6;

    private const int LongPoints = 600;
    private const int LiveWindowLimit = 3600;

    private readonly SensorStore _store;
    private readonly AppSettings _settings;
    private readonly DialogService _dialogs;
    private readonly DispatcherTimer _longRefresh;
    private Dictionary<string, TimeSeries> _longSeries = new(StringComparer.Ordinal);
    private int _loadVersion;
    private CancellationTokenSource? _export;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _exportStatus;

    [ObservableProperty]
    private bool _isExporting;

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private HistoryWindow? _selectedWindow;

    [ObservableProperty]
    private IReadOnlyList<ChartSeries> _series = [];

    [ObservableProperty]
    private string? _hint;

    public HistoryViewModel(SensorStore store, AppSettings settings, SensorLinker linker, DialogService dialogs)
    {
        _dialogs = dialogs;
        _longRefresh = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _longRefresh.Tick += (_, _) => _ = LoadLongAsync();
        ArgumentNullException.ThrowIfNull(linker);
        linker.Relinked += (_, _) => Restore(); // settings.HistorySensors was rewritten
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(settings);
        _store = store;
        _settings = settings;
        SelectedWindow = Windows.FirstOrDefault(w => w.Seconds == settings.HistoryWindowSeconds) ?? Windows[1];

        var view = new ListCollectionView(store.Sensors);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(SensorItem.GroupName)));
        view.Filter = Matches;
        Picker = view;

        store.CatalogApplied += Restore;
        store.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SensorStore.SupportsLongHistory) && IsLongWindow)
            {
                _ = LoadLongAsync();
            }
        };
        Themes.SkinManager.SkinChanged += RebuildSeries; // series colors come from the skin
        store.CustomizationApplied += _ =>
        {
            Picker.Refresh(); // renamed / hidden sensors
            RebuildSeries(); // legend names
        };
    }

    public IReadOnlyList<HistoryWindow> Windows { get; } =
    [
        new(60, "1 min"),
        new(300, "5 min"),
        new(900, "15 min"),
        new(3600, "1 hour"),
        new(6 * 3600, "6 hours"),
        new(24 * 3600, "24 hours"),
        new(7 * 24 * 3600, "7 days"),
    ];

    /// <summary>Ranges longer than an hour come from the collector's disk history.</summary>
    public bool IsLongWindow => SelectedWindow?.Seconds > LiveWindowLimit;

    private bool _isPageVisible = true;

    /// <summary>Set by the main window: the long-range chart (a disk-history query each minute) refreshes only while shown.</summary>
    public bool IsPageVisible
    {
        get => _isPageVisible;
        set
        {
            _isPageVisible = value;
            if (!value)
            {
                _longRefresh.Stop();
            }
            else if (IsLongWindow && !_longRefresh.IsEnabled)
            {
                _longRefresh.Start();
                _ = LoadLongAsync(); // catch up on what was skipped
            }
        }
    }

    /// <summary>Sensors available to add, filtered by <see cref="SearchText"/>.</summary>
    public ICollectionView Picker { get; }

    public ObservableCollection<HistoryLegendEntry> Legend { get; } = [];

    partial void OnSearchTextChanged(string value) => Picker.Refresh();

    partial void OnSelectedWindowChanged(HistoryWindow? value)
    {
        if (value is null)
        {
            return;
        }

        _settings.HistoryWindowSeconds = value.Seconds;
        _settings.Save();
        OnPropertyChanged(nameof(IsLongWindow));
        if (IsLongWindow)
        {
            if (_isPageVisible)
            {
                _longRefresh.Start();
            }

            _ = LoadLongAsync();
        }
        else
        {
            _longRefresh.Stop();
            _longSeries = new(StringComparer.Ordinal);
            Hint = null;
            RebuildSeries();
        }
    }

    /// <summary>Loads the chosen range for the charted sensors from the collector (averaged to ~600 points).</summary>
    private async Task LoadLongAsync()
    {
        if (!IsLongWindow || SelectedWindow is not { } window)
        {
            return;
        }

        if (!_store.SupportsLongHistory)
        {
            Hint = _store.ConnectionState == ConnectionState.Connected
                ? "Ranges over an hour need the collector's disk history: update and restart the collector."
                : "Not connected to the collector.";
            _longSeries = new(StringComparer.Ordinal);
            RebuildSeries();
            return;
        }

        var sensors = Legend.Select(e => e.Sensor).ToList();
        if (sensors.Count == 0)
        {
            return;
        }

        var version = ++_loadVersion;
        IsLoading = true;
        try
        {
            var to = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var from = to - (window.Seconds * 1000L);
            var response = await _store.Client.RequestHistoryAsync(
                new HistoryRequest(0, [.. sensors.Select(s => s.Id)], window.Seconds, from, to, LongPoints));
            if (version != _loadVersion)
            {
                return; // a newer load started meanwhile
            }

            var series = new Dictionary<string, TimeSeries>(StringComparer.Ordinal);
            for (var i = 0; i < sensors.Count && i < response.Values.Length; i++)
            {
                var ts = new TimeSeries(Math.Max(2, response.TimestampsUnixMs.Length));
                ts.Backfill(response.TimestampsUnixMs, sensors[i].CorrectRaw(response.Values[i]));
                series[sensors[i].Id] = ts;
            }

            _longSeries = series;
            Hint = response.TimestampsUnixMs.Length == 0 ? "No stored history for this range yet: the collector saves it from now on." : null;
            RebuildSeries();
        }
        // ArgumentException: a malformed answer (e.g. a missing value column). This runs fire-and-forget, so anything
        // not caught here would be lost silently instead of showing the hint.
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException or ObjectDisposedException or ArgumentException)
        {
            if (version == _loadVersion)
            {
                Hint = "Couldn't load history from the collector.";
            }
        }
        finally
        {
            if (version == _loadVersion)
            {
                IsLoading = false;
            }
        }
    }

    /// <summary>Saves every stored sample of the charted sensors in the selected range as CSV (click again to cancel).</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ExportCsvAsync()
    {
        if (IsExporting)
        {
            _export?.Cancel();
            return;
        }

        var sensors = Legend.Select(e => e.Sensor).ToList();
        if (sensors.Count == 0 || SelectedWindow is not { } window)
        {
            ExportStatus = "Chart some sensors first.";
            return;
        }

        if (_store.CollectorProtocolVersion < ProtocolInfo.LongHistoryVersion)
        {
            ExportStatus = "Exporting needs the updated collector: rebuild and restart it.";
            return;
        }

        var path = _dialogs.PickSaveFile("Export history", $"SensorDeck history {DateTime.Now:yyyy-MM-dd HHmm}", "CSV file (*.csv)|*.csv");
        if (path is null)
        {
            return;
        }

        IsExporting = true;
        using var cancel = new CancellationTokenSource();
        _export = cancel;
        var to = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var from = to - (window.Seconds * 1000L);
        var progress = new Progress<long>(t => ExportStatus = $"Exporting… {Math.Clamp((t - from) * 100 / Math.Max(1, to - from), 0, 100)} %");
        try
        {
            var rows = await HistoryExport.ExportAsync(_store.Client, sensors, from, to, path, progress, cancel.Token);
            ExportStatus = $"Saved {rows:N0} rows to {Path.GetFileName(path)}."
                + (window.Seconds > (_store.CollectorStatus?.HistorySeconds ?? 3600) && !_store.SupportsLongHistory ? " (Only the last hour is available without disk history.)" : "");
        }
        catch (OperationCanceledException)
        {
            ExportStatus = "Export cancelled.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or TimeoutException or ObjectDisposedException)
        {
            ExportStatus = $"Export failed: {ex.Message}";
        }
        finally
        {
            IsExporting = false;
            _export = null;
        }
    }

    /// <summary>Adds the sensor to the chart, or removes it if it's already there.</summary>
    [RelayCommand]
    private void Toggle(SensorItem? sensor)
    {
        if (sensor is null)
        {
            return;
        }

        var existing = Legend.FirstOrDefault(e => e.Sensor == sensor);
        if (existing is not null)
        {
            Legend.Remove(existing);
        }
        else if (Legend.Count >= MaxSeries)
        {
            Hint = $"Up to {MaxSeries} sensors per chart. Remove one first.";
            return;
        }
        else
        {
            Legend.Add(new HistoryLegendEntry(sensor, Brushes.Transparent));
        }

        Hint = null;
        RebuildSeries();
        Save();
        if (IsLongWindow)
        {
            _ = LoadLongAsync();
        }
    }

    [RelayCommand]
    private void Clear()
    {
        Legend.Clear();
        RebuildSeries();
        Save();
    }

    private void RebuildSeries()
    {
        foreach (var sensor in _store.Sensors)
        {
            sensor.IsCharted = Legend.Any(e => e.Sensor == sensor);
        }

        // Re-color in order so the palette stays stable as sensors are added and removed.
        var entries = Legend.Select((e, i) => new HistoryLegendEntry(e.Sensor, SkinManager.Brush($"Skin.Series.{i % 6}"))).ToList();
        Legend.Clear();
        foreach (var entry in entries)
        {
            Legend.Add(entry);
        }

        Series = [.. entries.Select(e => new ChartSeries(e.Sensor.Name, SeriesFor(e.Sensor), e.Brush, e.Sensor.Kind, _store.Display))];
    }

    /// <summary>Live history up to an hour; the loaded long-range data otherwise (empty until it arrives).</summary>
    private TimeSeries SeriesFor(SensorItem sensor) =>
        !IsLongWindow ? sensor.History
        : _longSeries.TryGetValue(sensor.Id, out var series) ? series
        : EmptySeries;

    private static readonly TimeSeries EmptySeries = new(2);

    private void Restore()
    {
        var ids = _settings.HistorySensors.Count > 0
            ? _settings.HistorySensors
            : DefaultIds();

        Legend.Clear();
        foreach (var id in ids.Take(MaxSeries))
        {
            if (_store.Sensors.FirstOrDefault(s => s.Id == id) is { } sensor)
            {
                Legend.Add(new HistoryLegendEntry(sensor, Brushes.Transparent));
            }
        }

        RebuildSeries();
        Picker.Refresh();
        if (IsLongWindow)
        {
            _ = LoadLongAsync();
        }
    }

    /// <summary>The CPU temperature and each GPU's (discrete first), found by the default dashboard's names (Intel and AMD).</summary>
    private List<string> DefaultIds() =>
    [
        .. new[] { DefaultDashboard.Pick(_store.Sensors, HardwareKind.Cpu, SensorKind.Temperature, DefaultDashboard.CpuTemperatureNames, anyFallback: false) }
            .Concat(DefaultDashboard.GpusByImportance(_store.Sensors)
                .Select(gpu => DefaultDashboard.Pick(gpu, HardwareKind.Gpu, SensorKind.Temperature, DefaultDashboard.GpuTemperatureNames, anyFallback: false)))
            .OfType<SensorItem>()
            .Select(s => s.Id),
    ];

    private void Save()
    {
        _settings.HistorySensors = [.. Legend.Select(e => e.Sensor.Id)];
        _settings.Save();
    }

    private bool Matches(object obj)
    {
        if (obj is not SensorItem item || item.IsAdvanced || (item.IsHidden && !item.IsCharted))
        {
            return false;
        }

        return SensorsViewModel.MatchesSearch(item, SearchText);
    }
}
