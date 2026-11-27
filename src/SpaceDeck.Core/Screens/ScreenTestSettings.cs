using System.Globalization;

namespace SpaceDeck.Core.Screens;

/// <summary>
/// How long and how fast <c>spacedeck-cli screen-test</c> sends its test picture: <c>--seconds</c> and <c>--fps</c>. Pure,
/// so the bounds are unit-tested.
/// </summary>
public sealed record ScreenTestSettings(int Seconds, int FramesPerSecond)
{
    public const int DefaultSeconds = 30;
    public const int MinSeconds = 5;
    public const int MaxSeconds = 600;

    /// <summary>One frame a second unless asked: what the app's dashboards send.</summary>
    public const int DefaultFramesPerSecond = 1;

    public const int MinFramesPerSecond = 1;

    /// <summary>The collector's own limit per screen; more would only be dropped there.</summary>
    public const int MaxFramesPerSecond = ScreenFrameRules.MaxFramesPerSecond;

    /// <summary>
    /// The settings from the option values (null when an option wasn't given), or null with <paramref name="error"/> when
    /// <c>--fps</c> isn't a whole number from 1 to 30. <c>--seconds</c> is kept within 5–600 and falls back to 30 when it
    /// isn't a number, as before <c>--fps</c> existed.
    /// </summary>
    public static ScreenTestSettings? Parse(string? seconds, string? framesPerSecond, out string? error)
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

        error = null;
        return new ScreenTestSettings(testSeconds, fps);
    }
}
