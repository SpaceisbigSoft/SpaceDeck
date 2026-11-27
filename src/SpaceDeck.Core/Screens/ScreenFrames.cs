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

    /// <summary>
    /// Frames a second per screen on average; more are dropped, not queued. 30 is enough for smooth motion and what
    /// SpaceDeck's FanControl plugin sends to a cooler at most.
    /// </summary>
    public const int MaxFramesPerSecond = 30;

    /// <summary>
    /// Frames that may pass at once after a quiet spell, per screen and on the screen pipe: the size of their token buckets
    /// (<see cref="ScreenFrameBucket"/>). A fifth of a second of the refill, so frames at up to
    /// <see cref="MaxFramesPerSecond"/> that come a timer tick (15.6 ms on Windows) early or late all pass, while the average
    /// stays at the limit.
    /// </summary>
    public const int BurstFrames = 6;

    /// <summary>
    /// A screen's sender that has sent nothing for this long lets another program take the screen
    /// (<see cref="ScreenFrameGate"/>); one that disconnects lets it go at once.
    /// </summary>
    public static readonly TimeSpan SenderIdle = TimeSpan.FromSeconds(3);

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
/// A token bucket of <c>capacity</c> tokens refilled at <c>perSecond</c> a second, on a <see cref="TimeProvider"/>: a frame
/// may go the moment a token is there, so frames at up to the rate pass however they jitter (within the capacity), and
/// never more than the rate on average. Nothing sleeps to pace: <see cref="Wait"/> says when the next token is due. Kept as
/// the time every token taken is paid back (the generic cell rate algorithm), counted in ticks × <c>perSecond</c> so a
/// thirtieth of a second isn't rounded. Not thread-safe: its owner serializes the calls.
/// </summary>
public sealed class ScreenFrameBucket
{
    private readonly TimeProvider _time;
    private readonly long _origin;
    private readonly long _perSecond;

    /// <summary>How far ahead of now <see cref="_paidBack"/> may be with a token still there: all tokens but one taken.</summary>
    private readonly long _tolerance;

    /// <summary>
    /// When every token taken so far is paid back, in ticks × perSecond since <see cref="_origin"/> (a token costs a
    /// second's ticks); at or before now, the bucket is full.
    /// </summary>
    private long _paidBack;

    public ScreenFrameBucket(int capacity, int perSecond, TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(perSecond, 1);
        _time = timeProvider ?? TimeProvider.System;
        _origin = _time.GetTimestamp();
        _perSecond = perSecond;
        _tolerance = (capacity - 1) * TimeSpan.TicksPerSecond;
    }

    /// <summary>How long until a token is there (rounded up to a whole tick); zero when one is there now.</summary>
    public TimeSpan Wait()
    {
        var early = _paidBack - _tolerance - Now();
        return early <= 0 ? TimeSpan.Zero : TimeSpan.FromTicks((early + _perSecond - 1) / _perSecond);
    }

    /// <summary>Takes a token if one is there (true); false when there is none until <see cref="Wait"/>.</summary>
    public bool TryTake()
    {
        if (Wait() > TimeSpan.Zero)
        {
            return false;
        }

        Take();
        return true;
    }

    /// <summary>Takes a token; the caller has seen <see cref="Wait"/> say one is there.</summary>
    public void Take() => _paidBack = Math.Max(_paidBack, Now()) + TimeSpan.TicksPerSecond;

    private long Now() => _time.GetElapsedTime(_origin).Ticks * _perSecond;
}

/// <summary>What <see cref="ScreenFrameGate"/> says about a frame.</summary>
public enum ScreenFrameVerdict
{
    /// <summary>The frame may pass (and was counted).</summary>
    Accepted,

    /// <summary>Over the screen's rate (or more screens than the gate tracks): drop it.</summary>
    OverLimit,

    /// <summary>Another program sends to this screen: drop it until that one is idle or gone.</summary>
    AnotherSender,
}

/// <summary>
/// Which frames from the app pipe may go to a screen (<c>docs/SCREEN-PIPE.md</c> §2). One sender per screen: the client
/// whose frame was last accepted owns it, and frames from any other are refused until the owner has sent nothing for
/// <see cref="ScreenFrameRules.SenderIdle"/> or disconnects (<see cref="Forget"/>), so two programs never interleave on one
/// screen. Then a token bucket per screen (<see cref="ScreenFrameRules.BurstFrames"/>, refilled at
/// <see cref="ScreenFrameRules.MaxFramesPerSecond"/> a second): a frame over the rate is refused, not delayed. Thread-safe.
/// </summary>
public sealed class ScreenFrameGate
{
    /// <summary>Screens tracked; a client inventing ids can't grow this without bound (ids are checked before, too).</summary>
    private const int MaxScreens = 256;

    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, ScreenState> _screens = new(StringComparer.Ordinal);

    public ScreenFrameGate(TimeProvider? timeProvider = null)
    {
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Whether a frame for <paramref name="screenId"/> from client <paramref name="senderId"/> (a program named
    /// <paramref name="senderName"/>, for the status line) may pass now; an accepted one is counted.
    /// </summary>
    public ScreenFrameVerdict TryAccept(string screenId, long senderId, string senderName)
    {
        ArgumentNullException.ThrowIfNull(screenId);
        ArgumentNullException.ThrowIfNull(senderName);
        var now = _time.GetTimestamp();
        lock (_gate)
        {
            if (!_screens.TryGetValue(screenId, out var screen))
            {
                if (_screens.Count >= MaxScreens)
                {
                    return ScreenFrameVerdict.OverLimit;
                }

                screen = new ScreenState(new ScreenFrameBucket(ScreenFrameRules.BurstFrames, ScreenFrameRules.MaxFramesPerSecond, _time));
                _screens[screenId] = screen;
            }

            if (screen.SenderName is not null && screen.SenderId != senderId && _time.GetElapsedTime(screen.LastFrame, now) < ScreenFrameRules.SenderIdle)
            {
                return ScreenFrameVerdict.AnotherSender;
            }

            // The sender's frames over the rate still count as sending: it keeps the screen while it sends at all.
            screen.SenderId = senderId;
            screen.SenderName = senderName;
            screen.LastFrame = now;
            return screen.Bucket.TryTake() ? ScreenFrameVerdict.Accepted : ScreenFrameVerdict.OverLimit;
        }
    }

    /// <summary>Client <paramref name="senderId"/> disconnected: the screens it sent to are free for the next sender.</summary>
    public void Forget(long senderId)
    {
        lock (_gate)
        {
            foreach (var screen in _screens.Values)
            {
                if (screen.SenderName is not null && screen.SenderId == senderId)
                {
                    screen.SenderName = null;
                }
            }
        }
    }

    /// <summary>
    /// The names of the programs that hold a screen now (sent within <see cref="ScreenFrameRules.SenderIdle"/>), each once,
    /// in order; empty when none does.
    /// </summary>
    public IReadOnlyList<string> Senders()
    {
        var now = _time.GetTimestamp();
        lock (_gate)
        {
            return _screens.Values
                .Where(s => _time.GetElapsedTime(s.LastFrame, now) < ScreenFrameRules.SenderIdle)
                .Select(s => s.SenderName)
                .OfType<string>()
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToList();
        }
    }

    /// <summary>One screen's bucket and sender; <see cref="SenderName"/> null = no sender. Under the gate's lock.</summary>
    private sealed class ScreenState(ScreenFrameBucket bucket)
    {
        public ScreenFrameBucket Bucket { get; } = bucket;

        public long SenderId { get; set; }

        public string? SenderName { get; set; }

        public long LastFrame { get; set; }
    }
}

/// <summary>
/// Frames a second over the last <see cref="Window"/>, for the screen pipe's status line: each counted frame is kept by its
/// time until it is older than the window. Thread-safe.
/// </summary>
public sealed class ScreenFrameRate
{
    /// <summary>How far back the rate looks: long enough to smooth out a frame's jitter, short enough to follow a change.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Times kept at most: the screen pipe writes at most 30 messages a second, 150 in a window, so the cap only bounds
    /// memory if that ever changes.
    /// </summary>
    private const int MaxKept = 1024;

    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly Queue<long> _counted = new();

    public ScreenFrameRate(TimeProvider? timeProvider = null)
    {
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>A frame went out now.</summary>
    public void Count()
    {
        var now = _time.GetTimestamp();
        lock (_gate)
        {
            Forget(now);
            if (_counted.Count >= MaxKept)
            {
                _counted.Dequeue();
            }

            _counted.Enqueue(now);
        }
    }

    /// <summary>The frames counted within the last <see cref="Window"/>, a second.</summary>
    public double PerSecond()
    {
        var now = _time.GetTimestamp();
        lock (_gate)
        {
            Forget(now);
            return _counted.Count / Window.TotalSeconds;
        }
    }

    /// <summary>Drops the times that are a whole window old or older. Caller holds <see cref="_gate"/>.</summary>
    private void Forget(long now)
    {
        while (_counted.Count > 0 && _time.GetElapsedTime(_counted.Peek(), now) >= Window)
        {
            _counted.Dequeue();
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

/// <summary>
/// Paces the screen pipe (<c>docs/SCREEN-PIPE.md</c> §2): at most <see cref="MessagesPerSecond"/> messages a second in all on
/// average, at most <see cref="BurstMessages"/> at once (<see cref="ScreenFrameBucket"/>). A frame goes out the moment it
/// is taken while a token is there, so frames at up to the rate never wait for a minimum gap that a timer would round up
/// (a 33.3 ms gap slept on Windows' 15.6 ms timer is 46.8 ms: 21 frames a second). Without a token the frame stays in its
/// slot (newest only) and <see cref="TryTake"/> says when to come back. One per connection; not thread-safe (the
/// connection's writer is its only caller).
/// </summary>
public sealed class ScreenPipePacer
{
    /// <summary>Messages a second on average: one screen's frames at their own limit, and within the plugin's (30, bursts of 60).</summary>
    public const int MessagesPerSecond = ScreenFrameRules.MaxFramesPerSecond;

    /// <summary>Messages written at once after a quiet spell; well within the plugin's burst of 60.</summary>
    public const int BurstMessages = ScreenFrameRules.BurstFrames;

    private readonly ScreenFrameMailbox _mailbox;
    private readonly ScreenFrameBucket _bucket;

    public ScreenPipePacer(ScreenFrameMailbox mailbox, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(mailbox);
        _mailbox = mailbox;
        _bucket = new ScreenFrameBucket(BurstMessages, MessagesPerSecond, timeProvider);
    }

    /// <summary>
    /// The frame to write now, its token taken; null when no frame is waiting (<paramref name="wait"/> zero: wait for the
    /// next one) or no token is there yet (<paramref name="wait"/>: when one is due; call again then, whether or not a frame
    /// is waiting). The frame is taken only once a token is there, so one replaced meanwhile goes out as its newest version.
    /// </summary>
    public PendingScreenFrame? TryTake(out TimeSpan wait)
    {
        wait = _bucket.Wait();
        if (wait > TimeSpan.Zero || _mailbox.Take() is not { } frame)
        {
            return null;
        }

        _bucket.Take();
        return frame;
    }
}
