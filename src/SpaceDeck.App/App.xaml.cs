using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using SpaceDeck.App.Services;
using SpaceDeck.App.Themes;
using SpaceDeck.App.ViewModels;
using SpaceDeck.Core.Dashboards;
using SpaceDeck.Integrations.FanControl;

namespace SpaceDeck.App;

public partial class App : Application
{
    /// <summary>Held while this (normal, non-replay) instance runs; one per Windows session.</summary>
    private const string InstanceMutexName = @"Local\SpaceDeck.App";

    /// <summary>Set by a second start to ask the running instance to show its window.</summary>
    private const string ShowEventName = @"Local\SpaceDeck.App.Show";

    /// <summary>ASFW_ANY: any process may take the foreground (lets the running instance bring its window to the front).</summary>
    private const uint AllowAnyProcess = uint.MaxValue;

    private ServiceProvider? _services;
    private ReplayHost? _replay;
    private Mutex? _instanceMutex;
    private bool _ownsInstanceMutex;
    private EventWaitHandle? _showRequested;
    private RegisteredWaitHandle? _showWait;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Last-resort safety net: log, tell the user, keep running.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogCrash(args.Exception);
            args.SetObserved();
        };

        AppMode.Parse(e.Args);
        // The previous instance must have exited (and saved) before a restore replaces its files; if it hasn't,
        // the restore stays staged for the next start.
        var previousGone = WaitForPreviousInstance();

        // One normal instance per user session: a second one would run its own automation (switching FanControl against
        // the first), notify every alert twice and overwrite the first one's settings. It shows the running one instead
        // and exits, before a staged restore could be applied. Replays are separate by design.
        if (!AppMode.IsReplay && !AcquireSingleInstance(showOther: !e.Args.Contains(AppLifetime.TrayArgument, StringComparer.OrdinalIgnoreCase)))
        {
            Shutdown();
            return;
        }

        // First start after the rename from SensorDeck: bring the old settings, dashboards and start-with-Windows entry over.
        if (!AppMode.IsReplay)
        {
            LegacyMigration.Run();
        }

        // Restarted after a restore and the previous instance took longer than the wait above, but has let go of the
        // instance name by now: it is all but gone, so give it a moment more and apply the restore after all.
        if (!previousGone && !AppMode.IsReplay)
        {
            previousGone = WaitForPreviousInstance();
        }

        // Memory part numbers come from WMI, which can be slow: read them off the UI thread before a RAM tile needs them
        // (a tile that asks first waits for this same query, never a second one).
        _ = Task.Run(() => MemoryModules.All);

        var restoreMessage = AppMode.IsReplay || !previousGone ? null : BackupService.ApplyPendingRestore();

        var settings = AppSettings.Load();
        if (AppMode.IsReplay)
        {
            settings.SetupCompleted = true; // a replay has nothing to set up
        }

        // Skin first: converters and views resolve "Skin.*" resources as soon as they're created.
        SkinManager.Apply(settings.Skin);
        if (Appearance.TryParseColor(settings.AccentColor, out var accent))
        {
            SkinManager.SetAccent(accent);
        }

        Appearance.Set(settings.UiScale, settings.ClockFormat);

        var services = new ServiceCollection();
        services.AddSingleton(settings);
        services.AddSingleton<SensorCustomizationService>();
        services.AddSingleton<SensorLinker>();
        services.AddSingleton(_ => new CollectorClient(AppMode.PipeName));
        services.AddSingleton<ReplayRecorder>();
        services.AddSingleton<BackupService>();
        services.AddSingleton<UpdateService>();
        services.AddSingleton<SensorStore>();
        services.AddSingleton<FanProfileService>();
        services.AddSingleton(new DashboardRepository(Path.Combine(AppSettings.Directory, "dashboards")));
        services.AddSingleton<DialogService>();
        services.AddSingleton<TrayService>();
        services.AddSingleton<AlertService>();
        services.AddSingleton<GlobalHotkeyService>();
        services.AddSingleton<NtfyService>();
        services.AddSingleton<SpaceDeck.App.Remote.RemoteServer>();
        services.AddSingleton<RemoteSettingsViewModel>();
        services.AddSingleton<SidebarService>();
        services.AddSingleton<SidebarSettingsViewModel>();
        services.AddSingleton<RecordingService>();
        services.AddSingleton<RecordingsViewModel>();
        services.AddSingleton<AutomationService>();
        services.AddSingleton<AutomationViewModel>();
        services.AddSingleton<AlertsViewModel>();
        services.AddSingleton<TrayReadout>();
        services.AddSingleton<DashboardViewModel>();
        services.AddSingleton<SensorsViewModel>();
        services.AddSingleton<VoltagesViewModel>();
        services.AddSingleton<FansViewModel>();
        services.AddSingleton<HistoryViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<DiagnosticsViewModel>();
        services.AddSingleton<AboutViewModel>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();
        _services = services.BuildServiceProvider();

        // Build the UI first (the store subscribes to the client), then start connecting.
        // Alerts and the tray readout watch the store from the first reading on, whichever page is open.
        FanScale.Initialize(settings, _services.GetRequiredService<FanProfileService>());
        _services.GetRequiredService<AlertService>();
        _services.GetRequiredService<TrayReadout>();
        _services.GetRequiredService<BackupService>(); // applies restored collector settings once connected

        if (AppMode.IsReplay)
        {
            _replay = new ReplayHost();
            if (!_replay.Start(out var replayError))
            {
                MessageBox.Show($"Couldn't start the replay of {AppMode.ReplayName}.\n\n{replayError}", "SpaceDeck", MessageBoxButton.OK, MessageBoxImage.Warning);
                Shutdown();
                return;
            }

            // Only that process may answer on this window's replay pipe (another program could create the name first).
            if (_replay.ProcessId is { } replayProcessId)
            {
                _services.GetRequiredService<CollectorClient>().TrustServerProcess(replayProcessId);
            }
        }

        var window = _services.GetRequiredService<MainWindow>();
        MainWindow = window;
        if (AppMode.IsReplay)
        {
            window.Title = $"SpaceDeck — replay of {AppMode.ReplayName}";
        }

        // Started with Windows: stay in the tray until the user opens the window.
        if (!e.Args.Contains(AppLifetime.TrayArgument, StringComparer.OrdinalIgnoreCase))
        {
            window.Show();
        }

        _services.GetRequiredService<DashboardViewModel>().RestoreWindows();

        // Signing out / shutting down Windows: a real exit (not close-to-tray); keep dashboard windows for next time.
        SessionEnding += (_, _) =>
        {
            AppLifetime.IsExiting = true;
            _services.GetRequiredService<DialogService>().SaveDashboardWindowsForExit();
            settings.Save();
        };
        _services.GetRequiredService<CollectorClient>().Start();
        var tray = _services.GetRequiredService<TrayService>();
        var dialogs = _services.GetRequiredService<DialogService>();
        tray.ClickThroughToggled += dialogs.ToggleClickThrough;
        tray.IsClickThrough = () => dialogs.AnyClickThrough;
        var sidebar = _services.GetRequiredService<SidebarService>();
        tray.SidebarToggled += () => sidebar.SetEnabled(!sidebar.Settings.Enabled);
        tray.IsSidebarOn = () => sidebar.IsOpen;
        if (!AppMode.IsReplay)
        {
            var fans = _services.GetRequiredService<FanProfileService>();
            tray.GetFanProfiles = () =>
            {
                fans.Refresh();
                return (fans.Configs.Select(c => c.Name).ToList(), fans.LastProfile);
            };
            tray.FanProfilePicked += name =>
            {
                var result = fans.Switch(name);
                if (result.Outcome is not (SwitchOutcome.Sent or SwitchOutcome.StartedWithConfig))
                {
                    tray.Notify("Fan profile", result.Message, critical: false);
                }
            };
        }

        sidebar.Apply();

        // A replay window only shows recorded data: nothing that acts on this PC or reaches other devices.
        if (!AppMode.IsReplay)
        {
            _services.GetRequiredService<AutomationService>().Start();
            _services.GetRequiredService<GlobalHotkeyService>().Start();
            _ = _services.GetRequiredService<SpaceDeck.App.Remote.RemoteServer>().ApplyAsync(); // starts only if turned on
            UpdateService.CleanUpDownloads(); // installers from earlier updates
            _services.GetRequiredService<UpdateService>().StartAutomaticCheck();
        }

        if (restoreMessage is not null)
        {
            Dispatcher.BeginInvoke(() => MessageBox.Show(restoreMessage, "SpaceDeck", MessageBoxButton.OK,
                restoreMessage.StartsWith("Restored", StringComparison.Ordinal) ? MessageBoxImage.Information : MessageBoxImage.Warning));
        }
    }

    /// <summary>
    /// True when this is the only normal instance in this Windows session. Otherwise asks the running one to show its
    /// window (unless <paramref name="showOther"/> is false, e.g. started with Windows into the tray) and returns false.
    /// </summary>
    private bool AcquireSingleInstance(bool showOther)
    {
        try
        {
            _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out var createdNew);

            // Restarting after a restore (--wait-for): the previous instance may still be closing. It lets go of the
            // name when it exits, so wait for that instead of handing over to an instance that is going away.
            _ownsInstanceMutex = createdNew || (AppMode.WaitForProcessId is not null && TakeOverFromExitingInstance(_instanceMutex));
            _showRequested = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            if (!_ownsInstanceMutex)
            {
                if (showOther)
                {
                    _ = AllowSetForegroundWindow(AllowAnyProcess);
                    _showRequested.Set();
                }

                return false;
            }

            // A later start asks for the window: shown on the UI thread, the same way as the tray's "Open SpaceDeck".
            _showWait = ThreadPool.RegisterWaitForSingleObject(
                _showRequested, (_, _) => Dispatcher.BeginInvoke(new Action(ShowForOtherStart)), null, Timeout.Infinite, executeOnlyOnce: false);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or WaitHandleCannotBeOpenedException or IOException)
        {
            // Another program holds the name with an ACL we can't open: run anyway rather than refuse to start.
            return true;
        }
    }

    /// <summary>Waits for the exiting instance to release the instance mutex; true once this one owns it.</summary>
    private static bool TakeOverFromExitingInstance(Mutex mutex)
    {
        try
        {
            return mutex.WaitOne(TimeSpan.FromSeconds(30));
        }
        catch (AbandonedMutexException)
        {
            return true; // it exited without releasing it: ours now
        }
    }

    private void ShowForOtherStart()
    {
        if (!AppLifetime.IsExiting && MainWindow is global::SpaceDeck.App.MainWindow main)
        {
            main.RestoreFromTray();
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);

    /// <summary>After "restore from backup" the new instance starts before the old one has exited and saved.</summary>
    private static bool WaitForPreviousInstance()
    {
        if (AppMode.WaitForProcessId is not { } pid)
        {
            return true;
        }

        try
        {
            using var previous = System.Diagnostics.Process.GetProcessById(pid);
            return previous.WaitForExit(TimeSpan.FromSeconds(20));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return true; // already gone
        }
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var path = LogCrash(e.Exception);
        MessageBox.Show(
            $"Something went wrong:\n\n{e.Exception.Message}\n\nDetails were saved to {path}. SpaceDeck will keep running.",
            "SpaceDeck",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        e.Handled = true;
    }

    private static string LogCrash(Exception exception)
    {
        var path = Path.Combine(AppSettings.Directory, "app-errors.log");
        LogFiles.Append(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {exception}{Environment.NewLine}{Environment.NewLine}");

        return path;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.GetService<AppSettings>()?.Save();
        _services?.Dispose();
        _replay?.Dispose();
        _showWait?.Unregister(null);
        _showRequested?.Dispose();
        if (_ownsInstanceMutex)
        {
            _instanceMutex?.ReleaseMutex(); // same (UI) thread that created it
        }

        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
