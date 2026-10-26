using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SensorDeck.App.Services;
using SensorDeck.Core.Calculated;
using SensorDeck.Core.Formatting;

namespace SensorDeck.App.ViewModels;

public sealed record CalcChoice(CalcOperation Operation, string Label);

/// <summary>Add or edit a calculated sensor: pick inputs, an operation, an optional factor/offset; live preview.</summary>
public sealed partial class CalculatedSensorViewModel : ObservableObject, IDisposable
{
    private readonly SensorStore _store;
    private readonly CalculatedSensor _sensor;

    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private CalcChoice _selectedOperation = null!;

    [ObservableProperty]
    private string _factorText = "1";

    [ObservableProperty]
    private string _offsetText = "0";

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private string _preview = "";

    [ObservableProperty]
    private string? _error;

    public CalculatedSensorViewModel(SensorStore store, CalculatedSensor? existing)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _sensor = existing?.Clone() ?? new CalculatedSensor();
        IsNew = existing is null;

        var view = new ListCollectionView(store.Sensors);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(SensorItem.GroupName)));
        view.Filter = Matches;
        Picker = view;

        Name = IsNew ? "" : _sensor.Name;
        // An unknown operation (saved by a newer version, or hand-edited) opens as the first choice instead of throwing.
        SelectedOperation = Operations.FirstOrDefault(o => o.Operation == _sensor.Operation) ?? Operations[0];
        FactorText = _sensor.Factor.ToString(CultureInfo.CurrentCulture);
        OffsetText = _sensor.Offset.ToString(CultureInfo.CurrentCulture);
        foreach (var id in _sensor.Inputs)
        {
            if (store.SensorsById.TryGetValue(id, out var input))
            {
                Chosen.Add(input);
            }
        }

        store.ValuesApplied += OnValues;
        UpdatePreview();
    }

    public bool IsNew { get; }

    public string Title => IsNew ? "New calculated sensor" : "Edit calculated sensor";

    public IReadOnlyList<CalcChoice> Operations { get; } =
    [
        new(CalcOperation.Sum, "Sum (A + B + …), e.g. CPU + GPU power"),
        new(CalcOperation.Difference, "Difference (A − B − …), e.g. hot spot − core"),
        new(CalcOperation.Average, "Average, e.g. average case-fan speed"),
        new(CalcOperation.Max, "Highest of the inputs"),
        new(CalcOperation.Min, "Lowest of the inputs"),
    ];

    public ICollectionView Picker { get; }

    /// <summary>Inputs in order (the first one is "A").</summary>
    public ObservableCollection<SensorItem> Chosen { get; } = [];

    public string KindHint =>
        (Chosen.Select(c => c.Kind).Distinct().Count() > 1 ? "The inputs measure different things; the result uses the first input's unit. " : "")
        + (MissingInputs > 0 ? $"{MissingInputs} input(s) aren't available right now and are kept." : "");

    private int MissingInputs => _sensor.Inputs.Count(id => !_store.SensorsById.ContainsKey(id));

    public CalculatedSensor? Result { get; private set; }

    public bool Deleted { get; private set; }

    public event Action? RequestClose;

    partial void OnSearchTextChanged(string value) => Picker.Refresh();

    partial void OnSelectedOperationChanged(CalcChoice value) => UpdatePreview();

    partial void OnFactorTextChanged(string value) => UpdatePreview();

    partial void OnOffsetTextChanged(string value) => UpdatePreview();

    [RelayCommand]
    private void Toggle(SensorItem? sensor)
    {
        if (sensor is null)
        {
            return;
        }

        if (!Chosen.Remove(sensor))
        {
            Chosen.Add(sensor);
        }

        Error = null;
        Picker.Refresh(); // hidden/advanced inputs show only while chosen
        OnPropertyChanged(nameof(KindHint));
        UpdatePreview();
    }

    [RelayCommand]
    private void MakeFirst(SensorItem? sensor)
    {
        if (sensor is not null && Chosen.IndexOf(sensor) is > 0 and var index)
        {
            Chosen.Move(index, 0);
            UpdatePreview();
        }
    }

    [RelayCommand]
    private void Save()
    {
        if (Chosen.Count == 0)
        {
            Error = "Pick at least one input sensor.";
            return;
        }

        if (SelectedOperation.Operation == CalcOperation.Difference && Chosen.Count < 2)
        {
            Error = "A difference needs two or more inputs.";
            return;
        }

        if (!TryParse(FactorText, out var factor) || !TryParse(OffsetText, out var offset))
        {
            Error = "Factor and offset must be numbers.";
            return;
        }

        var first = Chosen[0];
        _sensor.Name = string.IsNullOrWhiteSpace(Name) ? DefaultName() : Name.Trim();
        _sensor.Operation = SelectedOperation.Operation;
        // Keep inputs that aren't present right now (device unplugged, collector not connected).
        var missing = _sensor.Inputs.Where(id => !_store.SensorsById.ContainsKey(id)).ToList();
        _sensor.Inputs = [.. Chosen.Select(c => c.Id), .. missing];
        _sensor.Factor = factor;
        _sensor.Offset = offset;

        // Temperature minus temperature is a gap, not a temperature (10 °C apart = 18 °F apart, not 50 °F).
        var isGap = SelectedOperation.Operation == CalcOperation.Difference && first.Kind == Core.Model.SensorKind.Temperature;
        _sensor.Kind = isGap ? Core.Model.SensorKind.TemperatureDifference : first.Kind;
        _sensor.Unit = first.Unit;
        Result = _sensor;
        RequestClose?.Invoke();
    }

    [RelayCommand]
    private void Delete()
    {
        Deleted = true;
        RequestClose?.Invoke();
    }

    public void Dispose()
    {
        _store.ValuesApplied -= OnValues;

        // The picker listens to the store's (app-lifetime) sensor list; without this every dialog opened stays alive
        // and is re-filtered on each new catalog.
        if (Picker is CollectionView view)
        {
            view.DetachFromSourceCollection();
        }
    }

    private void OnValues(long timestamp) => UpdatePreview();

    private void UpdatePreview()
    {
        if (Chosen.Count == 0)
        {
            Preview = "Pick input sensors on the right.";
            return;
        }

        if (!TryParse(FactorText, out var factor))
        {
            factor = 1;
        }

        TryParse(OffsetText, out var offset);
        var value = CalculatedSensor.Evaluate(SelectedOperation.Operation, [.. Chosen.Select(c => (double?)c.Value)], factor, offset);
        var kind = SelectedOperation.Operation == CalcOperation.Difference && Chosen[0].Kind == Core.Model.SensorKind.Temperature
            ? Core.Model.SensorKind.TemperatureDifference
            : Chosen[0].Kind;
        Preview = $"Now: {UnitFormatter.Format(kind, value is { } v ? (float)v : null, _store.TemperatureUnit)}";
    }

    private string DefaultName() => SelectedOperation.Operation switch
    {
        CalcOperation.Sum => string.Join(" + ", Chosen.Take(3).Select(c => c.Name)),
        CalcOperation.Difference => string.Join(" − ", Chosen.Take(3).Select(c => c.Name)),
        CalcOperation.Average => $"Average of {Chosen.Count}",
        CalcOperation.Max => $"Highest of {Chosen.Count}",
        _ => $"Lowest of {Chosen.Count}",
    };

    private static bool TryParse(string text, out double value)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            value = 0;
            return false;
        }

        return double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out value) && double.IsFinite(value);
    }

    private bool Matches(object obj)
    {
        if (obj is not SensorItem item || CalculatedSensor.IsCalculated(item.Id))
        {
            return false; // no calculated inputs: avoids loops
        }

        if (item.IsAdvanced || item.IsHidden)
        {
            return Chosen.Contains(item);
        }

        return string.IsNullOrWhiteSpace(SearchText)
            || SearchText.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(w =>
                item.Name.Contains(w, StringComparison.OrdinalIgnoreCase) || item.GroupName.Contains(w, StringComparison.OrdinalIgnoreCase));
    }
}
