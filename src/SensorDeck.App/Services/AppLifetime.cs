using System.IO;
using Microsoft.Win32;

namespace SensorDeck.App.Services;

/// <summary>App-wide lifetime state: real exit vs. "close to tray", and the start-with-Windows registration.</summary>
public static class AppLifetime
{
    /// <summary>Command-line switch used by the start-with-Windows entry: start in the tray, no window.</summary>
    public const string TrayArgument = "--tray";

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "SensorDeck";

    /// <summary>True once the app is really quitting (tray Exit, or closing the window with close-to-tray off).</summary>
    public static bool IsExiting { get; set; }

    /// <summary>Whether SensorDeck starts when you sign in (a per-user Run entry; no admin rights needed).</summary>
    public static bool StartsWithWindows
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(RunValue) is string command
                    && Environment.ProcessPath is { } exe
                    && command.Contains(exe, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                return false;
            }
        }
    }

    /// <summary>Adds or removes the Run entry. Returns false if the registry couldn't be written.</summary>
    public static bool SetStartWithWindows(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled && Environment.ProcessPath is { } exe)
            {
                key.SetValue(RunValue, $"\"{exe}\" {TrayArgument}");
            }
            else
            {
                key.DeleteValue(RunValue, throwOnMissingValue: false);
            }

            return true;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }
}
