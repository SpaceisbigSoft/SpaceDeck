using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using SensorDeck.App.Services;
using SensorDeck.Core.Model;

namespace SensorDeck.App.ViewModels;

/// <summary>A fan's speed sensor paired with the duty (%) FanControl is driving it at, when available.</summary>
public sealed class FanRow
{
    public FanRow(SensorItem fan, SensorItem? duty)
    {
        Fan = fan;
        Duty = duty;
    }

    public SensorItem Fan { get; }

    public SensorItem? Duty { get; }

    public string Name => Fan.Name;

    public string GroupName => Fan.GroupName;

    public bool HasDuty => Duty is not null;

    /// <summary>
    /// Pairs each fan speed sensor with its duty sensor. LHM names a header's duty sensor (Control) the same as its
    /// speed sensor (Fan) on the same chip.
    /// </summary>
    public static IEnumerable<FanRow> Build(IEnumerable<SensorItem> sensors)
    {
        var all = sensors.ToList();
        var controls = all
            .Where(s => s.Kind == SensorKind.Control)
            .GroupBy(s => (s.HardwareId, s.OriginalName))
            .ToDictionary(g => g.Key, g => g.First());

        foreach (var fan in all.Where(s => s.Kind == SensorKind.Fan))
        {
            controls.TryGetValue((fan.HardwareId, fan.OriginalName), out var duty);
            yield return new FanRow(fan, duty);
        }
    }
}

/// <summary>Every fan and pump header with RPM and duty. Read-only: FanControl does the controlling.</summary>
public sealed class FansViewModel
{
    private readonly SensorStore _store;

    public FansViewModel(SensorStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        var view = new ListCollectionView(Rows);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(FanRow.GroupName)));
        View = view;
        store.CatalogApplied += Rebuild;
        store.CustomizationApplied += _ => Rebuild(); // renamed / hidden fans
    }

    public ObservableCollection<FanRow> Rows { get; } = [];

    public ICollectionView View { get; }

    private void Rebuild()
    {
        Rows.Clear();
        foreach (var row in FanRow.Build(_store.Sensors).Where(r => !r.Fan.IsHidden))
        {
            Rows.Add(row);
        }
    }
}
