using System.Globalization;

namespace SpaceDeck.Core.Screens;

/// <summary>
/// How long and how fast <c>spacedeck-cli screen-test</c> sends its test picture, and at which JPEG quality: <c>--seconds</c>,
/// <c>--fps</c> and <c>--quality</c>. Pure, so the bounds are unit-tested.
/// </summary>
public sealed record ScreenTestSettings(int Seconds, int FramesPerSecond, int Quality)
{
    public const int DefaultSeconds = 30;
    public const int MinSeconds = 5;
    public const int MaxSeconds = 600;

    /// <summary>One frame a second unless asked: what the app's dashboards send.</summary>
    public const int DefaultFramesPerSecond = 1;

    public const int MinFramesPerSecond = 1;

    /// <summary>The collector's own limit per screen; more would only be dropped there.</summary>
    public const int MaxFramesPerSecond = ScreenFrameRules.MaxFramesPerSecond;

    /// <summary>The JPEG quality unless asked: what the app's dashboards are encoded at.</summary>
    public const int DefaultQuality = 85;

    /// <summary>Below this a test picture is too blocky to judge the screen by.</summary>
    public const int MinQuality = 30;

    public const int MaxQuality = 100;

    /// <summary>
    /// The settings from the option values (null when an option wasn't given), or null with <paramref name="error"/> when
    /// <c>--fps</c> isn't a whole number from 1 to 30 or <c>--quality</c> one from 30 to 100. <c>--seconds</c> is kept
    /// within 5–600 and falls back to 30 when it isn't a number, as before <c>--fps</c> existed.
    /// </summary>
    public static ScreenTestSettings? Parse(string? seconds, string? framesPerSecond, string? quality, out string? error)
    {
        var testSeconds = int.TryParse(seconds, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s)
            ? Math.Clamp(s, MinSeconds, MaxSeconds)
            : DefaultSeconds;

        var fps = DefaultFramesPerSecond;
        if (framesPerSecond is not null
            && (!int.TryParse(framesPerSecond, NumberStyles.Integer, CultureInfo.InvariantCulture, out fps)
                || fps < MinFramesPerSecond
                || fps > MaxFramesPerSecond))
        {
            error = string.Create(CultureInfo.InvariantCulture,
                $"--fps must be a whole number from {MinFramesPerSecond} to {MaxFramesPerSecond} (frames a second), not '{framesPerSecond}'.");
            return null;
        }

        var jpegQuality = DefaultQuality;
        if (quality is not null
            && (!int.TryParse(quality, NumberStyles.Integer, CultureInfo.InvariantCulture, out jpegQuality)
                || jpegQuality < MinQuality
                || jpegQuality > MaxQuality))
        {
            error = string.Create(CultureInfo.InvariantCulture,
                $"--quality must be a whole number from {MinQuality} to {MaxQuality} (JPEG quality), not '{quality}'.");
            return null;
        }

        error = null;
        return new ScreenTestSettings(testSeconds, fps, jpegQuality);
    }
}
