using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SpaceDeck.App.Services;
using SpaceDeck.App.Services.Media;
using SpaceDeck.Core.Screens;

namespace SpaceDeck.App.ViewModels;

/// <summary>
/// One cooler screen in Settings → Cooler screen, and what feeds it while it is on (and the collector takes frames):
/// <list type="bullet">
/// <item>a dashboard: drawn on the UI thread every 1–5 s, or at up to the max frames a second while it has an animated
/// GIF; encoded off the UI thread;</item>
/// <item>a picture, GIF, video or slideshow: a <see cref="ScreenMediaPlayer"/> on its own thread draws, encodes and
/// sends at the content's own rate up to the max frames a second (a still picture every 5 s).</item>
/// </list>
/// Each frame is drawn fresh and sent once; when it stops, nothing more is sent. UI thread.
/// </summary>
public sealed partial class CoolerScreenViewModel : ObservableObject, IDisposable, IScreenPlayerHost
{
    /// <summary>A dashboard with a GIF is checked against the pacer this often (the pacer decides when to send).</summary>
    private static readonly TimeSpan AnimatedPoll = TimeSpan.FromMilliseconds(5);

    private static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(5);

    private readonly CoolerScreenService _service;
    private readonly CoolerScreenSettings _settings;
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly FrameRateMeter _meter = new();
    private CoolerScreenRenderer? _renderer;
    private ScreenMediaPlayer? _player;
    private FramePacer? _pacer;
    private IDisposable? _timerResolution;
    private ScreenTarget _target;
    private TimeSpan _lastModeCheck;
    private long _frameNumber;
    private bool _busy;
    private bool _disposed;
    private bool _creatingDashboard;
    private bool _importing;
    private string? _lastError;

    /// <summary>The last frame drawn (what the cooler shows, upright), for the small preview.</summary>
    [ObservableProperty]
    private BitmapSource? _preview;

    [ObservableProperty]
    private string _status = "Off";

    internal CoolerScreenViewModel(CoolerScreenService service, CoolerScreenSettings settings, ScreenTarget target)
    {
        _service = service;
        _settings = settings;
        _target = target;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(settings.IntervalSeconds) };
        _timer.Tick += (_, _) => OnTick();
    }

    public string HardwareId => _target.HardwareId;

    /// <summary>"Lian Li GA II LCD · 480 × 480".</summary>
    public string Title => string.Create(CultureInfo.CurrentCulture, $"{_target.Name} · {_target.Width} × {_target.Height}");

    internal ScreenTarget Target
    {
        get => _target;
        set
        {
            if (value == _target)
            {
                return;
            }

            _target = value;
            OnPropertyChanged(nameof(Title));
        }
    }

    /// <summary>On: draw and send. Turning it on the first time makes the "Cooler screen" dashboard.</summary>
    public bool Enabled
    {
        get => _settings.Enabled;
        set
        {
            if (value == _settings.Enabled)
            {
                return;
            }

            _settings.Enabled = value;
            if (value && _settings.Content == ScreenContentMode.Dashboard)
            {
                EnsureDashboard();
            }

            _service.Save();
            OnPropertyChanged();
            UpdateRunning();
            _service.UpdateScreenUse();
        }
    }

    // ---- What it shows ----------------------------------------------------------------------------------------------

    public IReadOnlyList<Option<ScreenContentMode>> ContentModes { get; } =
    [
        new("A dashboard", ScreenContentMode.Dashboard),
        new("A picture", ScreenContentMode.Picture),
        new("An animated GIF or a video", ScreenContentMode.Animation),
        new("A slideshow of a folder", ScreenContentMode.Slideshow),
    ];

    public Option<ScreenContentMode>? ContentMode
    {
        get => ContentModes.FirstOrDefault(o => o.Value == _settings.Content);
        set
        {
            if (value is null || value.Value == _settings.Content)
            {
                return;
            }

            _settings.Content = value.Value;
            if (_settings.Enabled && value.Value == ScreenContentMode.Dashboard)
            {
                EnsureDashboard();
            }

            _service.Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsDashboardMode));
            OnPropertyChanged(nameof(IsFileMode));
            OnPropertyChanged(nameof(IsSlideshowMode));
            OnPropertyChanged(nameof(IsMediaMode));
            OnPropertyChanged(nameof(FileHint));
            UpdateRunning();
            _service.UpdateScreenUse();
        }
    }

    public bool IsDashboardMode => _settings.Content == ScreenContentMode.Dashboard;

    /// <summary>A picture or an animation: one file.</summary>
    public bool IsFileMode => _settings.Content is ScreenContentMode.Picture or ScreenContentMode.Animation;

    public bool IsSlideshowMode => _settings.Content == ScreenContentMode.Slideshow;

    /// <summary>Anything but a dashboard: fit and background colour apply.</summary>
    public bool IsMediaMode => !IsDashboardMode;

    /// <summary>The chosen file's name, or what to do.</summary>
    public string FileText => _settings.MediaName ?? (_settings.MediaPath is { } path ? Path.GetFileName(path) : "No file chosen");

    public string FileHint => _settings.Content == ScreenContentMode.Animation
        ? "GIFs play at their own speed; videos (MP4, MOV, WMV, AVI…) play whatever Windows plays, without sound. Both loop."
        : "PNG, JPEG, BMP, TIFF or GIF.";

    public string FolderText => _settings.SlideshowFolder ?? "No folder chosen";

    public System.Collections.ObjectModel.ObservableCollection<DashboardDocumentViewModel> Dashboards => _service.Dashboards;

    public DashboardDocumentViewModel? Dashboard
    {
        get => _service.FindDashboard(_settings.DashboardId);
        set
        {
            if (value is null || value.Id == _settings.DashboardId)
            {
                return;
            }

            if (Dashboard is { } old)
            {
                old.ScreenPreview = null;
            }

            _settings.DashboardId = value.Id;
            _service.Save();
            OnPropertyChanged();
            UpdateRunning();
            _service.UpdateScreenUse();
        }
    }

    public IReadOnlyList<Option<int>> Intervals { get; } =
    [
        .. Enumerable.Range(CoolerScreenSettings.MinIntervalSeconds, CoolerScreenSettings.MaxIntervalSeconds - CoolerScreenSettings.MinIntervalSeconds + 1)
            .Select(s => new Option<int>(s == 1 ? "Every second" : $"Every {s} seconds", s)),
    ];

    public Option<int>? Interval
    {
        get => Intervals.FirstOrDefault(o => o.Value == _settings.IntervalSeconds);
        set
        {
            if (value is null || value.Value == _settings.IntervalSeconds)
            {
                return;
            }

            _settings.IntervalSeconds = value.Value;
            _service.Save();
            if (_pacer is null)
            {
                _timer.Interval = TimeSpan.FromSeconds(value.Value);
            }

            OnPropertyChanged();
            UpdateRunning();
        }
    }

    public IReadOnlyList<Option<int>> SlideDurations { get; } =
        [new("5 seconds", 5), new("10 seconds", 10), new("15 seconds", 15), new("30 seconds", 30), new("1 minute", 60), new("5 minutes", 300)];

    public Option<int>? SlideDuration
    {
        get => SlideDurations.FirstOrDefault(o => o.Value == _settings.SlideSeconds);
        set
        {
            if (value is null || value.Value == _settings.SlideSeconds)
            {
                return;
            }

            _settings.SlideSeconds = value.Value;
            _service.Save();
            OnPropertyChanged();
            UpdateRunning();
        }
    }

    public IReadOnlyList<Option<ImageFit>> Fits { get; } =
        [new("Fill the screen (crop the edges)", ImageFit.Fill), new("Fit inside (bars in the background colour)", ImageFit.Fit)];

    public Option<ImageFit>? Fit
    {
        get => Fits.FirstOrDefault(o => o.Value == _settings.Fit);
        set
        {
            if (value is null || value.Value == _settings.Fit)
            {
                return;
            }

            _settings.Fit = value.Value;
            _service.Save();
            OnPropertyChanged();
            UpdateRunning();
        }
    }

    public IReadOnlyList<Option<string>> Backgrounds { get; } =
        [new("Black", "#000000"), new("Dark grey", "#202020"), new("Dark blue", "#0B1730"), new("White", "#FFFFFF")];

    public Option<string>? Background
    {
        get => Backgrounds.FirstOrDefault(o => string.Equals(o.Value, _settings.Background, StringComparison.OrdinalIgnoreCase));
        set
        {
            if (value is null || string.Equals(value.Value, _settings.Background, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _settings.Background = value.Value;
            _service.Save();
            OnPropertyChanged();
            UpdateRunning();
        }
    }

    public IReadOnlyList<Option<int>> FrameRates { get; } =
    [
        .. new[] { 1, 2, 5, 10, 12, 15, 20, 24, 25, 30 }
            .Select(f => new Option<int>(f == CoolerScreenSettings.DefaultFps ? $"{f} (default)" : $"{f}", f)),
    ];

    /// <summary>Most frames a second for animations, videos and dashboards with a GIF (1–30).</summary>
    public Option<int>? MaxFrameRate
    {
        get => FrameRates.FirstOrDefault(o => o.Value == _settings.MaxFramesPerSecond);
        set
        {
            if (value is null || value.Value == _settings.MaxFramesPerSecond)
            {
                return;
            }

            _settings.MaxFramesPerSecond = value.Value;
            _service.Save();
            OnPropertyChanged();
            UpdateRunning();
        }
    }

    public IReadOnlyList<Option<int>> Rotations { get; } =
        [new("Upright", 0), new("90° clockwise", 90), new("Upside down", 180), new("90° counter-clockwise", 270)];

    public Option<int>? Rotation
    {
        get => Rotations.FirstOrDefault(o => o.Value == _settings.Rotation);
        set
        {
            if (value is null || value.Value == _settings.Rotation)
            {
                return;
            }

            _settings.Rotation = value.Value;
            _service.Save();
            OnPropertyChanged();
            UpdateRunning();
        }
    }

    public IReadOnlyList<Option<int>> Qualities { get; } =
    [
        .. new[] { 50, 60, 70, 80, 85, 90, 95, 100 }.Select(q => new Option<int>(q == CoolerScreenSettings.DefaultQuality ? $"{q} (default)" : $"{q}", q)),
    ];

    public Option<int>? Quality
    {
        get => Qualities.FirstOrDefault(o => o.Value == _settings.JpegQuality);
        set
        {
            if (value is null || value.Value == _settings.JpegQuality)
            {
                return;
            }

            _settings.JpegQuality = value.Value;
            _service.Save();
            OnPropertyChanged();
            UpdateRunning();
        }
    }

    // ---- Starting and stopping --------------------------------------------------------------------------------------

    /// <summary>Starts or stops the loop for the current settings and collector state, and says why in <see cref="Status"/>.</summary>
    internal void UpdateRunning()
    {
        if (_disposed)
        {
            return;
        }

        if (!_settings.Enabled)
        {
            Stop("Off");
            Preview = null;
            return;
        }

        if (_service.Blocked is { } blocked)
        {
            Stop(blocked);
            return;
        }

        if (_settings.Content == ScreenContentMode.Dashboard)
        {
            StopPlayer();
            RunDashboard();
        }
        else
        {
            StopDashboard();
            RunMedia();
        }
    }

    internal void OnDashboardsChanged()
    {
        OnPropertyChanged(nameof(Dashboard));
        UpdateRunning();
    }

    /// <summary>True while this screen is on and shows <paramref name="dashboard"/>.</summary>
    internal bool ShowsDashboard(DashboardDocumentViewModel dashboard) =>
        _settings.Enabled && _settings.Content == ScreenContentMode.Dashboard && _settings.DashboardId == dashboard.Id;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopDashboard();
        StopPlayer();
    }

    private bool CanRun => !_disposed && _settings.Enabled && _service.CanSend;

    private void Stop(string status)
    {
        StopDashboard();
        StopPlayer();
        Status = status;
    }

    private void StopDashboard()
    {
        _timer.Stop();
        _pacer = null;
        _timerResolution?.Dispose();
        _timerResolution = null;

        // The offscreen window is only kept while frames are being drawn.
        if (_renderer is not null)
        {
            _renderer.Dispose();
            _renderer = null;
            if (Dashboard is { } dashboard)
            {
                dashboard.ScreenPreview = null; // the square editor's preview isn't live any more
            }
        }
    }

    private void StopPlayer()
    {
        var player = _player;
        _player = null; // reports from it are ignored from now on
        player?.Dispose();
    }

    // ---- A dashboard ------------------------------------------------------------------------------------------------

    private void RunDashboard()
    {
        // Made here only while none was ever chosen (no sensors yet when it was turned on). A deleted one isn't made
        // again behind the user's back: turning the screen off and on, or choosing another, does that.
        if (_settings.DashboardId is null)
        {
            EnsureDashboard();
        }

        if (Dashboard is not { } dashboard)
        {
            Stop(_settings.DashboardId is null ? "Waiting for sensors…" : "Its dashboard was deleted: choose another.");
            return;
        }

        if (dashboard.HasAnimation)
        {
            // A GIF on it: drawn as often as the pacer allows, on a fine timer.
            if (_pacer is null || _pacer.MaxFramesPerSecond != _settings.MaxFramesPerSecond)
            {
                _pacer = new FramePacer(_settings.MaxFramesPerSecond, KeepAlive);
            }

            _timerResolution ??= TimerResolution.Begin();
            _timer.Interval = AnimatedPoll;
        }
        else
        {
            _pacer = null;
            _timerResolution?.Dispose();
            _timerResolution = null;
            _timer.Interval = TimeSpan.FromSeconds(_settings.IntervalSeconds);
        }

        if (!_timer.IsEnabled)
        {
            _meter.Clear();
            _timer.Start();
            Status = "Starting…";
            _ = SendFrameAsync(); // the first frame now, not one interval later
        }
    }

    private void OnTick()
    {
        // A GIF added to the dashboard or taken off it switches between the two paces (checked once a second at most).
        var now = _clock.Elapsed;
        if (now - _lastModeCheck >= TimeSpan.FromSeconds(1) || _pacer is null)
        {
            _lastModeCheck = now;
            if (Dashboard is { } dashboard && dashboard.HasAnimation != (_pacer is not null))
            {
                UpdateRunning();
                return;
            }
        }

        if (_pacer is { } pacer)
        {
            if (!_busy && pacer.ShouldSend(now, ++_frameNumber))
            {
                _ = SendFrameAsync();
            }

            return;
        }

        _ = SendFrameAsync();
    }

    /// <summary>Turned on without a dashboard (the first time, or after its dashboard was deleted): it gets the built-in one.</summary>
    private void EnsureDashboard()
    {
        // Adding the dashboard raises the dashboard list's change, which comes back here before the id is stored.
        if (_creatingDashboard || Dashboard is not null)
        {
            return;
        }

        _creatingDashboard = true;
        try
        {
            if (_service.CreateDefaultDashboard(_target) is { } created)
            {
                _settings.DashboardId = created.Id;
                _service.Save();
                OnPropertyChanged(nameof(Dashboard));
            }
        }
        finally
        {
            _creatingDashboard = false;
        }
    }

    /// <summary>
    /// Draws (UI thread), turns and encodes (off it) and sends one frame of the dashboard. A tick that comes while the
    /// last frame is still on its way is skipped, so frames never queue up.
    /// </summary>
    private async Task SendFrameAsync()
    {
        if (_busy || !CanRun || _settings.Content != ScreenContentMode.Dashboard || Dashboard is not { } dashboard)
        {
            return;
        }

        _busy = true;
        try
        {
            _renderer ??= new CoolerScreenRenderer();
            var rotation = _settings.Rotation;
            var (drawWidth, drawHeight) = ScreenGeometry.DrawSize(_target.Width, _target.Height, rotation);
            var upright = _renderer.Render(dashboard, drawWidth, drawHeight);
            Preview = upright;
            dashboard.ScreenPreview = upright;

            var quality = _settings.JpegQuality;
            var jpeg = await Task.Run(() => CoolerScreenRenderer.EncodeJpeg(CoolerScreenRenderer.Rotate(upright, rotation), quality, ScreenFrameRules.MaxJpegBytes));
            if (jpeg is null)
            {
                Status = "This dashboard doesn't fit in a 256 KiB picture: choose a simpler one or a lower quality.";
                return;
            }

            // Turned off, switched to a picture, or the collector went away while encoding: that frame is never sent.
            if (!CanRun || _settings.Content != ScreenContentMode.Dashboard)
            {
                return;
            }

            await _service.SendAsync(_target.HardwareId, jpeg);
            _meter.Add(_clock.Elapsed);
            Status = StatusText(jpeg.Length, _meter.FramesPerSecond(_clock.Elapsed));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or ObjectDisposedException)
        {
            Status = "Waiting for the collector…"; // disconnected mid-send; the store's state change stops the loop
        }
        catch (Exception ex)
        {
            // Drawing failed (a broken template, out of memory for the bitmap): stop instead of failing every tick.
            Stop($"Couldn't draw the screen: {ex.Message}");
            LogOnce(ex);
        }
        finally
        {
            _busy = false;
        }
    }

    // ---- A picture, an animation, a slideshow -----------------------------------------------------------------------

    /// <summary>Starts a player for the current settings (a new one whenever they changed).</summary>
    private void RunMedia()
    {
        if (_importing)
        {
            StopPlayer();
            Status = "Copying the file…";
            return;
        }

        MediaPlayback playback;
        if (_settings.Content == ScreenContentMode.Slideshow)
        {
            if (_settings.SlideshowFolder is null)
            {
                Stop("Choose a folder of pictures or GIFs.");
                return;
            }

            playback = Playback(null, _settings.SlideshowFolder);
        }
        else
        {
            if (_settings.MediaPath is null)
            {
                Stop(_settings.Content == ScreenContentMode.Picture ? "Choose a picture." : "Choose a GIF or a video.");
                return;
            }

            playback = Playback(_settings.MediaPath, null);
        }

        if (_player is { } running && running.Playback == playback)
        {
            return;
        }

        StopPlayer();
        Status = "Starting…";
        var hardwareId = _target.HardwareId;
        _player = new ScreenMediaPlayer(playback, jpeg => _service.SendAsync(hardwareId, jpeg), _timer.Dispatcher, this);
        _player.Start();
    }

    private MediaPlayback Playback(string? path, string? folder) => new(
        _settings.Content,
        path,
        folder,
        _settings.SlideSeconds,
        _settings.Fit,
        _settings.GetBackgroundRgb(),
        _settings.Rotation,
        _settings.JpegQuality,
        _settings.MaxFramesPerSecond,
        _target.Width,
        _target.Height);

    void IScreenPlayerHost.OnFrameSent(ScreenMediaPlayer player, BitmapSource? preview, int bytes, double framesPerSecond)
    {
        if (!ReferenceEquals(player, _player))
        {
            return;
        }

        if (preview is not null)
        {
            Preview = preview;
        }

        Status = StatusText(bytes, framesPerSecond);
    }

    void IScreenPlayerHost.OnProblem(ScreenMediaPlayer player, string status, Exception? error)
    {
        if (!ReferenceEquals(player, _player))
        {
            return;
        }

        if (error is null)
        {
            Status = status;
            return;
        }

        Stop(status);
        LogOnce(error);
    }

    [RelayCommand]
    private async Task ChooseFileAsync()
    {
        var animation = _settings.Content == ScreenContentMode.Animation;
        var path = _service.PickFile(animation ? "GIF or video for the cooler screen" : "Picture for the cooler screen",
            animation ? MediaFiles.AnimationFilter : MediaFiles.PictureFilter);
        if (path is null || _importing)
        {
            return;
        }

        if (MediaFiles.KindOf(path) == MediaKind.Unknown)
        {
            Status = "That isn't a picture, GIF or video SpaceDeck knows (by its extension).";
            return;
        }

        _importing = true;
        StopPlayer();
        Status = "Copying the file…";
        string stored;
        try
        {
            stored = await Task.Run(() => MediaLibrary.Import(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Status = $"Couldn't copy the file: {ex.Message}";
            return;
        }
        finally
        {
            _importing = false;
        }

        _settings.MediaPath = stored;
        _settings.MediaName = Path.GetFileName(path);
        _service.Save();
        OnPropertyChanged(nameof(FileText));
        UpdateRunning();
    }

    [RelayCommand]
    private void ChooseFolder()
    {
        if (_service.PickFolder("Folder of pictures and GIFs for the cooler screen") is not { } folder)
        {
            return;
        }

        _settings.SlideshowFolder = folder;
        _service.Save();
        OnPropertyChanged(nameof(FolderText));
        UpdateRunning();
    }

    // ---- The dashboard's layout -------------------------------------------------------------------------------------

    /// <summary>The cooler screen row's "Reset to default layout": the built-in large-number layout for this screen.</summary>
    [RelayCommand]
    private void ResetLayout()
    {
        if (Dashboard is { } dashboard && _service.ResetLayout(dashboard, _target))
        {
            _service.UpdateScreenUse();
            UpdateRunning();
        }
    }

    /// <summary>The cooler screen row's "Edit layout": the dashboard page, editing this dashboard in the square frame.</summary>
    [RelayCommand]
    private void EditLayout()
    {
        if (Dashboard is { } dashboard)
        {
            _service.RequestEditLayout(dashboard);
        }
    }

    // ---- Status and errors ------------------------------------------------------------------------------------------

    /// <summary>"On · 24.0 fps · 38 KB", or for a slow pace "On · a frame every 2 s · 31 KB, sent 12:34:56".</summary>
    private string StatusText(int bytes, double framesPerSecond)
    {
        var kilobytes = Math.Max(1, (bytes + 512) / 1024);
        if (framesPerSecond >= 1)
        {
            return string.Create(CultureInfo.CurrentCulture, $"On · {framesPerSecond:0.0} fps · {kilobytes} KB");
        }

        var pace = _settings.Content == ScreenContentMode.Dashboard
            ? string.Create(CultureInfo.CurrentCulture, $"a frame every {_settings.IntervalSeconds} s")
            : _settings.Content == ScreenContentMode.Slideshow
                ? string.Create(CultureInfo.CurrentCulture, $"a picture every {_settings.SlideSeconds} s")
                : "a still picture, sent again every 5 s";
        return string.Create(CultureInfo.CurrentCulture, $"On · {pace} · {kilobytes} KB, sent {DateTime.Now:T}");
    }

    private void LogOnce(Exception ex)
    {
        var text = ex.ToString();
        if (text == _lastError)
        {
            return;
        }

        _lastError = text;
        LogFiles.Append(Path.Combine(AppSettings.Directory, "app-errors.log"),
            string.Create(CultureInfo.InvariantCulture, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  Cooler screen {_target.HardwareId}: {text}{Environment.NewLine}"));
    }
}
