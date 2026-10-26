using System.IO;

namespace SensorDeck.App.Services;

/// <summary>Appends to the app's small text logs, keeping each under a size cap (the old part moves to ".old").</summary>
public static class LogFiles
{
    public const long DefaultMaxBytes = 2L * 1024 * 1024;

    /// <summary>Best effort: never throws for IO problems.</summary>
    public static void Append(string path, string text, long maxBytes = DefaultMaxBytes)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var info = new FileInfo(path);
            if (info.Exists && info.Length > maxBytes)
            {
                File.Move(path, path + ".old", overwrite: true);
            }

            File.AppendAllText(path, text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Logging is optional.
        }
    }
}
