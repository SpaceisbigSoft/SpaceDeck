using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Microsoft.Win32;

namespace SpaceDeck.App.Services;

/// <summary>
/// Runs a private collector that plays back a recording (replay mode). It needs no admin rights and reads no
/// hardware; it stops when this app exits (it watches our process id), and we also stop it on exit.
/// </summary>
public sealed class ReplayHost : IDisposable
{
    private const string CollectorExe = "SpaceDeck.Collector.exe";
    private Process? _process;

    /// <summary>The replay collector's process id once started; the app's client accepts only this process on the replay pipe.</summary>
    public int? ProcessId => _process?.Id;

    /// <summary>Starts the collector for <see cref="AppMode.ReplayFile"/>. False (with <paramref name="error"/>) if it couldn't.</summary>
    public bool Start(out string? error)
    {
        error = null;
        if (AppMode.ReplayFile is not { } file)
        {
            return false;
        }

        if (!File.Exists(file))
        {
            error = $"The replay file doesn't exist: {file}";
            return false;
        }

        var exe = FindCollector();
        if (exe is null)
        {
            error = $"Couldn't find {CollectorExe} (next to SpaceDeck, where the service runs from, or in the source tree's build output). Build the collector first.";
            return false;
        }

        try
        {
            var start = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
            };
            start.ArgumentList.Add("--replay");
            start.ArgumentList.Add(file);
            start.ArgumentList.Add("--pipe");
            start.ArgumentList.Add(AppMode.PipeName);
            start.ArgumentList.Add("--parent-pid");
            start.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            _process = Process.Start(start);
            return _process is not null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            error = $"Couldn't start the replay collector: {ex.Message}";
            return false;
        }
    }

    /// <summary>Starts a second SpaceDeck window that replays <paramref name="file"/>.</summary>
    public static bool Launch(string file, out string? error)
    {
        error = null;
        if (Environment.ProcessPath is not { } exe)
        {
            error = "Couldn't find SpaceDeck's own program file.";
            return false;
        }

        try
        {
            var start = new ProcessStartInfo { FileName = exe, UseShellExecute = false };
            start.ArgumentList.Add(AppMode.ReplayArgument);
            start.ArgumentList.Add(file);
            Process.Start(start)?.Dispose();
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            error = ex.Message;
            return false;
        }
    }

    public void Dispose()
    {
        if (_process is null)
        {
            return;
        }

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException or AggregateException)
        {
            // Already gone (or partly); it also exits by itself when we do.
        }

        _process.Dispose();
        _process = null;
    }

    /// <summary>
    /// Next to the app (installed), the service's folder, or (only when the app itself runs from the source tree's
    /// build output) the newest collector build in that source tree.
    /// </summary>
    private static string? FindCollector()
    {
        var beside = Path.Combine(AppContext.BaseDirectory, CollectorExe);
        if (File.Exists(beside))
        {
            return beside;
        }

        if (ServiceExecutable() is { } service && File.Exists(service))
        {
            return service;
        }

        // Not by walking up from an installed app: any local user can create C:\src\… and the exe found is run.
        if (CollectorServiceStatus.SourceTreeRoot() is not { } root)
        {
            return null;
        }

        var bin = Path.Combine(root, "src", "SpaceDeck.Collector", "bin");
        try
        {
            return Directory.Exists(bin)
                ? Directory.EnumerateFiles(bin, CollectorExe, SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The installed service's program path (ImagePath, without quotes or arguments).</summary>
    private static string? ServiceExecutable()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{CollectorServiceStatus.ServiceName}");
            if (key?.GetValue("ImagePath") is not string image || image.Length == 0)
            {
                return null;
            }

            image = Environment.ExpandEnvironmentVariables(image.Trim());
            if (image.StartsWith('"'))
            {
                var end = image.IndexOf('"', 1);
                return end > 1 ? image[1..end] : null;
            }

            var exeEnd = image.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            return exeEnd > 0 ? image[..(exeEnd + 4)] : image;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}
