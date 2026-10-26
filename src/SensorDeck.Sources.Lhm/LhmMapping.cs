using LibreHardwareMonitor.Hardware;
using SensorDeck.Core.Model;

namespace SensorDeck.Sources.Lhm;

/// <summary>Maps LibreHardwareMonitor types onto SensorDeck's source-agnostic model.</summary>
/// <remarks>
/// Switches on the enum *names* rather than members so a newer or older LHM that adds or drops a
/// member still compiles; unknown values fall back to Other.
/// </remarks>
internal static class LhmMapping
{
    public const string SourceName = "lhm";

    public static string HardwareId(IHardware hardware) => $"{SourceName}:{hardware.Identifier}";

    public static string SensorId(ISensor sensor) => $"{SourceName}:{sensor.Identifier}";

    public static HardwareKind ToHardwareKind(HardwareType type) => type.ToString() switch
    {
        "Motherboard" => HardwareKind.Motherboard,
        "SuperIO" => HardwareKind.SuperIo,
        "EmbeddedController" => HardwareKind.EmbeddedController,
        "Cpu" => HardwareKind.Cpu,
        "GpuNvidia" or "GpuAmd" or "GpuIntel" => HardwareKind.Gpu,
        "Memory" => HardwareKind.Memory,
        "Storage" => HardwareKind.Storage,
        "Network" => HardwareKind.Network,
        "Cooler" => HardwareKind.Cooler,
        "Psu" or "PowerMonitor" => HardwareKind.Psu,
        "Battery" => HardwareKind.Battery,
        _ => HardwareKind.Other,
    };

    /// <summary>Name to show when the hardware reports a garbled one.</summary>
    public static string FallbackName(HardwareKind kind) => kind switch
    {
        HardwareKind.Memory => "Memory module",
        HardwareKind.SuperIo => "Super I/O chip",
        HardwareKind.EmbeddedController => "Embedded controller",
        HardwareKind.Psu => "Power supply",
        _ => kind.ToString(),
    };

    public static SensorKind ToSensorKind(SensorType type) => type.ToString() switch
    {
        "Voltage" => SensorKind.Voltage,
        "Current" => SensorKind.Current,
        "Power" => SensorKind.Power,
        "Clock" => SensorKind.Clock,
        "Temperature" => SensorKind.Temperature,
        "Load" => SensorKind.Load,
        "Frequency" => SensorKind.Frequency,
        "Fan" => SensorKind.Fan,
        "Flow" => SensorKind.Flow,
        "Control" => SensorKind.Control,
        "Level" => SensorKind.Level,
        "Factor" => SensorKind.Factor,
        "Data" => SensorKind.Data,
        "SmallData" => SensorKind.SmallData,
        "Throughput" => SensorKind.Throughput,
        "TimeSpan" => SensorKind.TimeSpan,
        "Energy" => SensorKind.Energy,
        "Noise" => SensorKind.Noise,
        "Humidity" => SensorKind.Humidity,
        "Timing" => SensorKind.Timing,
        _ => SensorKind.Other,
    };
}
