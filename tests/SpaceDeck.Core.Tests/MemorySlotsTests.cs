using SpaceDeck.Core.Formatting;

namespace SpaceDeck.Core.Tests;

public class MemorySlotsTests
{
    [Theory]
    [InlineData("Controller0-DIMMA1", 1)]
    [InlineData("Controller0-DIMMA2", 2)]
    [InlineData("Controller1-DIMMB1", 3)]
    [InlineData("Controller1-DIMMB2", 4)]
    [InlineData("DIMM_B2", 4)]
    [InlineData("ChannelA-DIMM0", 1)]
    [InlineData("ChannelB-DIMM1", 4)]
    [InlineData("DIMM 3", 3)]
    [InlineData("DIMM1", 1)]
    [InlineData("P0 CHANNEL A", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void SlotNumber_ReadsCommonLocators(string? locator, int? expected) =>
        Assert.Equal(expected, MemorySlots.SlotNumber(locator));
}
