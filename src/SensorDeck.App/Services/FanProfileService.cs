using System.IO;
using System.Windows.Threading;
using SensorDeck.Integrations.FanControl;

namespace SensorDeck.App.Services;

/// <summary>
/// Lists FanControl configs and switches between them. FanControl stays the only program that controls fans.
/// Also follows FanControl's own record of the loaded config (<c>Configurations\CACHE</c>), so a profile picked in
/// FanControl itself shows up in SensorDeck too.
/// </summary>
public sealed class FanProfileService : IDisposable
{
    private readonly AppSettings _settings;
    private readonly Dispatcher _dispatcher;

    /// <summary>Waits for FanControl to finish writing CACHE (it raises several change events per save).</summary>
    private readonly DispatcherTimer _cacheChanged;

    private FileSystemWatcher? _watcher;
    private string? _watchedDirectory;

    public FanProfileService(AppSettings settings)
    {
        _settings = settings;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _cacheChanged = new DispatcherTimer(TimeSpan.FromMilliseconds(750), DispatcherPriority.Background, OnCacheChanged, _dispatcher);
        _cacheChanged.Stop();
    }

    public FanControlInstall? Install { get; private set; }

    /// <summary>
    /// When <see cref="Install"/> is null: why a FanControl found automatically wasn't used (others can modify it);
    /// null if none was found at all.
    /// </summary>
    public string? Rejected { get; private set; }

    /// <summary>Raised after every successful switch (picker, dashboard tile or automation), with the config name.</summary>
    public event Action<string>? ProfileSwitched;

    /// <summary>Raised after a switch the user made (not automation), so automation can hold off.</summary>
    public event Action<string>? ManuallySwitched;

    public IReadOnlyList<FanControlConfig> Configs { get; private set; } = [];

    /// <summary>
    /// The active profile: what FanControl says it has loaded when it says so (kept in step by <see cref="Refresh"/>
    /// and a file watcher), otherwise the config SensorDeck last switched to.
    /// </summary>
    public string? LastProfile => _settings.LastFanProfile;

    /// <summary>Finds FanControl and lists its configs. Cheap enough to call when the picker opens.</summary>
    public void Refresh()
    {
        try
        {
            Install = FanControlLocator.Find(_settings.FanControlExePath, out var rejected);
            Rejected = rejected;
            Configs = Install is null ? [] : FanControlConfigs.List(Install);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Configs = [];
        }

        // Quietly adopt what FanControl has loaded (no events: callers are mid-refresh of their own lists).
        if (ReadActiveConfig() is { } active && !active.Equals(_settings.LastFanProfile, StringComparison.OrdinalIgnoreCase))
        {
            _settings.LastFanProfile = active;
            _settings.Save();
        }

        Watch(Install is null || AppMode.IsReplay ? null : FanControlConfigs.ConfigurationsDirectory(Install));
    }

    public void Dispose()
    {
        _cacheChanged.Stop();
        _watcher?.Dispose();
        _watcher = null;
    }

    /// <summary>FanControl's loaded config, if CACHE names one of the saved configs.</summary>
    private string? ReadActiveConfig()
    {
        if (Install is null || AppMode.IsReplay || FanControlConfigs.ReadActive(Install) is not { } name)
        {
            return null;
        }

        return Configs.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Name;
    }

    /// <summary>Watches FanControl's CACHE file in <paramref name="directory"/> (null stops watching).</summary>
    private void Watch(string? directory)
    {
        if (string.Equals(directory, _watchedDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _watcher?.Dispose();
        _watcher = null;
        _watchedDirectory = directory;
        if (directory is null || !Directory.Exists(directory))
        {
            return;
        }

        try
        {
            var watcher = new FileSystemWatcher(directory, FanControlConfigs.CacheFileName)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };
            FileSystemEventHandler changed = (_, _) => _dispatcher.InvokeAsync(RestartCacheTimer);
            watcher.Changed += changed;
            watcher.Created += changed;
            watcher.Renamed += (_, _) => _dispatcher.InvokeAsync(RestartCacheTimer);
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            // No live updates; the picker still re-reads CACHE whenever it opens.
        }
    }

    private void RestartCacheTimer()
    {
        _cacheChanged.Stop();
        _cacheChanged.Start();
    }

    /// <summary>
    /// CACHE changed. A switch SensorDeck made already set <see cref="LastProfile"/>, so only a profile picked in
    /// FanControl itself gets here; it counts as the user's choice (automation holds off, like the picker).
    /// </summary>
    private void OnCacheChanged(object? sender, EventArgs e)
    {
        _cacheChanged.Stop();
        if (Install is not null)
        {
            try
            {
                Configs = FanControlConfigs.List(Install); // a newly saved config can be the one just loaded
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;
            }
        }

        if (ReadActiveConfig() is not { } active || active.Equals(_settings.LastFanProfile, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _settings.LastFanProfile = active;
        _settings.Save();
        ProfileSwitched?.Invoke(active);
        ManuallySwitched?.Invoke(active);
    }

    /// <param name="automatic">True when automation switches (doesn't count as the user's choice).</param>
    public SwitchResult Switch(string name, bool automatic = false)
    {
        if (AppMode.IsReplay)
        {
            return new SwitchResult(SwitchOutcome.Failed, "A replay window doesn't switch FanControl profiles.");
        }

        if (Install is null)
        {
            Refresh();
        }

        if (Install is null)
        {
            return new SwitchResult(SwitchOutcome.Failed, "FanControl wasn't found. Set its location in Settings.");
        }

        var config = Configs.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? FanControlConfigs.Find(Install, name);
        if (config is null)
        {
            return new SwitchResult(SwitchOutcome.Failed, $"FanControl config '{name}' wasn't found.");
        }

        var result = FanControlSwitcher.Switch(Install, config);
        if (result.Outcome is SwitchOutcome.Sent or SwitchOutcome.StartedWithConfig)
        {
            _settings.LastFanProfile = config.Name;
            _settings.Save();
            ProfileSwitched?.Invoke(config.Name);
            if (!automatic)
            {
                ManuallySwitched?.Invoke(config.Name);
            }
        }

        return result;
    }
}
