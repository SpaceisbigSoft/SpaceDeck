using SpaceDeck.Core.Model;

namespace SpaceDeck.Core.Screens;

/// <summary>
/// What the collector accepts as a cooler screen frame from the app (<c>docs/SCREEN-PIPE.md</c> §2). Pure, so every rule is
/// unit-tested on any platform.
/// </summary>
public static class ScreenFrameRules
{
    /// <summary>Largest JPEG, in bytes (also the screen pipe's payload limit).</summary>
    public const int MaxJpegBytes = 256 * 1024;

    /// <summary>Frames a second per screen; more are dropped, not queued.</summary>
    public const int MaxFramesPerSecond = 2;

    /// <summary>A frame not passed on within this long is dropped: the cooler must never show a stale picture late.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Null if <paramref name="jpeg"/> may go to a screen of <paramref name="screen"/>'s size; otherwise why not (for
    /// counters and logs). Checks the size limit, the SOI/EOI markers and the frame header's width and height only: the
    /// collector never decodes a picture.
    /// </summary>
    public static string? Validate(ReadOnlySpan<byte> jpeg, ScreenInfo screen)
    {
        ArgumentNullException.ThrowIfNull(screen);
        if (jpeg.Length is 0 or > MaxJpegBytes)
        {
            return "the JPEG is empty or larger than 256 KiB";
        }

        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8 || jpeg[^2] != 0xFF || jpeg[^1] != 0xD9)
        {
            return "the JPEG doesn't start with FF D8 and end with FF D9";
        }

        if (!TryReadSize(jpeg, out var width, out var height))
        {
            return "the JPEG has no readable frame header";
        }

        return width == screen.Width && height == screen.Height
            ? null
            : "the JPEG isn't the screen's size";
    }

    /// <summary>
    /// Reads the width and height from the first frame header (SOF0–SOF15, not DHT/JPG/DAC), walking the marker segments
    /// from SOI. False for anything that isn't a well-formed run of segments up to a frame header (a scan before it
    /// included). Never reads outside <paramref name="jpeg"/>.
    /// </summary>
    public static bool TryReadSize(ReadOnlySpan<byte> jpeg, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
        {
            return false;
        }

        var i = 2;
        while (i < jpeg.Length)
        {
            if (jpeg[i] != 0xFF)
            {
                return false;
            }

            while (i < jpeg.Length && jpeg[i] == 0xFF)
            {
                i++; // the marker and any fill bytes before it
            }

            if (i >= jpeg.Length)
            {
                return false;
            }

            var marker = jpeg[i++];
            if (marker is 0x01 or (>= 0xD0 and <= 0xD7))
            {
                continue; // TEM and RSTn stand alone (no length)
            }

            if (marker is 0x00 or 0xD8 or 0xD9 or 0xDA)
            {
                return false; // a stuffed byte, a second SOI, EOI or a scan before any frame header
            }

            if (i + 2 > jpeg.Length)
            {
                return false;
            }

            var length = (jpeg[i] << 8) | jpeg[i + 1];
            if (length < 2 || length > jpeg.Length - i)
            {
                return false;
            }

            if (marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC))
            {
                // Lf(2) P(1) Y(2) X(2) Nf(1)…
                if (length < 8)
                {
                    return false;
                }

                height = (jpeg[i + 3] << 8) | jpeg[i + 4];
                width = (jpeg[i + 5] << 8) | jpeg[i + 6];
                return true;
            }

            i += length;
        }

        return false;
    }
}

/// <summary>
/// At most <see cref="ScreenFrameRules.MaxFramesPerSecond"/> frames in any one second per screen (a sliding window over the
/// last accepted frames); a frame over the limit is refused, not delayed. Thread-safe.
/// </summary>
public sealed class ScreenFrameGate
{
    /// <summary>Screens tracked; a client inventing ids can't grow this without bound (ids are checked before, too).</summary>
    private const int MaxScreens = 256;

    private static readonly TimeSpan Window = TimeSpan.FromSeconds(1);

    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, long[]> _accepted = new(StringComparer.Ordinal);

    public ScreenFrameGate(TimeProvider? timeProvider = null)
    {
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>True if a frame for <paramref name="screenId"/> may pass now (and counts it); false = drop it.</summary>
    public bool TryAccept(string screenId)
    {
        ArgumentNullException.ThrowIfNull(screenId);
        var now = _time.GetTimestamp();
        lock (_gate)
        {
            if (!_accepted.TryGetValue(screenId, out var recent))
            {
                if (_accepted.Count >= MaxScreens)
                {
                    return false;
                }

                // Timestamps of the last accepted frames, oldest first; long.MinValue = none yet.
                recent = new long[ScreenFrameRules.MaxFramesPerSecond];
                Array.Fill(recent, long.MinValue);
                _accepted[screenId] = recent;
            }

            if (recent[0] != long.MinValue && _time.GetElapsedTime(recent[0], now) < Window)
            {
                return false;
            }

            Array.Copy(recent, 1, recent, 0, recent.Length - 1);
            recent[^1] = now;
            return true;
        }
    }
}

/// <summary>One frame waiting to go to a screen.</summary>
/// <param name="ScreenId">The plugin's own hardware id (<c>LianLi/0</c>).</param>
public sealed record PendingScreenFrame(string ScreenId, byte[] Jpeg, long Timestamp);

/// <summary>
/// The newest frame per screen, waiting for the screen pipe (<c>docs/SCREEN-PIPE.md</c> §2): a new frame replaces an
/// unsent one (counted as dropped), each frame is taken once, and one older than <see cref="ScreenFrameRules.MaxAge"/> is
/// dropped instead of taken. Thread-safe; <see cref="FrameReady"/> is raised (outside the lock) when a frame is put.
/// </summary>
public sealed class ScreenFrameMailbox
{
    /// <summary>Screens with a slot; bounds memory (each slot holds at most 256 KiB).</summary>
    public const int MaxScreens = 32;

    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, PendingScreenFrame> _slots = new(StringComparer.Ordinal);
    private long _dropped;

    public ScreenFrameMailbox(TimeProvider? timeProvider = null)
    {
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>A frame was put; the screen pipe's writer wakes up.</summary>
    public event Action? FrameReady;

    /// <summary>Frames replaced before they were sent, or that grew too old (since the collector started).</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>Puts the newest frame for a screen, replacing an unsent one. False when all slots are taken by other screens.</summary>
    public bool Put(string screenId, byte[] jpeg)
    {
        ArgumentNullException.ThrowIfNull(screenId);
        ArgumentNullException.ThrowIfNull(jpeg);
        lock (_gate)
        {
            if (_slots.ContainsKey(screenId))
            {
                Interlocked.Increment(ref _dropped);
            }
            else if (_slots.Count >= MaxScreens)
            {
                return false;
            }

            _slots[screenId] = new PendingScreenFrame(screenId, jpeg, _time.GetTimestamp());
        }

        FrameReady?.Invoke();
        return true;
    }

    /// <summary>
    /// Takes the oldest waiting frame that is still fresh, removing it; frames older than
    /// <see cref="ScreenFrameRules.MaxAge"/> are dropped on the way. Null when nothing is waiting.
    /// </summary>
    public PendingScreenFrame? Take()
    {
        var now = _time.GetTimestamp();
        lock (_gate)
        {
            PendingScreenFrame? oldest = null;
            List<string>? stale = null;
            foreach (var frame in _slots.Values)
            {
                if (_time.GetElapsedTime(frame.Timestamp, now) > ScreenFrameRules.MaxAge)
                {
                    (stale ??= []).Add(frame.ScreenId);
                }
                else if (oldest is null || frame.Timestamp < oldest.Timestamp)
                {
                    oldest = frame;
                }
            }

            if (stale is not null)
            {
                foreach (var id in stale)
                {
                    _slots.Remove(id);
                    Interlocked.Increment(ref _dropped);
                }
            }

            if (oldest is not null)
            {
                _slots.Remove(oldest.ScreenId);
            }

            return oldest;
        }
    }

    /// <summary>Drops waiting frames that are too old (so a frame for an absent plugin doesn't sit in memory). Returns how many.</summary>
    public int DropStale()
    {
        var now = _time.GetTimestamp();
        lock (_gate)
        {
            var stale = _slots.Values.Where(f => _time.GetElapsedTime(f.Timestamp, now) > ScreenFrameRules.MaxAge).Select(f => f.ScreenId).ToList();
            foreach (var id in stale)
            {
                _slots.Remove(id);
                Interlocked.Increment(ref _dropped);
            }

            return stale.Count;
        }
    }
}
