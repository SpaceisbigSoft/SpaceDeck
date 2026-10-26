using System.ComponentModel;
using System.Diagnostics;

namespace SensorDeck.Integrations.FanControl;

public enum SwitchOutcome
{
    /// <summary>The command was handed to FanControl. FanControl reports nothing back, so this means "sent", not "confirmed".</summary>
    Sent,
    /// <summary>FanControl wasn't running, so the command started it with the requested config.</summary>
    StartedWithConfig,
    /// <summary>The user cancelled the Windows administrator (UAC) prompt.</summary>
    ElevationCancelled,
    Failed,
}

public sealed record SwitchResult(SwitchOutcome Outcome, string Message);

/// <summary>
/// Switches FanControl's configuration by running <c>FanControl.exe -c &lt;config&gt;.json</c>.
/// If FanControl is already running, the new process forwards the request to it and exits.
/// </summary>
/// <remarks>
/// FanControl is the only program that writes to fans. SensorDeck never does, so the two can't fight.
/// FanControl may request administrator rights when launched; if Windows shows a UAC prompt on every
/// switch, a scheduled-task launcher is the planned workaround (see docs/HANDOFF.md §8, FanControl phase).
/// </remarks>
public static class FanControlSwitcher
{
    /// <summary>ERROR_CANCELLED: the user declined the UAC prompt.</summary>
    private const int ErrorCancelled = 1223;

    public static SwitchResult Switch(FanControlInstall install, FanControlConfig config)
    {
        ArgumentNullException.ThrowIfNull(install);
        ArgumentNullException.ThrowIfNull(config);

        if (!File.Exists(config.FullPath))
        {
            return new SwitchResult(SwitchOutcome.Failed, $"Config file not found: {config.FullPath}");
        }

        var wasRunning = FanControlLocator.IsRunning();

        // The FanControl wiki uses a bare file name with "Start in" set to the folder holding the exe and configs.
        // Match that when the config sits next to the exe; otherwise pass the full path.
        var configDir = Path.GetDirectoryName(config.FullPath)!;
        var sameFolder = string.Equals(Path.GetFullPath(configDir), Path.GetFullPath(install.Directory), StringComparison.OrdinalIgnoreCase);
        var argument = sameFolder ? config.FileName : config.FullPath;

        var startInfo = new ProcessStartInfo
        {
            FileName = install.ExePath,
            WorkingDirectory = install.Directory,
            // Shell execute lets Windows show a UAC prompt if FanControl requires elevation.
            UseShellExecute = true,
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(startInfo);
            return wasRunning
                ? new SwitchResult(SwitchOutcome.Sent, $"Asked FanControl to load '{config.Name}'.")
                : new SwitchResult(SwitchOutcome.StartedWithConfig, $"FanControl wasn't running; started it with '{config.Name}'.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return new SwitchResult(SwitchOutcome.ElevationCancelled, "The administrator prompt was cancelled, so the config wasn't changed.");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return new SwitchResult(SwitchOutcome.Failed, $"Couldn't run FanControl: {ex.Message}");
        }
    }
}
