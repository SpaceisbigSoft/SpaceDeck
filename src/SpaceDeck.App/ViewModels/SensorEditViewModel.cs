using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SpaceDeck.App.Services;
using SpaceDeck.Core.Customization;
using SpaceDeck.Core.Formatting;
using SpaceDeck.Core.Model;

namespace SpaceDeck.App.ViewModels;

/// <summary>
/// Customize one sensor: display name, hidden, and a correction (reading × scale + offset) for boards that report a
/// value at the wrong scale. Shows the raw reading and the corrected result live.
/// </summary>
public sealed partial class SensorEditViewModel : ObservableObject, IDisposable
{
    private readonly SensorCustomizationService _customizations;
    private readonly TemperatureUnit _unit;

    [ObservableProperty]
    private string _nameText = "";

    [ObservableProperty]
    private bool _hidden;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Preview))]
    private string _scaleText = "1";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Preview))]
    private string _offsetText = "0";

    [ObservableProperty]
    private string? _error;

    public SensorEditViewModel(SensorItem sensor, SensorCustomizationService customizations, TemperatureUnit unit)
    {
        ArgumentNullException.ThrowIfNull(sensor);
        Sensor = sensor;
        _customizations = customizations;
        _unit = unit;
        var current = customizations.Get(sensor.Id);
        NameText = current?.Name ?? "";
        Hidden = current?.Hidden ?? false;
        ScaleText = (current?.Scale ?? 1).ToString("0.######", CultureInfo.CurrentCulture);
        OffsetText = (current?.Offset ?? 0).ToString("0.######", CultureInfo.CurrentCulture);
        sensor.PropertyChanged += OnSensorChanged;
    }

    /// <summary>Temperatures: the offset is in °C whatever the display unit.</summary>
    public string CorrectionLabel => Sensor.Kind == SensorKind.Temperature
        ? "Correction: reading × scale + offset (offset in °C)"
        : "Correction: reading × scale + offset";

    public void Dispose() => Sensor.PropertyChanged -= OnSensorChanged;

    private void OnSensorChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SensorItem.Value))
        {
            OnPropertyChanged(nameof(Preview));
        }
    }

    public SensorItem Sensor { get; }

    public string OriginalName => Sensor.OriginalName;

    /// <summary>"Hardware reports 1.104 V → shown as 12.144 V".</summary>
    public string Preview
    {
        get
        {
            var raw = Sensor.RawValue;
            var rawText = UnitFormatter.Format(Sensor.Kind, raw is { } r ? (float)r : null, _unit);
            if (!TryParse(ScaleText, out var scale) || !TryParse(OffsetText, out var offset) || scale == 0)
            {
                return $"Hardware reports {rawText}";
            }

            var shown = raw is { } v ? (float?)((v * scale) + offset) : null;
            return scale == 1 && offset == 0
                ? $"Hardware reports {rawText} (no correction)"
                : $"Hardware reports {rawText} → shown as {UnitFormatter.Format(Sensor.Kind, shown, _unit)}";
        }
    }

    public event Action? RequestClose;

    [RelayCommand]
    private void Save()
    {
        if (!TryParse(ScaleText, out var scale) || scale == 0)
        {
            Error = "Scale must be a number other than 0 (1 = unchanged).";
            return;
        }

        if (!TryParse(OffsetText, out var offset))
        {
            Error = "Offset must be a number (0 = unchanged).";
            return;
        }

        _customizations.Set(Sensor.Id, new SensorOverride
        {
            Name = string.IsNullOrWhiteSpace(NameText) || NameText.Trim() == Sensor.OriginalName ? null : NameText.Trim(),
            Hidden = Hidden,
            Scale = scale,
            Offset = offset,
        });
        RequestClose?.Invoke();
    }

    [RelayCommand]
    private void Reset()
    {
        _customizations.Set(Sensor.Id, null);
        RequestClose?.Invoke();
    }

    private static bool TryParse(string text, out double value) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out value) && double.IsFinite(value);
}
