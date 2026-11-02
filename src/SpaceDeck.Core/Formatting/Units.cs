using SpaceDeck.Core.Model;

namespace SpaceDeck.Core.Formatting;

/// <summary>Canonical units per sensor kind. Sources report values in these units.</summary>
public static class Units
{
    public static string For(SensorKind kind) => kind switch
    {
        SensorKind.Voltage => "V",
        SensorKind.Current => "A",
        SensorKind.Power => "W",
        SensorKind.Clock => "MHz",
        SensorKind.Temperature => "°C",
        SensorKind.Load => "%",
        SensorKind.Frequency => "Hz",
        SensorKind.Fan => "RPM",
        SensorKind.Flow => "L/h",
        SensorKind.Control => "%",
        SensorKind.Level => "%",
        SensorKind.Factor => "",
        SensorKind.Data => "GB",
        SensorKind.SmallData => "MB",
        SensorKind.Throughput => "B/s",
        SensorKind.TimeSpan => "s",
        SensorKind.Energy => "mWh",
        SensorKind.Noise => "dBA",
        SensorKind.Humidity => "%",
        SensorKind.Timing => "",
        SensorKind.TemperatureDifference => "°C",
        _ => "",
    };
}
