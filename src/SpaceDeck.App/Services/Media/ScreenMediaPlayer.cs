using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SpaceDeck.Core.Screens;

namespace SpaceDeck.App.Services.Media;

/// <summary>Everything one run of a cooler screen player needs; a different value means a new player.</summary>
internal sealed record MediaPlayback(
    ScreenContentMode Mode,
    string? MediaPath,
    string? Folder,
    int SlideSeconds,
    ImageFit Fit,
    int BackgroundRgb,
    int Rotation,
    int Quality,
    int MaxFramesPerSecond,
    int Width,
    int Height);

/// <summary>What a player reports, on the UI thread.</summary>
internal interface IScreenPlayerHost
{
    /// <summary>A frame went to the collector: the upright preview (now and then, else null), its size and the rate so far.</summary>
    void OnFrameSent(ScreenMediaPlayer player, BitmapSource? preview, int bytes, double framesPerSecond);

    /// <summary>
    /// Something to show in the status line. With <paramref name="error"/> the player has stopped (the file can't be
    /// played); without, it goes on (e.g. the collector went away for a moment).
    /// </summary>
    void OnProblem(ScreenMediaPlayer player, string status, Exception? error);
}

/// <summary>
/// Plays a picture, GIF, video or slideshow on one cooler screen, on its own background thread: moves the content to
/// the current time, and whenever the <see cref="FramePacer"/> says so (on a change, at most the max frames a second,
/// a still picture every 5 s) composes it at the screen's size (fill or fit), turns it, encodes a JPEG and sends it.
/// Nothing touches the UI thread except the reports, posted to it. Each frame is drawn fresh and sent once; once
/// disposed, nothing more is sent.
/// </summary>
internal sealed class ScreenMediaPlayer : IDisposable
{
    private static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(5);

    /// <summary>The preview and status line update this often at most (the cooler gets every frame).</summary>
    private static readonly TimeSpan ReportEvery = TimeSpan.FromMilliseconds(200);

    private readonly Func<byte[], Task> _send;
    private readonly Dispatcher _dispatcher;
    private readonly IScreenPlayerHost _host;
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _thread;
    private int _disposed;

    /// <param name="send">Sends one JPEG to the screen (any thread).</param>
    /// <param name="dispatcher">The UI thread's, for the reports.</param>
    public ScreenMediaPlayer(MediaPlayback playback, Func<byte[], Task> send, Dispatcher dispatcher, IScreenPlayerHost host)
    {
        ArgumentNullException.ThrowIfNull(playback);
        ArgumentNullException.ThrowIfNull(send);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(host);
        Playback = playback;
        _send = send;
        _dispatcher = dispatcher;
        _host = host;
        _thread = new Thread(Run) { IsBackground = true, Name = "SpaceDeck cooler screen player" };
        _thread.SetApartmentState(ApartmentState.MTA);
    }

    public MediaPlayback Playback { get; }

    public void Start() => _thread.Start();

    /// <summary>Stops the player; returns once it has (or after half a second, when a send is stuck: it sends nothing more).</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _stop.Cancel();
        if (_thread.IsAlive && _thread != Thread.CurrentThread)
        {
            _thread.Join(TimeSpan.FromMilliseconds(500));
        }

        if (!_thread.IsAlive)
        {
            _stop.Dispose(); // a thread still finishing a send keeps the token until it ends
        }
    }

    /// <summary>The content for a playback.</summary>
    /// <exception cref="IOException">The file or folder can't be read, or Windows can't play it.</exception>
    private static IScreenContent Open(MediaPlayback playback)
    {
        if (playback.Mode == ScreenContentMode.Slideshow)
        {
            if (string.IsNullOrEmpty(playback.Folder) || !System.IO.Directory.Exists(playback.Folder))
            {
                throw new DirectoryNotFoundException("The slideshow's folder isn't there.");
            }

            var files = MediaFiles.SlideshowOrder(System.IO.Directory.EnumerateFiles(playback.Folder));
            return files.Count > 0
                ? new SlideshowContent(files, TimeSpan.FromSeconds(playback.SlideSeconds))
                : throw new FileNotFoundException("The slideshow's folder has no pictures or GIFs.");
        }

        if (string.IsNullOrEmpty(playback.MediaPath) || !File.Exists(playback.MediaPath))
        {
            throw new FileNotFoundException("The file isn't there any more: choose it again.", playback.MediaPath);
        }

        return MediaFiles.KindOf(playback.MediaPath) == MediaKind.Video
            ? new VideoContent(playback.MediaPath)
            : new ClipContent(MediaCache.LoadAsync(playback.MediaPath).GetAwaiter().GetResult());
    }

    /// <summary>A frozen bitmap of <paramref name="image"/> (opaque), usable on any thread.</summary>
    private static BitmapSource ToBitmap(BgraImage image)
    {
        var bitmap = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgr32, null, image.Pixels, image.Stride);
        bitmap.Freeze();
        return bitmap;
    }

    private void Run()
    {
        using var resolution = TimerResolution.Begin();
        IScreenContent? content = null;
        var token = _stop.Token;
        try
        {
            content = Open(Playback);
            var (drawWidth, drawHeight) = ScreenGeometry.DrawSize(Playback.Width, Playback.Height, Playback.Rotation);
            var upright = new BgraImage(drawWidth, drawHeight);
            var turned = new BgraImage(Playback.Width, Playback.Height);
            var pacer = new FramePacer(Playback.MaxFramesPerSecond, KeepAlive);
            var meter = new FrameRateMeter();
            var picker = new JpegQualityPicker(Playback.Quality, ScreenFrameRules.MaxJpegBytes);
            var clock = Stopwatch.StartNew();
            TimeSpan? lastReport = null; // not TimeSpan.MinValue: now - MinValue overflows after the first frame
            string? lastSkip = null;
            while (!token.IsCancellationRequested)
            {
                content.Seek(clock.Elapsed);
                if (content is SlideshowContent { LastError: { } skipped } && skipped != lastSkip)
                {
                    lastSkip = skipped;
                    Post(() => _host.OnProblem(this, $"Skipped {skipped}", null));
                }

                if (pacer.ShouldSend(clock.Elapsed, content.Version))
                {
                    ScreenCompositor.Compose(content.Picture, content.IsOpaque, upright, Playback.Fit, Playback.BackgroundRgb);
                    ScreenCompositor.Rotate(upright, Playback.Rotation, turned);
                    // A moving picture shares the animation budget (the GA II LCD flashes on large frames at 30 a
                    // second); a still one (NextChange null) is sent rarely and may use the whole 256 KiB.
                    picker.Configure(Playback.Quality, content.NextChange is null
                        ? ScreenFrameRules.MaxJpegBytes
                        : ScreenFrameBudget.ForAnimation(Playback.MaxFramesPerSecond));
                    var jpeg = CoolerScreenRenderer.EncodeJpeg(ToBitmap(turned), picker);
                    if (jpeg is null)
                    {
                        Post(() => _host.OnProblem(this, "This picture doesn't fit in a 256 KiB JPEG: choose a lower picture quality.", null));
                    }
                    else if (!token.IsCancellationRequested && TrySend(jpeg))
                    {
                        var now = clock.Elapsed;
                        meter.Add(now);
                        if (lastReport is not { } reported || now - reported >= ReportEvery)
                        {
                            lastReport = now;
                            var preview = ToBitmap(upright);
                            var bytes = jpeg.Length;
                            var rate = meter.FramesPerSecond(now);
                            Post(() => _host.OnFrameSent(this, preview, bytes, rate));
                        }
                    }
                }

                var wait = pacer.NextCheck(clock.Elapsed, content.Version, content.NextChange) - clock.Elapsed;
                if (wait > TimeSpan.Zero)
                {
                    token.WaitHandle.WaitOne(wait);
                }
            }
        }
        catch (Exception ex)
        {
            // Can't open or decode the file, or out of memory: report it once and end (nothing more is sent).
            if (!token.IsCancellationRequested)
            {
                var name = Path.GetFileName(Playback.Mode == ScreenContentMode.Slideshow ? Playback.Folder : Playback.MediaPath);
                Post(() => _host.OnProblem(this, $"Couldn't play {name}: {ex.Message}", ex));
            }
        }
        finally
        {
            content?.Dispose();
        }
    }

    /// <summary>Sends one frame; false when the collector isn't there (the screen's state change stops the player).</summary>
    private bool TrySend(byte[] jpeg)
    {
        try
        {
            _send(jpeg).GetAwaiter().GetResult();
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or ObjectDisposedException or OperationCanceledException)
        {
            Post(() => _host.OnProblem(this, "Waiting for the collector…", null));
            return false;
        }
    }

    private void Post(Action report)
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            _dispatcher.BeginInvoke(DispatcherPriority.Background, report);
        }
    }
}

/// <summary>
/// Asks Windows for a 1 ms timer while frames are paced (the default 15.6 ms tick makes 24 a second uneven), and gives
/// it back afterwards. Per process on Windows 10 2004 and later.
/// </summary>
internal static class TimerResolution
{
    public static IDisposable Begin() => TimeBeginPeriod(1) == 0 ? new Period() : new None();

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint period);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint period);

    private sealed class Period : IDisposable
    {
        private int _ended;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _ended, 1) == 0)
            {
                _ = TimeEndPeriod(1);
            }
        }
    }

    private sealed class None : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
