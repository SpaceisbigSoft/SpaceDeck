using System.Globalization;
using SpaceDeck.Core.Alerts;

namespace SpaceDeck.Core.Automation;

/// <summary>What makes an automation rule active.</summary>
public enum TriggerKind
{
    /// <summary>One of the listed programs is running.</summary>
    Process,

    /// <summary>A sensor is at or above the threshold for <see cref="AutomationRule.ForSeconds"/>.</summary>
    SensorAbove,

    /// <summary>A sensor is at or below the threshold for <see cref="AutomationRule.ForSeconds"/>.</summary>
    SensorBelow,

    /// <summary>The clock is between <see cref="AutomationRule.From"/> and <see cref="AutomationRule.To"/>.</summary>
    TimeOfDay,
}

/// <summary>"While X, use FanControl config Y." Rules are checked top to bottom; the first active one wins.</summary>
public sealed class AutomationRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "";

    public bool Enabled { get; set; } = true;

    public TriggerKind Trigger { get; set; }

    /// <summary>Program names for <see cref="TriggerKind.Process"/>, with or without ".exe" (any case).</summary>
    public List<string> Processes { get; set; } = [];

    public string? SensorId { get; set; }

    /// <summary>In the sensor's base unit (°C, RPM, %…).</summary>
    public double Threshold { get; set; }

    /// <summary>How long a sensor condition must hold before the rule turns on — and stay false before it turns off.</summary>
    public int ForSeconds { get; set; } = 10;

    /// <summary>"HH:mm" local time; the window may cross midnight.</summary>
    public string? From { get; set; }

    public string? To { get; set; }

    /// <summary>FanControl config to switch to.</summary>
    public string Profile { get; set; } = "";

    /// <summary>Also record a session (Recordings page) while this rule is active.</summary>
    public bool Record { get; set; }

    public bool IsSensorRule => Trigger is TriggerKind.SensorAbove or TriggerKind.SensorBelow;

    public AutomationRule Clone() => new()
    {
        Id = Id,
        Name = Name,
        Enabled = Enabled,
        Trigger = Trigger,
        Processes = [.. Processes],
        SensorId = SensorId,
        Threshold = Threshold,
        ForSeconds = ForSeconds,
        From = From,
        To = To,
        Profile = Profile,
        Record = Record,
    };
}

/// <summary>What to do while a sensor alert is active (any sensor).</summary>
public sealed class AlertActions
{
    public string? WarningProfile { get; set; }

    public string? CriticalProfile { get; set; }

    public bool WarningSound { get; set; }

    public bool CriticalSound { get; set; } = true;

    /// <summary>Go back to the previous profile when the alert ends (otherwise stay on the alert profile).</summary>
    public bool RevertOnClear { get; set; } = true;

    /// <summary>Critical falls back to the warning profile: a critical alert is also a warning.</summary>
    public string? ProfileFor(AlertLevel level) => level switch
    {
        AlertLevel.Critical => Blank(CriticalProfile) ?? Blank(WarningProfile),
        AlertLevel.Warning => Blank(WarningProfile),
        _ => null,
    };

    public bool SoundFor(AlertLevel level) => level switch
    {
        AlertLevel.Critical => CriticalSound,
        AlertLevel.Warning => WarningSound,
        _ => false,
    };

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}

public sealed class AutomationSettings
{
    /// <summary>Rules on/off. Alert actions work either way.</summary>
    public bool Enabled { get; set; }

    /// <summary>Profile when no rule is active; null = go back to the one in use before a rule switched.</summary>
    public string? DefaultProfile { get; set; }

    /// <summary>Minimum time between automatic switches (alerts ignore it).</summary>
    public int CooldownSeconds { get; set; } = 30;

    /// <summary>After you pick a profile yourself, rules pause this long. 0 = until you resume them.</summary>
    public int HoldMinutes { get; set; } = 60;

    public List<AutomationRule> Rules { get; set; } = [];

    public AlertActions Alerts { get; set; } = new();

    /// <summary>Saved engine state: the profile to go back to (the user's own choice), so a restart while a rule's
    /// profile is active still returns to it.</summary>
    public string? BaselineProfile { get; set; }

    /// <summary>Saved engine state: an active manual pause (<see cref="DateTimeOffset.MaxValue"/> = until resumed).</summary>
    public DateTimeOffset? HoldUntil { get; set; }
}

/// <summary>Daily time windows like 22:00–07:00.</summary>
public static class TimeWindow
{
    private static readonly string[] Formats = ["H:mm", "HH:mm"];

    public static bool TryParse(string? text, out TimeOnly time) =>
        TimeOnly.TryParseExact(text?.Trim(), Formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    /// <summary>From inclusive, to exclusive; crosses midnight when <paramref name="to"/> is earlier. Equal = empty.</summary>
    public static bool Contains(TimeOnly from, TimeOnly to, TimeOnly now) =>
        from < to ? now >= from && now < to
        : from > to && (now >= from || now < to);

    public static bool Contains(string? from, string? to, TimeOnly now) =>
        TryParse(from, out var f) && TryParse(to, out var t) && Contains(f, t, now);
}

/// <summary>Program names compared the way Windows shows them, ignoring ".exe", folders and case.</summary>
public static class ProcessNames
{
    public static string Normalize(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var n = name.Trim().Trim('"');
        var slash = n.LastIndexOfAny(['\\', '/']);
        if (slash >= 0)
        {
            n = n[(slash + 1)..];
        }

        if (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            n = n[..^4];
        }

        return n.ToUpperInvariant();
    }

    /// <summary>"game.exe, other" → ["game.exe", "other"] (comma, semicolon or new line).</summary>
    public static List<string> Split(string? text) =>
        [.. (text ?? "").Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)];
}
