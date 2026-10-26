using System.IO;
using SensorDeck.Core.Customization;

namespace SensorDeck.App.Services;

/// <summary>
/// Owns sensors.json: the user's renames, hidden sensors and scale/offset fixes, plus the last-known name of every
/// sensor seen (for re-linking sensors that disappear). Changes apply live through <see cref="Changed"/>.
/// </summary>
public sealed class SensorCustomizationService
{
    private readonly string _path = Path.Combine(AppSettings.Directory, "sensors.json");
    private readonly SensorCustomizations _data;

    public SensorCustomizationService()
    {
        _data = CustomizationStore.Load(_path);
    }

    /// <summary>Raised with the sensor id whose customization changed (null = many changed, e.g. a profile import).</summary>
    public event Action<string?>? Changed;

    public int Count => _data.Overrides.Count;

    public SensorOverride? Get(string sensorId) => _data.Overrides.TryGetValue(sensorId, out var o) ? o : null;

    public KnownSensor? Known(string sensorId) => _data.Known.TryGetValue(sensorId, out var k) ? k : null;

    /// <summary>Saves (or clears, when empty) a sensor's customization.</summary>
    public void Set(string sensorId, SensorOverride? value)
    {
        if (value is null || value.IsEmpty)
        {
            _data.Overrides.Remove(sensorId);
        }
        else
        {
            _data.Overrides[sensorId] = value;
        }

        Save();
        Changed?.Invoke(sensorId);
    }

    public void ResetAll()
    {
        _data.Overrides.Clear();
        Save();
        Changed?.Invoke(null);
    }

    /// <summary>Moves a customization (and last-known info) to a new id when a sensor is re-linked.</summary>
    public void Move(string fromId, string toId)
    {
        var changed = false;
        if (_data.Overrides.Remove(fromId, out var o))
        {
            // Keep the new sensor's own customization if it already has one.
            _data.Overrides.TryAdd(toId, o);
            changed = true;
        }

        changed |= _data.Known.Remove(fromId);
        if (changed)
        {
            Save();
            Changed?.Invoke(null);
        }
    }

    /// <summary>Remembers names/kinds of the sensors in the current catalog (saved only when something new appears).</summary>
    public void RecordKnown(IEnumerable<SensorItem> sensors)
    {
        var changed = false;
        foreach (var s in sensors)
        {
            if (!_data.Known.TryGetValue(s.Id, out var k) || k.Name != s.OriginalName || k.Kind != s.Kind || k.Hardware != s.GroupName)
            {
                _data.Known[s.Id] = new KnownSensor { Name = s.OriginalName, Kind = s.Kind, Hardware = s.GroupName };
                changed = true;
            }
        }

        if (changed)
        {
            Save();
        }
    }

    public BoardProfile ExportProfile(string board) => new()
    {
        Board = board,
        Overrides = _data.Overrides.ToDictionary(kv => kv.Key, kv => kv.Value.Clone(), StringComparer.Ordinal),
    };

    /// <summary>Applies a board profile on top of the current customizations (profile wins per sensor).</summary>
    public int Import(BoardProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        foreach (var (id, o) in profile.Overrides)
        {
            _data.Overrides[id] = o.Clone();
        }

        Save();
        Changed?.Invoke(null);
        return profile.Overrides.Count;
    }

    private void Save()
    {
        try
        {
            CustomizationStore.Save(_path, _data);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Kept in memory; next save retries.
        }
    }
}
