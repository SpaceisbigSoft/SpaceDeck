using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.ServiceProcess;

namespace SpaceDeck.App.Services;

public enum CollectorInstallState
{
    NotInstalled,
    Stopped,
    Running,
    Unknown,
}

/// <summary>Checks and starts the SpaceDeck collector Windows Service.</summary>
public static class CollectorServiceStatus
{
    public const string ServiceName = "SpaceDeckCollector";

    /// <summary>ERROR_CANCELLED: the user declined the UAC prompt.</summary>
    private const int ErrorCancelled = 1223;

    public static CollectorInstallState Query()
    {
        try
        {
            using var service = new ServiceController(ServiceName);
            return service.Status == ServiceControllerStatus.Running ? CollectorInstallState.Running : CollectorInstallState.Stopped;
        }
        catch (InvalidOperationException)
        {
            // Thrown when the service doesn't exist.
            return CollectorInstallState.NotInstalled;
        }
        catch (Win32Exception)
        {
            return CollectorInstallState.Unknown;
        }
    }

    /// <summary>Starts the service. Needs administrator rights, so Windows shows a UAC prompt.</summary>
    public static bool TryStart(out string? error)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                // Full path: a bare "sc.exe" is looked up through the search path, and this runs as administrator.
                FileName = Path.Combine(Environment.SystemDirectory, "sc.exe"),
                Arguments = $"start {ServiceName}",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            error = null;
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            error = "The administrator prompt was cancelled.";
            return false;
        }
        catch (Win32Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Opens a URL, file or folder with the default handler. Returns false instead of throwing. Only for values
    /// SpaceDeck itself chose (its own folders and files); a program path would be run, so anything that came from a
    /// settings file, a backup or the network goes through <see cref="OpenWebUrl"/> instead.
    /// </summary>
    public static bool OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            // InvalidOperationException: an empty name.
            return false;
        }
    }

    /// <summary>Opens an http:// or https:// address in the browser; refuses anything else (file paths, file:, other protocols).</summary>
    public static bool OpenWebUrl(string? url) =>
        Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
        && OpenUrl(uri.AbsoluteUri);

    /// <summary>
    /// The source tree's root when this app runs from its own build output (<c>&lt;root&gt;\src\SpaceDeck.App\bin\…</c>),
    /// else null. Developer fallbacks (the install script, a freshly built collector) are looked up only there, never
    /// by walking up from an installed app: folders such as <c>C:\scripts</c> or <c>C:\src</c> can be created by any
    /// local user, and what is found there is run (the install script even as administrator).
    /// </summary>
    public static string? SourceTreeRoot()
    {
        var sep = Path.DirectorySeparatorChar;
        var marker = $"{sep}src{sep}SpaceDeck.App{sep}bin{sep}";
        var baseDir = AppContext.BaseDirectory;
        var at = baseDir.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return at > 0 ? baseDir[..(at + 1)] : null;
    }
}
