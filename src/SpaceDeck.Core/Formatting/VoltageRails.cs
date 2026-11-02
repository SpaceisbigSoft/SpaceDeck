using System.Globalization;
using System.Text.RegularExpressions;
using SpaceDeck.Core.Model;

namespace SpaceDeck.Core.Formatting;

/// <summary>
/// Recognizes standard voltage rails from sensor names and judges how far a reading is from nominal.
/// </summary>
public static partial class VoltageRails
{
    /// <summary>ATX allows ±5% on +12V, +5V, +3.3V and +5VSB.</summary>
    public const double AtxTolerance = 0.05;

    /// <summary>Beyond this deviation a rail is critical rather than a warning.</summary>
    public const double CriticalTolerance = 0.08;

    /// <summary>CMOS battery: below this it's worth replacing soon; below the critical level, settings may be lost.</summary>
    public const double BatteryWarningVolts = 2.8;
    public const double BatteryCriticalVolts = 2.6;

    // Names LHM and board vendors commonly use for fixed rails, matched case-insensitively and ignoring spaces.
    private static readonly Dictionary<string, double> KnownNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["3VCC"] = 3.3,
        ["AVCC"] = 3.3,
        ["AVCC3"] = 3.3,
        ["3VSB"] = 3.3,
        ["VSB"] = 3.3,
        ["AVSB"] = 3.3,
        ["3VSB_ATX"] = 3.3,
        ["5VSB"] = 5.0,
        ["5VCC"] = 5.0,
        ["12VCC"] = 12.0,
        ["VBAT"] = 3.0,
        ["VBATTERY"] = 3.0,
        ["CMOSBATTERY"] = 3.0,
        ["BATTERY"] = 3.0,
    };

    // "+12V", "12V", "+3.3V", "-12 V", "+5V Standby"… but not "Vcore", "VDDSOC" or "Voltage #3".
    [GeneratedRegex(@"^(?<sign>[+-]?)\s*(?<num>\d{1,2}(?:\.\d{1,2})?)\s*V(?:\b|SB|CC|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RailPattern();

    /// <summary>Returns the nominal voltage for a recognizable rail name, or null.</summary>
    public static double? InferNominal(string? sensorName)
    {
        if (string.IsNullOrWhiteSpace(sensorName))
        {
            return null;
        }

        var name = sensorName.Trim();
        var compact = name.Replace(" ", "", StringComparison.Ordinal);

        if (KnownNames.TryGetValue(compact, out var known))
        {
            return known;
        }

        var match = RailPattern().Match(name);
        if (!match.Success)
        {
            return null;
        }

        var number = double.Parse(match.Groups["num"].Value, CultureInfo.InvariantCulture);

        // Only standard rails. Other "x.xV" labels are usually setpoints (DRAM 1.35V etc.) rather than rails.
        if (number is not (12.0 or 5.0 or 3.3))
        {
            return null;
        }

        return match.Groups["sign"].Value == "-" ? -number : number;
    }

    public static bool IsBattery(double nominal) => Math.Abs(nominal - 3.0) < 0.001;

    /// <summary>Signed fractional deviation from nominal, e.g. +0.021 for 12.25 V on a 12 V rail.</summary>
    public static double Deviation(double value, double nominal) =>
        nominal == 0 ? 0 : (value - nominal) / Math.Abs(nominal);

    /// <summary>Judges a reading against its nominal value.</summary>
    public static RailStatus Evaluate(float? value, double? nominal)
    {
        if (value is not { } v || nominal is not { } n || float.IsNaN(v))
        {
            return RailStatus.Unknown;
        }

        if (IsBattery(n))
        {
            return v >= BatteryWarningVolts ? RailStatus.Ok
                : v >= BatteryCriticalVolts ? RailStatus.Warning
                : RailStatus.Critical;
        }

        // -12V has a looser ±10% ATX tolerance.
        var tolerance = n < 0 ? 0.10 : AtxTolerance;
        var critical = n < 0 ? 0.15 : CriticalTolerance;
        var deviation = Math.Abs(Deviation(v, n));

        return deviation <= tolerance ? RailStatus.Ok
            : deviation <= critical ? RailStatus.Warning
            : RailStatus.Critical;
    }
}
