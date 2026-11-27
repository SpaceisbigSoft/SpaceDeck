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

    public const int MinFps = 1;

    /// <summary>What the collector and the plugin pass at most (their token buckets refill at 30 a second).</summary>
    public const int MaxFps = 30;

    /// <summary>Smooth on the GA II LCD (measured: 24 and 30 frames a second pass with no drops).</summary>
    public const int DefaultFps = 24;

    public const int MinSlideSeconds = 2;

    public const int MaxSlideSeconds = 3600;

    public const int DefaultSlideSeconds = 10;

    /// <summary>The bars around a fitted picture: black.</summary>
    public const string DefaultBackground = "#000000";

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

    /// <summary>What the screen shows: a dashboard (the default, as before this setting existed), a picture, an animation or a slideshow.</summary>
    public ScreenContentMode Content { get; set; } = ScreenContentMode.Dashboard;

    /// <summary>
    /// The picture (<see cref="ScreenContentMode.Picture"/>) or the GIF / video (<see cref="ScreenContentMode.Animation"/>):
    /// SpaceDeck's own copy in <c>%AppData%\SpaceDeck\screens</c> (or the original, for a file too big to copy).
    /// </summary>
    public string? MediaPath { get; set; }

    /// <summary>The chosen file's own name, for Settings (the copy is named by its content).</summary>
    public string? MediaName { get; set; }

    /// <summary>The folder a slideshow shows (pictures and GIFs in it, by name), referenced in place.</summary>
    public string? SlideshowFolder { get; set; }

    /// <summary>Seconds each slideshow picture shows, 2–3600.</summary>
    public int SlideSeconds { get; set; } = DefaultSlideSeconds;

    /// <summary>How a picture, animation or slideshow fills the screen.</summary>
    public ImageFit Fit { get; set; } = ImageFit.Fill;

    /// <summary>The colour around a fitted picture and behind transparent pixels, "#RRGGBB".</summary>
    public string Background { get; set; } = DefaultBackground;

    /// <summary>
    /// Most frames a second, 1–30: animations, videos and dashboards with an animated picture are sent at their own rate
    /// up to this. Still pictures and other dashboards keep their own pace.
    /// </summary>
    public int MaxFramesPerSecond { get; set; } = DefaultFps;

    /// <summary>Brings hand-edited or older values into range (rotation to the nearest quarter turn).</summary>
    public void Normalize()
    {
        HardwareId ??= "";
        DashboardId = string.IsNullOrWhiteSpace(DashboardId) ? null : DashboardId;
        IntervalSeconds = Math.Clamp(IntervalSeconds, MinIntervalSeconds, MaxIntervalSeconds);
        JpegQuality = Math.Clamp(JpegQuality, MinQuality, MaxQuality);
        Rotation = NormalizeRotation(Rotation);
        Content = Enum.IsDefined(Content) ? Content : ScreenContentMode.Dashboard;
        MediaPath = string.IsNullOrWhiteSpace(MediaPath) ? null : MediaPath.Trim();
        MediaName = string.IsNullOrWhiteSpace(MediaName) ? null : MediaName.Trim();
        SlideshowFolder = string.IsNullOrWhiteSpace(SlideshowFolder) ? null : SlideshowFolder.Trim();
        SlideSeconds = Math.Clamp(SlideSeconds, MinSlideSeconds, MaxSlideSeconds);
        Fit = Enum.IsDefined(Fit) ? Fit : ImageFit.Fill;
        Background = MediaFiles.TryParseColor(Background, out var rgb) ? MediaFiles.FormatColor(rgb) : DefaultBackground;
        MaxFramesPerSecond = Math.Clamp(MaxFramesPerSecond, MinFps, MaxFps);
    }

    /// <summary><see cref="Background"/> as 0xRRGGBB (black if it isn't a colour).</summary>
    public int GetBackgroundRgb() => MediaFiles.TryParseColor(Background, out var rgb) ? rgb : 0;

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
