namespace SensorDeck.Core.Alerts;

/// <summary>How worrying a reading is.</summary>
public enum AlertLevel
{
    Normal,
    Warning,
    Critical,
}

/// <summary>
/// Warning/critical limits for one sensor, in the sensor's base unit (°C, V, RPM…). Any limit may be null (off).
/// "Above" limits fire at or above the value, "below" limits at or below it.
/// </summary>
public sealed class AlertRule
{
    public string SensorId { get; set; } = "";

    public bool Enabled { get; set; } = true;

    public double? WarningAbove { get; set; }

    public double? CriticalAbove { get; set; }

    public double? WarningBelow { get; set; }

    public double? CriticalBelow { get; set; }

    public bool HasLimits => WarningAbove is not null || CriticalAbove is not null || WarningBelow is not null || CriticalBelow is not null;

    public AlertRule Clone() => new()
    {
        SensorId = SensorId,
        Enabled = Enabled,
        WarningAbove = WarningAbove,
        CriticalAbove = CriticalAbove,
        WarningBelow = WarningBelow,
        CriticalBelow = CriticalBelow,
    };

    /// <summary>
    /// The level a value is at right now. <paramref name="hysteresis"/> (≥ 0) loosens every limit by that fraction of
    /// its magnitude; trackers use it to decide when a level has clearly ended.
    /// </summary>
    public AlertLevel Classify(double value, double hysteresis = 0)
    {
        if (!Enabled || double.IsNaN(value))
        {
            return AlertLevel.Normal;
        }

        static bool AtOrAbove(double v, double? limit, double h) => limit is { } l && v >= l - Margin(l, h);
        static bool AtOrBelow(double v, double? limit, double h) => limit is { } l && v <= l + Margin(l, h);

        if (AtOrAbove(value, CriticalAbove, hysteresis) || AtOrBelow(value, CriticalBelow, hysteresis))
        {
            return AlertLevel.Critical;
        }

        if (AtOrAbove(value, WarningAbove, hysteresis) || AtOrBelow(value, WarningBelow, hysteresis))
        {
            return AlertLevel.Warning;
        }

        return AlertLevel.Normal;
    }

    private static double Margin(double limit, double fraction) => Math.Max(Math.Abs(limit) * fraction, fraction > 0 ? 0.01 : 0);
}

/// <summary>A sensor moving from one level to another.</summary>
public sealed record AlertTransition(string SensorId, AlertLevel From, AlertLevel To, double Value, long TimestampUnixMs)
{
    public bool IsRaise => To > From;
}

/// <summary>
/// Debounce and hysteresis for one sensor, so a reading hovering on a limit doesn't flap:
/// a higher level must hold for <see cref="RaiseSamples"/> readings in a row before it counts, and a level only ends
/// once the reading is clearly back (limits loosened by <see cref="Hysteresis"/>) for <see cref="ClearSamples"/> readings.
/// </summary>
public sealed class AlertTracker
{
    public const int DefaultRaiseSamples = 3;
    public const int DefaultClearSamples = 5;
    public const double DefaultHysteresis = 0.02;

    private int _warningRun;
    private int _criticalRun;
    private int _clearCount;

    public AlertTracker(string sensorId)
    {
        SensorId = sensorId;
    }

    public string SensorId { get; }

    public AlertLevel Level { get; private set; } = AlertLevel.Normal;

    public int RaiseSamples { get; init; } = DefaultRaiseSamples;

    public int ClearSamples { get; init; } = DefaultClearSamples;

    public double Hysteresis { get; init; } = DefaultHysteresis;

    /// <summary>Feeds one reading. Returns a transition when the confirmed level changes, else null.</summary>
    public AlertTransition? Update(AlertRule? rule, double? value, long timestampUnixMs)
    {
        if (rule is null || !rule.Enabled || !rule.HasLimits)
        {
            // Rule removed or disabled: end any active alert straight away.
            return Reset(value ?? double.NaN, timestampUnixMs);
        }

        if (value is not { } v || double.IsNaN(v) || double.IsInfinity(v))
        {
            // No reading: neither raise nor clear.
            _warningRun = 0;
            _criticalRun = 0;
            _clearCount = 0;
            return null;
        }

        // Separate runs per level, so a reading bouncing between warning and critical still raises (to warning).
        var now = rule.Classify(v);
        _warningRun = now >= AlertLevel.Warning ? _warningRun + 1 : 0;
        _criticalRun = now >= AlertLevel.Critical ? _criticalRun + 1 : 0;
        if (now > Level)
        {
            _clearCount = 0;
            if (_criticalRun >= RaiseSamples && Level < AlertLevel.Critical)
            {
                return Move(AlertLevel.Critical, v, timestampUnixMs);
            }

            if (_warningRun >= RaiseSamples && Level < AlertLevel.Warning)
            {
                return Move(AlertLevel.Warning, v, timestampUnixMs);
            }

            return null;
        }

        var loose = rule.Classify(v, Hysteresis);
        if (loose < Level)
        {
            if (++_clearCount >= ClearSamples)
            {
                return Move(loose, v, timestampUnixMs);
            }
        }
        else
        {
            _clearCount = 0;
        }

        return null;
    }

    private AlertTransition? Reset(double value, long timestampUnixMs)
    {
        _warningRun = 0;
        _criticalRun = 0;
        _clearCount = 0;
        return Level == AlertLevel.Normal ? null : Move(AlertLevel.Normal, value, timestampUnixMs);
    }

    private AlertTransition Move(AlertLevel to, double value, long timestampUnixMs)
    {
        var transition = new AlertTransition(SensorId, Level, to, value, timestampUnixMs);
        Level = to;
        _clearCount = 0;
        if (to < AlertLevel.Critical)
        {
            _criticalRun = 0;
        }

        if (to < AlertLevel.Warning)
        {
            _warningRun = 0;
        }
        return transition;
    }
}
