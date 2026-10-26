using System.Diagnostics;
using Microsoft.Extensions.Options;
using SensorDeck.Core.Model;
using SensorDeck.Core.Protocol;

namespace SensorDeck.Collector.Polling;

/// <summary>
/// Reads every source on one dedicated thread (LibreHardwareMonitor is not thread-safe) and publishes
/// catalog, values and status to the <see cref="SensorHub"/>.
/// </summary>
public sealed partial class PollingService : BackgroundService
{
    private readonly SensorHub _hub;
    private readonly ISensorSourceFactory _factory;
    private readonly ILogger<PollingService> _logger;
    private readonly CollectorOptions _settings;
    private readonly HashSet<string> _loggedErrors = new(StringComparer.Ordinal);
    private readonly string _configPath;
    private readonly string? _persistBlockedReason;

    private static readonly TimeSpan WaitSlice = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Opening LibreHardwareMonitor is heavy (driver, WMI, SMART wakes drives), so sources are reopened at most this
    /// often however fast clients toggle the sensor groups; a later change waits for the next slot.
    /// </summary>
    private static readonly TimeSpan MinReopenInterval = TimeSpan.FromSeconds(5);

    private bool _reopenDue;
    private long _lastReopenTimestamp;

    private IReadOnlyList<ISensorSource> _sources = [];
    private int[] _offsets = [];
    private SensorReading[] _buffer = [];
    private int _catalogVersion;
    private long _sequence;
    private string[] _ids = [];

    // Min/max restarted by a client ("reset min/max"): tracked here instead of the source's since-start values.
    private readonly Dictionary<string, (float Min, float Max)> _resetMinMax = new(StringComparer.Ordinal);
    private readonly long _startedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private long? _minMaxResetUnixMs;

    /// <param name="configPath">collector.json to save settings to (tests pass a temp file).</param>
    /// <param name="persistBlockedReason">When set, saving settings is refused with this message (the data folder isn't safe).</param>
    public PollingService(SensorHub hub, ISensorSourceFactory factory, IOptions<CollectorOptions> options, ILogger<PollingService> logger,
        string? configPath = null, string? persistBlockedReason = null)
    {
        _configPath = configPath ?? CollectorConfigFile.DefaultPath;
        _persistBlockedReason = persistBlockedReason;
        ArgumentNullException.ThrowIfNull(options);
        _hub = hub;
        _factory = factory;
        _logger = logger;

        // Copy so runtime changes (SetConfig) don't mutate the shared options instance.
        var o = options.Value;
        _settings = new CollectorOptions
        {
            PollIntervalMs = CollectorOptions.ClampInterval(o.PollIntervalMs),
            HistorySeconds = o.HistorySeconds,
            StorageEnabled = o.StorageEnabled,
            NetworkEnabled = o.NetworkEnabled,
            ControllersEnabled = o.ControllersEnabled,
            PipeName = o.PipeName,
            ReplayFile = o.ReplayFile,
        };
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                Run(stoppingToken);
                done.TrySetResult();
            }
            catch (Exception ex)
            {
                LogPollThreadCrashed(_logger, ex);
                done.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "SensorDeck poll",
        };
        thread.Start();
        return done.Task;
    }

    private void Run(CancellationToken token)
    {
        _hub.SetPollInterval(_settings.PollIntervalMs);
        PublishCollectorStatus();
        OpenSources();
        _lastReopenTimestamp = Stopwatch.GetTimestamp();

        try
        {
            while (!token.IsCancellationRequested)
            {
                var started = Stopwatch.GetTimestamp();

                ApplyPendingConfig();
                ReopenIfDue();
                Tick();

                var elapsed = Stopwatch.GetElapsedTime(started);
                var wait = TimeSpan.FromMilliseconds(_hub.PollIntervalMs) - elapsed;
                // Wait in short slices so a settings change from the app applies promptly even at a 5 s interval.
                while (wait > TimeSpan.Zero && !token.IsCancellationRequested && !_hub.HasPendingConfig)
                {
                    var slice = wait < WaitSlice ? wait : WaitSlice;
                    token.WaitHandle.WaitOne(slice);
                    wait -= slice;
                }
            }
        }
        finally
        {
            CloseSources();
        }
    }

    private void Tick()
    {
        var catalogChanged = false;
        foreach (var source in _sources)
        {
            try
            {
                source.Update();
                catalogChanged |= source.TryRefreshCatalog();
            }
            catch (Exception ex)
            {
                LogOnce(source.Name, ex);
            }
        }

        if (catalogChanged)
        {
            PublishCatalog();
        }

        for (var i = 0; i < _sources.Count; i++)
        {
            var count = _sources[i].Catalog.Sensors.Count;
            var slice = _buffer.AsSpan(_offsets[i], count);
            try
            {
                _sources[i].Read(slice);
            }
            catch (Exception ex)
            {
                slice.Clear();
                LogOnce(_sources[i].Name, ex);
            }
        }

        ApplyResetRequests();
        var values = new float?[_buffer.Length];
        var min = new float?[_buffer.Length];
        var max = new float?[_buffer.Length];
        var tracked = _resetMinMax.Count > 0 && _ids.Length == _buffer.Length;
        for (var i = 0; i < _buffer.Length; i++)
        {
            values[i] = _buffer[i].Value;
            min[i] = _buffer[i].Min;
            max[i] = _buffer[i].Max;
            if (tracked && _resetMinMax.TryGetValue(_ids[i], out var range))
            {
                if (values[i] is { } v && !float.IsNaN(v))
                {
                    range = (float.IsNaN(range.Min) ? v : Math.Min(range.Min, v), float.IsNaN(range.Max) ? v : Math.Max(range.Max, v));
                    _resetMinMax[_ids[i]] = range;
                }

                min[i] = float.IsNaN(range.Min) ? null : range.Min;
                max[i] = float.IsNaN(range.Max) ? null : range.Max;
            }
        }

        _hub.PublishValues(new ValuesMessage(_catalogVersion, ++_sequence, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), values, min, max));
        PublishStatus();
    }

    /// <summary>Starts min/max over for the requested sensors (null = all, which also stamps the reset time).</summary>
    private void ApplyResetRequests()
    {
        var (all, ids) = _hub.TakeResetRequests();
        if (!all && ids.Length == 0)
        {
            return;
        }

        if (all)
        {
            _resetMinMax.Clear();
            foreach (var id in _ids)
            {
                _resetMinMax[id] = (float.NaN, float.NaN);
            }

            _minMaxResetUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            LogMinMaxReset(_logger, _ids.Length);
        }
        else
        {
            var known = _ids.ToHashSet(StringComparer.Ordinal);
            foreach (var id in ids.Where(known.Contains))
            {
                _resetMinMax[id] = (float.NaN, float.NaN);
            }

            LogMinMaxReset(_logger, ids.Length);
        }

        PublishCollectorStatus();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Min/max reset for {Count} sensors.")]
    private static partial void LogMinMaxReset(ILogger logger, int count);

    private void ApplyPendingConfig()
    {
        if (_hub.TakeConfigRequest() is not { } request)
        {
            return;
        }

        if (request.PollIntervalMs is { } interval)
        {
            _settings.PollIntervalMs = CollectorOptions.ClampInterval(interval);
            _hub.SetPollInterval(_settings.PollIntervalMs);
            LogIntervalChanged(_logger, _settings.PollIntervalMs);
        }

        if (request.StorageEnabled is { } storage && storage != _settings.StorageEnabled)
        {
            _settings.StorageEnabled = storage;
            _reopenDue = true;
        }

        if (request.NetworkEnabled is { } network && network != _settings.NetworkEnabled)
        {
            _settings.NetworkEnabled = network;
            _reopenDue = true;
        }

        if (request.ControllersEnabled is { } controllers && controllers != _settings.ControllersEnabled)
        {
            _settings.ControllersEnabled = controllers;
            _reopenDue = true;
        }

        string? configError = null;
        if (request.Persist && _settings.IsReplay)
        {
            configError = "Replay mode: settings aren't saved.";
        }
        else if (request.Persist && _persistBlockedReason is not null)
        {
            configError = _persistBlockedReason;
        }
        else if (request.Persist)
        {
            try
            {
                CollectorConfigFile.Save(_configPath, _settings);
                LogConfigSaved(_logger, _configPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                configError = $"Couldn't save {_configPath}: {ex.Message}";
                LogConfigSaveFailed(_logger, ex, _configPath);
            }
        }

        PublishCollectorStatus(configError);
    }

    /// <summary>Reopens the sources for changed sensor groups, unless they were reopened less than <see cref="MinReopenInterval"/> ago.</summary>
    private void ReopenIfDue()
    {
        if (!_reopenDue || Stopwatch.GetElapsedTime(_lastReopenTimestamp) < MinReopenInterval)
        {
            return;
        }

        _reopenDue = false;
        LogReopening(_logger, _settings.StorageEnabled, _settings.NetworkEnabled, _settings.ControllersEnabled);
        CloseSources();
        OpenSources();
        _lastReopenTimestamp = Stopwatch.GetTimestamp();
    }

    /// <summary>Tells clients the current settings and how the collector is running.</summary>
    private void PublishCollectorStatus(string? configError = null) =>
        _hub.PublishCollectorStatus(new CollectorStatusMessage(
            _settings.PollIntervalMs,
            _settings.StorageEnabled,
            _settings.NetworkEnabled,
            _settings.ControllersEnabled,
            _settings.HistorySeconds,
            IsService: Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService(),
            IsElevated: SensorDeck.Sources.Lhm.SystemChecks.Elevation.IsElevated(),
            ConfigPath: _configPath,
            ConfigError: configError,
            StartedUnixMs: _startedUnixMs,
            MinMaxResetUnixMs: _minMaxResetUnixMs,
            DiskHistory: _hub.Disk is not null));

    [LoggerMessage(Level = LogLevel.Information, Message = "Saved settings to {Path}.")]
    private static partial void LogConfigSaved(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't save settings to {Path}.")]
    private static partial void LogConfigSaveFailed(ILogger logger, Exception ex, string path);

    private void OpenSources()
    {
        _sources = _factory.Create(_settings);
        foreach (var source in _sources)
        {
            try
            {
                source.Open();
                LogSourceOpened(_logger, source.Name, source.Status, source.Catalog.Sensors.Count, source.StatusMessage ?? "");
            }
            catch (Exception ex)
            {
                LogOnce(source.Name, ex);
            }
        }

        PublishCatalog();
        PublishStatus();
    }

    private void CloseSources()
    {
        foreach (var source in _sources)
        {
            try
            {
                source.Dispose();
            }
            catch (Exception ex)
            {
                LogOnce(source.Name, ex);
            }
        }

        _sources = [];
    }

    /// <summary>Merges all source catalogs into one and resizes the read buffer to match.</summary>
    private void PublishCatalog()
    {
        var hardware = new List<HardwareInfo>();
        var sensors = new List<SensorInfo>();
        _offsets = new int[_sources.Count];

        for (var i = 0; i < _sources.Count; i++)
        {
            var catalog = _sources[i].Catalog;
            _offsets[i] = sensors.Count;
            hardware.AddRange(catalog.Hardware);
            sensors.AddRange(catalog.Sensors);
        }

        _buffer = new SensorReading[sensors.Count];
        _ids = [.. sensors.Select(s => s.Id)];

        // Keep resets only for sensors that still exist; after a "reset all", new sensors start fresh too.
        var current = _ids.ToHashSet(StringComparer.Ordinal);
        foreach (var gone in _resetMinMax.Keys.Where(id => !current.Contains(id)).ToList())
        {
            _resetMinMax.Remove(gone);
        }

        if (_minMaxResetUnixMs is not null)
        {
            foreach (var id in _ids)
            {
                _resetMinMax.TryAdd(id, (float.NaN, float.NaN));
            }
        }
        var merged = new SensorCatalog(++_catalogVersion, hardware, sensors);
        _hub.PublishCatalog(merged);
        LogCatalogPublished(_logger, merged.Version, hardware.Count, sensors.Count);
    }

    private void PublishStatus()
    {
        var statuses = _sources.Select(s => new SourceStatusEntry(s.Name, s.Status, s.StatusMessage)).ToArray();
        _hub.PublishStatus(statuses);
    }

    /// <summary>Logs each distinct error once as a warning, so a failing sensor doesn't flood the log every second.</summary>
    private void LogOnce(string source, Exception ex)
    {
        var key = $"{source}|{ex.GetType().FullName}|{ex.Message}";
        if (_loggedErrors.Count < 1000 && _loggedErrors.Add(key))
        {
            LogSourceError(_logger, ex, source);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Source '{Source}' opened: {Status}, {SensorCount} sensors. {StatusMessage}")]
    private static partial void LogSourceOpened(ILogger logger, string source, SourceStatus status, int sensorCount, string statusMessage);

    [LoggerMessage(Level = LogLevel.Information, Message = "Catalog v{Version}: {HardwareCount} hardware, {SensorCount} sensors.")]
    private static partial void LogCatalogPublished(ILogger logger, int version, int hardwareCount, int sensorCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "Poll interval set to {IntervalMs} ms.")]
    private static partial void LogIntervalChanged(ILogger logger, int intervalMs);

    [LoggerMessage(Level = LogLevel.Information, Message = "Reopening sources (storage: {Storage}, network: {Network}, controllers: {Controllers}).")]
    private static partial void LogReopening(ILogger logger, bool storage, bool network, bool controllers);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Source '{Source}' error (further identical errors are not logged).")]
    private static partial void LogSourceError(ILogger logger, Exception ex, string source);

    [LoggerMessage(Level = LogLevel.Critical, Message = "The poll thread crashed.")]
    private static partial void LogPollThreadCrashed(ILogger logger, Exception ex);
}
