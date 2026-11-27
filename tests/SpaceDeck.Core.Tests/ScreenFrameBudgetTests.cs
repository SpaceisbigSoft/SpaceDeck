using SpaceDeck.Core.Screens;

namespace SpaceDeck.Core.Tests;

public class ScreenFrameBudgetTests
{
    [Theory]
    [InlineData(30, 72 * 1024)]
    [InlineData(24, 90 * 1024)]
    [InlineData(10, 216 * 1024)]
    [InlineData(8, 256 * 1024)] // 270 KiB, held to the 256 KiB frame limit
    [InlineData(1, 256 * 1024)]
    [InlineData(0, 256 * 1024)] // clamped to 1
    [InlineData(60, 72 * 1024)] // clamped to 30
    public void Animation_SharesSeventyTwoKiBTimesThirtyASecond(int framesPerSecond, int expected)
    {
        Assert.Equal(72 * 1024 * 30, ScreenFrameBudget.MaxBytesPerSecond);
        Assert.Equal(expected, ScreenFrameBudget.ForAnimation(framesPerSecond));
        Assert.True(ScreenFrameBudget.ForAnimation(framesPerSecond) <= ScreenFrameRules.MaxJpegBytes);
    }

    [Fact]
    public void Picker_StepsDownUntilTheFrameFits_AndStartsTheNextFrameThere()
    {
        var picker = new JpegQualityPicker(85, 70_000);
        Assert.Equal(85, picker.Start);
        Assert.Equal(80, picker.After(85, 128_000));
        Assert.Equal(75, picker.After(80, 100_000));
        Assert.Null(picker.After(75, 69_000));
        Assert.Equal(75, picker.Start);

        Assert.Null(picker.After(75, 60_000)); // the next frame fits at once: encoded only once
        Assert.Equal(75, picker.Start);
        Assert.Null(picker.After(75, 70_000)); // exactly the budget fits
        Assert.Equal(75, picker.Start);
    }

    [Fact]
    public void Picker_StepsBackUp_AfterThirtyFramesInARowWellUnderTheBudget()
    {
        Assert.Equal(30, JpegQualityPicker.FramesBeforeRaise);
        var picker = new JpegQualityPicker(85, 100_000);
        Assert.Equal(80, picker.After(85, 150_000));
        for (var i = 0; i < JpegQualityPicker.FramesBeforeRaise - 1; i++)
        {
            Assert.Null(picker.After(80, 40_000));
            Assert.Equal(80, picker.Start);
        }

        Assert.Null(picker.After(80, 40_000));
        Assert.Equal(85, picker.Start);

        for (var i = 0; i < 100; i++)
        {
            Assert.Null(picker.After(85, 1_000)); // never above the chosen quality
            Assert.Equal(85, picker.Start);
        }
    }

    [Fact]
    public void Picker_AFrameNearTheBudget_StartsTheCountAgain()
    {
        var picker = new JpegQualityPicker(85, 100_000);
        picker.After(85, 150_000);
        for (var i = 0; i < JpegQualityPicker.FramesBeforeRaise - 1; i++)
        {
            picker.After(80, 40_000);
        }

        Assert.Null(picker.After(80, 80_000)); // fits, but above 70 % of the budget: no step up, count from 0
        for (var i = 0; i < JpegQualityPicker.FramesBeforeRaise - 1; i++)
        {
            Assert.Null(picker.After(80, 40_000));
            Assert.Equal(80, picker.Start);
        }

        Assert.Null(picker.After(80, 40_000));
        Assert.Equal(85, picker.Start);
    }

    [Fact]
    public void Picker_StopsAtTheLowestQuality_AndEveryFrameEnds()
    {
        var picker = new JpegQualityPicker(100, 1_000);
        var encodes = 0;
        for (int? next = picker.Start; next is { } quality; )
        {
            encodes++;
            Assert.True(encodes <= 15, "the picker must end a frame");
            next = picker.After(quality, 50_000); // never fits
        }

        Assert.Equal(15, encodes); // 100, 95, …, 30
        Assert.Equal(CoolerScreenSettings.MinQuality, picker.Start);
        Assert.Null(picker.After(CoolerScreenSettings.MinQuality, 50_000));
        Assert.Null(picker.After(10, 50_000)); // a quality below the lowest is treated as the lowest
    }

    [Fact]
    public void Picker_Configure_StartsAgainOnlyWhenTheQualityOrBudgetChanged()
    {
        var picker = new JpegQualityPicker(85, 100_000);
        picker.After(85, 150_000);
        Assert.Equal(80, picker.Start);

        Assert.False(picker.Configure(85, 100_000));
        Assert.Equal(80, picker.Start);

        Assert.True(picker.Configure(85, 120_000));
        Assert.Equal(85, picker.Start);
        Assert.Equal(120_000, picker.BudgetBytes);

        Assert.True(picker.Configure(70, 120_000));
        Assert.Equal(70, picker.Start);
        Assert.Equal(70, picker.Chosen);
    }

    [Fact]
    public void Picker_ClampsTheQualityAndBudget()
    {
        var high = new JpegQualityPicker(150, 10_000_000);
        Assert.Equal(CoolerScreenSettings.MaxQuality, high.Chosen);
        Assert.Equal(ScreenFrameRules.MaxJpegBytes, high.BudgetBytes);

        var low = new JpegQualityPicker(10, 0);
        Assert.Equal(CoolerScreenSettings.MinQuality, low.Chosen);
        Assert.Equal(1, low.BudgetBytes);
        Assert.Equal(CoolerScreenSettings.MinQuality, low.Start);
    }
}
