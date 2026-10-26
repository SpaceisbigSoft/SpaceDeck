using SensorDeck.Core.Alerts;

namespace SensorDeck.Core.Tests;

public class AlertTests
{
    private static readonly AlertRule Temperature = new() { SensorId = "t", WarningAbove = 80, CriticalAbove = 90 };
    private static readonly AlertRule Rail = new() { SensorId = "v", WarningBelow = 11.4, CriticalBelow = 11.04, WarningAbove = 12.6, CriticalAbove = 12.96 };

    [Theory]
    [InlineData(79.9, AlertLevel.Normal)]
    [InlineData(80, AlertLevel.Warning)]
    [InlineData(89.9, AlertLevel.Warning)]
    [InlineData(90, AlertLevel.Critical)]
    public void Classify_Above(double value, AlertLevel expected) => Assert.Equal(expected, Temperature.Classify(value));

    [Theory]
    [InlineData(12.0, AlertLevel.Normal)]
    [InlineData(11.4, AlertLevel.Warning)]
    [InlineData(11.0, AlertLevel.Critical)]
    [InlineData(12.7, AlertLevel.Warning)]
    [InlineData(13.0, AlertLevel.Critical)]
    public void Classify_Band(double value, AlertLevel expected) => Assert.Equal(expected, Rail.Classify(value));

    [Fact]
    public void Classify_DisabledRule_IsNormal()
    {
        var rule = Temperature.Clone();
        rule.Enabled = false;
        Assert.Equal(AlertLevel.Normal, rule.Classify(120));
    }

    [Fact]
    public void Tracker_RaisesOnlyAfterSustainedReadings()
    {
        var tracker = new AlertTracker("t");
        Assert.Null(tracker.Update(Temperature, 85, 1));
        Assert.Null(tracker.Update(Temperature, 85, 2));
        var t = tracker.Update(Temperature, 85, 3);

        Assert.NotNull(t);
        Assert.Equal(AlertLevel.Normal, t.From);
        Assert.Equal(AlertLevel.Warning, t.To);
        Assert.True(t.IsRaise);
        Assert.Equal(AlertLevel.Warning, tracker.Level);
    }

    [Fact]
    public void Tracker_SingleSpikeDoesNotRaise()
    {
        var tracker = new AlertTracker("t");
        tracker.Update(Temperature, 95, 1);
        tracker.Update(Temperature, 60, 2);
        tracker.Update(Temperature, 95, 3);
        tracker.Update(Temperature, 60, 4);
        Assert.Equal(AlertLevel.Normal, tracker.Level);
    }

    [Fact]
    public void Tracker_HoveringJustBelowLimit_DoesNotClear()
    {
        var tracker = new AlertTracker("t");
        for (var i = 0; i < 3; i++)
        {
            tracker.Update(Temperature, 82, i);
        }

        // 79.5 °C is below 80 but inside the 2 % hysteresis (78.4): still a warning.
        for (var i = 0; i < 20; i++)
        {
            Assert.Null(tracker.Update(Temperature, 79.5, 10 + i));
        }

        Assert.Equal(AlertLevel.Warning, tracker.Level);
    }

    [Fact]
    public void Tracker_ClearsAfterClearlyBackForSeveralReadings()
    {
        var tracker = new AlertTracker("t");
        for (var i = 0; i < 3; i++)
        {
            tracker.Update(Temperature, 92, i);
        }

        Assert.Equal(AlertLevel.Critical, tracker.Level);

        AlertTransition? last = null;
        for (var i = 0; i < AlertTracker.DefaultClearSamples; i++)
        {
            last = tracker.Update(Temperature, 70, 10 + i);
        }

        Assert.NotNull(last);
        Assert.Equal(AlertLevel.Critical, last.From);
        Assert.Equal(AlertLevel.Normal, last.To);
        Assert.False(last.IsRaise);
    }

    [Fact]
    public void Tracker_BouncingBetweenWarningAndCritical_StillRaises()
    {
        var tracker = new AlertTracker("t");
        Assert.Null(tracker.Update(Temperature, 89, 1));
        Assert.Null(tracker.Update(Temperature, 91, 2));
        var t = tracker.Update(Temperature, 89, 3);

        Assert.NotNull(t);
        Assert.Equal(AlertLevel.Warning, t.To);
    }

    [Fact]
    public void Tracker_MissingReadings_NeitherRaiseNorClear()
    {
        var tracker = new AlertTracker("t");
        tracker.Update(Temperature, 85, 1);
        tracker.Update(Temperature, 85, 2);
        tracker.Update(Temperature, null, 3);
        Assert.Null(tracker.Update(Temperature, 85, 4)); // count restarted after the gap
        Assert.Equal(AlertLevel.Normal, tracker.Level);
    }

    [Fact]
    public void Tracker_DisablingRule_EndsActiveAlert()
    {
        var tracker = new AlertTracker("t");
        for (var i = 0; i < 3; i++)
        {
            tracker.Update(Temperature, 85, i);
        }

        var t = tracker.Update(null, 85, 10);
        Assert.NotNull(t);
        Assert.Equal(AlertLevel.Normal, t.To);
    }
}
