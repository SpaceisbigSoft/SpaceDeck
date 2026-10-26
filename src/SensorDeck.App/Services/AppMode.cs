using System.IO;
using SensorDeck.Core.Protocol;

namespace SensorDeck.App.Services;

/// <summary>
/// How this instance was started: normally, or as a replay of a recording (<c>--replay file</c>), which runs its own
/// collector on a private pipe and keeps its settings in a separate folder so the real setup is never touched.
/// </summary>
public static class AppMode
{
    public const string ReplayArgument = "--replay";

    /// <summary>Wait for this process to exit before starting (used when restarting after a restore).</summary>
    public const string WaitForArgument = "--wait-for";

    public static string PipeName { get; private set; } = ProtocolInfo.PipeName;

    public static string? ReplayFile { get; private set; }

    public static bool IsReplay => ReplayFile is not null;

    public static string ReplayName => ReplayFile is null ? "" : Path.GetFileName(ReplayFile);

    public static int? WaitForProcessId { get; private set; }

    /// <summary>Reads the command line. Call first thing at startup.</summary>
    public static void Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (args[i].Equals(ReplayArgument, StringComparison.OrdinalIgnoreCase))
            {
                ReplayFile = Path.GetFullPath(args[i + 1]);
                PipeName = $"{ProtocolInfo.PipeName}-replay-{Environment.ProcessId}";
            }
            else if (args[i].Equals(WaitForArgument, StringComparison.OrdinalIgnoreCase)
                     && int.TryParse(args[i + 1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var pid))
            {
                WaitForProcessId = pid;
            }
        }

        if (ReplayFile is not null)
        {
            // One profile per recording, so its default dashboard is built for the recorded hardware and edits stick.
            var name = Path.GetFileNameWithoutExtension(ReplayFile);
            var invalid = Path.GetInvalidFileNameChars();
            // Trimmed of dots and spaces: a recording named "...sdrec" would otherwise give the profile folder "..",
            // which is the real settings folder.
            var safe = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim(' ', '.');
            AppSettings.UseDirectory(Path.Combine(AppSettings.DefaultDirectory, "replay-profiles", safe.Length > 0 ? safe : "replay"));
        }
    }
}
