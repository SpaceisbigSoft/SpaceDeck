using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SpaceDeck.Core.Customization;
using SpaceDeck.App.Services;
using SpaceDeck.Core.Model;

namespace SpaceDeck.App.ViewModels;

/// <summary>Every sensor, grouped by hardware, with search and category chips.</summary>
public sealed partial class SensorsViewModel : ObservableObject
{
    private readonly SensorStore _store;
    private readonly DialogService _dialogs;

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private string _category = "All";

    [ObservableProperty]
    private bool _showAdvanced;

    [ObservableProperty]
    private bool _showHidden;

    [ObservableProperty]
    private int _visibleCount;

    public SensorsViewModel(SensorStore store, DialogService dialogs)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _dialogs = dialogs;
        var view = new ListCollectionView(store.Sensors);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(SensorItem.GroupName)));
        view.Filter = Matches;
        View = view;
        store.CatalogApplied += Refresh;
        store.CustomizationApplied += _ => Refresh();
    }

    public IReadOnlyList<string> Categories { get; } = ["All", "Temperatures", "Voltages", "Fans", "Power", "Clocks", "Load", "Data"];

    public ICollectionView View { get; }

    public SensorStore Store => _store;

    /// <summary>Rename / hide / correct (double-click a row, or right-click → Customize…).</summary>
    [RelayCommand]
    private void Customize(SensorItem? sensor)
    {
        if (sensor is not null)
        {
            _dialogs.EditSensor(sensor);
        }
    }

    [RelayCommand]
    private void ToggleHidden(SensorItem? sensor)
    {
        if (sensor is null)
        {
            return;
        }

        var o = _store.Customizations.Get(sensor.Id)?.Clone() ?? new SensorOverride();
        o.Hidden = !sensor.IsHidden;
        _store.Customizations.Set(sensor.Id, o);
    }

    [ObservableProperty]
    private string? _resetMessage;

    [RelayCommand]
    private void AddCalculated() => _dialogs.EditCalculatedSensor(null);

    /// <summary>Right-click → edit; only for calculated sensors.</summary>
    [RelayCommand]
    private void EditCalculated(SensorItem? sensor)
    {
        var def = sensor is null ? null : _store.CalculatedSensors.FirstOrDefault(c => c.SensorId == sensor.Id);
        if (def is null)
        {
            ResetMessage = "Only calculated sensors (group \"Calculated\") can be edited this way.";
            return;
        }

        _dialogs.EditCalculatedSensor(def);
    }

    /// <summary>Restarts min/max for one sensor (context menu).</summary>
    [RelayCommand]
    private async Task ResetMinMaxAsync(SensorItem? sensor)
    {
        if (sensor is not null)
        {
            await ResetAsync([sensor.Id]);
        }
    }

    /// <summary>Restarts min/max for every sensor.</summary>
    [RelayCommand]
    private Task ResetAllMinMaxAsync() => ResetAsync(null);

    private async Task ResetAsync(IReadOnlyList<string>? ids)
    {
        _store.ResetCalculatedMinMax(ids); // app-side, works without the collector
        if (!_store.SupportsMinMaxReset)
        {
            ResetMessage = "Resetting min/max needs the updated collector: rebuild and restart it.";
            return;
        }

        ResetMessage = await _store.ResetMinMaxAsync(ids) ? null : "Couldn't reach the collector.";
    }

    partial void OnSearchTextChanged(string value) => Refresh();

    partial void OnCategoryChanged(string value) => Refresh();

    partial void OnShowAdvancedChanged(bool value) => Refresh();

    partial void OnShowHiddenChanged(bool value) => Refresh();

    private void Refresh()
    {
        View.Refresh();
        VisibleCount = View.Cast<object>().Count();
    }

    private bool Matches(object obj)
    {
        if (obj is not SensorItem item)
        {
            return false;
        }

        if (!ShowAdvanced && item.IsAdvanced)
        {
            return false;
        }

        if (!ShowHidden && item.IsHidden)
        {
            return false;
        }

        if (!InCategory(item.Kind, Category))
        {
            return false;
        }

        return MatchesSearch(item, SearchText);
    }

    /// <summary>
    /// The sensor search used across the app: every word must appear somewhere in the display name, the hardware's
    /// name for it, the group or the kind ("cpu temp", "fan 3", "12v"). Blank matches everything.
    /// </summary>
    internal static bool MatchesSearch(SensorItem item, string? search)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (string.IsNullOrWhiteSpace(search))
        {
            return true;
        }

        foreach (var word in search.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!item.Name.Contains(word, StringComparison.OrdinalIgnoreCase)
                && !item.OriginalName.Contains(word, StringComparison.OrdinalIgnoreCase)
                && !item.GroupName.Contains(word, StringComparison.OrdinalIgnoreCase)
                && !item.Kind.ToString().Contains(word, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    internal static bool InCategory(SensorKind kind, string category) => category switch
    {
        "Temperatures" => kind == SensorKind.Temperature,
        "Voltages" => kind == SensorKind.Voltage,
        "Fans" => kind is SensorKind.Fan or SensorKind.Control or SensorKind.Flow,
        "Power" => kind is SensorKind.Power or SensorKind.Current or SensorKind.Energy,
        "Clocks" => kind is SensorKind.Clock or SensorKind.Frequency,
        "Load" => kind is SensorKind.Load or SensorKind.Level,
        "Data" => kind is SensorKind.Data or SensorKind.SmallData or SensorKind.Throughput,
        _ => true,
    };
}
