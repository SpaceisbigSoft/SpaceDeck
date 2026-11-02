using SpaceDeck.Core.Formatting;

namespace SpaceDeck.Core.Tests;

public class DisplayNamesTests
{
    [Theory]
    [InlineData("G Skill Intl - F5-8000J4048F24G (#3)", "G Skill Intl - F5-8000J4048F24G (#3)")]
    [InlineData("Nuvoton NCT6687D", "Nuvoton NCT6687D")]
    [InlineData("  MSI PRO   Z790-A WIFI (MS-7E07) ", "MSI PRO Z790-A WIFI (MS-7E07)")]
    [InlineData("Intel Core i9-14900K", "Intel Core i9-14900K")]
    [InlineData("P-Core #1 Distance to TjMax", "P-Core #1 Distance to TjMax")]
    public void Sanitize_KeepsRealNames(string input, string expected)
    {
        Assert.Equal(expected, DisplayNames.Sanitize(input, "Fallback"));
    }

    [Theory]
    [InlineData("e\u0001z\u0083\u0010>\u00b6>\u00b6>} (#1)", "Memory module (#1)")]
    [InlineData("\u0000\u0000\u0000", "Memory module")]
    [InlineData("?>?>}", "Memory module")]
    [InlineData("", "Memory module")]
    [InlineData(null, "Memory module")]
    public void Sanitize_FallsBackForGarbage(string? input, string expected)
    {
        Assert.Equal(expected, DisplayNames.Sanitize(input, "Memory module"));
    }

    [Fact]
    public void Sanitize_StripsControlCharactersFromOtherwiseGoodNames()
    {
        Assert.Equal("CPU Fan", DisplayNames.Sanitize("CPU\tFan\n", "x"));
    }
}
