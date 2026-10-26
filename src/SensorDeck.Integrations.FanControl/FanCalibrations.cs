using System.Text.Json;

namespace SensorDeck.Integrations.FanControl;

/// <summary>
/// A fan's speed range as FanControl measured it ("Calibrate" / pairing): the RPM at 100 % and the lowest
/// spinning RPM. <see cref="FanIdentifier"/> is FanControl's sensor identifier, which for LibreHardwareMonitor fans is
/// LHM's own (e.g. "/lpc/nct6687d/fan/2").
/// </summary>
public sealed record FanCalibration(string FanIdentifier, float MaxRpm, float MinRpm);

/// <summary>Reads FanControl's saved configs (read-only) for fan calibrations and the user's fan names.</summary>
public static class FanCalibrations
{
    private const long MaxConfigBytes = 5 * 1024 * 1024;

    /// <summary>Calibrated fans, keyed by FanControl's fan identifier (the highest calibration across all configs).</summary>
    public static IReadOnlyDictionary<string, FanCalibration> Read(FanControlInstall install)
    {
        ArgumentNullException.ThrowIfNull(install);
        var result = new Dictionary<string, FanCalibration>(StringComparer.OrdinalIgnoreCase);
        foreach (var config in FanControlConfigs.List(install))
        {
            foreach (var calibration in ReadFile(config.FullPath))
            {
                if (!result.TryGetValue(calibration.FanIdentifier, out var known) || calibration.MaxRpm > known.MaxRpm)
                {
                    result[calibration.FanIdentifier] = calibration;
                }
            }
        }

        return result;
    }

    /// <summary>The calibrations in one config file (empty if it can't be read).</summary>
    public static IReadOnlyList<FanCalibration> ReadFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxConfigBytes)
            {
                return [];
            }

            using var stream = File.OpenRead(path);
            using var doc = JsonDocument.Parse(stream, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (!doc.RootElement.TryGetProperty("FanControl", out var fanControl)
                || !fanControl.TryGetProperty("Controls", out var controls)
                || controls.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var list = new List<FanCalibration>();
            foreach (var control in controls.EnumerateArray())
            {
                if (!control.TryGetProperty("PairedFanSensor", out var paired) || paired.ValueKind != JsonValueKind.Object
                    || !paired.TryGetProperty("Identifier", out var id) || id.GetString() is not { Length: > 0 } fan
                    || !control.TryGetProperty("Calibration", out var points) || points.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                // Each point is [percent, rpm, flag].
                var rpms = points.EnumerateArray()
                    .Where(p => p.ValueKind == JsonValueKind.Array && p.GetArrayLength() >= 2 && p[1].ValueKind == JsonValueKind.Number)
                    .Select(p => p[1].GetDouble())
                    .Where(r => r > 0 && double.IsFinite(r))
                    .ToList();
                if (rpms.Count > 0)
                {
                    list.Add(new FanCalibration(fan, (float)rpms.Max(), (float)rpms.Min()));
                }
            }

            return list;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException
                                       or FormatException)
        {
            return []; // FormatException: e.g. a number out of range for GetDouble
        }
    }
}
