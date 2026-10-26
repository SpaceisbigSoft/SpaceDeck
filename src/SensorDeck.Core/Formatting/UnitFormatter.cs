using System.Globalization;
using SensorDeck.Core.Model;

namespace SensorDeck.Core.Formatting;

public enum TemperatureUnit
{
    Celsius,
    Fahrenheit,
}

/// <summary>Turns raw sensor values into display strings with sensible precision per kind.</summary>
public static class UnitFormatter
{
    public const string Missing = "—";

    public static float CelsiusToFahrenheit(float celsius) => (celsius * 9f / 5f) + 32f;

    /// <summary>Formats a value with its unit, e.g. <c>1.212 V</c>, <c>45.0 °C</c>, <c>1,250 RPM</c>.</summary>
    public static string Format(SensorKind kind, float? value, TemperatureUnit temperatureUnit = TemperatureUnit.Celsius, IFormatProvider? culture = null)
    {
        if (value is not { } v || float.IsNaN(v) || float.IsInfinity(v))
        {
            return Missing;
        }

        culture ??= CultureInfo.CurrentCulture;

        return kind switch
        {
            SensorKind.Temperature when temperatureUnit == TemperatureUnit.Fahrenheit
                => string.Create(culture, $"{CelsiusToFahrenheit(v):F1} °F"),
            SensorKind.Temperature => string.Create(culture, $"{v:F1} °C"),
            SensorKind.TemperatureDifference when temperatureUnit == TemperatureUnit.Fahrenheit
                => string.Create(culture, $"{v * 9f / 5f:F1} °F"),
            SensorKind.TemperatureDifference => string.Create(culture, $"{v:F1} °C"),
            SensorKind.Voltage => string.Create(culture, $"{v:F3} V"),
            SensorKind.Current => string.Create(culture, $"{v:F2} A"),
            SensorKind.Power => string.Create(culture, $"{v:F1} W"),
            SensorKind.Clock => string.Create(culture, $"{v:N0} MHz"),
            SensorKind.Frequency => string.Create(culture, $"{v:N0} Hz"),
            SensorKind.Fan => string.Create(culture, $"{v:N0} RPM"),
            SensorKind.Flow => string.Create(culture, $"{v:F1} L/h"),
            SensorKind.Load or SensorKind.Control or SensorKind.Level or SensorKind.Humidity
                => string.Create(culture, $"{v:F1} %"),
            SensorKind.Data => string.Create(culture, $"{v:F1} GB"),
            SensorKind.SmallData => string.Create(culture, $"{v:N0} MB"),
            SensorKind.Throughput => FormatThroughput(v, culture),
            SensorKind.TimeSpan => string.Create(culture, $"{v:N0} s"),
            SensorKind.Energy => string.Create(culture, $"{v:N0} mWh"),
            SensorKind.Noise => string.Create(culture, $"{v:F1} dBA"),
            SensorKind.Timing => string.Create(culture, $"{v:G4}"),
            _ => string.Create(culture, $"{v:G4}"),
        };
    }

    /// <summary>
    /// Like <see cref="Format"/> but with a fixed number of decimals (a dashboard tile setting). Throughput keeps its
    /// automatic B/s…GB/s scaling. Null <paramref name="decimals"/> means the default precision.
    /// </summary>
    public static string Format(SensorKind kind, float? value, int? decimals, TemperatureUnit temperatureUnit = TemperatureUnit.Celsius, IFormatProvider? culture = null)
    {
        var text = Format(kind, value, temperatureUnit, culture);
        if (decimals is not { } d || text == Missing || kind == SensorKind.Throughput || value is not { } v)
        {
            return text;
        }

        culture ??= CultureInfo.CurrentCulture;
        var shown = temperatureUnit != TemperatureUnit.Fahrenheit ? v
            : kind == SensorKind.Temperature ? CelsiusToFahrenheit(v)
            : kind == SensorKind.TemperatureDifference ? v * 9f / 5f
            : v;
        var space = text.IndexOf(' ', StringComparison.Ordinal);
        var unit = space < 0 ? "" : text[space..];
        return shown.ToString("N" + Math.Clamp(d, 0, 4).ToString(CultureInfo.InvariantCulture), culture) + unit;
    }

    private static string FormatThroughput(float bytesPerSecond, IFormatProvider culture)
    {
        string[] units = ["B/s", "KB/s", "MB/s", "GB/s"];
        double value = bytesPerSecond;
        var unit = 0;
        while (Math.Abs(value) >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? string.Create(culture, $"{value:N0} {units[unit]}")
            : string.Create(culture, $"{value:F1} {units[unit]}");
    }
}
