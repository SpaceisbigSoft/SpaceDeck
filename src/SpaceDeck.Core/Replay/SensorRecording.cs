using System.Buffers;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SpaceDeck.Core.Formatting;
using SpaceDeck.Core.Model;

namespace SpaceDeck.Core.Replay;

/// <summary>One recorded poll: values in the order of <see cref="SensorRecording.Catalogs"/>[<see cref="CatalogIndex"/>].</summary>
/// <param name="OffsetMs">Milliseconds since the first frame.</param>
public sealed record RecordingFrame(long OffsetMs, int CatalogIndex, float?[] Values);

/// <summary>
/// A recorded sensor stream (<c>.sdrec</c>) or a single-snapshot hardware report, loaded for replay.
/// </summary>
/// <remarks>
/// <para><c>.sdrec</c> files are gzip-compressed JSON lines: a header line, then <c>{"catalog":…}</c> lines whenever the
/// sensor list changes and <c>{"t":ms,"v":[…]}</c> lines for each poll. Non-finite values are written as null.</para>
/// <para>Hardware reports (Diagnostics → Save hardware report, or <c>spacedeck-cli json</c>) load as one frame.</para>
/// </remarks>
public sealed class SensorRecording
{
    public const string FileExtension = ".sdrec";
    public const string FormatName = "spacedeck-recording";

    /// <summary>The format name recordings had before the rename to SpaceDeck (2026-11); still read.</summary>
    public const string LegacyFormatName = "sensordeck-recording";
    public const int FormatVersion = 1;

    /// <summary>Guards against absurd files: at most this many frames and sensors are loaded.</summary>
    public const int MaxFrames = 200_000;
    public const int MaxSensors = 20_000;

    /// <summary>At most this many catalog lines are loaded (a real recording has a handful).</summary>
    public const int MaxCatalogs = 1_000;

    /// <summary>
    /// At most this many values (frames × sensors, about 256 MB in memory) are loaded; a longer recording is cut there,
    /// like at <see cref="MaxFrames"/>. A gzip file can expand enormously, so the limits apply to what is decompressed.
    /// </summary>
    public const long MaxTotalValues = 32_000_000;

    /// <summary>Longest line read; a catalog of <see cref="MaxSensors"/> sensors fits easily. Longer means a damaged file.</summary>
    public const int MaxLineChars = 16 * 1024 * 1024;

    /// <summary>Latest frame offset kept (a year; recordings last at most an hour). Larger values are damage and are clamped.</summary>
    public const long MaxOffsetMs = 366L * 24 * 3_600_000;

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public SensorRecording(IReadOnlyList<SensorCatalog> catalogs, IReadOnlyList<RecordingFrame> frames, string? description = null, DateTimeOffset? created = null)
    {
        ArgumentNullException.ThrowIfNull(catalogs);
        ArgumentNullException.ThrowIfNull(frames);
        if (frames.Count == 0)
        {
            throw new InvalidDataException("The recording has no samples.");
        }

        Catalogs = catalogs;
        Frames = frames;
        Description = description;
        Created = created;
    }

    public IReadOnlyList<SensorCatalog> Catalogs { get; }

    public IReadOnlyList<RecordingFrame> Frames { get; }

    /// <summary>What was recorded (machine name, notes), if the file says.</summary>
    public string? Description { get; }

    public DateTimeOffset? Created { get; }

    public long DurationMs => Frames[^1].OffsetMs;

    /// <summary>A single frame (a hardware report): replay holds the values with a little movement.</summary>
    public bool IsSnapshot => Frames.Count == 1;

    /// <summary>Loads an <c>.sdrec</c> recording or a JSON hardware report (detected from the content, not the name).</summary>
    /// <exception cref="InvalidDataException">Not a recording or hardware report, or damaged.</exception>
    /// <exception cref="IOException">The file couldn't be read.</exception>
    public static SensorRecording Load(string path)
    {
        using var stream = File.OpenRead(path);
        var first = stream.ReadByte();
        var second = stream.ReadByte();
        stream.Position = 0;
        if (first == 0x1F && second == 0x8B)
        {
            return Read(stream);
        }

        if (stream.Length > 64L * 1024 * 1024)
        {
            throw new InvalidDataException("That file is too big to be a hardware report.");
        }

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return HardwareReport.Parse(reader.ReadToEnd());
    }

    /// <summary>Reads a gzip-compressed <c>.sdrec</c> stream.</summary>
    /// <exception cref="InvalidDataException">Not a SpaceDeck recording, or damaged.</exception>
    public static SensorRecording Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        try
        {
            using var gzip = new GZipStream(stream, CompressionMode.Decompress, leaveOpen: true);
            using var reader = new StreamReader(gzip, Encoding.UTF8);

            var lineBuffer = new StringBuilder();
            var header = ReadHeader(ReadLine(reader, lineBuffer));
            var catalogs = new List<SensorCatalog>();
            var frames = new List<RecordingFrame>();
            long totalValues = 0;
            string? line;
            while ((line = ReadLine(reader, lineBuffer)) is not null && frames.Count < MaxFrames && totalValues < MaxTotalValues)
            {
                if (line.Length == 0)
                {
                    continue;
                }

                JsonDocument doc;
                try
                {
                    doc = JsonDocument.Parse(line);
                }
                catch (JsonException) when (frames.Count > 0)
                {
                    break; // cut off mid-line (recording stopped by a crash or a full disk): keep what came before
                }

                using var _ = doc;
                var root = doc.RootElement;
                if (root.TryGetProperty("catalog", out var catalogElement))
                {
                    if (catalogs.Count >= MaxCatalogs)
                    {
                        break; // keep what came before, like the frame limit
                    }

                    var catalog = Sanitize(catalogElement.Deserialize<SensorCatalog>(Json) ?? throw new InvalidDataException("Empty catalog line."));
                    totalValues += catalog.Sensors.Count;
                    catalogs.Add(catalog);
                }
                else if (root.TryGetProperty("t", out var t) && root.TryGetProperty("v", out var v) && catalogs.Count > 0)
                {
                    var count = catalogs[^1].Sensors.Count;
                    var values = new float?[count];
                    var i = 0;
                    foreach (var item in v.EnumerateArray())
                    {
                        if (i >= count)
                        {
                            break;
                        }

                        values[i++] = item.ValueKind == JsonValueKind.Number && item.TryGetSingle(out var f) && float.IsFinite(f) ? f : null;
                    }

                    frames.Add(new RecordingFrame(Math.Clamp(t.GetInt64(), 0, MaxOffsetMs), catalogs.Count - 1, values));
                    totalValues += count;
                }
            }

            if (frames.Count == 0)
            {
                throw new InvalidDataException("The recording has no samples (it may have been stopped right away).");
            }

            // Offsets must not go backwards (replay searches them).
            for (var i = 1; i < frames.Count; i++)
            {
                if (frames[i].OffsetMs < frames[i - 1].OffsetMs)
                {
                    frames[i] = frames[i] with { OffsetMs = frames[i - 1].OffsetMs };
                }
            }

            return new SensorRecording(catalogs, frames, header.Description, header.Created);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or KeyNotFoundException or NotSupportedException
                                       or ArgumentOutOfRangeException)
        {
            throw new InvalidDataException($"The recording is damaged: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// <see cref="TextReader.ReadLine"/> with a length limit, so a file without line breaks can't make one huge string.
    /// </summary>
    /// <exception cref="InvalidDataException">A line is longer than <see cref="MaxLineChars"/>.</exception>
    private static string? ReadLine(TextReader reader, StringBuilder buffer)
    {
        buffer.Clear();
        while (true)
        {
            var c = reader.Read();
            if (c < 0)
            {
                return buffer.Length > 0 ? buffer.ToString() : null;
            }

            if (c == '\n')
            {
                if (buffer.Length > 0 && buffer[buffer.Length - 1] == '\r')
                {
                    buffer.Length--;
                }

                return buffer.ToString();
            }

            if (buffer.Length >= MaxLineChars)
            {
                throw new InvalidDataException("The recording is damaged: a line is far too long.");
            }

            buffer.Append((char)c);
        }
    }

    /// <summary>
    /// Checks a catalog read from a file: lists present, every sensor has an id, no missing names (the collector and app
    /// assume these are never null), not too many sensors. Hardware entries without an id are dropped.
    /// </summary>
    /// <exception cref="InvalidDataException">The catalog can't be used.</exception>
    internal static SensorCatalog Sanitize(SensorCatalog catalog)
    {
        if (catalog.Hardware is null || catalog.Sensors is null)
        {
            throw new InvalidDataException("A catalog line has no hardware or sensor list.");
        }

        if (catalog.Sensors.Count > MaxSensors)
        {
            throw new InvalidDataException("The recording has too many sensors.");
        }

        var hardware = new List<HardwareInfo>(catalog.Hardware.Count);
        foreach (var h in catalog.Hardware)
        {
            if (h is not null && !string.IsNullOrEmpty(h.Id))
            {
                hardware.Add(h with { Name = h.Name ?? h.Id, Source = h.Source ?? "replay" });
            }
        }

        var sensors = new List<SensorInfo>(catalog.Sensors.Count);
        foreach (var s in catalog.Sensors)
        {
            if (s is null || string.IsNullOrEmpty(s.Id))
            {
                // Values are matched by position, so a sensor can't just be skipped.
                throw new InvalidDataException("A sensor in the recording has no id.");
            }

            sensors.Add(s with { HardwareId = s.HardwareId ?? "", Name = s.Name ?? s.Id, Unit = s.Unit ?? "", Source = s.Source ?? "replay" });
        }

        return catalog with { Hardware = hardware, Sensors = sensors };
    }

    private static (string? Description, DateTimeOffset? Created) ReadHeader(string? line)
    {
        if (string.IsNullOrEmpty(line))
        {
            throw new InvalidDataException("Not a SpaceDeck recording.");
        }

        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        if (!root.TryGetProperty("format", out var format) || format.GetString() is not (FormatName or LegacyFormatName))
        {
            throw new InvalidDataException("Not a SpaceDeck recording.");
        }

        if (root.TryGetProperty("version", out var version) && version.GetInt32() > FormatVersion)
        {
            throw new InvalidDataException("This recording was made by a newer SpaceDeck. Update SpaceDeck to replay it.");
        }

        var description = root.TryGetProperty("description", out var d) ? d.GetString() : null;
        DateTimeOffset? created = root.TryGetProperty("createdUnixMs", out var c) && c.TryGetInt64(out var ms)
                                  && ms is >= -62_135_596_800_000L and <= 253_402_300_799_999L // what DateTimeOffset can hold
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
            : null;
        return (description, created);
    }
}

/// <summary>
/// Writes an <c>.sdrec</c> recording. Not thread-safe: call from one thread at a time (or lock around it).
/// </summary>
public sealed class SensorRecordingWriter : IDisposable
{
    private static readonly byte[] NewLine = "\n"u8.ToArray();
    private readonly GZipStream _gzip;
    private readonly ArrayBufferWriter<byte> _buffer = new(16 * 1024);
    private SensorCatalog? _catalog;
    private long _firstUnixMs = -1;
    private bool _disposed;

    public SensorRecordingWriter(Stream stream, string? description = null, int intervalMs = 1000)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _gzip = new GZipStream(stream, CompressionLevel.Optimal, leaveOpen: false);
        WriteLine(w =>
        {
            w.WriteStartObject();
            w.WriteString("format", SensorRecording.FormatName);
            w.WriteNumber("version", SensorRecording.FormatVersion);
            w.WriteNumber("createdUnixMs", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            w.WriteNumber("intervalMs", intervalMs);
            if (description is not null)
            {
                w.WriteString("description", description);
            }

            w.WriteEndObject();
        });
    }

    /// <summary>Samples written so far.</summary>
    public int FrameCount { get; private set; }

    /// <summary>Time between the first and the last sample.</summary>
    public long DurationMs { get; private set; }

    /// <summary>Starts using <paramref name="catalog"/> for the samples that follow (skipped if it's the one already in use).</summary>
    public void WriteCatalog(SensorCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (ReferenceEquals(catalog, _catalog))
        {
            return;
        }

        _catalog = catalog;
        WriteLine(w =>
        {
            w.WriteStartObject();
            w.WritePropertyName("catalog");
            JsonSerializer.Serialize(w, catalog, SensorRecording.Json);
            w.WriteEndObject();
        });
    }

    /// <summary>Writes one sample, in the order of the last catalog. Ignored before the first catalog.</summary>
    public void WriteValues(long timestampUnixMs, ReadOnlySpan<float?> values)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_catalog is null)
        {
            return;
        }

        if (_firstUnixMs < 0)
        {
            _firstUnixMs = timestampUnixMs;
        }

        var offset = Math.Max(0, timestampUnixMs - _firstUnixMs);
        var count = Math.Min(values.Length, _catalog.Sensors.Count);
        _buffer.ResetWrittenCount();
        using (var w = new Utf8JsonWriter(_buffer))
        {
            w.WriteStartObject();
            w.WriteNumber("t", offset);
            w.WriteStartArray("v");
            for (var i = 0; i < count; i++)
            {
                if (values[i] is { } v && float.IsFinite(v))
                {
                    // Four significant decimals are plenty for sensors and keep files small.
                    w.WriteRawValue(Math.Round(v, 4).ToString("R", CultureInfo.InvariantCulture), skipInputValidation: true);
                }
                else
                {
                    w.WriteNullValue();
                }
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        _gzip.Write(_buffer.WrittenSpan);
        _gzip.Write(NewLine);
        FrameCount++;
        DurationMs = offset;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gzip.Dispose();
    }

    private void WriteLine(Action<Utf8JsonWriter> write)
    {
        _buffer.ResetWrittenCount();
        using (var w = new Utf8JsonWriter(_buffer))
        {
            write(w);
        }

        _gzip.Write(_buffer.WrittenSpan);
        _gzip.Write(NewLine);
    }
}

/// <summary>Reads hardware reports (the app's Diagnostics report or <c>spacedeck-cli json</c>) as a one-frame recording.</summary>
public static class HardwareReport
{
    /// <exception cref="InvalidDataException">Not a hardware report.</exception>
    public static SensorRecording Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("Not a hardware report.");
            }

            var hardware = new List<HardwareInfo>();
            var sensors = new List<SensorInfo>();
            var values = new List<float?>();
            var seenHardware = new HashSet<string>(StringComparer.Ordinal);
            var seenSensors = new HashSet<string>(StringComparer.Ordinal);

            if (Get(root, "hardware") is { ValueKind: JsonValueKind.Array } hardwareArray)
            {
                // spacedeck-cli json: hardware[] with nested sensors[].
                foreach (var h in hardwareArray.EnumerateArray())
                {
                    var id = Text(h, "id");
                    if (id is null || !seenHardware.Add(id))
                    {
                        continue;
                    }

                    hardware.Add(new HardwareInfo(id, Text(h, "name") ?? id, Enum<HardwareKind>(h, "kind", HardwareKind.Other), Text(h, "parentId"), SourceOf(id)));
                    if (Get(h, "sensors") is { ValueKind: JsonValueKind.Array } list)
                    {
                        foreach (var s in list.EnumerateArray())
                        {
                            AddSensor(s, id);
                        }
                    }
                }
            }
            else if (Get(root, "sensors") is { ValueKind: JsonValueKind.Array } sensorArray)
            {
                // The app's report: a flat sensors[] with the hardware's name and kind on each sensor.
                foreach (var s in sensorArray.EnumerateArray())
                {
                    var id = Text(s, "id");
                    if (id is null || id.StartsWith("calc:", StringComparison.Ordinal))
                    {
                        continue; // calculated sensors are the app's own (see AddSensor)
                    }

                    var hardwareId = Text(s, "hardwareId") ?? HardwareIdOf(id);
                    if (seenHardware.Add(hardwareId))
                    {
                        hardware.Add(new HardwareInfo(hardwareId, Text(s, "hardware") ?? hardwareId, Enum<HardwareKind>(s, "hardwareKind", HardwareKind.Other), null, SourceOf(hardwareId)));
                    }

                    AddSensor(s, hardwareId);
                }
            }
            else
            {
                throw new InvalidDataException("Not a hardware report (no hardware or sensors list).");
            }

            if (sensors.Count == 0)
            {
                throw new InvalidDataException("The hardware report has no sensors.");
            }

            var description = Text(root, "board") is { } board ? $"Hardware report: {board}" : "Hardware report";
            DateTimeOffset? created = Get(root, "generated") is { ValueKind: JsonValueKind.String } g && g.TryGetDateTimeOffset(out var when) ? when
                : Get(root, "generatedAt") is { ValueKind: JsonValueKind.String } g2 && g2.TryGetDateTimeOffset(out var when2) ? when2
                : null;

            var catalog = new SensorCatalog(1, hardware, sensors);
            return new SensorRecording([catalog], [new RecordingFrame(0, 0, [.. values])], description, created);

            void AddSensor(JsonElement s, string hardwareId)
            {
                var id = Text(s, "id");
                // Calculated sensors are the app's own; they're recreated from settings, not replayed.
                if (id is null || id.StartsWith("calc:", StringComparison.Ordinal) || !seenSensors.Add(id) || sensors.Count >= SensorRecording.MaxSensors)
                {
                    return;
                }

                var kind = Enum<SensorKind>(s, "kind", SensorKind.Other);
                var nominal = Number(s, "nominalValue") ?? Number(s, "nominal");
                sensors.Add(new SensorInfo(id, hardwareId, Text(s, "name") ?? id, kind, Text(s, "unit") ?? Units.For(kind), SourceOf(id), nominal));
                values.Add(Number(s, "value") is { } v ? (float)v : null);
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Not a hardware report: {ex.Message}", ex);
        }
    }

    /// <summary>"lhm:/amdcpu/0/temperature/2" → "lhm:/amdcpu/0" (sensor ids end in /type/index).</summary>
    internal static string HardwareIdOf(string sensorId)
    {
        var end = sensorId.Length;
        for (var cut = 0; cut < 2; cut++)
        {
            var slash = sensorId.LastIndexOf('/', end - 1);
            if (slash <= 0)
            {
                return sensorId;
            }

            end = slash;
        }

        return sensorId[..end];
    }

    private static string SourceOf(string id)
    {
        var colon = id.IndexOf(':', StringComparison.Ordinal);
        return colon > 0 ? id[..colon] : "replay";
    }

    /// <summary>Case-insensitive property lookup (the app writes PascalCase, the CLI camelCase).</summary>
    private static JsonElement? Get(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var p in obj.EnumerateObject())
        {
            if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return p.Value;
            }
        }

        return null;
    }

    private static string? Text(JsonElement obj, string name) =>
        Get(obj, name) is { ValueKind: JsonValueKind.String } e ? e.GetString() : null;

    private static double? Number(JsonElement obj, string name) => Get(obj, name) switch
    {
        { ValueKind: JsonValueKind.Number } e when e.TryGetDouble(out var d) && double.IsFinite(d) => d,
        _ => null, // null, "NaN" and anything else
    };

    private static T Enum<T>(JsonElement obj, string name, T fallback)
        where T : struct, System.Enum => Get(obj, name) switch
        {
            { ValueKind: JsonValueKind.String } e when System.Enum.TryParse<T>(e.GetString(), ignoreCase: true, out var v) => v,
            { ValueKind: JsonValueKind.Number } e when e.TryGetInt32(out var n) && System.Enum.IsDefined(typeof(T), n) => (T)System.Enum.ToObject(typeof(T), n),
            _ => fallback,
        };
}
