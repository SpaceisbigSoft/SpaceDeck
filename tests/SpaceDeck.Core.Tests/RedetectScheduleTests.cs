using SpaceDeck.Core.Model;

namespace SpaceDeck.Core.Tests;

public class RedetectScheduleTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public void TheDefaults_Are10sThen30sThen2Minutes()
    {
        Assert.Equal(new[] { S(10), S(30), S(120) }, RedetectSchedule.DefaultDelays);
    }

    [Fact]
    public void MissingHardware_IsLookedForOncePerDelay_ThenNoMore()
    {
        var schedule = new RedetectSchedule();

        Assert.False(schedule.IsDue(S(0), missing: true));
        Assert.False(schedule.IsDue(S(9.9), missing: true));
        Assert.True(schedule.IsDue(S(10), missing: true));
        Assert.False(schedule.IsDue(S(11), missing: true)); // once per delay
        Assert.False(schedule.IsDue(S(29), missing: true));
        Assert.True(schedule.IsDue(S(30), missing: true));
        Assert.False(schedule.IsFinished);
        Assert.True(schedule.IsDue(S(120), missing: true));

        Assert.True(schedule.IsFinished);
        Assert.Equal(3, schedule.Attempts);
        Assert.False(schedule.IsDue(S(600), missing: true));
        Assert.Equal(3, schedule.Attempts);
    }

    [Fact]
    public void HardwareThereFromTheStart_IsNeverLookedFor()
    {
        var schedule = new RedetectSchedule();

        Assert.False(schedule.IsDue(S(1), missing: false));

        Assert.True(schedule.IsFinished);
        Assert.False(schedule.IsDue(S(10), missing: true)); // a later loss isn't this schedule's to fix
        Assert.Equal(0, schedule.Attempts);
    }

    [Fact]
    public void HardwareFoundByALook_EndsTheSchedule()
    {
        var schedule = new RedetectSchedule();
        Assert.True(schedule.IsDue(S(10), missing: true));

        Assert.False(schedule.IsDue(S(11), missing: false));

        Assert.True(schedule.IsFinished);
        Assert.False(schedule.IsDue(S(30), missing: true));
        Assert.Equal(1, schedule.Attempts);
    }

    [Fact]
    public void SeveralDelaysPassedAtOnce_CountAsOneLook()
    {
        var schedule = new RedetectSchedule();

        Assert.True(schedule.IsDue(S(45), missing: true)); // a poll that stalled past 10 s and 30 s
        Assert.Equal(1, schedule.Attempts);
        Assert.False(schedule.IsDue(S(46), missing: true));
        Assert.True(schedule.IsDue(S(120), missing: true));
        Assert.True(schedule.IsFinished);
    }

    [Fact]
    public void CustomDelays_AreUsed_AndMustIncrease()
    {
        var schedule = new RedetectSchedule([S(1)]);
        Assert.True(schedule.IsDue(S(1), missing: true));
        Assert.True(schedule.IsFinished);

        Assert.True(new RedetectSchedule([]).IsFinished);
        Assert.Throws<ArgumentException>(() => new RedetectSchedule([S(5), S(5)]));
        Assert.Throws<ArgumentException>(() => new RedetectSchedule([S(-1)]));
    }
}
