using System.ComponentModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using SpaceDeck.App.Themes;
using SpaceDeck.Core.Dashboards;
using SpaceDeck.Core.Formatting;

namespace SpaceDeck.App.Services;

/// <summary>
/// A sensor as one dashboard tile shows it: the tile's decimals, scale, thresholds and accent color applied on top of
/// the live <see cref="SensorItem"/>. Widgets bind to this instead of the sensor directly.
/// </summary>
public sealed class TileReading : ObservableObject, IDisposable
{
    private readonly TileSpec _spec;
    private readonly DisplayOptions _display;
    private ValueLevel _level;

    public TileReading(SensorItem sensor, TileSpec spec, DisplayOptions display)
    {
        ArgumentNullException.ThrowIfNull(sensor);
        Sensor = sensor;
        _spec = spec;
        _display = display;
        _level = ComputeLevel();
        sensor.PropertyChanged += OnSensorChanged;
    }

    public SensorItem Sensor { get; }

    public string Name => Sensor.Name;

    public float? Value => Sensor.Value;

    public TimeSeries History => Sensor.History;

    public string DisplayValue => UnitFormatter.Format(Sensor.Kind, Sensor.Value, _spec.Decimals, _display.TemperatureUnit);

    public string DisplayMin => UnitFormatter.Format(Sensor.Kind, Sensor.Min, _spec.Decimals, _display.TemperatureUnit);

    public string DisplayMax => UnitFormatter.Format(Sensor.Kind, Sensor.Max, _spec.Decimals, _display.TemperatureUnit);

    public string DisplayNumber => Split(DisplayValue).Number;

    public string DisplayUnit => Split(DisplayValue).Unit;

    /// <summary>"min 31.0 °C · max 49.0 °C".</summary>
    public string MinMaxText => $"min {DisplayMin} · max {DisplayMax}";

    public double ScaleMinimum => _spec.ScaleMin ?? 0;

    public double ScaleMaximum => _spec.ScaleMax is { } max && max > ScaleMinimum ? max : Math.Max(Sensor.ScaleMaximum, ScaleMinimum + 1);

    public ValueLevel Level => _level;

    /// <summary>Number color: warning/critical when the reading needs attention, else the tile accent or the skin's ink.</summary>
    public Brush Brush => _level switch
    {
        ValueLevel.Critical => SkinManager.Brush("Skin.Critical"),
        ValueLevel.Warning => SkinManager.Brush("Skin.Warning"),
        _ => _spec.Accent is { } a ? SkinManager.Brush($"Skin.Series.{a}") : SkinManager.Brush("Skin.Text"),
    };

    public void Dispose() => Sensor.PropertyChanged -= OnSensorChanged;

    private ValueLevel ComputeLevel()
    {
        if (_spec.Warning is null && _spec.Critical is null)
        {
            return Sensor.Level;
        }

        if (Sensor.Value is not { } v || float.IsNaN(v))
        {
            return ValueLevel.Normal;
        }

        return _spec.Critical is { } c && v >= c ? ValueLevel.Critical
            : _spec.Warning is { } w && v >= w ? ValueLevel.Warning
            : ValueLevel.Normal;
    }

    private void OnSensorChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SensorItem.Value):
            case nameof(SensorItem.DisplayValue):
                OnPropertyChanged(nameof(Value));
                OnPropertyChanged(nameof(DisplayValue));
                OnPropertyChanged(nameof(DisplayNumber));
                OnPropertyChanged(nameof(DisplayUnit));
                UpdateLevel();
                break;
            case nameof(SensorItem.DisplayMin):
            case nameof(SensorItem.DisplayMax):
                OnPropertyChanged(nameof(DisplayMin));
                OnPropertyChanged(nameof(DisplayMax));
                OnPropertyChanged(nameof(MinMaxText));
                break;
            case nameof(SensorItem.ScaleMaximum):
                OnPropertyChanged(nameof(ScaleMaximum));
                break;
            case nameof(SensorItem.Level):
                UpdateLevel();
                OnPropertyChanged(nameof(Brush)); // also raised on skin changes, when the level stays the same
                break;
        }
    }

    private void UpdateLevel()
    {
        var level = ComputeLevel();
        if (level != _level)
        {
            _level = level;
            OnPropertyChanged(nameof(Level));
            OnPropertyChanged(nameof(Brush));
        }
    }

    private static (string Number, string Unit) Split(string text)
    {
        var space = text.IndexOf(' ', StringComparison.Ordinal);
        return space < 0 ? (text, "") : (text[..space], text[(space + 1)..]);
    }
}
