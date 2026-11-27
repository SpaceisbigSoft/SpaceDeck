using System.Globalization;
using System.IO;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using SpaceDeck.App.Services;
using SpaceDeck.Core.Screens;

namespace SpaceDeck.App.ViewModels;

/// <summary>
/// One cooler screen in Settings → Cooler screen, and the loop that feeds it: while it is on (and the collector takes
/// frames), every 1–5 s it draws the chosen dashboard at the screen's size, encodes a JPEG and sends it. Each frame is
/// drawn fresh and sent once; when the loop stops, nothing more is sent. UI thread.
/// </summary>
public sealed partial class CoolerScreenViewModel : ObservableObject, IDisposable
{
    private readonly CoolerScreenService _service;
    private readonly CoolerScreenSettings _settings;
    private readonly DispatcherTimer _timer;
    private CoolerScreenRenderer? _renderer;
    private ScreenTarget _target;
    private bool _busy;
    private bool _disposed;
    private bool _creatingDashboard;
    private string? _lastError;

    /// <summary>The last frame drawn (what the cooler shows), for the small preview.</summary>
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
        _timer.Tick += (_, _) => _ = SendFrameAsync();
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
            if (value)
            {
                EnsureDashboard();
            }

            _service.Save();
            OnPropertyChanged();
            UpdateRunning();
        }
    }

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

            _settings.DashboardId = value.Id;
            _service.Save();
            OnPropertyChanged();
            UpdateRunning();
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
            _timer.Interval = TimeSpan.FromSeconds(value.Value);
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
        }
    }

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

        EnsureDashboard();
        if (Dashboard is null)
        {
            Stop(_settings.DashboardId is null ? "Waiting for sensors…" : "Its dashboard was deleted: choose another.");
            return;
        }

        if (!_timer.IsEnabled)
        {
            _timer.Start();
            Status = "Starting…";
            _ = SendFrameAsync(); // the first frame now, not one interval later
        }
    }

    internal void OnDashboardsChanged()
    {
        OnPropertyChanged(nameof(Dashboard));
        UpdateRunning();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _renderer?.Dispose();
        _renderer = null;
    }

    private bool CanRun => !_disposed && _settings.Enabled && _service.CanSend && Dashboard is not null;

    private void Stop(string status)
    {
        _timer.Stop();
        Status = status;

        // The offscreen window is only kept while frames are being drawn.
        _renderer?.Dispose();
        _renderer = null;
    }

    /// <summary>The first time the screen is on (or after its dashboard was deleted), it gets the built-in one.</summary>
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
            if (_service.CreateDefaultDashboard(_target.HardwareId) is { } created)
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
    /// Draws, encodes (off the UI thread) and sends one frame. A tick that comes while the last frame is still on its way
    /// is skipped, so frames never queue up.
    /// </summary>
    private async Task SendFrameAsync()
    {
        if (_busy || !CanRun || Dashboard is not { } dashboard)
        {
            return;
        }

        _busy = true;
        try
        {
            _renderer ??= new CoolerScreenRenderer();
            var frame = _renderer.Render(dashboard, _target.Width, _target.Height, _settings.Rotation);
            Preview = frame;

            var quality = _settings.JpegQuality;
            var jpeg = await Task.Run(() => CoolerScreenRenderer.EncodeJpeg(frame, quality, ScreenFrameRules.MaxJpegBytes));
            if (jpeg is null)
            {
                Status = "This dashboard doesn't fit in a 256 KiB picture: choose a simpler one.";
                return;
            }

            // Turned off, or the collector went away, while encoding: that frame is never sent.
            if (!CanRun)
            {
                return;
            }

            await _service.SendAsync(_target.HardwareId, jpeg);
            Status = string.Create(CultureInfo.CurrentCulture,
                $"On · a frame every {_settings.IntervalSeconds} s · {Math.Max(1, (jpeg.Length + 512) / 1024)} KB, sent {DateTime.Now:T}");
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
