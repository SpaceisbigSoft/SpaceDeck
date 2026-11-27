using System.Globalization;
using System.IO.Pipes;
using SpaceDeck.Core.Model;
using SpaceDeck.Core.Protocol;
using SpaceDeck.Core.Screens;

namespace SpaceDeck.Cli;

/// <summary>
/// <c>spacedeck-cli screen-test --hardware lianli:/LianLi/0 [--seconds 30]</c>: sends a test picture (colour bars, the
/// time, a frame number) to a cooler screen through the collector, once a second, the way the app does
/// (<c>docs/SCREEN-PIPE.md</c>). Tests the whole path (collector, screen pipe, FanControl plugin, cooler) without the app.
/// When it ends, the cooler shows its own screen again after about 20 seconds.
/// </summary>
internal static class ScreenTestCommand
{
    private static readonly TimeSpan FrameInterval = TimeSpan.FromSeconds(1);

    /// <summary>How long to wait for the collector's hello and catalog.</summary>
    private static readonly TimeSpan CatalogTimeout = TimeSpan.FromSeconds(10);

    public static async Task<int> RunAsync(string pipeName, string? hardwareId, int seconds, bool verify)
    {
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
            var target = screens.FirstOrDefault(h => h.Id == hardwareId);
            if (target?.Screen is not { } screen)
            {
                Console.Error.WriteLine(screens.Count == 0
                    ? "The collector knows no hardware with a screen. Is FanControl running with SpaceDeck's Lian Li plugin (a version with screen support)?"
                    : $"'{hardwareId}' has no screen. Hardware with a screen:{Environment.NewLine}"
                        + string.Join(Environment.NewLine, screens.Select(h => $"  {h.Id}  {h.Name}  {h.Screen!.Width} × {h.Screen.Height}")));
                return hardwareId == "?" ? 0 : 1;
            }

            Console.WriteLine($"Sending a test picture to {target.Name} ({target.Id}, {screen.Width} × {screen.Height}) once a second for {seconds} s. Ctrl+C to stop.");
            var sent = 0;
            var end = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < end && !cts.IsCancellationRequested && !reader.IsCompleted)
            {
                var now = TimeOnly.FromDateTime(DateTime.Now);
                var rgb = ScreenTestPattern.Render(screen.Width, screen.Height, now, sent + 1);
                var jpeg = EncodeUnderLimit(rgb, screen.Width, screen.Height);
                await FrameCodec.WriteAsync(pipe, new ScreenFrameRequest(target.Id, jpeg), cts.Token).ConfigureAwait(false);

                sent++;
                Console.WriteLine(string.Create(CultureInfo.CurrentCulture, $"  frame {sent,4}  {now:HH:mm:ss}  {jpeg.Length / 1024.0:0.0} KB"));
                await Task.Delay(FrameInterval, cts.Token).ConfigureAwait(false);
            }

            if (reader.IsCompleted && !cts.IsCancellationRequested)
            {
                Console.Error.WriteLine("The collector closed the connection.");
                return 1;
            }

            Console.WriteLine($"Sent {sent} frames. The cooler shows its own screen again in about 20 s.");
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

    /// <summary>A JPEG of the picture within the collector's 256 KiB limit (quality lowered if it isn't).</summary>
    private static byte[] EncodeUnderLimit(byte[] rgb, int width, int height)
    {
        for (var quality = 85; ; quality -= 15)
        {
            var jpeg = BaselineJpegEncoder.Encode(rgb, width, height, quality);
            if (jpeg.Length <= ScreenFrameRules.MaxJpegBytes || quality <= 25)
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
