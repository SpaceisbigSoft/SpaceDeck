using System.Globalization;
using SpaceDeck.Core.Formatting;
using SpaceDeck.Core.Model;

namespace SpaceDeck.Core.Tests;

public class UnitFormatterTests
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    [Theory]
    [InlineData(SensorKind.Voltage, 1.2126f, "1.213 V")]
    [InlineData(SensorKind.Temperature, 45.04f, "45.0 °C")]
    [InlineData(SensorKind.Fan, 1250f, "1,250 RPM")]
    [InlineData(SensorKind.Load, 37.26f, "37.3 %")]
    [InlineData(SensorKind.Control, 60f, "60.0 %")]
    [InlineData(SensorKind.Clock, 4875.4f, "4,875 MHz")]
    [InlineData(SensorKind.Power, 125.33f, "125.3 W")]
    [InlineData(SensorKind.Current, 1.5f, "1.50 A")]
    [InlineData(SensorKind.Flow, 98.76f, "98.8 L/h")]
    public void Format_UsesKindPrecisionAndUnit(SensorKind kind, float value, string expected)
    {
        Assert.Equal(expected, UnitFormatter.Format(kind, value, culture: Inv));
    }

    [Fact]
    public void Format_ConvertsToFahrenheit()
    {
        Assert.Equal("212.0 °F", UnitFormatter.Format(SensorKind.Temperature, 100f, TemperatureUnit.Fahrenheit, Inv));
    }

    [Theory]
    [InlineData(500f, "500 B/s")]
    [InlineData(2048f, "2.0 KB/s")]
    [InlineData(5_242_880f, "5.0 MB/s")]
    public void Format_ScalesThroughput(float value, string expected)
    {
        Assert.Equal(expected, UnitFormatter.Format(SensorKind.Throughput, value, culture: Inv));
    }

    [Fact]
    public void Format_ShowsDashForMissingValues()
    {
        Assert.Equal(UnitFormatter.Missing, UnitFormatter.Format(SensorKind.Voltage, null, culture: Inv));
        Assert.Equal(UnitFormatter.Missing, UnitFormatter.Format(SensorKind.Voltage, float.NaN, culture: Inv));
    }

    [Fact]
    public void Units_CoverEveryKind()
    {
        foreach (var kind in Enum.GetValues<SensorKind>())
        {
            Assert.NotNull(Units.For(kind));
        }

        Assert.Equal("RPM", Units.For(SensorKind.Fan));
        Assert.Equal("°C", Units.For(SensorKind.Temperature));
    }

    [Theory]
    [InlineData(SensorKind.Temperature, 45.26f, 0, "45 °C")]
    [InlineData(SensorKind.Voltage, 12.288f, 2, "12.29 V")]
    [InlineData(SensorKind.Fan, 1250f, 1, "1,250.0 RPM")]
    public void Format_WithDecimals_UsesFixedPrecision(SensorKind kind, float value, int decimals, string expected)
    {
        Assert.Equal(expected, UnitFormatter.Format(kind, value, decimals, TemperatureUnit.Celsius, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Format_WithNullDecimals_MatchesDefault()
    {
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        Assert.Equal(
            UnitFormatter.Format(SensorKind.Temperature, 45.26f, TemperatureUnit.Fahrenheit, culture),
            UnitFormatter.Format(SensorKind.Temperature, 45.26f, null, TemperatureUnit.Fahrenheit, culture));
    }
}
