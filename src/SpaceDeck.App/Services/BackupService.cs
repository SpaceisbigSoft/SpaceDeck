using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SpaceDeck.Core.Protocol;

namespace SpaceDeck.App.Services;

/// <summary>
/// Backup and restore of everything you set up: dashboards, alert rules, sensor renames/scales, calculated sensors,
/// automation and app settings (one .zip), plus the collector's settings when they can be read.
/// </summary>
/// <remarks>
/// <para>Secrets stay on this PC: paired network-dashboard devices, the pairing PIN and the ntfy access token are
/// left out of backups and never taken from one, and a restore keeps the current ones (the ntfy token only while the
/// restored ntfy server is still this PC's server). Settings that decide what SpaceDeck runs or links to
/// (FanControl's location, the update feed) are this PC's too: a restore keeps the current values, so a backup from
/// someone else can't point FanControl's location at another program.</para>
/// <para>A backup is untrusted input: only known JSON file names (no folders but <c>dashboards/</c>), at most
/// <see cref="MaxEntries"/> files, and sizes checked while decompressing, not just from the zip's own headers.</para>
/// <para>Restoring replaces files the running app holds in memory, so it's done at the next start: the backup is
/// staged as <c>restore-pending.zip</c>, the app restarts, and the new instance waits for the old one to exit before
/// applying it. The previous files are kept in <c>before-restore\&lt;time&gt;</c>.</para>
/// </remarks>
public sealed partial class BackupService
{
    public const string FormatName = "spacedeck-backup";

    /// <summary>The format name backups had before the rename to SpaceDeck (2026-11); still restored.</summary>
    public const string LegacyFormatName = "sensordeck-backup";
    public const int FormatVersion = 1;
    private const string ManifestEntry = "manifest.json";
    private const string CollectorEntry = "collector/collector.json";
    private const string PendingFile = "restore-pending.zip";
    private const string CollectorRestoreFile = "collector-restore.json";
    private const long MaxEntryBytes = 20L * 1024 * 1024;
    private const long MaxTotalBytes = 100L * 1024 * 1024;
    private const int MaxEntries = 2000;

    /// <summary>Top-level settings.json keys a restore never takes from the backup (see the remarks).</summary>
    private static readonly string[] MachineLocalKeys = ["FanControlExePath", "UpdateFeedUrl"];

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private readonly CollectorClient _client;

    public BackupService(CollectorClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;

        // After a restore, hand the backed-up collector settings to the collector once it's reachable.
        client.HelloReceived += hello =>
        {
            if (hello.ProtocolVersion >= ProtocolInfo.SettingsVersion)
            {
                _ = ApplyCollectorRestoreAsync();
            }
        };
    }

    /// <summary>Top-level files that make up "your setup" (logs, recordings and replay profiles are not included).</summary>
    [GeneratedRegex(@"^[A-Za-z0-9 ._-]{1,100}\.json$")]
    private static partial Regex SafeJsonNameShape();

    /// <summary>Windows device names (CON, NUL, COM1…): "NUL.json" would open the device, not a file.</summary>
    [GeneratedRegex(@"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])$", RegexOptions.IgnoreCase)]
    private static partial Regex ReservedDeviceName();

    /// <summary>A plain JSON file name: allowed characters only, and not a reserved device name (the part before the first dot, trailing spaces ignored).</summary>
    private static bool IsSafeJsonName(string name) =>
        SafeJsonNameShape().IsMatch(name) && !ReservedDeviceName().IsMatch(name.Split('.')[0].TrimEnd(' '));

    /// <summary>Writes a backup of the current setup to <paramref name="path"/>. Returns how many files it holds.</summary>
    public static int Create(string path, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Save(); // the newest in-memory state
        var dir = AppSettings.Directory;
        var count = 0;
        var temp = path + ".tmp";
        try
        {
            using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
            {
                Write(zip, ManifestEntry, JsonSerializer.Serialize(new
                {
                    format = FormatName,
                    version = FormatVersion,
                    app = ViewModels.DiagnosticsViewModel.AppVersion,
                    created = DateTimeOffset.Now,
                    machine = Environment.MachineName,
                }, Indented));

                foreach (var file in SetupFiles(dir))
                {
                    var name = Path.GetRelativePath(dir, file).Replace('\\', '/');
                    var text = File.ReadAllText(file);
                    if (name.Equals("settings.json", StringComparison.OrdinalIgnoreCase))
                    {
                        text = WithoutSecrets(text);
                    }

                    Write(zip, name, text);
                    count++;
                }

                if (ReadCollectorConfig() is { } collector)
                {
                    Write(zip, CollectorEntry, collector);
                }
            }

            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            TryDelete(temp); // only left over when something failed
        }

        return count;
    }

    /// <summary>
    /// Checks <paramref name="path"/> is a SpaceDeck backup and stages it for the next start.
    /// Throws <see cref="InvalidDataException"/> with a readable reason if it isn't usable.
    /// </summary>
    public static BackupInfo Stage(string path)
    {
        var info = Inspect(path);
        Directory.CreateDirectory(AppSettings.Directory);
        File.Copy(path, Path.Combine(AppSettings.Directory, PendingFile), overwrite: true);
        return info;
    }

    /// <summary>Reads a backup's manifest and checks every entry, without changing anything.</summary>
    public static BackupInfo Inspect(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var manifest = zip.GetEntry(ManifestEntry) ?? throw new InvalidDataException("That isn't a SpaceDeck backup (no manifest).");
            using var doc = JsonDocument.Parse(ReadEntry(manifest));
            var root = doc.RootElement;
            if (!root.TryGetProperty("format", out var format) || format.GetString() is not (FormatName or LegacyFormatName))
            {
                throw new InvalidDataException("That isn't a SpaceDeck backup.");
            }

            if (root.TryGetProperty("version", out var version) && version.GetInt32() > FormatVersion)
            {
                throw new InvalidDataException("This backup was made by a newer SpaceDeck. Update SpaceDeck to restore it.");
            }

            if (zip.Entries.Count > MaxEntries)
            {
                throw new InvalidDataException("The backup holds unexpectedly many files; it won't be restored.");
            }

            long total = 0;
            var files = 0;
            var hasSettings = false;
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.EndsWith('/'))
                {
                    continue; // folder entry
                }

                if (!IsAllowed(entry.FullName))
                {
                    throw new InvalidDataException($"The backup contains an unexpected file ({entry.FullName}); it won't be restored.");
                }

                // ReadEntry also stops at MaxEntryBytes, whatever the entry's header claims.
                var text = ReadEntry(entry);
                total += text.Length;
                if (total > MaxTotalBytes)
                {
                    throw new InvalidDataException("The backup is unexpectedly large; it won't be restored.");
                }

                // collector.json may carry comments and trailing commas: the collector reads it leniently, and so does
                // the restore (ApplyCollectorRestoreAsync), so the user's own backup must not be refused as damaged.
                var parseOptions = entry.FullName == CollectorEntry
                    ? new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }
                    : default;
                using (var json = JsonDocument.Parse(text, parseOptions))
                {
                    // Every file must be valid JSON, and a dashboard may hold no more tiles than an imported one keeps
                    // (restored dashboards are loaded as they are).
                    if (entry.FullName.StartsWith("dashboards/", StringComparison.Ordinal)
                        && json.RootElement.ValueKind == JsonValueKind.Object
                        && json.RootElement.TryGetProperty("tiles", out var tiles)
                        && tiles.ValueKind == JsonValueKind.Array
                        && tiles.GetArrayLength() > SpaceDeck.Core.Dashboards.DashboardRepository.MaxTiles)
                    {
                        throw new InvalidDataException($"A dashboard in the backup ({entry.FullName}) has more than {SpaceDeck.Core.Dashboards.DashboardRepository.MaxTiles} tiles; it won't be restored.");
                    }
                }

                if (entry.FullName != ManifestEntry)
                {
                    files++;
                }

                hasSettings |= entry.FullName.Equals("settings.json", StringComparison.OrdinalIgnoreCase);
            }

            if (!hasSettings)
            {
                throw new InvalidDataException("The backup has no settings.json.");
            }

            DateTimeOffset? created = root.TryGetProperty("created", out var c) && c.TryGetDateTimeOffset(out var when) ? when : null;
            var app = root.TryGetProperty("app", out var a) ? a.GetString() : null;
            var machine = root.TryGetProperty("machine", out var m) ? m.GetString() : null;
            return new BackupInfo(created, app, machine, files);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException($"The backup is damaged: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// At startup, before settings load: applies a staged restore, if any. Returns a message for the user, or null
    /// when there was nothing to restore.
    /// </summary>
    public static string? ApplyPendingRestore()
    {
        var dir = AppSettings.Directory;
        var pending = Path.Combine(dir, PendingFile);
        if (!File.Exists(pending))
        {
            return null;
        }

        var keep = Path.Combine(dir, "before-restore", DateTime.Now.ToString("yyyy-MM-dd HHmmss", CultureInfo.InvariantCulture));
        var moved = new List<(string From, string To)>();
        var written = new List<string>();
        try
        {
            Inspect(pending); // still valid?
            var currentSettings = File.Exists(Path.Combine(dir, "settings.json")) ? File.ReadAllText(Path.Combine(dir, "settings.json")) : null;

            // Keep the current setup aside (moved, so files the backup doesn't have don't linger).
            foreach (var file in SetupFiles(dir).ToList())
            {
                var target = Path.Combine(keep, Path.GetRelativePath(dir, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(file, target);
                moved.Add((file, target));
            }

            using (var zip = ZipFile.OpenRead(pending))
            {
                foreach (var entry in zip.Entries.Where(e => !e.FullName.EndsWith('/') && e.FullName != ManifestEntry))
                {
                    var text = ReadEntry(entry);
                    if (entry.FullName == CollectorEntry)
                    {
                        var collectorFile = Path.Combine(dir, CollectorRestoreFile);
                        written.Add(collectorFile);
                        File.WriteAllText(collectorFile, text);
                        continue;
                    }

                    if (entry.FullName.Equals("settings.json", StringComparison.OrdinalIgnoreCase))
                    {
                        text = WithSecretsFrom(text, currentSettings);
                    }

                    var target = Path.Combine(dir, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    written.Add(target);
                    File.WriteAllText(target, text);
                }
            }

            File.Delete(pending);
            return $"Restored your setup from the backup. Your previous files are in {keep}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException
                                       or ArgumentException or NotSupportedException)
        {
            // ArgumentException: e.g. a duplicate key in the backup's settings.json. This runs at startup with the
            // backup still staged, so anything unexpected escaping here would crash every start.
            // Put the previous setup back so a failed restore never leaves the user with nothing: first remove what
            // the restore wrote (including files only the backup had), then move the originals back.
            foreach (var path in written)
            {
                TryDelete(path);
            }

            foreach (var (from, to) in moved)
            {
                try
                {
                    File.Move(to, from, overwrite: true);
                }
                catch (Exception undo) when (undo is IOException or UnauthorizedAccessException)
                {
                    // Still in before-restore for manual recovery.
                }
            }

            try
            {
                File.Delete(pending);
            }
            catch (Exception del) when (del is IOException or UnauthorizedAccessException)
            {
                // Tried; the next start reports again.
            }

            return $"Couldn't restore the backup: {ex.Message} Nothing was changed.";
        }
    }

    /// <summary>Starts a new SpaceDeck that waits for this one to exit (then applies the staged restore).</summary>
    public static bool Restart()
    {
        if (Environment.ProcessPath is not { } exe)
        {
            return false;
        }

        try
        {
            var start = new ProcessStartInfo { FileName = exe, UseShellExecute = false };
            start.ArgumentList.Add(AppMode.WaitForArgument);
            start.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            Process.Start(start)?.Dispose();
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Sends restored collector settings (poll interval, sensor groups) and saves them on the collector side.</summary>
    private async Task ApplyCollectorRestoreAsync()
    {
        var path = Path.Combine(AppSettings.Directory, CollectorRestoreFile);
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            var root = JsonNode.Parse(await File.ReadAllTextAsync(path).ConfigureAwait(false),
                new JsonNodeOptions { PropertyNameCaseInsensitive = true },
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var section = root?["Collector"];
            var request = new SetConfigRequest(
                Int(section?["PollIntervalMs"]),
                Bool(section?["StorageEnabled"]),
                Bool(section?["NetworkEnabled"]),
                Bool(section?["ControllersEnabled"]),
                Persist: true);
            if (request.PollIntervalMs is not null || request.StorageEnabled is not null || request.NetworkEnabled is not null || request.ControllersEnabled is not null)
            {
                await _client.SetConfigAsync(request).ConfigureAwait(false);
            }

            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            // Not connected after all: try again on the next connection. A damaged file is dropped.
            if (ex is JsonException)
            {
                TryDelete(path);
            }
        }

        static int? Int(JsonNode? node) => node is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;

        static bool? Bool(JsonNode? node) => node is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort.
        }
    }

    /// <summary>settings.json, sensors.json, any other top-level *.json, and dashboards\*.json.</summary>
    private static IEnumerable<string> SetupFiles(string dir)
    {
        if (!Directory.Exists(dir))
        {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            var name = Path.GetFileName(file);
            if (IsSafeJsonName(name) && !name.Equals(CollectorRestoreFile, StringComparison.OrdinalIgnoreCase))
            {
                yield return file;
            }
        }

        var dashboards = Path.Combine(dir, "dashboards");
        if (Directory.Exists(dashboards))
        {
            foreach (var file in Directory.EnumerateFiles(dashboards, "*.json"))
            {
                if (IsSafeJsonName(Path.GetFileName(file)))
                {
                    yield return file;
                }
            }
        }
    }

    private static bool IsAllowed(string entry)
    {
        if (entry == ManifestEntry || entry == CollectorEntry)
        {
            return true;
        }

        var parts = entry.Split('/');
        return parts switch
        {
            [var name] => IsSafeJsonName(name) && !name.Equals(CollectorRestoreFile, StringComparison.OrdinalIgnoreCase),
            ["dashboards", var name] => IsSafeJsonName(name),
            _ => false,
        };
    }

    private static string? ReadCollectorConfig()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SpaceDeck", "collector.json");
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var text = File.ReadAllText(path);
            using (JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }))
            {
                return text;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null; // optional
        }
    }

    /// <summary>Drops paired devices, the pairing PIN and the ntfy access token.</summary>
    internal static string WithoutSecrets(string settingsJson)
    {
        if (JsonNode.Parse(settingsJson) is not JsonObject root)
        {
            return settingsJson;
        }

        if (root["Remote"] is JsonObject remote)
        {
            remote.Remove("Devices");
            remote.Remove("Pin");
        }

        if (root["Ntfy"] is JsonObject ntfy)
        {
            ntfy.Remove("AccessToken");
        }

        return root.ToJsonString(Indented);
    }

    /// <summary>Restored settings, with this PC's paired devices, PIN and ntfy token carried over.</summary>
    internal static string WithSecretsFrom(string restoredJson, string? currentJson)
    {
        if (JsonNode.Parse(restoredJson) is not JsonObject restored)
        {
            throw new InvalidDataException("The backup's settings.json isn't a settings file.");
        }

        JsonObject? current = null;
        try
        {
            current = currentJson is null ? null : JsonNode.Parse(currentJson) as JsonObject;
        }
        catch (JsonException)
        {
            // Current settings unreadable: nothing to carry over.
        }

        // Secrets never come from the backup, whether or not this PC has any yet (a fresh install is the usual time to
        // restore): a crafted backup's paired devices or PIN would let its author use the network dashboard.
        if (restored["Remote"] is JsonObject restoredRemote)
        {
            restoredRemote.Remove("Devices");
            restoredRemote.Remove("Pin");
        }

        if (restored["Ntfy"] is JsonObject restoredNtfy)
        {
            restoredNtfy.Remove("AccessToken");
        }

        if (current?["Remote"] is JsonObject currentRemote)
        {
            if (restored["Remote"] is not JsonObject remote)
            {
                remote = new JsonObject();
                restored["Remote"] = remote;
            }

            foreach (var key in new[] { "Devices", "Pin" })
            {
                if (currentRemote[key] is { } value)
                {
                    remote[key] = value.DeepClone();
                }
            }
        }

        // This PC's ntfy token goes only to this PC's ntfy server: with another server from the backup it would be sent
        // there with the next alert, so it is dropped (the user enters it again for the new server).
        if (current?["Ntfy"] is JsonObject currentNtfy && currentNtfy["AccessToken"] is { } token
            && SameServer(restored["Ntfy"] as JsonObject, currentNtfy))
        {
            if (restored["Ntfy"] is not JsonObject ntfy)
            {
                ntfy = new JsonObject();
                restored["Ntfy"] = ntfy;
            }

            ntfy["AccessToken"] = token.DeepClone();
        }

        foreach (var key in MachineLocalKeys)
        {
            restored.Remove(key);
            if (current?[key] is { } value)
            {
                restored[key] = value.DeepClone();
            }
        }

        // The restored setup is already set up: don't show the first-run checklist again.
        restored["SetupCompleted"] = true;
        return restored.ToJsonString(Indented);

        // A missing Server means the default one (that's what loading the settings does).
        static bool SameServer(JsonObject? a, JsonObject b) =>
            string.Equals(ServerOf(a), ServerOf(b), StringComparison.OrdinalIgnoreCase);

        static string ServerOf(JsonObject? section) =>
            (section?["Server"] is JsonValue node && node.TryGetValue<string>(out var url) ? url : NtfySettings.DefaultServer)
                .Trim().TrimEnd('/');
    }

    private static void Write(ZipArchive zip, string name, string text)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(text);
    }

    /// <summary>Reads an entry as text, stopping past <see cref="MaxEntryBytes"/> characters (a zip bomb can lie about its size).</summary>
    private static string ReadEntry(ZipArchiveEntry entry)
    {
        if (entry.Length > MaxEntryBytes)
        {
            throw new InvalidDataException("A file in the backup is unexpectedly large.");
        }

        using var reader = new StreamReader(entry.Open());
        var text = new StringBuilder();
        var buffer = new char[81920];
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            text.Append(buffer, 0, read);
            if (text.Length > MaxEntryBytes)
            {
                throw new InvalidDataException("A file in the backup is unexpectedly large.");
            }
        }

        return text.ToString();
    }
}

/// <summary>What a backup holds, for the confirmation message.</summary>
public sealed record BackupInfo(DateTimeOffset? Created, string? AppVersion, string? Machine, int Files);
