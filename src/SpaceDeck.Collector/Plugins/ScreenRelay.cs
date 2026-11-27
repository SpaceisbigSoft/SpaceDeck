using SpaceDeck.Core.Model;
using SpaceDeck.Core.Protocol;
using SpaceDeck.Core.Screens;

namespace SpaceDeck.Collector.Plugins;

/// <summary>
/// Cooler screen frames on their way from the app to SpaceDeck's FanControl plugin (<c>docs/SCREEN-PIPE.md</c>): app
/// clients <see cref="Submit"/> frames, which are checked against the screens the plugin listed and rate-limited, then wait
/// in <see cref="Mailbox"/> (newest only, at most 5 s) for <see cref="ScreenPipeServer"/>. Counts what happened to them for
/// the <c>lianli-screen</c> status entry. Thread-safe: any number of app sessions submit while the pipe server takes.
/// </summary>
public sealed partial class ScreenRelay
{
    private const int MaxLoggedReasons = 100;

    private readonly PluginBridgeSource _bridge;
    private readonly ILogger<ScreenRelay> _logger;
    private readonly ScreenFrameGate _gate;
    private readonly ScreenFrameRate _rate;
    private readonly Lock _logGate = new();
    private readonly HashSet<string> _loggedReasons = new(StringComparer.Ordinal);
    private long _sent;
    private long _refused;
    private volatile bool _connected;

    public ScreenRelay(PluginBridgeSource bridge, ILogger<ScreenRelay> logger, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        ArgumentNullException.ThrowIfNull(logger);
        _bridge = bridge;
        _logger = logger;
        _gate = new ScreenFrameGate(timeProvider);
        _rate = new ScreenFrameRate(timeProvider);
        Mailbox = new ScreenFrameMailbox(timeProvider);
    }

    /// <summary>Frames waiting for the screen pipe, by the plugin's own hardware id.</summary>
    public ScreenFrameMailbox Mailbox { get; }

    /// <summary>Frames written to the plugin (since the collector started).</summary>
    public long Sent => Interlocked.Read(ref _sent);

    /// <summary>
    /// Frames that didn't reach the plugin: refused (unknown screen, wrong size or markers, over 30 a second) or dropped
    /// while waiting (a newer one came first, or 5 s passed).
    /// </summary>
    public long Dropped => Interlocked.Read(ref _refused) + Mailbox.Dropped;

    /// <summary>True while a verified plugin is connected to the screen pipe.</summary>
    public bool IsConnected => _connected;

    /// <summary>
    /// A frame from an app client. Never throws and never answers: a frame that can't be used is counted (and its reason
    /// logged once), so a client can't make the collector do more than drop it.
    /// </summary>
    public void Submit(ScreenFrameRequest frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        // MessagePack can leave "non-nullable" fields null in a frame a client made by hand.
        if (frame.HardwareId is not { } hardwareId || frame.Jpeg is not { } jpeg)
        {
            Refuse("a frame without a hardware id or picture");
            return;
        }

        if (!_bridge.TryGetScreen(hardwareId, out var screen) || PluginBridgeSource.PluginIdOf(hardwareId) is not { } screenId)
        {
            Refuse("a frame for hardware without a screen");
            return;
        }

        if (ScreenFrameRules.Validate(jpeg, screen) is { } problem)
        {
            Refuse(problem);
            return;
        }

        if (!_gate.TryAccept(screenId))
        {
            // Over 30 a second: expected now and then (a second app, a slow tick catching up), so counted but not logged.
            Interlocked.Increment(ref _refused);
            return;
        }

        if (!Mailbox.Put(screenId, jpeg))
        {
            Refuse("more screens than the collector keeps frames for");
        }
    }

    internal void SetConnected(bool connected) => _connected = connected;

    internal void CountSent()
    {
        Interlocked.Increment(ref _sent);
        _rate.Count();
    }

    /// <summary>Frames a second written to the plugin over the last <see cref="ScreenFrameRate.Window"/>.</summary>
    public double FramesPerSecond => _rate.PerSecond();

    /// <summary>
    /// The status entry next to the <c>lianli</c> source: shown once the plugin has listed a screen or the pipe was used,
    /// so PCs without a screen cooler see nothing new. Ok while a plugin is connected; DeviceNotFound otherwise. The counts
    /// are <see cref="ScreenPipeCounts.Format"/>'s, with the frames a second over the last few seconds.
    /// </summary>
    public IEnumerable<SourceStatusEntry> StatusEntries()
    {
        Mailbox.DropStale(); // once a poll: frames for an absent plugin don't sit in memory
        var connected = _connected;
        if (!connected && !_bridge.HasScreens && Sent == 0 && Dropped == 0)
        {
            yield break;
        }

        var counts = new ScreenPipeCounts(Sent, Dropped, FramesPerSecond).Format();
        yield return connected
            ? new SourceStatusEntry(ScreenPipeProtocol.StatusSource, SourceStatus.Ok, $"Screen pipe connected · {counts}")
            : new SourceStatusEntry(ScreenPipeProtocol.StatusSource, SourceStatus.DeviceNotFound,
                $"Screen pipe: FanControl's Lian Li plugin isn't connected · {counts}");
    }

    private void Refuse(string reason)
    {
        Interlocked.Increment(ref _refused);
        bool first;
        lock (_logGate)
        {
            first = _loggedReasons.Count < MaxLoggedReasons && _loggedReasons.Add(reason);
        }

        if (first)
        {
            LogRefused(_logger, reason);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Dropped a cooler screen frame: {Reason}. Further frames dropped for the same reason are only counted.")]
    private static partial void LogRefused(ILogger logger, string reason);
}
