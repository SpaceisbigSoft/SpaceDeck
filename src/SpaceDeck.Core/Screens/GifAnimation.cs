namespace SpaceDeck.Core.Screens;

/// <summary>What a GIF frame asks for once its time is over, before the next frame is drawn (GIF89a "disposal method").</summary>
public enum GifDisposal
{
    /// <summary>Not specified: left as it is.</summary>
    None = 0,

    /// <summary>Left as it is; the next frame draws on top.</summary>
    Keep = 1,

    /// <summary>The frame's rectangle is cleared to transparent (what browsers do for "restore to background").</summary>
    Background = 2,

    /// <summary>The canvas goes back to how it was before this frame was drawn.</summary>
    Previous = 3,
}

/// <summary>
/// One frame of an animation as stored: a rectangle at (<see cref="Left"/>, <see cref="Top"/>) on the canvas, its BGRA
/// pixels (alpha 0 = transparent, so the canvas shows through), how long it shows and its disposal.
/// </summary>
public sealed class AnimationFrame
{
    /// <exception cref="ArgumentException">The size is not positive or <paramref name="pixels"/> is too short.</exception>
    public AnimationFrame(int left, int top, int width, int height, byte[] pixels, TimeSpan delay, GifDisposal disposal)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        if (width < 1 || height < 1 || (long)width * height * 4 > pixels.Length)
        {
            throw new ArgumentException("Expected a positive size and width × height × 4 bytes.", nameof(pixels));
        }

        Left = left;
        Top = top;
        Width = width;
        Height = height;
        Pixels = pixels;
        Delay = delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
        Disposal = disposal;
    }

    public int Left { get; }

    public int Top { get; }

    public int Width { get; }

    public int Height { get; }

    public byte[] Pixels { get; }

    public TimeSpan Delay { get; }

    public GifDisposal Disposal { get; }
}

/// <summary>
/// A decoded picture or animation: the canvas size and its frames, in order. A still picture is one full-size frame. The
/// frames are kept as stored (optimized GIFs keep only what changed), so memory stays small; <see cref="GifComposer"/>
/// draws them in turn.
/// </summary>
public sealed class AnimationClip
{
    /// <exception cref="ArgumentException">No frames, or a size that is not positive.</exception>
    public AnimationClip(int width, int height, IReadOnlyList<AnimationFrame> frames)
    {
        ArgumentNullException.ThrowIfNull(frames);
        if (width < 1 || height < 1 || frames.Count == 0)
        {
            throw new ArgumentException("Expected a positive size and at least one frame.", nameof(frames));
        }

        Width = width;
        Height = height;
        Frames = frames;
        Duration = TimeSpan.FromTicks(frames.Sum(f => f.Delay.Ticks));
    }

    public int Width { get; }

    public int Height { get; }

    public IReadOnlyList<AnimationFrame> Frames { get; }

    /// <summary>One pass through every frame.</summary>
    public TimeSpan Duration { get; }

    /// <summary>More than one frame, with time to show them.</summary>
    public bool IsAnimated => Frames.Count > 1 && Duration > TimeSpan.Zero;

    /// <summary>A still picture as a clip of one frame.</summary>
    public static AnimationClip Still(BgraImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return new AnimationClip(image.Width, image.Height, [new AnimationFrame(0, 0, image.Width, image.Height, image.Pixels, TimeSpan.Zero, GifDisposal.None)]);
    }
}

/// <summary>
/// Draws an animation's frames onto a canvas the way browsers do, so optimized GIFs (frames that only hold what
/// changed, with transparent holes and disposal methods) come out whole. Forward only; going back starts over from
/// the first frame. Not thread-safe: one composer per player.
/// </summary>
public sealed class GifComposer
{
    private readonly AnimationClip _clip;
    private byte[]? _saved;

    public GifComposer(AnimationClip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        _clip = clip;
        Canvas = new BgraImage(clip.Width, clip.Height);
    }

    /// <summary>The composed picture (straight alpha; transparent where no frame has drawn).</summary>
    public BgraImage Canvas { get; }

    /// <summary>The frame last drawn; -1 before the first.</summary>
    public int Index { get; private set; } = -1;

    /// <summary>Composes up to frame <paramref name="index"/> (clamped to the frames there are).</summary>
    public void MoveTo(int index)
    {
        index = Math.Clamp(index, 0, _clip.Frames.Count - 1);
        if (index < Index)
        {
            Reset();
        }

        while (Index < index)
        {
            Step();
        }
    }

    /// <summary>Back to an empty canvas, before the first frame.</summary>
    public void Reset()
    {
        Array.Clear(Canvas.Pixels);
        _saved = null;
        Index = -1;
    }

    private void Step()
    {
        if (Index >= 0)
        {
            ApplyDisposal(_clip.Frames[Index]);
        }

        Index++;
        var frame = _clip.Frames[Index];
        if (frame.Disposal == GifDisposal.Previous)
        {
            _saved = (byte[])Canvas.Pixels.Clone();
        }

        Draw(frame);
    }

    private void ApplyDisposal(AnimationFrame frame)
    {
        switch (frame.Disposal)
        {
            case GifDisposal.Background:
                ForEachPixel(frame, (canvas, _) => Array.Clear(Canvas.Pixels, canvas, 4));
                break;
            case GifDisposal.Previous when _saved is not null:
                Buffer.BlockCopy(_saved, 0, Canvas.Pixels, 0, Canvas.Pixels.Length);
                _saved = null;
                break;
        }
    }

    /// <summary>Opaque pixels replace the canvas; transparent ones (alpha 0) leave it showing.</summary>
    private void Draw(AnimationFrame frame) => ForEachPixel(frame, (canvas, source) =>
    {
        if (frame.Pixels[source + 3] != 0)
        {
            Buffer.BlockCopy(frame.Pixels, source, Canvas.Pixels, canvas, 4);
        }
    });

    /// <summary>Calls <paramref name="action"/>(canvas offset, frame offset) for the frame's pixels that lie on the canvas.</summary>
    private void ForEachPixel(AnimationFrame frame, Action<int, int> action)
    {
        var x0 = Math.Max(0, frame.Left);
        var y0 = Math.Max(0, frame.Top);
        var x1 = Math.Min(Canvas.Width, frame.Left + frame.Width);
        var y1 = Math.Min(Canvas.Height, frame.Top + frame.Height);
        for (var y = y0; y < y1; y++)
        {
            for (var x = x0; x < x1; x++)
            {
                action((y * Canvas.Stride) + (x * 4), (((y - frame.Top) * frame.Width) + (x - frame.Left)) * 4);
            }
        }
    }
}

/// <summary>Which frame of a looping animation shows when.</summary>
public static class AnimationTimeline
{
    /// <summary>Shortest frame time a GIF may ask for; shorter ones (and 0) play at <see cref="ShortDelayReplacement"/>.</summary>
    public static readonly TimeSpan MinimumDelay = TimeSpan.FromMilliseconds(20);

    /// <summary>What browsers use for a GIF frame delay under 20 ms (most of those were made for 10 fps).</summary>
    public static readonly TimeSpan ShortDelayReplacement = TimeSpan.FromMilliseconds(100);

    /// <summary>A GIF frame delay, given in hundredths of a second, as a time: under 20 ms becomes 100 ms.</summary>
    public static TimeSpan GifDelay(int hundredths)
    {
        var delay = TimeSpan.FromMilliseconds(Math.Max(0, hundredths) * 10.0);
        return delay < MinimumDelay ? ShortDelayReplacement : delay;
    }

    /// <summary>
    /// The frame showing at <paramref name="time"/> since the animation started (looping forever), how many loops
    /// came before it, and when the next frame is due (in the same time; null for a still picture).
    /// </summary>
    public static (int Index, long Loop, TimeSpan? NextChange) At(AnimationClip clip, TimeSpan time)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (!clip.IsAnimated)
        {
            return (0, 0, null);
        }

        if (time < TimeSpan.Zero)
        {
            time = TimeSpan.Zero;
        }

        var loop = time.Ticks / clip.Duration.Ticks;
        var loopStart = loop * clip.Duration.Ticks;
        var into = time.Ticks - loopStart;
        var end = 0L;
        for (var i = 0; i < clip.Frames.Count; i++)
        {
            end += clip.Frames[i].Delay.Ticks;
            if (into < end)
            {
                return (i, loop, TimeSpan.FromTicks(loopStart + end));
            }
        }

        return (clip.Frames.Count - 1, loop, TimeSpan.FromTicks(loopStart + clip.Duration.Ticks)); // not reached: into < Duration
    }
}
