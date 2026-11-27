using System.Globalization;
using System.Text.RegularExpressions;

namespace SpaceDeck.Core.Screens;

/// <summary>
/// The counts in the screen pipe's status line (the <c>lianli-screen</c> source, <c>docs/SCREEN-PIPE.md</c> §2): frames
/// written to the plugin and frames dropped since the collector started, and the frames a second written over the last
/// <see cref="ScreenFrameRate.Window"/>. The collector writes them with <see cref="Format"/> and
/// <c>spacedeck-cli screen-test</c> reads them back with <see cref="TryFind"/>, so the two can't drift apart.
/// </summary>
public sealed partial class ScreenPipeCounts(long sent, long dropped, double framesPerSecond)
{
    /// <summary>Frames written to the plugin since the collector started.</summary>
    public long Sent { get; } = sent;

    /// <summary>Frames that didn't reach the plugin since the collector started (refused, replaced or too old).</summary>
    public long Dropped { get; } = dropped;

    /// <summary>Frames a second written to the plugin over the last <see cref="ScreenFrameRate.Window"/>.</summary>
    public double FramesPerSecond { get; } = framesPerSecond;

    /// <summary>"12 frames sent · 3 dropped · 29.8 frames/s".</summary>
    public string Format() =>
        string.Create(CultureInfo.InvariantCulture, $"{Sent} frames sent · {Dropped} dropped · {FramesPerSecond:0.0} frames/s");

    /// <summary>The counts in a status line written by <see cref="Format"/>; null when it has none (an older collector).</summary>
    public static ScreenPipeCounts? TryFind(string? message)
    {
        if (message is null)
        {
            return null;
        }

        var match = CountsPattern().Match(message);
        return match.Success
            && long.TryParse(match.Groups["sent"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var sentCount)
            && long.TryParse(match.Groups["dropped"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var droppedCount)
            && double.TryParse(match.Groups["rate"].ValueSpan, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var perSecond)
                ? new ScreenPipeCounts(sentCount, droppedCount, perSecond)
                : null;
    }

    [GeneratedRegex(@"(?<sent>[0-9]{1,19}) frames sent · (?<dropped>[0-9]{1,19}) dropped · (?<rate>[0-9]{1,9}\.[0-9]) frames/s", RegexOptions.CultureInvariant)]
    private static partial Regex CountsPattern();
}
