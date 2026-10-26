using SensorDeck.Integrations.FanControl;

namespace SensorDeck.App.Services;

/// <summary>
/// What "100 %" means for each fan: the maximum RPM FanControl measured when it calibrated the fan, or else the
/// highest RPM SensorDeck has seen (remembered across restarts).
/// </summary>
public static class FanScale
{
    /// <summary>Readings above this are sensor glitches (e.g. 65535 from a misread register), not a real fan.</summary>
    internal const float MaxPlausibleRpm = 30_000;

    private static readonly TimeSpan ReloadAfter = TimeSpan.FromMinutes(2);
    private static readonly Lock Gate = new();
    private static AppSettings? _settings;
    private static FanProfileService? _fans;
    private static IReadOnlyDictionary<string, FanCalibration> _calibrations = new Dictionary<string, FanCalibration>();
    private static DateTime _loadedUtc = DateTime.MinValue;
    private static DateTime _savedUtc = DateTime.MinValue;
    private static bool _dirty;

    /// <summary>Call once at startup.</summary>
    public static void Initialize(AppSettings settings, FanProfileService fans)
    {
        _settings = settings;
        _fans = fans;
    }

    /// <summary>The RPM shown as 100 % for this fan, and where it came from; null max when nothing is known yet.</summary>
    public static (float? MaxRpm, bool Calibrated) MaxFor(SensorItem fan)
    {
        ArgumentNullException.ThrowIfNull(fan);
        lock (Gate)
        {
            ReloadIfStale();

            // FanControl names LibreHardwareMonitor fans by LHM's own identifier; ours adds the "lhm:" prefix.
            var colon = fan.Id.IndexOf(':', StringComparison.Ordinal);
            var fanControlId = colon >= 0 ? fan.Id[(colon + 1)..] : fan.Id;
            if (_calibrations.TryGetValue(fanControlId, out var calibration) && calibration.MaxRpm > 0)
            {
                return (calibration.MaxRpm, true);
            }

            // The session max counts only when plausible (same bound as Observe): one glitched 65535 RPM reading would
            // otherwise squash the bar for the rest of the session.
            var learned = _settings?.FanMaxRpm.GetValueOrDefault(fan.Id) ?? 0;
            var sessionMax = fan.Max is { } m && float.IsFinite(m) && m <= MaxPlausibleRpm ? m : 0;
            var seen = Math.Max(learned, sessionMax);
            return (seen > 0 ? (float?)seen : null, false);
        }
    }

    /// <summary>Remembers a new highest RPM for a fan without a calibration (saved now and then).</summary>
    public static void Observe(SensorItem fan)
    {
        ArgumentNullException.ThrowIfNull(fan);
        if (_settings is null || fan.Value is not { } rpm || !float.IsFinite(rpm) || rpm <= 0 || rpm > MaxPlausibleRpm)
        {
            return;
        }

        lock (Gate)
        {
            if (rpm > _settings.FanMaxRpm.GetValueOrDefault(fan.Id))
            {
                _settings.FanMaxRpm[fan.Id] = rpm;
                _dirty = true;
            }

            if (_dirty && DateTime.UtcNow - _savedUtc > TimeSpan.FromMinutes(1))
            {
                _dirty = false;
                _savedUtc = DateTime.UtcNow;
                _settings.Save();
            }
        }
    }

    private static void ReloadIfStale()
    {
        if (DateTime.UtcNow - _loadedUtc < ReloadAfter || AppMode.IsReplay)
        {
            return;
        }

        _loadedUtc = DateTime.UtcNow;
        try
        {
            var install = _fans?.Install ?? FanControlLocator.Find(_settings?.FanControlExePath);
            _calibrations = install is null ? new Dictionary<string, FanCalibration>() : FanCalibrations.Read(install);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            // FanControl saving a config at that moment, or its folder unreadable: keep the last calibrations and try
            // again at the next reload (this runs inside property getters, which must not throw).
        }
    }
}
