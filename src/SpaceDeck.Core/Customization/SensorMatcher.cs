using SpaceDeck.Core.Model;

namespace SpaceDeck.Core.Customization;

/// <summary>A sensor that could replace a missing one.</summary>
public sealed record SensorCandidate(string Id, string Name, SensorKind Kind, string Hardware);

/// <summary>
/// Suggests replacements for a sensor that disappeared (GPU/drive swapped, BIOS update renumbered a chip), so tiles and
/// alerts can be re-linked in one click. Only same-kind sensors qualify; same name and same hardware family rank first.
/// </summary>
public static class SensorMatcher
{
    public static IReadOnlyList<SensorCandidate> Suggest(string missingId, KnownSensor? known, IEnumerable<SensorCandidate> candidates, int max = 6)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var kind = known?.Kind ?? KindFromId(missingId);
        if (kind is null)
        {
            return [];
        }

        var family = Family(missingId);
        var tail = Tail(missingId);
        var name = known?.Name ?? "";

        return
        [
            .. candidates
                .Where(c => c.Kind == kind && c.Id != missingId)
                .Select(c => (Candidate: c, Score: Score(c, name, family, tail)))
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.Candidate.Name, StringComparer.OrdinalIgnoreCase)
                .Take(max)
                .Select(x => x.Candidate),
        ];
    }

    private static double Score(SensorCandidate c, string name, string family, string tail)
    {
        var score = 0.1; // same kind
        if (name.Length > 0)
        {
            if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                score += 5;
            }
            else
            {
                score += 3 * Similarity(c.Name, name);
            }
        }

        if (Family(c.Id) == family)
        {
            score += 2;
        }

        if (Tail(c.Id) == tail)
        {
            score += 0.5;
        }

        return score;
    }

    /// <summary>Shared words / all words, case-insensitive (0–1).</summary>
    internal static double Similarity(string a, string b)
    {
        static HashSet<string> Words(string s) =>
            s.Split(new[] { ' ', '#', '-', '_', '(', ')' }, StringSplitOptions.RemoveEmptyEntries)
             .Select(w => w.ToUpperInvariant())
             .ToHashSet();

        var wa = Words(a);
        var wb = Words(b);
        if (wa.Count == 0 || wb.Count == 0)
        {
            return 0;
        }

        var shared = wa.Count(wb.Contains);
        return shared / (double)wa.Union(wb).Count();
    }

    /// <summary>"gpu", "cpu", "storage", "board" or the first id segment: "/gpu-nvidia/0/…" → "gpu".</summary>
    internal static string Family(string id)
    {
        // Ids are prefixed by their source ("lhm:/gpu-nvidia/0/…"); the family is the first path segment after it.
        var path = id[(id.IndexOf(':', StringComparison.Ordinal) + 1)..];
        var first = path.TrimStart('/').Split('/', 2)[0].ToUpperInvariant();
        return first switch
        {
            _ when first.Contains("GPU", StringComparison.Ordinal) => "gpu",
            _ when first.Contains("CPU", StringComparison.Ordinal) => "cpu",
            "NVME" or "HDD" or "SSD" or "ATA" or "SCSI" => "storage",
            "LPC" or "MAINBOARD" or "MOTHERBOARD" => "board",
            _ => first.ToLowerInvariant(),
        };
    }

    /// <summary>The last two id segments, e.g. "temperature/0".</summary>
    internal static string Tail(string id)
    {
        var parts = id.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? $"{parts[^2]}/{parts[^1]}".ToUpperInvariant() : id.ToUpperInvariant();
    }

    /// <summary>Kind from an LHM id's type segment ("/…/temperature/0"), when the sensor was never seen.</summary>
    internal static SensorKind? KindFromId(string id)
    {
        var parts = id.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return null;
        }

        return parts[^2].ToUpperInvariant() switch
        {
            "TEMPERATURE" => SensorKind.Temperature,
            "VOLTAGE" => SensorKind.Voltage,
            "FAN" => SensorKind.Fan,
            "CONTROL" => SensorKind.Control,
            "POWER" => SensorKind.Power,
            "LOAD" => SensorKind.Load,
            "CLOCK" => SensorKind.Clock,
            "CURRENT" => SensorKind.Current,
            "DATA" => SensorKind.Data,
            "SMALLDATA" => SensorKind.SmallData,
            "THROUGHPUT" => SensorKind.Throughput,
            "LEVEL" => SensorKind.Level,
            "FLOW" => SensorKind.Flow,
            "FREQUENCY" => SensorKind.Frequency,
            "FACTOR" => SensorKind.Factor,
            "TIMESPAN" => SensorKind.TimeSpan,
            "ENERGY" => SensorKind.Energy,
            "NOISE" => SensorKind.Noise,
            "HUMIDITY" => SensorKind.Humidity,
            _ => null,
        };
    }
}
