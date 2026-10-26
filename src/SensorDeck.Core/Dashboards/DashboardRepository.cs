using System.Text.Json;

namespace SensorDeck.Core.Dashboards;

/// <summary>
/// Stores dashboards as one JSON file each (<c>&lt;id&gt;.json</c>) in a folder — normally
/// <c>%AppData%\SensorDeck\dashboards</c>. Unreadable files are skipped, never fatal.
/// </summary>
public sealed class DashboardRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Largest file <see cref="Import"/> accepts; real dashboards are a few KB.</summary>
    public const long MaxImportBytes = 4L * 1024 * 1024;

    /// <summary>
    /// Most tiles an imported (shared) dashboard keeps; a file with more is cut there (the grid lays out every tile on each
    /// change). The user's own saved dashboards are never cut on load.
    /// </summary>
    public const int MaxTiles = 500;

    public DashboardRepository(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        Directory = directory;
    }

    public string Directory { get; }

    /// <summary>Every readable dashboard, in tab order.</summary>
    public IReadOnlyList<DashboardDocument> LoadAll()
    {
        if (!System.IO.Directory.Exists(Directory))
        {
            return [];
        }

        var result = new List<DashboardDocument>();
        foreach (var path in System.IO.Directory.EnumerateFiles(Directory, "*.json"))
        {
            var doc = TryRead(path);
            if (doc is null)
            {
                continue;
            }

            // The file name is the id (a hand-edited "id" inside the file can't point somewhere else).
            doc.Id = Path.GetFileNameWithoutExtension(path);
            if (!IsValidId(doc.Id))
            {
                // A hand-copied file like "Main copy.json": move it to a proper id so saves and deletes find it.
                doc.Id = DashboardDocument.NewId();
                try
                {
                    Save(doc);
                    File.Delete(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }
            }

            result.Add(doc);
        }

        return [.. result.OrderBy(d => d.Order).ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>Writes the dashboard (via a temp file, so a crash mid-write can't leave a half file).</summary>
    public void Save(DashboardDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (!IsValidId(doc.Id))
        {
            doc.Id = DashboardDocument.NewId();
        }

        System.IO.Directory.CreateDirectory(Directory);
        var path = PathFor(doc.Id);
        var temp = path + ".tmp";
        File.WriteAllText(temp, Serialize(doc));
        File.Move(temp, path, overwrite: true);
    }

    public void Delete(string id)
    {
        if (IsValidId(id) && File.Exists(PathFor(id)))
        {
            File.Delete(PathFor(id));
        }
    }

    /// <summary>Writes a copy for sharing.</summary>
    public static void Export(DashboardDocument doc, string path)
    {
        ArgumentNullException.ThrowIfNull(doc);
        File.WriteAllText(path, Serialize(doc));
    }

    /// <summary>Reads a shared dashboard. It gets a new id so it never replaces an existing one.</summary>
    /// <exception cref="InvalidDataException">The file isn't a SensorDeck dashboard.</exception>
    public static DashboardDocument Import(string path)
    {
        if (new FileInfo(path).Length > MaxImportBytes)
        {
            throw new InvalidDataException("This file is too big to be a SensorDeck dashboard.");
        }

        DashboardDocument? doc;
        try
        {
            doc = Deserialize(File.ReadAllText(path));
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("This file isn't a SensorDeck dashboard.", ex);
        }

        if (doc is null)
        {
            throw new InvalidDataException("This file isn't a SensorDeck dashboard.");
        }

        doc.Tiles = [.. doc.Tiles.Take(MaxTiles)];
        doc.Id = DashboardDocument.NewId();
        return doc;
    }

    public static string Serialize(DashboardDocument doc) => JsonSerializer.Serialize(doc, JsonOptions);

    /// <summary>Parses and repairs a document (missing lists, out-of-range tiles). Null for "null" JSON.</summary>
    public static DashboardDocument? Deserialize(string json)
    {
        var doc = JsonSerializer.Deserialize<DashboardDocument>(json, JsonOptions);
        if (doc is null)
        {
            return null;
        }

        doc.Name = string.IsNullOrWhiteSpace(doc.Name) ? "Dashboard" : doc.Name.Trim();
        doc.Tiles = [.. (doc.Tiles ?? []).Where(t => t is not null)];
        foreach (var tile in doc.Tiles)
        {
            tile.Sensors = [.. (tile.Sensors ?? []).Where(s => !string.IsNullOrEmpty(s))];
            tile.Id = string.IsNullOrEmpty(tile.Id) ? DashboardDocument.NewId() : tile.Id;
            tile.WindowSeconds = Math.Clamp(tile.WindowSeconds, 30, 3600);
            tile.Decimals = tile.Decimals is { } d ? Math.Clamp(d, 0, 4) : null;
            tile.Accent = tile.Accent is { } a ? Math.Clamp(a, 0, 5) : null;
            GridLayout.Clamp(tile);
        }

        return doc;
    }

    /// <summary>Ids are short lowercase hex (see <see cref="DashboardDocument.NewId"/>); anything else is rejected.</summary>
    public static bool IsValidId(string? id) =>
        !string.IsNullOrEmpty(id) && id.Length <= 64 && id.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_');

    private string PathFor(string id) => Path.Combine(Directory, id + ".json");

    private static DashboardDocument? TryRead(string path)
    {
        try
        {
            return Deserialize(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
