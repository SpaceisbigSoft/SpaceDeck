using SpaceDeck.Core.Alerts;
using SpaceDeck.Core.Automation;

namespace SpaceDeck.Core.Tests;

public class AutomationTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    private static AutomationInputs At(
        double seconds,
        string[]? processes = null,
        double? cpu = null,
        AlertLevel alert = AlertLevel.Normal,
        DateTimeOffset? baseTime = null,
        bool sensorData = true) =>
        new((baseTime ?? T0).AddSeconds(seconds),
            (processes ?? []).Select(ProcessNames.Normalize).ToHashSet(),
            id => id == "cpu" ? cpu : null,
            alert,
            sensorData);

    private static AutomationSettings GameRule(params AutomationRule[] extra) => new()
    {
        Enabled = true,
        CooldownSeconds = 30,
        Rules =
        [
            new AutomationRule { Id = "game", Trigger = TriggerKind.Process, Processes = ["Game.exe"], Profile = "Gaming" },
            .. extra,
        ],
    };

    [Fact]
    public void ProcessRule_SwitchesAndReturnsAfterCooldown()
    {
        var settings = GameRule();
        var engine = new AutomationEngine("Quiet");

        var start = engine.Evaluate(settings, At(0, ["game"]));
        Assert.True(start.ShouldSwitch);
        Assert.Equal("Gaming", start.Target);
        Assert.Equal("game", start.ActiveRuleId);
        engine.Switched("Gaming", T0);

        // Game closes 10 s later: cool-down holds the switch back.
        var early = engine.Evaluate(settings, At(10));
        Assert.False(early.ShouldSwitch);
        Assert.Equal("Quiet", early.Target);

        var later = engine.Evaluate(settings, At(31));
        Assert.True(later.ShouldSwitch);
        Assert.Equal("Quiet", later.Target);
    }

    [Fact]
    public void AlreadyOnTarget_DoesNotSwitch()
    {
        var engine = new AutomationEngine("Gaming");
        Assert.False(engine.Evaluate(GameRule(), At(0, ["GAME"])).ShouldSwitch);
    }

    [Fact]
    public void FirstActiveRule_Wins()
    {
        var settings = GameRule(new AutomationRule { Id = "night", Trigger = TriggerKind.TimeOfDay, From = "11:00", To = "13:00", Profile = "Night" });
        var engine = new AutomationEngine("Quiet");

        Assert.Equal("Gaming", engine.Evaluate(settings, At(0, ["game.exe"])).Target);
        Assert.Equal("Night", engine.Evaluate(settings, At(1)).Target);
    }

    [Fact]
    public void SensorRule_NeedsSustainedReading_BothWays()
    {
        var settings = new AutomationSettings
        {
            Enabled = true,
            CooldownSeconds = 0,
            Rules = [new AutomationRule { Id = "hot", Trigger = TriggerKind.SensorAbove, SensorId = "cpu", Threshold = 80, ForSeconds = 10, Profile = "Max" }],
        };
        var engine = new AutomationEngine("Quiet");

        Assert.False(engine.Evaluate(settings, At(0, cpu: 85)).ShouldSwitch);
        Assert.False(engine.Evaluate(settings, At(5, cpu: 70)).ShouldSwitch); // spike: timer resets
        Assert.False(engine.Evaluate(settings, At(6, cpu: 85)).ShouldSwitch);
        var on = engine.Evaluate(settings, At(16, cpu: 85));
        Assert.True(on.ShouldSwitch);
        Assert.Equal("Max", on.Target);
        engine.Switched("Max", T0.AddSeconds(16));

        Assert.Equal("Max", engine.Evaluate(settings, At(20, cpu: 70)).Target); // not yet off
        var off = engine.Evaluate(settings, At(30, cpu: 70));
        Assert.True(off.ShouldSwitch);
        Assert.Equal("Quiet", off.Target);
    }

    [Fact]
    public void SensorRule_KeepsItsStateWhileSensorDataIsUnavailable()
    {
        var settings = new AutomationSettings
        {
            Enabled = true,
            CooldownSeconds = 0,
            Rules = [new AutomationRule { Id = "hot", Trigger = TriggerKind.SensorAbove, SensorId = "cpu", Threshold = 80, ForSeconds = 10, Profile = "Max" }],
        };
        var engine = new AutomationEngine("Quiet");
        Assert.False(engine.Evaluate(settings, At(0, cpu: 85)).ShouldSwitch);
        Assert.True(engine.Evaluate(settings, At(10, cpu: 85)).ShouldSwitch);
        engine.Switched("Max", T0.AddSeconds(10));

        // Collector restart (or sleep): no readings for a minute. The rule stays on: no switch back to Quiet.
        for (var t = 11; t <= 70; t++)
        {
            var gap = engine.Evaluate(settings, At(t, sensorData: false));
            Assert.False(gap.ShouldSwitch);
            Assert.Equal("Max", gap.Target);
            Assert.Contains("hot", engine.ActiveRules);
        }

        // Readings are back and still hot: nothing changes.
        Assert.False(engine.Evaluate(settings, At(71, cpu: 85)).ShouldSwitch);

        // Cooled down: off only after ForSeconds of real readings.
        Assert.False(engine.Evaluate(settings, At(72, cpu: 70)).ShouldSwitch);
        Assert.Equal("Quiet", engine.Evaluate(settings, At(82, cpu: 70)).Target);
    }

    [Fact]
    public void SensorRule_OffStaysOffWhileSensorDataIsUnavailable_AndGapIsNotCountedAsHeld()
    {
        var settings = new AutomationSettings
        {
            Enabled = true,
            CooldownSeconds = 0,
            Rules = [new AutomationRule { Id = "hot", Trigger = TriggerKind.SensorAbove, SensorId = "cpu", Threshold = 80, ForSeconds = 10, Profile = "Max" }],
        };
        var engine = new AutomationEngine("Quiet");

        Assert.False(engine.Evaluate(settings, At(0, cpu: 85)).ShouldSwitch); // starts turning on
        Assert.False(engine.Evaluate(settings, At(5, sensorData: false)).ShouldSwitch); // gap: stays off
        Assert.Empty(engine.ActiveRules);

        // Back after the gap: ForSeconds starts over from the first real reading.
        Assert.False(engine.Evaluate(settings, At(20, cpu: 85)).ShouldSwitch);
        Assert.False(engine.Evaluate(settings, At(29, cpu: 85)).ShouldSwitch);
        Assert.True(engine.Evaluate(settings, At(30, cpu: 85)).ShouldSwitch);
    }

    [Fact]
    public void ProcessAndTimeRules_WorkWhileSensorDataIsUnavailable()
    {
        var settings = GameRule(new AutomationRule { Id = "night", Trigger = TriggerKind.TimeOfDay, From = "11:00", To = "13:00", Profile = "Night" });
        settings.CooldownSeconds = 0;
        var engine = new AutomationEngine("Quiet");

        var game = engine.Evaluate(settings, At(0, ["game.exe"], sensorData: false));
        Assert.True(game.ShouldSwitch);
        Assert.Equal("Gaming", game.Target);
        engine.Switched("Gaming", T0);

        var night = engine.Evaluate(settings, At(1, sensorData: false));
        Assert.True(night.ShouldSwitch);
        Assert.Equal("Night", night.Target);
    }

    [Fact]
    public void DefaultProfile_UsedWhenNothingMatches()
    {
        var settings = GameRule();
        settings.DefaultProfile = "Balanced";
        var engine = new AutomationEngine("Quiet");

        var status = engine.Evaluate(settings, At(0));
        Assert.True(status.ShouldSwitch);
        Assert.Equal("Balanced", status.Target);
    }

    [Fact]
    public void ManualSwitch_HoldsRules_ThenResumes()
    {
        var settings = GameRule();
        settings.HoldMinutes = 10;
        var engine = new AutomationEngine("Quiet");
        engine.ManualSwitch("Silent", T0, settings);

        var held = engine.Evaluate(settings, At(60, ["game"]));
        Assert.False(held.ShouldSwitch);
        Assert.True(held.IsHolding);

        var after = engine.Evaluate(settings, At(601, ["game"]));
        Assert.True(after.ShouldSwitch);
        Assert.Equal("Gaming", after.Target);
        engine.Switched("Gaming", T0.AddSeconds(601));

        // Game closes: back to the profile the user picked.
        Assert.Equal("Silent", engine.Evaluate(settings, At(700)).Target);
    }

    [Fact]
    public void Resume_EndsHold()
    {
        var settings = GameRule();
        settings.HoldMinutes = 0; // until resumed
        var engine = new AutomationEngine("Quiet");
        engine.ManualSwitch("Silent", T0, settings);

        Assert.True(engine.Evaluate(settings, At(10_000, ["game"])).IsHolding);
        engine.Resume();
        Assert.True(engine.Evaluate(settings, At(10_001, ["game"])).ShouldSwitch);
    }

    [Fact]
    public void Alert_OverridesHoldAndCooldown_AndReverts()
    {
        var settings = GameRule();
        settings.Alerts = new AlertActions { CriticalProfile = "Full", RevertOnClear = true };
        var engine = new AutomationEngine("Quiet");
        engine.ManualSwitch("Silent", T0, settings);

        var alert = engine.Evaluate(settings, At(1, alert: AlertLevel.Critical));
        Assert.True(alert.ShouldSwitch);
        Assert.Equal("Full", alert.Target);
        engine.Switched("Full", T0.AddSeconds(1));

        var cleared = engine.Evaluate(settings, At(2));
        Assert.True(cleared.ShouldSwitch);
        Assert.Equal("Silent", cleared.Target);
    }

    [Fact]
    public void Alert_WithoutRevert_StaysOnAlertProfile()
    {
        var settings = new AutomationSettings { Alerts = new AlertActions { WarningProfile = "Loud", RevertOnClear = false } };
        var engine = new AutomationEngine("Quiet");

        var alert = engine.Evaluate(settings, At(0, alert: AlertLevel.Critical)); // critical falls back to the warning profile
        Assert.Equal("Loud", alert.Target);
        engine.Switched("Loud", T0);

        Assert.False(engine.Evaluate(settings, At(100)).ShouldSwitch);
    }

    [Fact]
    public void RulesOff_StillRunsAlertActions_ButNotRules()
    {
        var settings = GameRule();
        settings.Enabled = false;
        settings.Alerts.CriticalProfile = "Full";
        var engine = new AutomationEngine("Quiet");

        Assert.False(engine.Evaluate(settings, At(0, ["game"])).ShouldSwitch);
        Assert.Equal("Full", engine.Evaluate(settings, At(1, alert: AlertLevel.Critical)).Target);
    }

    [Fact]
    public void DisabledRule_IsIgnored()
    {
        var settings = GameRule();
        settings.Rules[0].Enabled = false;
        Assert.False(new AutomationEngine("Quiet").Evaluate(settings, At(0, ["game"])).ShouldSwitch);
    }

    [Fact]
    public void Alert_WithoutRevert_HoldsAgainstRulesAndDefault()
    {
        var settings = GameRule();
        settings.DefaultProfile = "Balanced";
        settings.Alerts = new AlertActions { CriticalProfile = "Full", RevertOnClear = false };
        var engine = new AutomationEngine("Balanced");

        engine.Switched("Full", T0);
        Assert.Equal("Full", engine.Evaluate(settings, At(0, alert: AlertLevel.Critical)).Target);

        var after = engine.Evaluate(settings, At(1, ["game"]));
        Assert.False(after.ShouldSwitch);
        Assert.True(after.IsHolding);
        Assert.Equal("Full", engine.Baseline);
    }

    [Fact]
    public void FailedSwitch_IsNotRetriedEverySecond_EvenForAlerts()
    {
        var settings = new AutomationSettings { CooldownSeconds = 10, Alerts = new AlertActions { CriticalProfile = "Full" } };
        var engine = new AutomationEngine("Quiet");

        Assert.True(engine.Evaluate(settings, At(0, alert: AlertLevel.Critical)).ShouldSwitch);
        engine.SwitchFailed("Full", T0, settings);

        Assert.False(engine.Evaluate(settings, At(1, alert: AlertLevel.Critical)).ShouldSwitch);
        Assert.False(engine.Evaluate(settings, At(29, alert: AlertLevel.Critical)).ShouldSwitch); // at least 30 s
        Assert.True(engine.Evaluate(settings, At(30, alert: AlertLevel.Critical)).ShouldSwitch);
    }

    [Fact]
    public void Restart_RestoresBaselineAndHold()
    {
        var settings = GameRule();
        var engine = new AutomationEngine("Gaming", baseline: "Quiet");

        var status = engine.Evaluate(settings, At(0));
        Assert.True(status.ShouldSwitch);
        Assert.Equal("Quiet", status.Target);

        var held = new AutomationEngine("Silent", "Silent", DateTimeOffset.MaxValue);
        Assert.True(held.Evaluate(settings, At(0, ["game"])).IsHolding);
    }

    [Fact]
    public void EditedSensorRule_StartsOver()
    {
        var rule = new AutomationRule { Id = "r", Trigger = TriggerKind.SensorAbove, SensorId = "cpu", Threshold = 80, ForSeconds = 5, Profile = "Max" };
        var settings = new AutomationSettings { Enabled = true, CooldownSeconds = 0, Rules = [rule] };
        var engine = new AutomationEngine("Quiet");
        engine.Evaluate(settings, At(0, cpu: 85));
        Assert.Equal("Max", engine.Evaluate(settings, At(5, cpu: 85)).Target);

        rule.Threshold = 90; // edited: 85 no longer counts
        Assert.Equal("Quiet", engine.Evaluate(settings, At(6, cpu: 85)).Target);
    }

    [Theory]
    [InlineData("22:00", "07:00", "23:30", true)]
    [InlineData("22:00", "07:00", "06:59", true)]
    [InlineData("22:00", "07:00", "07:00", false)]
    [InlineData("22:00", "07:00", "12:00", false)]
    [InlineData("09:00", "17:00", "09:00", true)]
    [InlineData("09:00", "17:00", "17:00", false)]
    [InlineData("09:00", "09:00", "09:00", false)]
    [InlineData("bad", "17:00", "10:00", false)]
    public void TimeWindow_Contains(string from, string to, string now, bool expected) =>
        Assert.Equal(expected, TimeWindow.Contains(from, to, TimeOnly.Parse(now, System.Globalization.CultureInfo.InvariantCulture)));

    [Theory]
    [InlineData("Game.exe", "GAME")]
    [InlineData(@"C:\Games\Cyberpunk2077.EXE", "CYBERPUNK2077")]
    [InlineData(" \"eldenring\" ", "ELDENRING")]
    public void ProcessNames_Normalize(string input, string expected) => Assert.Equal(expected, ProcessNames.Normalize(input));

    [Fact]
    public void ProcessNames_Split() =>
        Assert.Equal(new[] { "a.exe", "b", "c" }, ProcessNames.Split("a.exe, b;\nc,,A.exe"));
}
