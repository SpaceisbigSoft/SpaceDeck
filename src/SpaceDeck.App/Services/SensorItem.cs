using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using SpaceDeck.Core.Alerts;
using SpaceDeck.Core.Customization;
using SpaceDeck.Core.Formatting;
using SpaceDeck.Core.Model;

namespace SpaceDeck.App.Services;

/// <summary>Display options shared by every sensor row.</summary>
public sealed class DisplayOptions
{
    public TemperatureUnit TemperatureUnit { get; set; } = TemperatureUnit.Celsius;
}

/// <summary>How worrying a reading is. Drives accent/critical coloring of numbers and bars.</summary>
public enum ValueLevel
{
    Normal,
    Warning,
    Critical,
}

/// <summary>
/// One sensor as the UI sees it: identity from the catalog plus live value, min, max and recent history.
/// Only raises change notifications for properties whose value actually changed.
/// </summary>
public sealed class SensorItem : ObservableObject
{
    /// <summary>One hour at the default 1 s poll interval.</summary>
    public const int HistoryCapacity = 3600;

    // Fallback warning/critical points for temperatures that have no alert rule (rules drive Level otherwise).
    private const float TemperatureWarning = 80f;
    private const float TemperatureCritical = 90f;

    private readonly DisplayOptions _display;
    private ValueLevel _level;
    private double _scaleMaximum;

    public SensorItem(SensorInfo info, string groupName, HardwareKind hardwareKind, DisplayOptions display)
    {
        ArgumentNullException.ThrowIfNull(info);
        _display = display;
        Id = info.Id;
        HardwareId = info.HardwareId;
        Name = info.Name;
        OriginalName = info.Name;
        GroupName = groupName;
        HardwareKind = hardwareKind;
        Kind = info.Kind;
        Nominal = info.NominalValue;
        Unit = info.Unit ?? "";
        IsAdvanced = info.HardwareId != "calc" && (Kind is SensorKind.Timing or SensorKind.Other
            || info.Name.Contains("Limit", StringComparison.OrdinalIgnoreCase)
            || info.Name.Contains("Resolution", StringComparison.OrdinalIgnoreCase)
            || info.Name.Contains("Distance to TjMax", StringComparison.OrdinalIgnoreCase)
            || info.Name.StartsWith("D3D ", StringComparison.Ordinal));
        _scaleMaximum = InitialScale(Kind, Nominal);
    }

    public string Id { get; }

    public string HardwareId { get; }

    /// <summary>The name shown everywhere: the user's rename, or the hardware's own name.</summary>
    public string Name { get; private set; }

    /// <summary>The hardware's own name. Use this (not <see cref="Name"/>) to recognize sensors in code.</summary>
    public string OriginalName { get; }

    /// <summary>Hidden by the user: left out of lists and pickers, still works where already used.</summary>
    public bool IsHidden { get; private set; }

    public bool IsRenamed => !string.Equals(Name, OriginalName, StringComparison.Ordinal);

    /// <summary>A scale/offset correction is applied to the readings.</summary>
    public bool HasCorrection => _correction is not null;

    /// <summary>Tooltip explaining customizations, e.g. "Originally "System Fan #1" · corrected…"; null when none.</summary>
    public string? CustomizationNote
    {
        get
        {
            var parts = new List<string>();
            if (IsRenamed)
            {
                parts.Add($"Originally \"{OriginalName}\"");
            }

            if (_correction is { } c)
            {
                parts.Add(System.FormattableString.Invariant($"corrected: reading × {c.Scale:0.####} {(c.Offset < 0 ? "−" : "+")} {Math.Abs(c.Offset):0.####}"));
            }

            if (IsHidden)
            {
                parts.Add("hidden");
            }

            return parts.Count == 0 ? null : string.Join(" · ", parts);
        }
    }

    /// <summary>Group header, e.g. "MSI PRO Z790-A WIFI · Nuvoton NCT6687D".</summary>
    public string GroupName { get; }

    public HardwareKind HardwareKind { get; }

    /// <summary>The collector's base unit ("°C", "V", "RPM", "B/s"…), used for exports.</summary>
    public string Unit { get; }

    public SensorKind Kind { get; }

    public double? Nominal { get; }

    public bool HasNominal => Nominal.HasValue;

    /// <summary>A standard ATX rail (±5 % applies). False for the CMOS battery, which is judged by minimum voltage instead.</summary>
    public bool HasTolerance => Nominal is { } n && !VoltageRails.IsBattery(n);

    /// <summary>Static limits, SPD timings and per-engine counters: hidden unless "Show advanced" is on.</summary>
    public bool IsAdvanced { get; }

    private bool _isCharted;
    private AlertRule? _rule;

    /// <summary>The alert rule in effect (default or user's), set by the alert service. Drives <see cref="Level"/>.</summary>
    public AlertRule? Rule
    {
        get => _rule;
        set
        {
            _rule = value;
            OnPropertyChanged();
            UpdateLevel();
        }
    }

    /// <summary>True while this sensor is plotted on the History page (drives the picker's marker).</summary>
    public bool IsCharted
    {
        get => _isCharted;
        set => SetProperty(ref _isCharted, value);
    }

    /// <summary>
    /// False for a fan whose rule has a "below" limit but which hasn't been seen above it yet (an unused header reads
    /// 0 RPM and shouldn't alarm). Everything else is always armed.
    /// </summary>
    public bool IsRuleArmed
    {
        get
        {
            if (Kind != SensorKind.Fan || _rule is null)
            {
                return true;
            }

            var below = Math.Max(_rule.WarningBelow ?? double.MinValue, _rule.CriticalBelow ?? double.MinValue);
            return below == double.MinValue || (Max is { } max && max > below);
        }
    }

    public float? Value { get; private set; }

    public float? Min { get; private set; }

    public float? Max { get; private set; }

    /// <summary>Recent samples for sparklines and charts (backfilled from the collector on connect).</summary>
    public TimeSeries History { get; } = new(HistoryCapacity);

    /// <summary>Upper end of the bar/gauge scale: fixed for percentages and temperatures, grows with the session max otherwise.</summary>
    public double ScaleMaximum => _scaleMaximum;

    public ValueLevel Level => _level;

    public string DisplayValue => UnitFormatter.Format(Kind, Value, _display.TemperatureUnit);

    public string DisplayMin => UnitFormatter.Format(Kind, Min, _display.TemperatureUnit);

    public string DisplayMax => UnitFormatter.Format(Kind, Max, _display.TemperatureUnit);

    /// <summary>The value without its unit, for big-number displays: "35.0".</summary>
    public string DisplayNumber => SplitUnit(DisplayValue).Number;

    /// <summary>The unit alone: "°C".</summary>
    public string DisplayUnit => SplitUnit(DisplayValue).Unit;

    public RailStatus RailStatus => VoltageRails.Evaluate(Value, Nominal);

    public string DeviationText =>
        HasTolerance && Nominal is { } n && Value is { } v
            ? string.Create(CultureInfo.CurrentCulture, $"{VoltageRails.Deviation(v, n) * 100:+0.0;-0.0;0.0}%")
            : "";

    /// <summary>Signed fractional deviation from nominal (0.026 = +2.6 %), or null.</summary>
    public double? Deviation => HasTolerance && Nominal is { } n && Value is { } v ? VoltageRails.Deviation(v, n) : null;

    /// <summary>
    /// Applies the user's rename / hidden / correction. A changed correction also re-maps the history and the
    /// current, min and max values, so charts don't jump.
    /// </summary>
    internal void ApplyOverride(SensorOverride? o)
    {
        var newName = string.IsNullOrWhiteSpace(o?.Name) ? OriginalName : o!.Name!;
        var newCorrection = o is { HasCorrection: true } ? o.Clone() : null;
        var oldCorrection = _correction;

        if (!SameCorrection(oldCorrection, newCorrection))
        {
            float Remap(float v) => float.IsNaN(v) ? v : (float)Correct(newCorrection, Raw(oldCorrection, v));
            History.Map(Remap);
            _correction = newCorrection;
            Value = Value is { } v ? Remap(v) : null;
            Min = Min is { } mn ? Remap(mn) : null;
            Max = Max is { } mx ? Remap(mx) : null;
            if (Min is { } a && Max is { } b && a > b)
            {
                (Min, Max) = (b, a); // negative scale
            }

            OnPropertyChanged(nameof(Value));
            OnPropertyChanged(nameof(Min));
            OnPropertyChanged(nameof(Max));
            OnPropertyChanged(nameof(HasCorrection));

            // Bars/gauges: start the scale over for the corrected range.
            _scaleMaximum = InitialScale(Kind, Nominal);
            GrowScale(Max);
            OnPropertyChanged(nameof(ScaleMaximum));
            if (HasNominal)
            {
                OnPropertyChanged(nameof(Deviation));
                OnPropertyChanged(nameof(DeviationText));
            }

            UpdateLevel();
        }

        if (newName != Name)
        {
            Name = newName;
            OnPropertyChanged(nameof(Name));
            OnPropertyChanged(nameof(IsRenamed));
        }

        var hidden = o?.Hidden ?? false;
        if (hidden != IsHidden)
        {
            IsHidden = hidden;
            OnPropertyChanged(nameof(IsHidden));
        }

        OnPropertyChanged(nameof(CustomizationNote));
        RefreshDisplay();
    }

    /// <summary>The current reading before the user's correction (what the hardware reports).</summary>
    public double? RawValue => ToRaw(Value);

    /// <summary>Undoes this sensor's scale/offset correction on a shown value (null for missing/NaN).</summary>
    public double? ToRaw(float? shown) => shown is { } v && !float.IsNaN(v) ? Raw(_correction, v) : null;

    /// <summary>Applies the correction to raw values from the collector (history backfill).</summary>
    internal float?[] CorrectRaw(float?[] raw)
    {
        if (_correction is null)
        {
            return raw;
        }

        var result = new float?[raw.Length];
        for (var i = 0; i < raw.Length; i++)
        {
            result[i] = raw[i] is { } v && !float.IsNaN(v) ? (float)_correction.Apply(v) : raw[i];
        }

        return result;
    }

    internal void Update(float? value, float? min, float? max, long timestampUnixMs)
    {
        if (_correction is { } c)
        {
            value = value is { } v && !float.IsNaN(v) ? (float)c.Apply(v) : value;
            var cmin = min is { } a && !float.IsNaN(a) ? (float)c.Apply(a) : min;
            var cmax = max is { } b && !float.IsNaN(b) ? (float)c.Apply(b) : max;
            (min, max) = c.Scale < 0 ? (cmax, cmin) : (cmin, cmax);
        }

        History.Add(timestampUnixMs, value);

        if (value != Value)
        {
            Value = value;
            OnPropertyChanged(nameof(Value));
            OnPropertyChanged(nameof(DisplayValue));
            OnPropertyChanged(nameof(DisplayNumber));
            OnPropertyChanged(nameof(DisplayUnit));
            if (HasNominal)
            {
                OnPropertyChanged(nameof(RailStatus));
                OnPropertyChanged(nameof(DeviationText));
                OnPropertyChanged(nameof(Deviation));
            }

            UpdateLevel();
        }

        if (min != Min)
        {
            Min = min;
            OnPropertyChanged(nameof(Min));
            OnPropertyChanged(nameof(DisplayMin));
        }

        if (max != Max)
        {
            Max = max;
            OnPropertyChanged(nameof(Max));
            OnPropertyChanged(nameof(DisplayMax));
            GrowScale(max);
        }
    }

    /// <summary>Re-renders all text and colors, e.g. after switching °C/°F or the skin.</summary>
    internal void RefreshDisplay()
    {
        OnPropertyChanged(nameof(DisplayValue));
        OnPropertyChanged(nameof(DisplayNumber));
        OnPropertyChanged(nameof(DisplayUnit));
        OnPropertyChanged(nameof(DisplayMin));
        OnPropertyChanged(nameof(DisplayMax));

        // Colors come from converters reading the skin; re-raising these makes bindings re-run them (skin change).
        OnPropertyChanged(nameof(Level));
        OnPropertyChanged(nameof(Kind));
        OnPropertyChanged(nameof(RailStatus));
    }

    private SensorOverride? _correction;

    private static double Correct(SensorOverride? c, double raw) => c is null ? raw : c.Apply(raw);

    private static double Raw(SensorOverride? c, double shown) => c is null || c.Scale == 0 ? shown : (shown - c.Offset) / c.Scale;

    private static bool SameCorrection(SensorOverride? a, SensorOverride? b) =>
        (a is null && b is null) || (a is not null && b is not null && a.Scale == b.Scale && a.Offset == b.Offset);

    private void UpdateLevel()
    {
        var level = ValueLevel.Normal;
        if (_rule is not null && (!_rule.Enabled || !_rule.HasLimits))
        {
            // The user switched this sensor's alert off: no warning colors either.
            level = ValueLevel.Normal;
        }
        else if (_rule is not null)
        {
            level = IsRuleArmed && Value is { } v && !float.IsNaN(v)
                ? _rule.Classify(v) switch
                {
                    AlertLevel.Critical => ValueLevel.Critical,
                    AlertLevel.Warning => ValueLevel.Warning,
                    _ => ValueLevel.Normal,
                }
                : ValueLevel.Normal;
        }
        else if (Kind == SensorKind.Temperature && Value is { } t && !IsAdvanced)
        {
            level = t >= TemperatureCritical ? ValueLevel.Critical : t >= TemperatureWarning ? ValueLevel.Warning : ValueLevel.Normal;
        }
        else if (HasNominal)
        {
            level = RailStatus switch
            {
                RailStatus.Critical => ValueLevel.Critical,
                RailStatus.Warning => ValueLevel.Warning,
                _ => ValueLevel.Normal,
            };
        }

        if (level != _level)
        {
            _level = level;
            OnPropertyChanged(nameof(Level));
        }
    }

    private void GrowScale(float? max)
    {
        if (Kind is SensorKind.Temperature or SensorKind.Load or SensorKind.Control or SensorKind.Level or SensorKind.Humidity
            || HasNominal || max is not { } m || !float.IsFinite(m))
        {
            return;
        }

        // A misread fan register (65535 RPM) would squash bars and gauges for the rest of the session.
        if (Kind == SensorKind.Fan && m > FanScale.MaxPlausibleRpm)
        {
            return;
        }

        // Round up to a "nice" step with ~15 % headroom so bars don't sit pinned at 100 %.
        var target = NiceCeiling(m * 1.15);
        if (target > _scaleMaximum)
        {
            _scaleMaximum = target;
            OnPropertyChanged(nameof(ScaleMaximum));
        }
    }

    private static double InitialScale(SensorKind kind, double? nominal) => kind switch
    {
        SensorKind.Temperature => 100,
        SensorKind.Load or SensorKind.Control or SensorKind.Level or SensorKind.Humidity => 100,
        SensorKind.Fan => 2000,
        SensorKind.Power => 100,
        SensorKind.Voltage when nominal is { } n => Math.Abs(n) * 1.25,
        SensorKind.Voltage => 2,
        SensorKind.Clock => 1000,
        _ => 1,
    };

    internal static double NiceCeiling(double value)
    {
        if (value <= 0 || !double.IsFinite(value))
        {
            return 1;
        }

        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(value)));
        foreach (var step in new[] { 1.0, 2.0, 2.5, 5.0, 10.0 })
        {
            if (step * magnitude >= value)
            {
                return step * magnitude;
            }
        }

        return 10 * magnitude;
    }

    private static (string Number, string Unit) SplitUnit(string text)
    {
        var space = text.IndexOf(' ', StringComparison.Ordinal);
        return space < 0 ? (text, "") : (text[..space], text[(space + 1)..]);
    }
}
