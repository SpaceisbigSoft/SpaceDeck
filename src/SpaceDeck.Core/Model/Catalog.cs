using MessagePack;

namespace SpaceDeck.Core.Model;

/// <summary>A hardware component (CPU, GPU, Super I/O chip, cooler…). Components form a tree via <see cref="ParentId"/>.</summary>
/// <param name="Id">Stable id, prefixed by source, e.g. <c>lhm:/lpc/nct6798d/0</c>.</param>
/// <param name="Screen">(v4) The hardware has a screen the app can draw on (a cooler's LCD, <c>docs/SCREEN-PIPE.md</c>); null = none.</param>
[MessagePackObject]
public sealed record HardwareInfo(
    [property: Key(0)] string Id,
    [property: Key(1)] string Name,
    [property: Key(2)] HardwareKind Kind,
    [property: Key(3)] string? ParentId,
    [property: Key(4)] string Source,
    [property: Key(5)] ScreenInfo? Screen = null);

/// <summary>A screen on a piece of hardware (v4): its size in pixels. Frames for it are JPEGs of exactly this size.</summary>
[MessagePackObject]
public sealed record ScreenInfo(
    [property: Key(0)] int Width,
    [property: Key(1)] int Height)
{
    /// <summary>Largest width or height a screen may report (<c>docs/SCREEN-PIPE.md</c>).</summary>
    public const int MaxDimension = 1024;

    /// <summary>True when both sides are 1–<see cref="MaxDimension"/> (a newer or damaged peer may send anything).</summary>
    [IgnoreMember]
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsValid => Width is >= 1 and <= MaxDimension && Height is >= 1 and <= MaxDimension;
}

/// <summary>Describes one sensor. Values arrive separately, in catalog order, as <see cref="SensorReading"/>s.</summary>
/// <param name="Id">Stable id, prefixed by source, e.g. <c>lhm:/lpc/nct6798d/0/voltage/0</c>. Layouts and renames key on this.</param>
/// <param name="NominalValue">Expected value for voltage rails (e.g. 12.0 for +12V); null when not applicable.</param>
[MessagePackObject]
public sealed record SensorInfo(
    [property: Key(0)] string Id,
    [property: Key(1)] string HardwareId,
    [property: Key(2)] string Name,
    [property: Key(3)] SensorKind Kind,
    [property: Key(4)] string Unit,
    [property: Key(5)] string Source,
    [property: Key(6)] double? NominalValue);

/// <summary>The full set of hardware and sensors a source (or the merged collector) currently exposes.</summary>
/// <param name="Version">Increments whenever the set changes, so value frames can be matched to the right catalog.</param>
[MessagePackObject]
public sealed record SensorCatalog(
    [property: Key(0)] int Version,
    [property: Key(1)] IReadOnlyList<HardwareInfo> Hardware,
    [property: Key(2)] IReadOnlyList<SensorInfo> Sensors)
{
    public static SensorCatalog Empty { get; } = new(0, [], []);
}

/// <summary>One sensor's current value plus the source's own session min/max. Stored in catalog order.</summary>
public readonly record struct SensorReading(float? Value, float? Min, float? Max)
{
    public static SensorReading None => default;
}
