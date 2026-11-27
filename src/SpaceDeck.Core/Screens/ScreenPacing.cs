namespace SpaceDeck.Core.Screens;

/// <summary>
/// When to send the next cooler screen frame: as soon as the picture changes, but never more often than the screen's
/// "max frames per second", and a picture that doesn't change again after <see cref="KeepAlive"/> (the cooler drops a
/// frame and shows its own screen after about 20 s). Times are from any clock that only moves forward; pure, so the
/// pacing is tested on a manual clock.
/// </summary>
/// <remarks>
/// Frames are kept on a grid of <see cref="MinInterval"/> while the content changes faster than that, so a timer that
/// wakes late (the 15.6 ms system tick) costs no frames on average: the next one is due a little sooner. After a pause,
/// or a stall longer than an interval, the grid starts again at the frame being sent.
/// </remarks>
public sealed class FramePacer
{
    private TimeSpan? _lastSlot;
    private long _lastVersion;

    /// <param name="maxFramesPerSecond">1–30 (clamped).</param>
    /// <param name="keepAlive">A picture that hasn't changed is sent again this long after the last frame.</param>
    public FramePacer(int maxFramesPerSecond, TimeSpan keepAlive)
    {
        MaxFramesPerSecond = Math.Clamp(maxFramesPerSecond, CoolerScreenSettings.MinFps, CoolerScreenSettings.MaxFps);
        MinInterval = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / MaxFramesPerSecond);
        KeepAlive = keepAlive < MinInterval ? MinInterval : keepAlive;
    }

    public int MaxFramesPerSecond { get; }

    public TimeSpan MinInterval { get; }

    public TimeSpan KeepAlive { get; }

    /// <summary>
    /// Whether to send a frame now, for the content's <paramref name="version"/> (any number that changes whenever the
    /// picture does). True also takes the slot: the next frame is measured from it.
    /// </summary>
    public bool ShouldSend(TimeSpan now, long version)
    {
        if (_lastSlot is not { } last)
        {
            _lastSlot = now;
            _lastVersion = version;
            return true;
        }

        var due = last + MinInterval;
        if (now < due || (version == _lastVersion && now < last + KeepAlive))
        {
            return false;
        }

        _lastSlot = now - due < MinInterval ? due : now;
        _lastVersion = version;
        return true;
    }

    /// <summary>
    /// When to look again: right away before the first frame; at the next slot when the content showing now
    /// (<paramref name="version"/>) hasn't been sent; otherwise at its next change (<paramref name="nextChange"/>, null
    /// for none), but not before the next slot and not after the keep-alive.
    /// </summary>
    public TimeSpan NextCheck(TimeSpan now, long version, TimeSpan? nextChange)
    {
        if (_lastSlot is not { } last)
        {
            return now;
        }

        var due = last + MinInterval;
        var keep = last + KeepAlive;
        var wake = version != _lastVersion ? due
            : nextChange is { } change ? Min(Max(change, due), keep)
            : keep;
        return Max(wake, now);
    }

    /// <summary>Forgets the last frame: the next <see cref="ShouldSend"/> sends at once.</summary>
    public void Reset() => _lastSlot = null;

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}

/// <summary>Frames a second over the last few seconds, for the status line ("24.0 fps").</summary>
public sealed class FrameRateMeter
{
    private readonly Queue<TimeSpan> _times = new();
    private readonly TimeSpan _window;

    public FrameRateMeter()
        : this(TimeSpan.FromSeconds(2))
    {
    }

    public FrameRateMeter(TimeSpan window)
    {
        _window = window > TimeSpan.Zero ? window : TimeSpan.FromSeconds(2);
    }

    /// <summary>A frame was sent at <paramref name="time"/>.</summary>
    public void Add(TimeSpan time)
    {
        _times.Enqueue(time);
        Trim(time);
    }

    /// <summary>
    /// Frames a second between the first and the last frame of the window ending at <paramref name="now"/>; 0 with
    /// fewer than two frames in it.
    /// </summary>
    public double FramesPerSecond(TimeSpan now)
    {
        Trim(now);
        if (_times.Count < 2)
        {
            return 0;
        }

        var span = (_times.Last() - _times.Peek()).TotalSeconds;
        return span > 0 ? (_times.Count - 1) / span : 0;
    }

    public void Clear() => _times.Clear();

    private void Trim(TimeSpan now)
    {
        while (_times.Count > 0 && _times.Peek() <= now - _window)
        {
            _times.Dequeue();
        }
    }
}
