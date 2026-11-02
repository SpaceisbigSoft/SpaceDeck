using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using SpaceDeck.App.Services;
using SpaceDeck.Core.Model;

namespace SpaceDeck.App.ViewModels;

/// <summary>All voltages, with standard rails checked against ATX tolerance.</summary>
public sealed partial class VoltagesViewModel : ObservableObject
{
    private readonly SensorStore _store;

    [ObservableProperty]
    private string _summary = "Waiting for data…";

    [ObservableProperty]
    private RailStatus _overallStatus = RailStatus.Unknown;

    public VoltagesViewModel(SensorStore store)
    {
        Themes.SkinManager.SkinChanged += () => OnPropertyChanged(nameof(OverallStatus)); // re-color the summary

        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        var view = new ListCollectionView(store.Sensors);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(SensorItem.GroupName)));
        view.Filter = o => o is SensorItem { Kind: SensorKind.Voltage, IsHidden: false };
        View = view;

        store.CatalogApplied += view.Refresh;
        store.CustomizationApplied += _ =>
        {
            view.Refresh();
            UpdateSummary();
        };
        store.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SensorStore.LastUpdate))
            {
                UpdateSummary();
            }
        };
    }

    public ICollectionView View { get; }

    private void UpdateSummary()
    {
        var rails = _store.Sensors.Where(s => s is { Kind: SensorKind.Voltage, HasNominal: true, IsHidden: false }).ToList();
        if (rails.Count == 0)
        {
            Summary = "No standard rails (+12V, +5V, +3.3V, standby, battery) were recognized on this board.";
            OverallStatus = RailStatus.Unknown;
            return;
        }

        var bad = rails.Where(r => r.RailStatus is RailStatus.Warning or RailStatus.Critical).ToList();
        if (bad.Count == 0)
        {
            Summary = $"All {rails.Count} standard rails are within tolerance.";
            OverallStatus = RailStatus.Ok;
            return;
        }

        OverallStatus = bad.Any(r => r.RailStatus == RailStatus.Critical) ? RailStatus.Critical : RailStatus.Warning;
        Summary = $"{bad.Count} of {rails.Count} rails out of tolerance: {string.Join(", ", bad.Select(r => $"{r.Name} {r.DisplayValue} ({r.DeviationText})"))}. "
            + "Board sensors can be off by a few percent; confirm with a multimeter before worrying.";
    }
}
