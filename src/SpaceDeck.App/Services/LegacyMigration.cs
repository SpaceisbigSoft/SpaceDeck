using System.IO;
using Microsoft.Win32;

namespace SpaceDeck.App.Services;

/// <summary>
/// One-time carry-over from the app's old name, SensorDeck (renamed to SpaceDeck in 2026-11): copies the old settings
/// folder and moves the start-with-Windows entry. The collector's data folder is copied by Install-Collector.ps1.
/// </summary>
internal static class LegacyMigration
{
    /// <summary>The old product name, as used in folder names and the Run entry.</summary>
    public const string OldName = "SensorDeck";

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Runs both steps. Never throws: a failed step just leaves the old data where it was.</summary>
    public static void Run()
    {
        CopyAppData();
        MoveRunEntry();
    }

    /// <summary>
    /// Copies <c>%AppData%\SensorDeck</c> to <c>%AppData%\SpaceDeck</c> when the new folder doesn't exist yet
    /// (dashboards, alert limits, automation, sensor names, recordings, settings). The old folder is left untouched.
    /// </summary>
    private static void CopyAppData()
    {
        var oldDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), OldName);
        var newDirectory = AppSettings.DefaultDirectory;
        if (Directory.Exists(newDirectory) || !Directory.Exists(oldDirectory))
        {
            return;
        }

        // Copy into a temporary folder and rename it at the end, so a half-finished copy is never taken for real data.
        var staging = newDirectory + ".migrating";
        try
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }

            CopyTree(new DirectoryInfo(oldDirectory), Directory.CreateDirectory(staging));
            Directory.Move(staging, newDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                if (Directory.Exists(staging))
                {
                    Directory.Delete(staging, recursive: true);
                }
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                // Left behind; the next start deletes it before trying again.
            }
        }
    }

    /// <summary>Copies files and folders, skipping links (junctions and symbolic links are never followed).</summary>
    private static void CopyTree(DirectoryInfo from, DirectoryInfo to)
    {
        foreach (var file in from.EnumerateFiles())
        {
            if (!file.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                file.CopyTo(Path.Combine(to.FullName, file.Name), overwrite: false);
            }
        }

        foreach (var directory in from.EnumerateDirectories())
        {
            if (!directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                CopyTree(directory, to.CreateSubdirectory(directory.Name));
            }
        }
    }

    /// <summary>
    /// Removes the old "SensorDeck" start-with-Windows entry (it would start the old exe) and, if it was there, adds
    /// the new one for this exe instead.
    /// </summary>
    private static void MoveRunEntry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(OldName) is null)
            {
                return;
            }

            key.DeleteValue(OldName, throwOnMissingValue: false);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return;
        }

        AppLifetime.SetStartWithWindows(true);
    }
}
