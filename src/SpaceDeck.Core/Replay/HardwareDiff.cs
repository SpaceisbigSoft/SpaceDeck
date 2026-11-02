using System.Globalization;
using System.Text;
using SpaceDeck.Core.Model;

namespace SpaceDeck.Core.Replay;

/// <summary>
/// Compares two hardware reports (or recordings), e.g. before and after updating LibreHardwareMonitor: which sensors
/// appeared, disappeared, were renamed or changed kind, and which readings moved suspiciously (a voltage that
/// suddenly reads 10 % differently usually means its scaling changed).
/// </summary>
public sealed class HardwareDiff
{
    private HardwareDiff()
    {
    }

    public List<string> AddedHardware { get; } = [];

    public List<string> RemovedHardware { get; } = [];

    public List<SensorInfo> AddedSensors { get; } = [];

    /// <summary>Gone sensors: dashboards, alert rules and renames pointing at these need relinking.</summary>
    public List<SensorInfo> RemovedSensors { get; } = [];

    public List<(SensorInfo Before, SensorInfo After)> Renamed { get; } = [];

    public List<(SensorInfo Before, SensorInfo After)> KindChanged { get; } = [];

    public List<(SensorInfo Before, SensorInfo After)> NominalChanged { get; } = [];

    /// <summary>Same sensor, reading far from before (a hint; reports are single moments).</summary>
    public List<(SensorInfo Sensor, float Before, float After)> ValueShifts { get; } = [];

    /// <summary>True when ids disappeared or changed meaning: saved layouts may need relinking.</summary>
    public bool HasBreakingChanges => RemovedSensors.Count > 0 || KindChanged.Count > 0;

    public bool IsEmpty => AddedHardware.Count == 0 && RemovedHardware.Count == 0 && AddedSensors.Count == 0 && RemovedSensors.Count == 0
        && Renamed.Count == 0 && KindChanged.Count == 0 && NominalChanged.Count == 0 && ValueShifts.Count == 0;

    /// <summary>Compares the first sample of each recording.</summary>
    public static HardwareDiff Compare(SensorRecording before, SensorRecording after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var diff = new HardwareDiff();
        var (oldCatalog, oldValues) = First(before);
        var (newCatalog, newValues) = First(after);

        var oldHardware = oldCatalog.Hardware.Select(h => h.Id).ToHashSet(StringComparer.Ordinal);
        var newHardware = newCatalog.Hardware.Select(h => h.Id).ToHashSet(StringComparer.Ordinal);
        diff.AddedHardware.AddRange(newCatalog.Hardware.Where(h => !oldHardware.Contains(h.Id)).Select(h => $"{h.Name} ({h.Id})"));
        diff.RemovedHardware.AddRange(oldCatalog.Hardware.Where(h => !newHardware.Contains(h.Id)).Select(h => $"{h.Name} ({h.Id})"));

        var oldIndex = Index(oldCatalog);
        var newIndex = Index(newCatalog);
        foreach (var (id, (info, i)) in newIndex)
        {
            if (!oldIndex.TryGetValue(id, out var old))
            {
                diff.AddedSensors.Add(info);
                continue;
            }

            if (!string.Equals(old.Info.Name, info.Name, StringComparison.Ordinal))
            {
                diff.Renamed.Add((old.Info, info));
            }

            if (old.Info.Kind != info.Kind)
            {
                diff.KindChanged.Add((old.Info, info));
                continue;
            }

            if (old.Info.NominalValue != info.NominalValue)
            {
                diff.NominalChanged.Add((old.Info, info));
            }

            if (Get(oldValues, old.Index) is { } a && Get(newValues, i) is { } b && IsShift(info.Kind, a, b))
            {
                diff.ValueShifts.Add((info, a, b));
            }
        }

        diff.RemovedSensors.AddRange(oldCatalog.Sensors.Where(s => !newIndex.ContainsKey(s.Id)));
        return diff;
    }

    /// <summary>A readable report, one finding per line.</summary>
    public string ToText()
    {
        if (IsEmpty)
        {
            return "No differences: same hardware, sensors, names and kinds.";
        }

        var c = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        Section("Hardware added", AddedHardware);
        Section("Hardware removed", RemovedHardware);
        Section("Sensors removed (dashboards/alerts using them need relinking)", RemovedSensors.Select(s => $"{s.Name} [{s.Kind}]  {s.Id}"));
        Section("Sensors added", AddedSensors.Select(s => $"{s.Name} [{s.Kind}]  {s.Id}"));
        Section("Renamed (ids unchanged, so layouts still work)", Renamed.Select(r => $"{r.Before.Name} → {r.After.Name}  {r.After.Id}"));
        Section("Kind changed (check tiles and alert rules)", KindChanged.Select(r => $"{r.After.Name}: {r.Before.Kind} → {r.After.Kind}  {r.After.Id}"));
        Section("Nominal voltage changed", NominalChanged.Select(r => $"{r.After.Name}: {Fmt(r.Before.NominalValue)} → {Fmt(r.After.NominalValue)}  {r.After.Id}"));
        Section("Readings moved a lot (possible scaling change; re-check under the same load)", ValueShifts.Select(v => string.Create(c, $"{v.Sensor.Name} [{v.Sensor.Kind}]: {v.Before:0.###} → {v.After:0.###}  {v.Sensor.Id}")));
        return sb.ToString().TrimEnd();

        void Section(string title, IEnumerable<string> lines)
        {
            var list = lines.ToList();
            if (list.Count == 0)
            {
                return;
            }

            sb.AppendLine(c, $"{title} ({list.Count}):");
            foreach (var line in list)
            {
                sb.AppendLine(c, $"  {line}");
            }

            sb.AppendLine();
        }

        static string Fmt(double? v) => v?.ToString("0.###", CultureInfo.InvariantCulture) ?? "none";
    }

    /// <summary>Thresholds tuned to catch scaling/offset bugs, not normal load changes.</summary>
    internal static bool IsShift(SensorKind kind, float before, float after) => kind switch
    {
        SensorKind.Voltage => Math.Abs(after - before) > 0.05 && Math.Abs(after - before) > Math.Abs(before) * 0.10,
        SensorKind.Temperature => Math.Abs(after - before) > 25,
        SensorKind.Fan => Math.Abs(after - before) > 500 && Math.Abs(after - before) > Math.Abs(before) * 0.5,
        _ => false,
    };

    private static (SensorCatalog Catalog, float?[] Values) First(SensorRecording recording)
    {
        var frame = recording.Frames[0];
        return (recording.Catalogs[frame.CatalogIndex], frame.Values);
    }

    private static Dictionary<string, (SensorInfo Info, int Index)> Index(SensorCatalog catalog)
    {
        var map = new Dictionary<string, (SensorInfo Info, int Index)>(StringComparer.Ordinal);
        for (var i = 0; i < catalog.Sensors.Count; i++)
        {
            map.TryAdd(catalog.Sensors[i].Id, (catalog.Sensors[i], i));
        }

        return map;
    }

    private static float? Get(float?[] values, int index) => index < values.Length ? values[index] : null;
}
