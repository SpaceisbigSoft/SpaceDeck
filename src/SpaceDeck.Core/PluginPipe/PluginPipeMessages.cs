namespace SpaceDeck.Core.PluginPipe;

/// <summary>
/// Limits and names of the plugin pipe (<c>docs/PLUGIN-PIPE.md</c>): readings from SpaceDeck's FanControl plugin to the
/// collector, one JSON object per line.
/// </summary>
public static class PluginPipeProtocol
{
    /// <summary>Pipe name (<c>\\.\pipe\SpaceDeck.Plugins</c>).</summary>
    public const string PipeName = "SpaceDeck.Plugins";

    /// <summary>The pipe protocol version this collector speaks. A <c>hello</c> with any other version disconnects.</summary>
    public const int Version = 1;

    /// <summary>Longest line, in bytes, without its <c>\n</c>.</summary>
    public const int MaxLineBytes = 64 * 1024;

    public const int MaxIdLength = 128;

    /// <summary>Longest name, in characters (Unicode scalar values, so an emoji counts once).</summary>
    public const int MaxNameLength = 64;

    public const int MaxHardware = 32;

    public const int MaxSensors = 512;

    /// <summary>True if <paramref name="value"/> is a plausible reading for <paramref name="kind"/>; others are stored as null.</summary>
    public static bool InRange(PluginSensorKind kind, double value) => kind switch
    {
        PluginSensorKind.Temperature => value is >= -50.0 and <= 150.0,
        PluginSensorKind.Fan => value is >= 0.0 and <= 20_000.0,
        PluginSensorKind.Control => value is >= 0.0 and <= 100.0,
        _ => false,
    };
}

/// <summary>Hardware kinds the plugin can name; anything it sends that isn't known becomes <see cref="Other"/>.</summary>
public enum PluginHardwareKind
{
    Other,
    Cooler,
    Fans,
}

/// <summary>Sensor kinds the plugin can name: °C, rpm and % duty.</summary>
public enum PluginSensorKind
{
    Temperature,
    Fan,
    Control,
}

/// <summary>A validated line from the plugin pipe.</summary>
public abstract record PluginMessage;

/// <summary>The first line after connecting.</summary>
/// <param name="Plugin">The plugin's FanControl name, cleaned for logs (control characters dropped, at most 64 characters).</param>
/// <param name="PluginVersion">The plugin build, cleaned the same way.</param>
/// <param name="Instance">Id of this plugin object, cleaned the same way.</param>
public sealed record PluginHello(int Version, string Plugin, string PluginVersion, string Instance) : PluginMessage;

/// <param name="Id">The plugin's own id (published by the collector with a source prefix).</param>
public sealed record PluginHardware(string Id, string Name, PluginHardwareKind Kind);

/// <param name="HardwareId">The <see cref="PluginHardware.Id"/> this sensor belongs to (checked to exist).</param>
public sealed record PluginSensor(string Id, string HardwareId, string Name, PluginSensorKind Kind);

/// <summary>The plugin's current hardware and sensors; <c>values</c> lines follow its sensor order.</summary>
public sealed record PluginCatalog(IReadOnlyList<PluginHardware> Hardware, IReadOnlyList<PluginSensor> Sensors) : PluginMessage;

/// <summary>One reading per catalog sensor, in catalog order. Null = not measured, or out of range for the kind.</summary>
public sealed record PluginValues(IReadOnlyList<double?> Values) : PluginMessage;

/// <summary>
/// What a line turned out to be: a message, nothing (an unknown type or a blank line, ignored), or an error that must
/// disconnect the client.
/// </summary>
public readonly record struct PluginLineResult(PluginMessage? Message, string? Error)
{
    /// <summary>A line to skip: an unknown type (a newer plugin) or a blank line.</summary>
    public static PluginLineResult Ignored => default;

    public bool IsError => Error is not null;

    public static PluginLineResult Fail(string error) => new(null, error);
}
