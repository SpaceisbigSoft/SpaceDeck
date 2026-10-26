namespace SensorDeck.Core.Model;

/// <summary>What a sensor measures. Source-agnostic; each source maps its own types onto these.</summary>
public enum SensorKind
{
    Voltage,
    Current,
    Power,
    Clock,
    Temperature,
    Load,
    Frequency,
    Fan,
    Flow,
    /// <summary>Fan/pump duty cycle or other control output, in percent.</summary>
    Control,
    Level,
    Factor,
    Data,
    SmallData,
    Throughput,
    TimeSpan,
    Energy,
    Noise,
    Humidity,
    Other,
    /// <summary>Memory timings from SPD (mixed nanoseconds and clock cycles, so no fixed unit). Appended last: enum values are wire format.</summary>
    Timing,

    /// <summary>A difference between two temperatures (°C apart; ×9/5 without +32 in °F). App-side calculated sensors only.</summary>
    TemperatureDifference,
}

/// <summary>Broad category of a hardware component, used for grouping and icons.</summary>
public enum HardwareKind
{
    Motherboard,
    SuperIo,
    EmbeddedController,
    Cpu,
    Gpu,
    Memory,
    Storage,
    Network,
    Cooler,
    Psu,
    Battery,
    Other,
}

/// <summary>Health of a sensor source. Anything other than <see cref="Ok"/> carries a user-facing message.</summary>
public enum SourceStatus
{
    NotOpened,
    Ok,
    /// <summary>Running without administrator rights: many sensors will be missing.</summary>
    NotElevated,
    /// <summary>The PawnIO driver isn't installed or isn't running: motherboard/CPU sensors will be missing.</summary>
    DriverMissing,
    /// <summary>The source's device (e.g. a USB cooler) isn't connected.</summary>
    DeviceNotFound,
    Error,
}

/// <summary>Health band for a voltage rail compared with its nominal value.</summary>
public enum RailStatus
{
    Unknown,
    Ok,
    Warning,
    Critical,
}
