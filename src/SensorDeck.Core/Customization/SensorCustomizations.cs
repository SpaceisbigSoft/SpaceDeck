using System.Text.Json;
using System.Text.Json.Serialization;
using SensorDeck.Core.Model;

namespace SensorDeck.Core.Customization;

/// <summary>
/// The user's changes to one sensor: a display name, hidden, and a linear correction (value × scale + offset) for
/// boards that report a reading at the wrong scale. Applied in the app; the collector always sends raw values.
/// </summary>
public sealed class SensorOverride
{
    /// <summary>Display name; null keeps the hardware's name.</summary>
    public string? Name { get; set; }

    /// <summary>Hidden from lists and pickers (still works where already used).</summary>
    public bool Hidden { get; set; }

    public double Scale { get; set; } = 1;

    public double Offset { get; set; }

    [JsonIgnore]
    public bool HasCorrection => Scale != 1 || Offset != 0;

    [JsonIgnore]
    public bool IsEmpty => string.IsNullOrWhiteSpace(Name) && !Hidden && !HasCorrection;

    public double Apply(double raw) => (raw * Scale) + Offset;

    public SensorOverride Clone() => new() { Name = Name, Hidden = Hidden, Scale = Scale, Offset = Offset };
}

/// <summary>What a sensor was last seen as, so a sensor that disappears (hardware swapped) can be matched to a new one.</summary>
public sealed class KnownSensor
{
    public string Name { get; set; } = "";

    public SensorKind Kind { get; set; }

    /// <summary>Hardware/group name, e.g. "NVIDIA GeForce RTX 3060 Ti".</summary>
    public string Hardware { get; set; } = "";
}

/// <summary>All per-sensor customizations plus the last-known names of every sensor seen. Saved as sensors.json.</summary>
public sealed class SensorCustomizations
{
    public int Version { get; set; } = 1;

    public Dictionary<string, SensorOverride> Overrides { get; set; } = new(StringComparer.Ordinal);

    public Dictionary<string, KnownSensor> Known { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// A shareable set of customizations for one motherboard model (renames, hidden sensors, scale fixes), so people
/// with the same board get correct names and voltages. Sensor ids from LibreHardwareMonitor are stable per chip.
/// </summary>
public sealed class BoardProfile
{
    public int Version { get; set; } = 1;

    /// <summary>The motherboard name the profile was made on, e.g. "MSI PRO Z790-A WIFI (MS-7E07)".</summary>
    public string Board { get; set; } = "";

    public Dictionary<string, SensorOverride> Overrides { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>JSON load/save for <see cref="SensorCustomizations"/> and <see cref="BoardProfile"/>. Bad files never throw on load.</summary>
public static class CustomizationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        Converters = { new JsonStringEnumConverter() },
    };

    public static SensorCustomizations Load(string path)
    {
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<SensorCustomizations>(File.ReadAllText(path), JsonOptions) is { } loaded)
            {
                return Repair(loaded);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Unreadable: start fresh rather than fail.
        }

        return new SensorCustomizations();
    }

    /// <summary>Writes via a temp file so a crash mid-write can't leave a half file.</summary>
    public static void Save(string path, SensorCustomizations customizations)
    {
        ArgumentNullException.ThrowIfNull(customizations);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(customizations, JsonOptions));
        File.Move(temp, path, overwrite: true);
    }

    public static void ExportProfile(string path, BoardProfile profile) =>
        File.WriteAllText(path, JsonSerializer.Serialize(profile, JsonOptions));

    /// <summary>Largest file <see cref="ImportProfile"/> accepts; real profiles are a few KB.</summary>
    public const long MaxProfileBytes = 4L * 1024 * 1024;

    /// <exception cref="InvalidDataException">Not a board profile.</exception>
    public static BoardProfile ImportProfile(string path)
    {
        if (new FileInfo(path).Length > MaxProfileBytes)
        {
            throw new InvalidDataException("This file is too big to be a SensorDeck board profile.");
        }

        BoardProfile? profile;
        try
        {
            profile = JsonSerializer.Deserialize<BoardProfile>(File.ReadAllText(path), JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("This file isn't a SensorDeck board profile.", ex);
        }

        if (profile?.Overrides is not { Count: > 0 })
        {
            throw new InvalidDataException("This file isn't a SensorDeck board profile (no sensor settings in it).");
        }

        profile.Overrides = Clean(profile.Overrides);
        return profile;
    }

    private static SensorCustomizations Repair(SensorCustomizations c)
    {
        c.Overrides = Clean(c.Overrides);
        c.Known = new Dictionary<string, KnownSensor>(
            (c.Known ?? new Dictionary<string, KnownSensor>()).Where(kv => !string.IsNullOrEmpty(kv.Key) && kv.Value is not null),
            StringComparer.Ordinal);
        return c;
    }

    private static Dictionary<string, SensorOverride> Clean(Dictionary<string, SensorOverride>? overrides)
    {
        var result = new Dictionary<string, SensorOverride>(StringComparer.Ordinal);
        foreach (var (id, o) in overrides ?? new Dictionary<string, SensorOverride>())
        {
            if (string.IsNullOrEmpty(id) || o is null)
            {
                continue;
            }

            // A zero or non-finite scale would wipe out the reading; treat it as "no correction".
            if (!double.IsFinite(o.Scale) || o.Scale == 0)
            {
                o.Scale = 1;
            }

            if (!double.IsFinite(o.Offset))
            {
                o.Offset = 0;
            }

            o.Name = string.IsNullOrWhiteSpace(o.Name) ? null : o.Name.Trim();
            if (!o.IsEmpty)
            {
                result[id] = o;
            }
        }

        return result;
    }
}
