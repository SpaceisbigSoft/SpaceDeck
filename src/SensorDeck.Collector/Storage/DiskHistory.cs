using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using SensorDeck.Core.History;

namespace SensorDeck.Collector.Storage;

/// <summary>Which on-disk history a query reads.</summary>
public enum HistoryTier
{
    /// <summary>Every sample (kept <see cref="DiskHistoryOptions.RawHours"/>).</summary>
    Raw,

    /// <summary>One min/avg/max row per minute (kept <see cref="DiskHistoryOptions.MinuteDays"/>).</summary>
    Minute,
}

/// <summary>Where disk history is kept and how much (the collector clamps the configured values).</summary>
public sealed class DiskHistoryOptions
{
    public string Directory { get; init; } = "";

    public int RawHours { get; init; } = 24;

    public int MinuteDays { get; init; } = 7;

    public long MaxBytes { get; init; } = 1024L * 1024 * 1024;
}

/// <summary>
/// History on disk so it survives restarts and reaches back further than the in-memory hour:
/// <list type="bullet">
/// <item>raw: every sample, one file per UTC hour (<c>raw-yyyyMMdd-HH-hash.sdh</c>), kept 24 h;</item>
/// <item>minute: min/avg/max per minute, one file per UTC day (<c>min-yyyyMMdd-hash.sdh</c>), kept 7 days.</item>
/// </list>
/// Files start with the list of sensor ids (the hash identifies that list), followed by fixed-size records:
/// Int64 Unix ms + Float32 per sensor (raw) or 3 × Float32 per sensor (minute). Appended on the poll thread; read by
/// pipe sessions concurrently (writers flush before a read). Oldest files are deleted past the retention or size cap.
/// </summary>
public sealed class DiskHistory : IDisposable
{
    private const string Magic = "SDH1";
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(5);

    private readonly DiskHistoryOptions _options;
    private readonly Lock _lock = new();
    private string[] _ids = [];
    private string _hash = "";
    private Segment? _raw;
    private Segment? _minute;
    private long _lastFlush;
    private long _lastCleanupHour = long.MinValue;

    // Current minute's aggregate.
    private long _minuteStart = long.MinValue;
    private float[] _minLow = [];
    private float[] _minHigh = [];
    private double[] _minSum = [];
    private int[] _minCount = [];

    public DiskHistory(DiskHistoryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        System.IO.Directory.CreateDirectory(options.Directory);
    }

    /// <summary>Oldest raw sample kept (by retention), for choosing a tier.</summary>
    public long RawHorizonUnixMs(long nowUnixMs) => nowUnixMs - (_options.RawHours * 3_600_000L);

    /// <summary>Starts writing a new column layout (the catalog changed).</summary>
    public void SetSensors(IReadOnlyList<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        lock (_lock)
        {
            Guard(FlushMinute);
            _ids = [.. ids];
            _hash = Hash(_ids);
            SafeDispose(ref _raw);
            SafeDispose(ref _minute);
            ResetMinute(long.MinValue);
        }
    }

    /// <summary>Appends one sample row (values in the order given to <see cref="SetSensors"/>).</summary>
    public void Append(long timestampUnixMs, ReadOnlySpan<float?> values)
    {
        lock (_lock)
        {
            if (values.Length != _ids.Length || _ids.Length == 0)
            {
                return;
            }

            try
            {
                AppendLocked(timestampUnixMs, values);
                LastError = null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Disk full, folder not writable…: keep collecting; try again (new files) next sample.
                LastError = ex.Message;
                SafeDispose(ref _raw);
                SafeDispose(ref _minute);
            }
        }
    }

    /// <summary>The last write problem, or null when writing works.</summary>
    public string? LastError { get; private set; }

    private void Guard(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastError = ex.Message;
        }
    }

    private void AppendLocked(long timestampUnixMs, ReadOnlySpan<float?> values)
    {
        var hour = timestampUnixMs / 3_600_000L;
        if (_raw is null || _raw.Period != hour)
        {
            SafeDispose(ref _raw);
            _raw = Segment.Open(_options.Directory, HistoryTier.Raw, hour, _ids, _hash);
        }

        var record = new byte[8 + (values.Length * 4)];
        BinaryPrimitives.WriteInt64LittleEndian(record, timestampUnixMs);
        for (var i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(record.AsSpan(8 + (i * 4)), values[i] ?? float.NaN);
        }

        _raw.Write(record);

        // Minute aggregate.
        var minute = timestampUnixMs / 60_000L * 60_000L;
        if (minute != _minuteStart)
        {
            FlushMinute();
            ResetMinute(minute);
        }

        for (var i = 0; i < values.Length; i++)
        {
            if (values[i] is { } v && !float.IsNaN(v))
            {
                _minLow[i] = float.IsNaN(_minLow[i]) ? v : Math.Min(_minLow[i], v);
                _minHigh[i] = float.IsNaN(_minHigh[i]) ? v : Math.Max(_minHigh[i], v);
                _minSum[i] += v;
                _minCount[i]++;
            }
        }

        if (Environment.TickCount64 - _lastFlush > FlushInterval.TotalMilliseconds)
        {
            _raw.Flush();
            _lastFlush = Environment.TickCount64;
        }

        if (hour != _lastCleanupHour)
        {
            _lastCleanupHour = hour;
            Cleanup(timestampUnixMs);
        }
    }

    /// <summary>Rows for these sensors with timestamps in [from, to], oldest first (NaN = no reading / unknown sensor).</summary>
    /// <param name="maxRows">Stop after this many rows (files are read oldest first), for paged answers.</param>
    public HistoryTable Read(HistoryTier tier, IReadOnlyList<string> ids, long fromUnixMs, long toUnixMs, int maxRows = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(ids);
        lock (_lock)
        {
            Guard(() =>
            {
                _raw?.Flush();
                _minute?.Flush();
            });
        }

        var table = new HistoryTable(ids.Count, withRange: tier == HistoryTier.Minute);
        foreach (var file in Files(tier).OrderBy(f => f.Start))
        {
            if (file.End <= fromUnixMs || file.Start > toUnixMs)
            {
                continue;
            }

            // Whole periods are done once enough rows were found before this file's start (later files are later).
            if (table.Count >= maxRows && table.Times.Max() < file.Start)
            {
                break;
            }

            try
            {
                ReadFile(file.Path, tier, ids, fromUnixMs, toUnixMs, table);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                // Deleted by retention meanwhile, or damaged: skip it.
            }
        }

        table.SortByTime();
        return table;
    }

    /// <summary>Total size of the history folder.</summary>
    public long SizeBytes() => AllFiles().Sum(f => f.Length);

    public void Dispose()
    {
        lock (_lock)
        {
            Guard(FlushMinute);
            SafeDispose(ref _raw);
            SafeDispose(ref _minute);
        }
    }

    /// <summary>Disposing flushes, which can throw again on a full disk: never let that escape.</summary>
    private void SafeDispose(ref Segment? segment)
    {
        try
        {
            segment?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastError = ex.Message;
        }

        segment = null;
    }

    private void ResetMinute(long start)
    {
        _minuteStart = start;
        _minLow = new float[_ids.Length];
        _minHigh = new float[_ids.Length];
        Array.Fill(_minLow, float.NaN);
        Array.Fill(_minHigh, float.NaN);
        _minSum = new double[_ids.Length];
        _minCount = new int[_ids.Length];
    }

    /// <summary>Writes the finished minute (if it has any reading) to the day's minute file.</summary>
    private void FlushMinute()
    {
        if (_minuteStart == long.MinValue || _ids.Length == 0 || !_minCount.Any(c => c > 0))
        {
            return;
        }

        var day = _minuteStart / 86_400_000L;
        if (_minute is null || _minute.Period != day)
        {
            SafeDispose(ref _minute);
            _minute = Segment.Open(_options.Directory, HistoryTier.Minute, day, _ids, _hash);
        }

        var record = new byte[8 + (_ids.Length * 12)];
        BinaryPrimitives.WriteInt64LittleEndian(record, _minuteStart);
        for (var i = 0; i < _ids.Length; i++)
        {
            var offset = 8 + (i * 12);
            var avg = _minCount[i] == 0 ? float.NaN : (float)(_minSum[i] / _minCount[i]);
            BinaryPrimitives.WriteSingleLittleEndian(record.AsSpan(offset), _minLow[i]);
            BinaryPrimitives.WriteSingleLittleEndian(record.AsSpan(offset + 4), avg);
            BinaryPrimitives.WriteSingleLittleEndian(record.AsSpan(offset + 8), _minHigh[i]);
        }

        _minute.Write(record);
        _minute.Flush();
        _minuteStart = long.MinValue;
    }

    /// <summary>Deletes files past retention, then the oldest files while over the size cap.</summary>
    private void Cleanup(long nowUnixMs)
    {
        var rawCutoff = nowUnixMs - (_options.RawHours * 3_600_000L);
        var minuteCutoff = nowUnixMs - (_options.MinuteDays * 86_400_000L);
        foreach (var file in Files(HistoryTier.Raw).Where(f => f.End <= rawCutoff))
        {
            TryDelete(file.Path);
        }

        foreach (var file in Files(HistoryTier.Minute).Where(f => f.End <= minuteCutoff))
        {
            TryDelete(file.Path);
        }

        var total = SizeBytes();
        if (total <= _options.MaxBytes)
        {
            return;
        }

        // Over the cap: raw (the big tier) goes first, oldest first; never the files being written.
        var open = new[] { _raw?.Path, _minute?.Path };
        var victims = Files(HistoryTier.Raw).OrderBy(f => f.Start)
            .Concat(Files(HistoryTier.Minute).OrderBy(f => f.Start))
            .Where(f => !open.Contains(f.Path, StringComparer.OrdinalIgnoreCase));
        foreach (var file in victims)
        {
            if (total <= _options.MaxBytes)
            {
                break;
            }

            var size = new FileInfo(file.Path).Length;
            if (TryDelete(file.Path))
            {
                total -= size;
            }
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private IEnumerable<FileInfo> AllFiles()
    {
        try
        {
            return new DirectoryInfo(_options.Directory).EnumerateFiles("*.sdh").ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>History files of a tier with the time span their name covers.</summary>
    internal IEnumerable<(string Path, long Start, long End)> Files(HistoryTier tier)
    {
        var prefix = tier == HistoryTier.Raw ? "raw-" : "min-";
        foreach (var file in AllFiles())
        {
            if (!file.Name.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var parts = file.Name[prefix.Length..].Split('-');
            if (tier == HistoryTier.Raw && parts.Length >= 3
                && DateTime.TryParseExact($"{parts[0]}{parts[1]}", "yyyyMMddHH", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var hour))
            {
                var start = new DateTimeOffset(hour, TimeSpan.Zero).ToUnixTimeMilliseconds();
                yield return (file.FullName, start, start + 3_600_000L);
            }
            else if (tier == HistoryTier.Minute && parts.Length >= 2
                     && DateTime.TryParseExact(parts[0], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var day))
            {
                var start = new DateTimeOffset(day, TimeSpan.Zero).ToUnixTimeMilliseconds();
                yield return (file.FullName, start, start + 86_400_000L);
            }
        }
    }

    private static void ReadFile(string path, HistoryTier tier, IReadOnlyList<string> ids, long from, long to, HistoryTable table)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        var fileIds = ReadHeader(reader);
        var columns = new int[ids.Count];
        var index = new Dictionary<string, int>(fileIds.Length, StringComparer.Ordinal);
        for (var i = 0; i < fileIds.Length; i++)
        {
            index.TryAdd(fileIds[i], i);
        }

        var any = false;
        for (var c = 0; c < ids.Count; c++)
        {
            columns[c] = index.TryGetValue(ids[c], out var i) ? i : -1;
            any |= columns[c] >= 0;
        }

        if (!any)
        {
            return;
        }

        var perSensor = tier == HistoryTier.Raw ? 4 : 12;
        var recordSize = 8 + (fileIds.Length * perSensor);
        // About 64 KB, below the 85 KB large-object threshold (264 sensors: 61 raw / 20 minute records per read).
        var buffer = new byte[recordSize * Math.Max(1, (1 << 16) / recordSize)];
        var filled = 0;
        while (true)
        {
            var read = stream.Read(buffer, filled, buffer.Length - filled);
            filled += read;
            var whole = filled / recordSize;
            for (var r = 0; r < whole; r++)
            {
                var record = buffer.AsSpan(r * recordSize, recordSize);
                var time = BinaryPrimitives.ReadInt64LittleEndian(record);
                if (time < from || time > to)
                {
                    continue;
                }

                var values = new float[ids.Count];
                float[]? lows = tier == HistoryTier.Minute ? new float[ids.Count] : null;
                float[]? highs = tier == HistoryTier.Minute ? new float[ids.Count] : null;
                for (var c = 0; c < ids.Count; c++)
                {
                    if (columns[c] < 0)
                    {
                        values[c] = float.NaN;
                        if (lows is not null)
                        {
                            lows[c] = float.NaN;
                            highs![c] = float.NaN;
                        }

                        continue;
                    }

                    var offset = 8 + (columns[c] * perSensor);
                    if (tier == HistoryTier.Raw)
                    {
                        values[c] = BinaryPrimitives.ReadSingleLittleEndian(record[offset..]);
                    }
                    else
                    {
                        lows![c] = BinaryPrimitives.ReadSingleLittleEndian(record[offset..]);
                        values[c] = BinaryPrimitives.ReadSingleLittleEndian(record[(offset + 4)..]);
                        highs![c] = BinaryPrimitives.ReadSingleLittleEndian(record[(offset + 8)..]);
                    }
                }

                table.Add(time, values, lows, highs);
            }

            // Keep a partial record (still being written) for the next read; stop at end of file.
            var leftover = filled - (whole * recordSize);
            Buffer.BlockCopy(buffer, whole * recordSize, buffer, 0, leftover);
            filled = leftover;
            if (read == 0)
            {
                break;
            }
        }
    }

    private static string[] ReadHeader(BinaryReader reader)
    {
        var magic = Encoding.ASCII.GetString(reader.ReadBytes(4));
        if (magic != Magic)
        {
            throw new InvalidDataException("Not a SensorDeck history file.");
        }

        _ = reader.ReadInt32(); // tier
        var count = reader.ReadInt32();
        if (count is < 0 or > 100_000)
        {
            throw new InvalidDataException("Bad sensor count.");
        }

        var ids = new string[count];
        for (var i = 0; i < count; i++)
        {
            try
            {
                ids[i] = reader.ReadString();
            }
            catch (FormatException ex)
            {
                // A damaged 7-bit string length: the same "damaged file" both callers already handle (skip the file).
                throw new InvalidDataException("Damaged sensor list.", ex);
            }
        }

        return ids;
    }

    /// <summary>Stable FNV-1a hash of the id list (string.GetHashCode differs between runs).</summary>
    internal static string Hash(IReadOnlyList<string> ids)
    {
        var hash = 2166136261u;
        foreach (var id in ids)
        {
            foreach (var b in Encoding.UTF8.GetBytes(id))
            {
                hash = (hash ^ b) * 16777619u;
            }

            hash = (hash ^ 0x1F) * 16777619u;
        }

        return hash.ToString("x8", CultureInfo.InvariantCulture);
    }

    /// <summary>One open history file being appended to.</summary>
    private sealed class Segment : IDisposable
    {
        private readonly FileStream _stream;

        private Segment(string path, long period, FileStream stream)
        {
            Path = path;
            Period = period;
            _stream = stream;
        }

        public string Path { get; }

        /// <summary>Hour (raw) or day (minute) number since the Unix epoch.</summary>
        public long Period { get; }

        /// <summary>Opens (or creates) the file for this period and id list, positioned after its last whole record.</summary>
        public static Segment Open(string directory, HistoryTier tier, long period, string[] ids, string hash)
        {
            var start = DateTimeOffset.FromUnixTimeMilliseconds(period * (tier == HistoryTier.Raw ? 3_600_000L : 86_400_000L)).UtcDateTime;
            var stamp = tier == HistoryTier.Raw
                ? start.ToString("yyyyMMdd-HH", CultureInfo.InvariantCulture)
                : start.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            var prefix = tier == HistoryTier.Raw ? "raw" : "min";
            var path = System.IO.Path.Combine(directory, $"{prefix}-{stamp}-{hash}.sdh");

            for (var attempt = 0; ; attempt++)
            {
                var candidate = attempt == 0 ? path : System.IO.Path.Combine(directory, $"{prefix}-{stamp}-{hash}-{attempt}.sdh");
                var stream = new FileStream(candidate, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete);
                var endsInHeader = false;
                try
                {
                    if (stream.Length == 0)
                    {
                        WriteHeader(stream, tier, ids);
                        return new Segment(candidate, period, stream);
                    }

                    using (var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true))
                    {
                        if (ReadHeader(reader).SequenceEqual(ids))
                        {
                            // Drop a partial record left by a crash, then append.
                            var headerEnd = stream.Position;
                            var recordSize = 8 + (ids.Length * (tier == HistoryTier.Raw ? 4 : 12));
                            var whole = (stream.Length - headerEnd) / recordSize;
                            stream.SetLength(headerEnd + (whole * recordSize));
                            stream.Seek(0, SeekOrigin.End);
                            return new Segment(candidate, period, stream);
                        }
                    }
                }
                catch (EndOfStreamException)
                {
                    endsInHeader = true; // see below
                }
                catch (InvalidDataException)
                {
                    // Damaged, or another file's layout: try the next name.
                }
                catch
                {
                    stream.Dispose(); // never leak the handle (it would lock the file until GC)
                    throw;
                }

                // The file ends inside its header. No longer than this id list's header means it is this file's own header
                // cut off (crash or full disk while it was written): no records can follow, so it is started again
                // instead of leaving one more unusable file per attempt. Anything longer may be another layout or
                // damage: left alone, next name.
                if (endsInHeader)
                {
                    try
                    {
                        if (stream.Length <= HeaderLength(tier, ids))
                        {
                            stream.SetLength(0);
                            stream.Position = 0;
                            WriteHeader(stream, tier, ids);
                            return new Segment(candidate, period, stream);
                        }
                    }
                    catch
                    {
                        stream.Dispose();
                        throw;
                    }
                }

                stream.Dispose();
                if (attempt > 20)
                {
                    throw new IOException($"Couldn't open a history file for {stamp}.");
                }
            }
        }

        public void Write(byte[] record) => _stream.Write(record);

        public void Flush() => _stream.Flush();

        public void Dispose() => _stream.Dispose();

        /// <summary>Length in bytes of the header <see cref="WriteHeader"/> writes for <paramref name="ids"/>.</summary>
        private static long HeaderLength(HistoryTier tier, string[] ids)
        {
            using var probe = new MemoryStream();
            WriteHeader(probe, tier, ids);
            return probe.Length;
        }

        private static void WriteHeader(Stream stream, HistoryTier tier, string[] ids)
        {
            using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
            writer.Write(Encoding.ASCII.GetBytes(Magic));
            writer.Write((int)tier);
            writer.Write(ids.Length);
            foreach (var id in ids)
            {
                writer.Write(id);
            }

            writer.Flush();
        }
    }
}
