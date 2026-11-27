using System.IO.Pipes;
using System.Runtime.InteropServices;
using SpaceDeck.Integrations.FanControl;

namespace SpaceDeck.Collector.Plugins;

/// <summary>Decides whether the process at the other end of the plugin pipe may feed readings. Swappable for tests.</summary>
public interface IPluginClientVerifier
{
    /// <summary>Null if the client is accepted; otherwise why not, for the log (identical reasons are logged once).</summary>
    /// <param name="processId">The client's process id (<c>GetNamedPipeClientProcessId</c>).</param>
    /// <param name="imagePath">Its full image path, or null if the process couldn't be opened.</param>
    string? Check(int processId, string? imagePath);
}

/// <summary>
/// Production rule: the client must be FanControl.exe or FanControl.Service.exe, in a folder that passes FanControl
/// auto-detection's "no untrusted writer" rule (<see cref="FanControlLocator.UntrustedReason"/>). Anything another
/// account could have put there might be a look-alike feeding forged readings (which drive alerts and automation).
/// </summary>
public sealed class FanControlClientVerifier : IPluginClientVerifier
{
    public const string AppExeName = FanControlLocator.ExeName;

    public const string ServiceExeName = "FanControl.Service.exe";

    public string? Check(int processId, string? imagePath)
    {
        // Reasons leave the process id out: they are logged once each, and every reconnect has a new id.
        if (string.IsNullOrEmpty(imagePath))
        {
            return "couldn't tell which program connected";
        }

        if (!IsFanControlImage(imagePath))
        {
            return $"{imagePath} isn't FanControl";
        }

        return FanControlLocator.UntrustedReason(imagePath);
    }

    /// <summary>
    /// True for a local path whose file name is FanControl.exe or FanControl.Service.exe. Network paths are refused: their
    /// permissions are the file server's, not this PC's.
    /// </summary>
    internal static bool IsFanControlImage(string imagePath)
    {
        if (imagePath.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return false;
        }

        var name = Path.GetFileName(imagePath);
        return name.Equals(AppExeName, StringComparison.OrdinalIgnoreCase) || name.Equals(ServiceExeName, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// The client check of the collector's FanControl pipes (plugin pipe and screen pipe): which process connected, then
/// <see cref="IPluginClientVerifier"/> on its image. Run before a byte is read from or written to the client.
/// </summary>
internal static class PluginPipeClient
{
    /// <summary>Null if the client process is accepted; otherwise why not.</summary>
    public static string? Check(NamedPipeServerStream pipe, IPluginClientVerifier verifier, out int processId)
    {
        processId = 0;
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid))
        {
            return "couldn't tell which process connected";
        }

        processId = (int)pid;
        return verifier.Check(processId, FanControlLocator.TryGetImagePath(processId));
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafeHandle pipe, out uint clientProcessId);
}
