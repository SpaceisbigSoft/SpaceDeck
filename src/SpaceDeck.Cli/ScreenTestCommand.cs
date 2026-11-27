using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using SpaceDeck.Core.Model;
using SpaceDeck.Core.Protocol;
using SpaceDeck.Core.Screens;

namespace SpaceDeck.Cli;

/// <summary>
/// <c>spacedeck-cli screen-test --hardware lianli:/LianLi/0 [--seconds 30] [--fps 1] [--quality 85]</c>: sends a test picture (colour bars,
/// the time, a frame number, a sweeping bar and the measured frame rate) to a cooler screen through the collector, the way
/// the app does (<c>docs/SCREEN-PIPE.md</c>), at 1 to 30 frames a second. Tests the whole path (collector, screen pipe,
/// FanControl plugin, cooler) without the app, and how many frames a second it carries: once a second it prints the frames
/// sent, the frames the collector dropped and the rate achieved, and warns once when the collector refuses its frames
/// because another sender (the app) holds the screen. When it ends, the cooler shows its own screen again after about 20
/// seconds.
/// </summary>
internal static class ScreenTestCommand
{
    /// <summary>How long to wait for the collector's hello and catalog.</summary>
    private static readonly TimeSpan CatalogTimeout = TimeSpan.FromSeconds(10);

    /// <summary>How often the progress line is printed.</summary>
    private static readonly TimeSpan ReportInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The last stretch before a frame is due is spun instead of slept: Task.Delay wakes on the system timer (about 15.6 ms
    /// on Windows), and a frame sent that late followed by one on time can put 31 frames in one second, which the collector
    /// drops.
    /// </summary>
    private static readonly TimeSpan SpinBefore = TimeSpan.FromMilliseconds(20);

    /// <summary>Frames rendered and encoded ahead on other cores, so the encoder's time doesn't cap the rate.</summary>
    private static readonly int EncodeAhead = Math.Clamp(Environment.ProcessorCount - 1, 1, 4);

    /// <summary>This program's name as the collector shows a screen's sender ("spacedeck-cli").</summary>
    private static readonly string OwnName = Path.GetFileNameWithoutExtension(Environment.ProcessPath) ?? "spacedeck-cli";

    /// <summary>The lowest quality a picture over the collector's size limit is encoded at again.</summary>
    private const int LowestQuality = 25;

    public static async Task<int> RunAsync(string pipeName, string? hardwareId, ScreenTestSettings settings, bool verify)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (string.IsNullOrWhiteSpace(hardwareId))
        {
            Console.Error.WriteLine("screen-test needs --hardware <id>, e.g. --hardware lianli:/LianLi/0 (spacedeck-cli screen-test --hardware ? lists them).");
            return 2;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(3000, cts.Token).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Console.Error.WriteLine($@"Couldn't connect to \\.\pipe\{pipeName}. Is the collector running?");
            return 1;
        }
        catch (UnauthorizedAccessException)
        {
            Console.Error.WriteLine("The collector refused the connection (access denied).");
            return 1;
        }

        // The app's rule (PipeServerIdentity): only the collector service (or an elevated development collector).
        var developmentFolder = PipeServerIdentity.DevelopmentCollectorFolder(AppContext.BaseDirectory, "SpaceDeck.Cli");
        if (verify && !PipeServerIdentity.IsCollectorService(pipe.SafePipeHandle, developmentFolder, out var problem))
        {
            Console.Error.WriteLine($"{problem}{Environment.NewLine}Use --no-verify to connect anyway.");
            return 1;
        }

        // Everything the collector sends is read all along (it drops a client that stops reading); the latest catalog
        // and screen pipe status are kept for the checks and the summary.
        var state = new CollectorState();
        var reader = Task.Run(() => ReadAsync(pipe, state, cts.Token), CancellationToken.None);
        try
        {
            using (var wait = CancellationTokenSource.CreateLinkedTokenSource(cts.Token))
            {
                wait.CancelAfter(CatalogTimeout);
                try
                {
                    await state.CatalogArrived.Task.WaitAsync(wait.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cts.IsCancellationRequested)
                {
                    Console.Error.WriteLine("The collector sent no catalog within 10 seconds.");
                    return 1;
                }
            }

            if (state.ProtocolVersion < ProtocolInfo.ScreenVersion)
            {
                Console.Error.WriteLine($"The collector speaks protocol {state.ProtocolVersion}; cooler screens need {ProtocolInfo.ScreenVersion}. Update the collector.");
                return 1;
            }

            var screens = state.Catalog.Hardware.Where(h => h.Screen is { IsValid: true }).ToList();
            var list = string.Join(Environment.NewLine, screens.Select(h => $"  {h.Id}  {h.Name}  {h.Screen!.Width} × {h.Screen.Height}"));
            const string noScreens = "The collector knows no hardware with a screen. Is FanControl running with SpaceDeck's Lian Li plugin (a version with screen support)?";
            if (hardwareId == "?")
            {
                if (screens.Count == 0)
                {
                    Console.Error.WriteLine(noScreens);
                }
                else
                {
                    Console.WriteLine($"Hardware with a screen:{Environment.NewLine}{list}");
                }

                return 0;
            }

            var target = screens.FirstOrDefault(h => h.Id == hardwareId);
            if (target?.Screen is not { } screen)
            {
                Console.Error.WriteLine(screens.Count == 0
                    ? noScreens
                    : $"'{hardwareId}' has no screen. Hardware with a screen:{Environment.NewLine}{list}");
                return 1;
            }

            var fps = settings.FramesPerSecond;
            var rate = fps == 1 ? "once a second" : string.Create(CultureInfo.InvariantCulture, $"{fps} times a second");
            Console.WriteLine($"Sending a test picture to {target.Name} ({target.Id}, {screen.Width} × {screen.Height}) {rate} for {settings.Seconds} s at JPEG quality {settings.Quality}. Ctrl+C to stop.");
            var (sent, elapsed) = await SendAsync(pipe, target.Id, screen, settings, state, reader, cts.Token).ConfigureAwait(false);

            if (reader.IsCompleted && !cts.IsCancellationRequested)
            {
                Console.Error.WriteLine("The collector closed the connection.");
                return 1;
            }

            Console.WriteLine(string.Create(CultureInfo.CurrentCulture,
                $"Sent {sent} frames in {elapsed.TotalSeconds:0.0} s ({sent / Math.Max(0.001, elapsed.TotalSeconds):0.0} a second). The cooler shows its own screen again in about 20 s."));
            await Task.Delay(TimeSpan.FromSeconds(1.5), cts.Token).ConfigureAwait(false); // the next status, with the last frames counted
            Console.WriteLine(state.ScreenStatus is { } status
                ? $"Collector: {status.Message ?? status.Status.ToString()}"
                : "Collector: no screen pipe status (an older collector?).");
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Stopped. The cooler shows its own screen again in about 20 s.");
            return 0;
        }
        catch (IOException)
        {
            Console.Error.WriteLine("Lost connection to the collector.");
            return 1;
        }
        finally
        {
            await cts.CancelAsync().ConfigureAwait(false);
            try
            {
                await reader.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Ended by the cancel above, or the pipe closing.
            }
        }
    }

    /// <summary>
    /// Sends frames on a fixed schedule for the test's length, each rendered and encoded ahead, and prints a progress line
    /// once a second. A frame that can't go out on time goes out as soon as it can and the schedule starts again from it,
    /// so a stall is never made up with a burst the collector would drop. Returns the frames sent and the time taken.
    /// </summary>
    private static async Task<(int Sent, TimeSpan Elapsed)> SendAsync(NamedPipeClientStream pipe, string hardwareId, ScreenInfo screen,
        ScreenTestSettings settings, CollectorState state, Task reader, CancellationToken token)
    {
        var period = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / settings.FramesPerSecond);
        var length = TimeSpan.FromSeconds(settings.Seconds);

        // The collector's counts before the first frame, so the lines show only what this test cost.
        var countsBefore = ScreenPipeCounts.TryFind(state.ScreenStatus?.Message);
        var droppedBefore = countsBefore?.Dropped ?? 0;
        var refusedBefore = countsBefore?.RefusedOtherSender ?? 0;
        var warnedOtherSender = false;
        double? measured = null;
        var encoding = new Queue<Task<EncodedFrame>>();
        var queued = 0;
        void EncodeNext()
        {
            var number = ++queued;
            var at = period * (number - 1);
            var shown = measured;
            encoding.Enqueue(Task.Run(() => Encode(screen, number, at, shown, settings.Quality), token));
        }

        for (var i = 0; i < EncodeAhead; i++)
        {
            EncodeNext();
        }

        var clock = Stopwatch.StartNew();
        var origin = TimeSpan.Zero; // moved on after a late frame, so the schedule continues from it
        var sent = 0;
        var lastReport = TimeSpan.Zero;
        var sentAtReport = 0;
        var encodeTime = TimeSpan.Zero;
        long bytes = 0;
        while (!token.IsCancellationRequested && !reader.IsCompleted)
        {
            var due = origin + (period * sent);
            if (due >= length)
            {
                break;
            }

            await WaitUntilAsync(clock, due, token).ConfigureAwait(false);
            var frame = await encoding.Dequeue().ConfigureAwait(false);
            EncodeNext();
            await FrameCodec.WriteAsync(pipe, new ScreenFrameRequest(hardwareId, frame.Jpeg), token).ConfigureAwait(false);
            sent++;
            encodeTime += frame.EncodeTime;
            bytes += frame.Jpeg.Length;

            var now = clock.Elapsed;
            if (now - (origin + (period * sent)) > period)
            {
                origin = now - (period * sent);
            }

            if (now - lastReport >= ReportInterval)
            {
                var achieved = (sent - sentAtReport) / (now - lastReport).TotalSeconds;
                measured = achieved;
                var counts = ScreenPipeCounts.TryFind(state.ScreenStatus?.Message);
                var dropped = counts is null ? "?" : (counts.Dropped - droppedBefore).ToString(CultureInfo.InvariantCulture);
                var toPlugin = counts is null ? "?" : counts.FramesPerSecond.ToString("0.0", CultureInfo.InvariantCulture);
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"  {now.TotalSeconds,5:0.0} s  sent {sent,5}  {achieved,5:0.0} fps  collector dropped {dropped,4}  to plugin {toPlugin,5}/s  encode {encodeTime.TotalMilliseconds / sent,5:0.0} ms  {bytes / 1024.0 / sent,5:0.0} KB"));
                if (!warnedOtherSender && counts is not null && counts.RefusedOtherSender > refusedBefore && !HoldsScreen(counts.Sender))
                {
                    warnedOtherSender = true;
                    Console.Error.WriteLine(
                        $"Warning: the collector refuses this test's frames because another sender ({counts.Sender ?? "another program"}) holds the screen. "
                        + "Turn the cooler screen off in SpaceDeck's Settings (or close the app) and run the test again; a screen is free 3 s after its sender's last frame.");
                }

                lastReport = now;
                sentAtReport = sent;
            }
        }

        return (sent, clock.Elapsed);
    }

    /// <summary>Whether this program is among the senders in the collector's status line ("SpaceDeck, spacedeck-cli").</summary>
    private static bool HoldsScreen(string? senders) =>
        senders is not null && senders.Split(", ").Contains(OwnName, StringComparer.OrdinalIgnoreCase);

    /// <summary>Waits until <paramref name="due"/> on <paramref name="clock"/>: sleeps most of the way, spins the rest.</summary>
    private static async Task WaitUntilAsync(Stopwatch clock, TimeSpan due, CancellationToken token)
    {
        while (true)
        {
            var remaining = due - clock.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                return;
            }

            if (remaining > SpinBefore)
            {
                await Task.Delay(remaining - SpinBefore, token).ConfigureAwait(false);
            }
            else
            {
                token.ThrowIfCancellationRequested();
                Thread.SpinWait(100);
            }
        }
    }

    /// <summary>Frame <paramref name="number"/> of the test, due <paramref name="at"/> after its start, as a JPEG.</summary>
    private static EncodedFrame Encode(ScreenInfo screen, int number, TimeSpan at, double? framesPerSecond, int quality)
    {
        var started = Stopwatch.GetTimestamp();
        var rgb = ScreenTestPattern.Render(screen.Width, screen.Height, TimeOnly.FromDateTime(DateTime.Now), number, at, framesPerSecond);
        var jpeg = EncodeUnderLimit(rgb, screen.Width, screen.Height, quality);
        return new EncodedFrame(jpeg, Stopwatch.GetElapsedTime(started));
    }

    private sealed record EncodedFrame(byte[] Jpeg, TimeSpan EncodeTime);

    /// <summary>
    /// A JPEG of the picture at <paramref name="quality"/>, within the collector's 256 KiB limit (quality lowered, down to
    /// <see cref="LowestQuality"/>, if it isn't).
    /// </summary>
    private static byte[] EncodeUnderLimit(byte[] rgb, int width, int height, int quality)
    {
        for (; ; quality = Math.Max(LowestQuality, quality - 15))
        {
            var jpeg = BaselineJpegEncoder.Encode(rgb, width, height, quality);
            if (jpeg.Length <= ScreenFrameRules.MaxJpegBytes || quality <= LowestQuality)
            {
                return jpeg;
            }
        }
    }

    private static async Task ReadAsync(NamedPipeClientStream pipe, CollectorState state, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                switch (await FrameCodec.ReadAsync(pipe, token).ConfigureAwait(false))
                {
                    case null:
                        return;

                    case HelloMessage hello:
                        state.ProtocolVersion = hello.ProtocolVersion;
                        break;

                    case CatalogMessage catalog:
                        state.Catalog = catalog.Catalog;
                        state.CatalogArrived.TrySetResult();
                        break;

                    case SourceStatusMessage status:
                        state.ScreenStatus = status.Sources.FirstOrDefault(s => s.Source == ScreenPipeProtocol.StatusSource);
                        break;
                }
            }
        }
        finally
        {
            state.CatalogArrived.TrySetCanceled(CancellationToken.None);
        }
    }

    private sealed class CollectorState
    {
        private volatile SensorCatalog _catalog = SensorCatalog.Empty;
        private volatile SourceStatusEntry? _screenStatus;
        private int _protocolVersion;

        public TaskCompletionSource CatalogArrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public SensorCatalog Catalog
        {
            get => _catalog;
            set => _catalog = value;
        }

        public SourceStatusEntry? ScreenStatus
        {
            get => _screenStatus;
            set => _screenStatus = value;
        }

        public int ProtocolVersion
        {
            get => Volatile.Read(ref _protocolVersion);
            set => Volatile.Write(ref _protocolVersion, value);
        }
    }
}
