using System.IO;
using SpaceDeck.Core.Screens;

namespace SpaceDeck.App.Services.Media;

/// <summary>
/// What a cooler screen plays, frame by frame, on the player's thread: a picture, a GIF, a video or a slideshow. The
/// player moves it to the current time, then reads the picture showing; <see cref="Version"/> changes whenever that
/// picture does, and <see cref="NextChange"/> says when it will next.
/// </summary>
internal interface IScreenContent : IDisposable
{
    /// <summary>Changes whenever <see cref="Picture"/> does.</summary>
    long Version { get; }

    /// <summary>When the picture changes next, in the player's time; null when it never does (a still picture).</summary>
    TimeSpan? NextChange { get; }

    /// <summary>The picture showing (BGRA; owned by the content, valid until the next <see cref="Seek"/>).</summary>
    BgraImage Picture { get; }

    /// <summary>The picture's fourth byte means nothing (video): draw it opaque.</summary>
    bool IsOpaque { get; }

    /// <summary>Moves to <paramref name="time"/> since playing began (only ever forward).</summary>
    void Seek(TimeSpan time);
}

/// <summary>A still picture or a GIF, looping at its own frame delays.</summary>
internal sealed class ClipContent : IScreenContent
{
    private readonly AnimationClip _clip;
    private readonly GifComposer _composer;

    public ClipContent(AnimationClip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        _clip = clip;
        _composer = new GifComposer(clip);
        _composer.MoveTo(0);
    }

    public long Version { get; private set; }

    public TimeSpan? NextChange { get; private set; }

    public BgraImage Picture => _composer.Canvas;

    public bool IsOpaque => false;

    public void Seek(TimeSpan time)
    {
        var (index, loop, next) = AnimationTimeline.At(_clip, time);
        _composer.MoveTo(index);
        Version = (loop * _clip.Frames.Count) + index;
        NextChange = next;
    }

    public void Dispose()
    {
    }
}

/// <summary>
/// A video file, looping. Frames are read in order as time passes; when the decoder can't keep up it takes a few frames
/// per call and the video plays slower rather than freezing.
/// </summary>
internal sealed class VideoContent : IScreenContent
{
    /// <summary>Frames read at most per <see cref="Seek"/>, so a slow decoder can't hold the player up for long.</summary>
    private const int MaxFramesPerSeek = 8;

    private static readonly TimeSpan DefaultFrameTime = TimeSpan.FromSeconds(1 / 30.0);

    private readonly VideoFileReader _reader;
    private BgraImage? _current;
    private BgraImage? _next;
    private TimeSpan? _nextTime;
    private TimeSpan _loopStart;
    private TimeSpan _firstTimestamp;
    private TimeSpan _lastTimestamp;
    private TimeSpan _frameTime = DefaultFrameTime;
    private bool _haveFirst;

    /// <exception cref="IOException">Windows can't play the file, or it has no frames.</exception>
    public VideoContent(string path)
    {
        _reader = new VideoFileReader(path);
        try
        {
            if (!ReadNext())
            {
                throw new IOException("The video has no frames.");
            }

            Promote();
            ReadNext();
        }
        catch
        {
            _reader.Dispose();
            throw;
        }
    }

    public long Version { get; private set; }

    public TimeSpan? NextChange => _nextTime;

    public BgraImage Picture => _current ?? throw new InvalidOperationException("No frame was read.");

    public bool IsOpaque => true;

    public void Seek(TimeSpan time)
    {
        for (var i = 0; i < MaxFramesPerSeek && _nextTime is { } due && due <= time; i++)
        {
            Promote();
            ReadNext();
        }
    }

    public void Dispose() => _reader.Dispose();

    /// <summary>The frame read ahead becomes the one showing (the buffers swap, nothing is copied).</summary>
    private void Promote()
    {
        (_current, _next) = (_next, _current);
        Version++;
    }

    /// <summary>Reads the frame after the one showing; at the end of the file, starts the next loop.</summary>
    private bool ReadNext()
    {
        if (!_reader.TryRead(ref _next, out var timestamp))
        {
            if (!_haveFirst)
            {
                _nextTime = null;
                return false;
            }

            // The next loop starts one frame after the last frame of this one.
            _loopStart += _lastTimestamp - _firstTimestamp + _frameTime;
            _reader.Rewind();
            if (!_reader.TryRead(ref _next, out timestamp))
            {
                _nextTime = null;
                return false;
            }

            _lastTimestamp = timestamp;
            _nextTime = _loopStart + (timestamp - _firstTimestamp);
            return true;
        }

        if (!_haveFirst)
        {
            _haveFirst = true;
            _firstTimestamp = timestamp;
        }
        else if (timestamp > _lastTimestamp)
        {
            _frameTime = timestamp - _lastTimestamp;
        }

        _lastTimestamp = timestamp;
        _nextTime = _loopStart + (timestamp - _firstTimestamp);
        return true;
    }
}

/// <summary>The pictures and GIFs of a folder in name order, each for a set time (GIFs play meanwhile), looping.</summary>
internal sealed class SlideshowContent : IScreenContent
{
    private static readonly BgraImage Blank = new(1, 1);

    private readonly IReadOnlyList<string> _files;
    private readonly TimeSpan _slide;
    private long _slot = -1;
    private ClipContent? _clip;

    /// <exception cref="ArgumentException">No files.</exception>
    public SlideshowContent(IReadOnlyList<string> files, TimeSpan slide)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count == 0)
        {
            throw new ArgumentException("A slideshow needs at least one picture.", nameof(files));
        }

        _files = files;
        _slide = slide > TimeSpan.Zero ? slide : TimeSpan.FromSeconds(CoolerScreenSettings.DefaultSlideSeconds);
    }

    public long Version { get; private set; }

    public TimeSpan? NextChange { get; private set; }

    public BgraImage Picture => _clip?.Picture ?? Blank;

    public bool IsOpaque => false;

    /// <summary>Why the last picture couldn't be shown (it is skipped: the screen shows the background), or null.</summary>
    public string? LastError { get; private set; }

    public void Seek(TimeSpan time)
    {
        var slot = Math.Max(0, time.Ticks / _slide.Ticks);
        if (slot != _slot)
        {
            _slot = slot;
            _clip = Open(_files[(int)(slot % _files.Count)]);
        }

        var start = TimeSpan.FromTicks(slot * _slide.Ticks);
        var end = start + _slide;
        _clip?.Seek(time - start);
        Version = (slot * 1_000_000_000L) + (_clip?.Version ?? 0);
        NextChange = _clip?.NextChange is { } change && start + change < end ? start + change : end;
    }

    public void Dispose()
    {
    }

    private ClipContent? Open(string path)
    {
        try
        {
            LastError = null;
            return new ClipContent(MediaCache.LoadAsync(path).GetAwaiter().GetResult());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidDataException or ArgumentException
                                       or System.Runtime.InteropServices.COMException or FileFormatException)
        {
            LastError = $"{Path.GetFileName(path)}: {ex.Message}";
            return null;
        }
    }
}
