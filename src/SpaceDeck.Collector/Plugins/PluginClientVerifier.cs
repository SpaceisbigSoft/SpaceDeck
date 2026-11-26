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
