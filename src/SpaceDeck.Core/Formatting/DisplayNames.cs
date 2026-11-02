using System.Globalization;
using System.Text;

namespace SpaceDeck.Core.Formatting;

/// <summary>Cleans up names reported by hardware, which are sometimes garbage (e.g. undecodable SPD vendor strings).</summary>
public static class DisplayNames
{
    /// <summary>
    /// Returns <paramref name="name"/> with control characters removed and whitespace collapsed,
    /// or <paramref name="fallback"/> if the name looks garbled.
    /// </summary>
    /// <remarks>
    /// A trailing instance suffix like <c>" (#1)"</c> is preserved and appended to the fallback, so
    /// "e?z??>} (#1)" becomes "Memory module (#1)".
    /// </remarks>
    public static string Sanitize(string? name, string fallback)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fallback);
        if (string.IsNullOrWhiteSpace(name))
        {
            return fallback;
        }

        var (body, suffix) = SplitInstanceSuffix(name.Trim());
        var cleaned = Clean(body);

        return LooksGarbled(body, cleaned) ? fallback + suffix : cleaned + suffix;
    }

    private static (string Body, string Suffix) SplitInstanceSuffix(string name)
    {
        // " (#12)" at the end.
        var open = name.LastIndexOf(" (#", StringComparison.Ordinal);
        if (open > 0 && name.EndsWith(')') && name[(open + 3)..^1].All(char.IsAsciiDigit) && name.Length - open > 4)
        {
            return (name[..open], name[open..]);
        }

        return (name, "");
    }

    private static string Clean(string s)
    {
        var sb = new StringBuilder(s.Length);
        var lastWasSpace = false;
        foreach (var c in s)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!lastWasSpace && sb.Length > 0)
                {
                    sb.Append(' ');
                }

                lastWasSpace = true;
                continue;
            }

            if (char.IsControl(c) || c == '\uFFFD')
            {
                continue;
            }

            sb.Append(c);
            lastWasSpace = false;
        }

        return sb.ToString().TrimEnd();
    }

    private static bool LooksGarbled(string original, string cleaned)
    {
        if (cleaned.Length < 2)
        {
            return true;
        }

        var bad = 0;
        var letters = 0;
        foreach (var c in original)
        {
            if (char.IsWhiteSpace(c))
            {
                continue;
            }

            var category = char.GetUnicodeCategory(c);
            if (char.IsControl(c) || c == '\uFFFD'
                || category is UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned or UnicodeCategory.Surrogate)
            {
                bad++;
            }
            else if (c > '\u007E' && !char.IsLetter(c))
            {
                // Non-ASCII symbols (box drawing, math, etc.) are a strong sign of a mis-decoded byte string.
                bad++;
            }

            if (char.IsLetterOrDigit(c))
            {
                letters++;
            }
        }

        // Any undecodable characters, or mostly punctuation, means it's not a real name.
        return bad > 0 || letters < cleaned.Length / 2;
    }
}
