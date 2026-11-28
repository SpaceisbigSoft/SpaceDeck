namespace SpaceDeck.Core.Model;

/// <summary>
/// When a source that opened without some hardware it should have (a motherboard's Super I/O chip) looks for it
/// again: once after each delay since it opened, until the hardware is there or the delays are used up. Hardware
/// found once (or there from the start) is never looked for again. Pure: the caller passes the time since opening.
/// </summary>
/// <remarks>Not thread-safe: one per source, used from the poll thread.</remarks>
public sealed class RedetectSchedule
{
    /// <summary>10 s, 30 s and 2 min after opening: past a busy moment of another program at boot or after a restart.</summary>
    public static readonly IReadOnlyList<TimeSpan> DefaultDelays =
        [TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2)];

    private readonly TimeSpan[] _delays;
    private int _next;

    /// <param name="delays">Times after opening, increasing; null for <see cref="DefaultDelays"/>.</param>
    public RedetectSchedule(IReadOnlyList<TimeSpan>? delays = null)
    {
        _delays = [.. delays ?? DefaultDelays];
        for (var i = 0; i < _delays.Length; i++)
        {
            if (_delays[i] < TimeSpan.Zero || (i > 0 && _delays[i] <= _delays[i - 1]))
            {
                throw new ArgumentException("Delays must be zero or more and increasing.", nameof(delays));
            }
        }
    }

    /// <summary>Looks made so far.</summary>
    public int Attempts { get; private set; }

    /// <summary>True once the hardware was seen or every delay was used: no more looks.</summary>
    public bool IsFinished => _next >= _delays.Length;

    /// <summary>
    /// Whether to look again now, <paramref name="sinceOpen"/> after opening, with the hardware still
    /// <paramref name="missing"/>. True at most once per call, and once per delay; several delays passed at once (a
    /// stalled poll) count as one look.
    /// </summary>
    public bool IsDue(TimeSpan sinceOpen, bool missing)
    {
        if (IsFinished)
        {
            return false;
        }

        if (!missing)
        {
            _next = _delays.Length;
            return false;
        }

        if (sinceOpen < _delays[_next])
        {
            return false;
        }

        while (_next < _delays.Length && sinceOpen >= _delays[_next])
        {
            _next++;
        }

        Attempts++;
        return true;
    }
}
