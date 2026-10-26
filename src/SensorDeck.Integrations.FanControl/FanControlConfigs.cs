using System.Text.Json;

namespace SensorDeck.Integrations.FanControl;

/// <summary>A saved FanControl configuration (a .json file made with File → Save in FanControl).</summary>
/// <param name="Name">File name without extension, e.g. <c>Quiet</c>.</param>
/// <param name="LooksLikeConfig">False if the file didn't look like a FanControl config (listed anyway as a fallback).</param>
public sealed record FanControlConfig(string Name, string FullPath, DateTime LastWriteTime, bool LooksLikeConfig)
{
    public string FileName => Path.GetFileName(FullPath);
}

public static class FanControlConfigs
{
    // FanControl saves configs in a "Configurations" folder next to the exe (verified on V2xx, 2026-10-22).
    // Older versions kept them next to the exe itself.
    private const string ConfigurationsFolder = "Configurations";
    private static readonly string[] SubFolders = [ConfigurationsFolder, ""];

    private const long MaxConfigBytes = 5 * 1024 * 1024;

    /// <summary>Lists saved configs. Files that look like FanControl configs come first; if none do, all .json files are returned.</summary>
    public static IReadOnlyList<FanControlConfig> List(FanControlInstall install)
    {
        ArgumentNullException.ThrowIfNull(install);

        var all = new List<FanControlConfig>();
        foreach (var sub in SubFolders)
        {
            var dir = Path.Combine(install.Directory, sub);
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
            {
                var info = new FileInfo(file);
                // Everything in Configurations is a saved config; next to the exe, other .json files exist too, so check content.
                var isConfig = sub == ConfigurationsFolder ? IsJsonObject(info) : LooksLikeConfig(info);
                all.Add(new FanControlConfig(Path.GetFileNameWithoutExtension(file), info.FullName, info.LastWriteTime, isConfig));
            }
        }

        var configs = all.Where(c => c.LooksLikeConfig).ToList();
        var result = configs.Count > 0 ? configs : all;
        return [.. result.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Finds a config by name (with or without .json), case-insensitively.</summary>
    public static FanControlConfig? Find(FanControlInstall install, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var wanted = name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? name[..^5] : name;
        return List(install).FirstOrDefault(c => c.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>FanControl's state file in the Configurations folder (JSON, no extension).</summary>
    public const string CacheFileName = "CACHE";

    /// <summary>The folder FanControl keeps its configs and <see cref="CacheFileName"/> in.</summary>
    public static string ConfigurationsDirectory(FanControlInstall install)
    {
        ArgumentNullException.ThrowIfNull(install);
        return Path.Combine(install.Directory, ConfigurationsFolder);
    }

    /// <summary>
    /// The config FanControl has loaded right now, from <c>Configurations\CACHE</c> ("CurrentConfigFileName",
    /// found 2026-10-23), without the .json extension. Null when the file is missing, unreadable or doesn't say.
    /// </summary>
    public static string? ReadActive(FanControlInstall install)
    {
        var path = Path.Combine(ConfigurationsDirectory(install), CacheFileName);
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length is 0 or > MaxConfigBytes)
            {
                return null;
            }

            // FanControl may be writing the file right now: share it rather than fail.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return ParseActive(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Reads "CurrentConfigFileName" from the contents of FanControl's CACHE file (see <see cref="ReadActive"/>).</summary>
    public static string? ParseActive(Stream cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        try
        {
            using var doc = JsonDocument.Parse(cache, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (property.Name.Equals("CurrentConfigFileName", StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.String)
                {
                    return ConfigName(property.Value.GetString());
                }
            }

            return null;
        }
        catch (JsonException)
        {
            return null; // half-written or not JSON
        }
    }

    /// <summary>"Basic Ramps.json" (or a full path to it) → "Basic Ramps"; null for empty or odd values.</summary>
    private static string? ConfigName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            return null;
        }

        var name = Path.GetFileName(fileName.Trim());
        if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^5];
        }

        return name.Length > 0 ? name : null;
    }

    private static bool IsJsonObject(FileInfo file)
    {
        if (file.Length is 0 or > MaxConfigBytes)
        {
            return false;
        }

        try
        {
            using var stream = file.OpenRead();
            using var doc = JsonDocument.Parse(stream);
            return doc.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Heuristic for .json files next to the exe: FanControl configs are JSON objects with a top-level "Main" section holding controls and curves.
    /// This is a heuristic, not a schema check; unknown layouts still get listed via the fallback.
    /// </summary>
    private static bool LooksLikeConfig(FileInfo file)
    {
        if (file.Length is 0 or > MaxConfigBytes)
        {
            return false;
        }

        try
        {
            using var stream = file.OpenRead();
            using var doc = JsonDocument.Parse(stream);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("Main", out var main)
                && main.ValueKind == JsonValueKind.Object;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
