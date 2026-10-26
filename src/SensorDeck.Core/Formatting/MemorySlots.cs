using System.Globalization;
using System.Text.RegularExpressions;

namespace SensorDeck.Core.Formatting;

/// <summary>Turns the slot names firmware reports for memory modules into motherboard slot numbers.</summary>
public static partial class MemorySlots
{
    /// <summary>
    /// The 1-based slot number for a module's locator, counting slots the way boards and CPU-Z do (A1, A2, B1, B2 →
    /// 1–4), or null if the name isn't recognised. Examples: "Controller1-DIMMB2" → 4, "ChannelA-DIMM1" → 2,
    /// "DIMM 3" → 3.
    /// </summary>
    public static int? SlotNumber(string? locator)
    {
        if (string.IsNullOrWhiteSpace(locator))
        {
            return null;
        }

        // "DIMMA2", "Controller0-DIMMB1", "DIMM_B2": channel letter + 1-based position (two slots per channel).
        if (LetterDigit().Match(locator) is { Success: true } m1)
        {
            return ((char.ToUpperInvariant(m1.Groups["ch"].Value[0]) - 'A') * 2) + int.Parse(m1.Groups["n"].Value, CultureInfo.InvariantCulture);
        }

        // "ChannelA-DIMM0": channel letter + 0-based position.
        if (ChannelDimm().Match(locator) is { Success: true } m2)
        {
            return ((char.ToUpperInvariant(m2.Groups["ch"].Value[0]) - 'A') * 2) + int.Parse(m2.Groups["n"].Value, CultureInfo.InvariantCulture) + 1;
        }

        // "DIMM 3", "DIMM3".
        if (PlainNumber().Match(locator) is { Success: true } m3)
        {
            var n = int.Parse(m3.Groups["n"].Value, CultureInfo.InvariantCulture);
            return n > 0 ? n : null;
        }

        return null;
    }

    [GeneratedRegex(@"DIMM[_ -]?(?<ch>[A-H])(?<n>[1-4])\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LetterDigit();

    [GeneratedRegex(@"Channel[_ ]?(?<ch>[A-H])[_ -]+DIMM[_ ]?(?<n>[0-3])\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ChannelDimm();

    [GeneratedRegex(@"^\s*DIMM[_ #-]?(?<n>\d{1,2})\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PlainNumber();
}
