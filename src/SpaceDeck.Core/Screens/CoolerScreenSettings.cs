namespace SpaceDeck.Core.Screens;

/// <summary>
/// One cooler screen's settings (Settings → Cooler screen), kept in the app's settings.json per hardware id. Off by
/// default: SpaceDeck draws on a cooler's screen only once the user turns it on.
/// </summary>
public sealed class CoolerScreenSettings
{
    public const int MinIntervalSeconds = 1;

    public const int MaxIntervalSeconds = 5;

    public const int DefaultIntervalSeconds = 2;

    public const int DefaultQuality = 85;

    /// <summary>Lowest JPEG quality offered (and the floor when a frame has to shrink under 256 KiB).</summary>
    public const int MinQuality = 30;

    public const int MaxQuality = 100;

    /// <summary>Catalog hardware id of the screen, e.g. <c>lianli:/LianLi/0</c>.</summary>
    public string HardwareId { get; set; } = "";

    public bool Enabled { get; set; }

    /// <summary>The dashboard shown; null = none chosen yet (turning on creates the "Cooler screen" dashboard).</summary>
    public string? DashboardId { get; set; }

    /// <summary>Seconds between frames, 1–5.</summary>
    public int IntervalSeconds { get; set; } = DefaultIntervalSeconds;

    /// <summary>Clockwise rotation of the picture: 0, 90, 180 or 270 degrees (for a cooler mounted turned).</summary>
    public int Rotation { get; set; }

    /// <summary>JPEG quality 30–100.</summary>
    public int JpegQuality { get; set; } = DefaultQuality;

    /// <summary>Brings hand-edited or older values into range (rotation to the nearest quarter turn).</summary>
    public void Normalize()
    {
        HardwareId ??= "";
        DashboardId = string.IsNullOrWhiteSpace(DashboardId) ? null : DashboardId;
        IntervalSeconds = Math.Clamp(IntervalSeconds, MinIntervalSeconds, MaxIntervalSeconds);
        JpegQuality = Math.Clamp(JpegQuality, MinQuality, MaxQuality);
        Rotation = NormalizeRotation(Rotation);
    }

    /// <summary>0, 90, 180 or 270: any angle rounded to the nearest quarter turn, clockwise.</summary>
    public static int NormalizeRotation(int degrees)
    {
        var turned = ((degrees % 360) + 360) % 360;
        return (int)(Math.Round(turned / 90.0, MidpointRounding.AwayFromZero) * 90) % 360;
    }

    /// <summary>
    /// Settings for every distinct, non-empty hardware id, normalized (the first entry of an id wins). For loading
    /// settings.json, where JSON null entries and duplicates can appear.
    /// </summary>
    public static List<CoolerScreenSettings> Clean(IEnumerable<CoolerScreenSettings?>? screens)
    {
        var result = new List<CoolerScreenSettings>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var screen in screens ?? [])
        {
            if (screen is null || string.IsNullOrWhiteSpace(screen.HardwareId) || !seen.Add(screen.HardwareId))
            {
                continue;
            }

            screen.Normalize();
            result.Add(screen);
        }

        return result;
    }
}
