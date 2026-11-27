namespace SpaceDeck.Core.Screens;

/// <summary>
/// How large a moving cooler screen frame may be. The Lian Li GA II LCD flashed with 128 KB frames at 30 a second
/// (≈ 3.8 MB/s) and was clean with ≈ 23 KB frames at 30 a second (≈ 0.7 MB/s), so animated frames share a budget of
/// <see cref="MaxBytesPerSecond"/>: 72 KiB each at 30 a second, more at lower rates, never more than
/// <see cref="ScreenFrameRules.MaxJpegBytes"/>. A still picture is sent rarely and may use the whole 256 KiB.
/// </summary>
public static class ScreenFrameBudget
{
    /// <summary>Bytes a second an animated screen may use: 72 KiB × 30.</summary>
    public const int MaxBytesPerSecond = 72 * 1024 * 30;

    /// <summary>The largest frame, in bytes, for an animation sent at up to <paramref name="framesPerSecond"/> (1–30, clamped).</summary>
    public static int ForAnimation(int framesPerSecond)
    {
        var fps = Math.Clamp(framesPerSecond, CoolerScreenSettings.MinFps, CoolerScreenSettings.MaxFps);
        return Math.Min(ScreenFrameRules.MaxJpegBytes, MaxBytesPerSecond / fps);
    }
}

/// <summary>
/// Picks the JPEG quality for each frame of one screen so frames stay within a byte budget, without encoding every frame
/// several times: it starts where the last frame fitted, steps down by <see cref="Step"/> while a frame is too large,
/// and creeps back up toward the chosen quality after <see cref="FramesBeforeRaise"/> frames in a row that came out
/// well under the budget. Not thread-safe: one per player.
/// </summary>
/// <remarks>
/// Per frame: encode at <see cref="Start"/>, then call <see cref="After"/> with the size and encode again at the quality
/// it returns, until it returns null (the frame fits, or the lowest quality was reached).
/// </remarks>
public sealed class JpegQualityPicker
{
    /// <summary>Quality points per step down or up.</summary>
    public const int Step = 5;

    /// <summary>Frames in a row under <see cref="RaiseBelow"/> of the budget before trying a step higher.</summary>
    public const int FramesBeforeRaise = 30;

    /// <summary>Fraction of the budget a frame must stay under to count toward a step up (so it doesn't seesaw).</summary>
    public const double RaiseBelow = 0.7;

    private int _underStreak;

    public JpegQualityPicker(int chosenQuality, int budgetBytes)
    {
        Configure(chosenQuality, budgetBytes);
    }

    /// <summary>The quality the user chose (30–100); the picker never goes above it.</summary>
    public int Chosen { get; private set; }

    /// <summary>The largest frame wanted, in bytes.</summary>
    public int BudgetBytes { get; private set; }

    /// <summary>The quality to encode the next frame at first.</summary>
    public int Start { get; private set; }

    /// <summary>
    /// Sets the chosen quality and budget; when either changed, starts again from the chosen quality. Returns whether it
    /// changed anything.
    /// </summary>
    public bool Configure(int chosenQuality, int budgetBytes)
    {
        var chosen = Math.Clamp(chosenQuality, CoolerScreenSettings.MinQuality, CoolerScreenSettings.MaxQuality);
        var budget = Math.Clamp(budgetBytes, 1, ScreenFrameRules.MaxJpegBytes);
        if (chosen == Chosen && budget == BudgetBytes)
        {
            return false;
        }

        Chosen = chosen;
        BudgetBytes = budget;
        Start = chosen;
        _underStreak = 0;
        return true;
    }

    /// <summary>
    /// After a frame encoded at <paramref name="quality"/> came out <paramref name="bytes"/> long: the quality to encode
    /// the same frame at again, or null when the frame is done (it fits, or <see cref="CoolerScreenSettings.MinQuality"/>
    /// was reached; the caller still checks the hard <see cref="ScreenFrameRules.MaxJpegBytes"/> limit).
    /// </summary>
    public int? After(int quality, int bytes)
    {
        quality = Math.Clamp(quality, CoolerScreenSettings.MinQuality, Chosen);
        if (bytes > BudgetBytes)
        {
            _underStreak = 0;
            if (quality <= CoolerScreenSettings.MinQuality)
            {
                Start = CoolerScreenSettings.MinQuality;
                return null;
            }

            var lower = Math.Max(CoolerScreenSettings.MinQuality, quality - Step);
            Start = lower;
            return lower;
        }

        Start = quality;
        if (quality < Chosen && bytes < BudgetBytes * RaiseBelow)
        {
            if (++_underStreak >= FramesBeforeRaise)
            {
                Start = Math.Min(Chosen, quality + Step);
                _underStreak = 0;
            }
        }
        else
        {
            _underStreak = 0;
        }

        return null;
    }
}
