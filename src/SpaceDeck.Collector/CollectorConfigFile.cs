using System.Text.Json;
using System.Text.Json.Nodes;

namespace SpaceDeck.Collector;

/// <summary>Where the collector keeps its files, and saving settings back to collector.json.</summary>
public static partial class CollectorConfigFile
{
    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SpaceDeck");

    public static string DefaultPath { get; } = Path.Combine(DataDirectory, "collector.json");

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    // Same leniency as the configuration loader (comments, trailing commas, any key casing).
    private static readonly JsonNodeOptions NodeOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonDocumentOptions DocumentOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>
    /// Writes the user-changeable settings into the "Collector" section, keeping everything else in the file
    /// (Logging, PipeName, unknown keys). Written via a temp file so a crash can't leave half a file.
    /// </summary>
    /// <exception cref="IOException">The file couldn't be written.</exception>
    /// <exception cref="UnauthorizedAccessException">No permission (collector not elevated).</exception>
    public static void Save(string path, CollectorOptions settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        JsonObject root;
        try
        {
            root = File.Exists(path) && JsonNode.Parse(File.ReadAllText(path), NodeOptions, DocumentOptions) is JsonObject existing
                ? existing
                : new JsonObject(NodeOptions);
        }
        catch (JsonException)
        {
            // Unreadable: keep a copy for the user, then replace it rather than refuse to save.
            File.Copy(path, path + ".bak", overwrite: true);
            root = new JsonObject(NodeOptions);
        }

        var section = Child(root, CollectorOptions.SectionName);
        Set(section, nameof(CollectorOptions.PollIntervalMs), JsonValue.Create(settings.PollIntervalMs));
        Set(section, nameof(CollectorOptions.StorageEnabled), JsonValue.Create(settings.StorageEnabled));
        Set(section, nameof(CollectorOptions.NetworkEnabled), JsonValue.Create(settings.NetworkEnabled));
        Set(section, nameof(CollectorOptions.ControllersEnabled), JsonValue.Create(settings.ControllersEnabled));

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        try
        {
            File.WriteAllText(temp, root.ToJsonString(WriteOptions));
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    /// <summary>The existing key, whatever its casing; replacing it in place avoids a duplicate key (which would stop
    /// the collector from starting).</summary>
    private static string KeyOf(JsonObject parent, string name) =>
        parent.Select(kv => kv.Key).FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase)) ?? name;

    private static JsonObject Child(JsonObject parent, string name)
    {
        var key = KeyOf(parent, name);
        if (parent[key] is JsonObject existing)
        {
            return existing;
        }

        var created = new JsonObject(NodeOptions);
        parent.Remove(key);
        parent[key] = created;
        return created;
    }

    private static void Set(JsonObject section, string name, JsonNode value) => section[KeyOf(section, name)] = value;

    [LoggerMessage(Level = LogLevel.Error, Message = "{Path} couldn't be read, so the collector runs on its default settings. Fix or delete the file, then restart the collector: {Problem}")]
    internal static partial void LogIgnored(ILogger logger, string path, string problem);
}

