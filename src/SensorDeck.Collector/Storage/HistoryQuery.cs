using SensorDeck.Collector.Polling;
using SensorDeck.Core.History;
using SensorDeck.Core.Protocol;

namespace SensorDeck.Collector.Storage;

/// <summary>Answers <see cref="HistoryRequest"/>s from memory (recent hour) or disk (older), averaged when asked.</summary>
public static class HistoryQuery
{
    public const int MaxIds = 512;

    /// <summary>Values per response (all arrays together), keeping frames well under the 16 MB limit.</summary>
    public const int MaxValues = 1_000_000;

    public const int MaxPoints = 5000;

    /// <summary>Longest span one request may cover (disk history keeps at most 366 days).</summary>
    public const long MaxSpanMs = 366L * 86_400_000;

    /// <summary>
    /// Values read from disk for one chart request before averaging (rows × sensors, about 128 MB), so asking for every
    /// sample of many sensors over a long span can't exhaust the service's memory. Bigger spans use per-minute rows.
    /// </summary>
    public const long MaxDiskValues = 32_000_000;

    /// <summary>
    /// Exports use per-minute rows only for times this far before the raw horizon. The horizon moves with the clock, so
    /// without slack a continuation starting at the previous answer's horizon would be "older" again on every request
    /// (a 24 h export crawled forward a few milliseconds per round trip). Raw files are deleted by whole hours, so raw
    /// rows reach back past the horizon.
    /// </summary>
    private const long ExportRawSlackMs = 60_000;

    public static HistoryResponse Answer(SensorHub hub, HistoryRequest request, long nowUnixMs)
    {
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(request);
        var ids = (request.SensorIds ?? []).Where(id => id is not null).Distinct(StringComparer.Ordinal).Take(MaxIds).ToArray();
        var maxPoints = Math.Clamp(request.MaxPoints, 0, MaxPoints);

        if (request.FromUnixMs is null && request.ToUnixMs is null && maxPoints == 0)
        {
            // v1/v2 request: the last WindowSeconds from memory; ids capped so ids × samples fits one frame.
            var samples = Math.Max(1, hub.History.Count);
            ids = [.. ids.Take(Math.Clamp(MaxValues / samples, 1, MaxIds))];
            var slice = hub.History.Query(ids, Math.Clamp(request.WindowSeconds, 1, 86_400), nowUnixMs);
            return new HistoryResponse(request.RequestId, slice.TimestampsUnixMs, ids, slice.Values);
        }

        // Clamped so the span arithmetic below can't overflow (a client may send long.MinValue).
        var to = Math.Clamp(request.ToUnixMs ?? nowUnixMs, 0, Math.Max(0, nowUnixMs));
        var from = request.FromUnixMs ?? (to - (Math.Clamp((long)request.WindowSeconds, 1, 366L * 86_400) * 1000));
        from = Math.Max(from, to - MaxSpanMs);
        if (ids.Length == 0 || from > to)
        {
            return new HistoryResponse(request.RequestId, [], ids, [.. ids.Select(_ => Array.Empty<float?>())]);
        }

        var perRow = ids.Length * (maxPoints > 0 ? 3 : 1);
        var maxRows = Math.Max(1, MaxValues / Math.Max(1, perRow));
        long? next = null;

        HistoryTable table;
        if (hub.Disk is null || (hub.History.OldestTimestamp is { } oldest && from >= oldest))
        {
            table = FromSlice(hub.History.Query(ids, from, to), ids.Length);
        }
        else
        {
            var rawStart = hub.Disk.RawHorizonUnixMs(nowUnixMs);
            if (maxPoints > 0)
            {
                // Charts: per-minute rows for long spans (fast, small), every sample otherwise — unless every sample of
                // these sensors (at the fastest poll interval) would be too much to hold.
                var tier = (to - from) / maxPoints >= 60_000 || from < rawStart - 3_600_000L ? HistoryTier.Minute : HistoryTier.Raw;
                if (tier == HistoryTier.Raw && (to - from) / CollectorOptions.MinPollIntervalMs * ids.Length > MaxDiskValues)
                {
                    tier = HistoryTier.Minute;
                }

                var valuesPerRow = ids.Length * (tier == HistoryTier.Minute ? 3 : 1);
                table = hub.Disk.Read(tier, ids, from, to, (int)Math.Min(int.MaxValue, MaxDiskValues / valuesPerRow));
            }
            else if (from < rawStart - ExportRawSlackMs)
            {
                // Exports: older than the raw files reach is only kept per minute; the rest follows every sample.
                var end = Math.Min(to, rawStart - 1);
                table = hub.Disk.Read(HistoryTier.Minute, ids, from, end, maxRows + 1);
                table = new HistoryTable(ids.Length, withRange: false).Append(table); // values only, like raw rows
                if (end < to && table.Count <= maxRows)
                {
                    next = rawStart;
                }
            }
            else
            {
                table = hub.Disk.Read(HistoryTier.Raw, ids, from, to, maxRows + 1);
            }
        }

        table = HistoryBuckets.Bucket(table, from, to, maxPoints);

        // Too much for one frame: send the first part and say where to continue.
        perRow = ids.Length * (table.Min is null ? 1 : 3);
        maxRows = Math.Max(1, MaxValues / Math.Max(1, perRow));
        if (table.Count > maxRows)
        {
            next = table.Times[maxRows];
            table = table.Slice(0, maxRows);
        }

        return new HistoryResponse(
            request.RequestId,
            [.. table.Times],
            ids,
            HistoryTable.ToColumns(table.Values, ids.Length),
            table.Min is null ? null : HistoryTable.ToColumns(table.Min, ids.Length),
            table.Max is null ? null : HistoryTable.ToColumns(table.Max, ids.Length),
            next);
    }

    private static HistoryTable FromSlice(HistorySlice slice, int columns)
    {
        var table = new HistoryTable(columns);
        for (var r = 0; r < slice.TimestampsUnixMs.Length; r++)
        {
            var row = new float[columns];
            for (var c = 0; c < columns; c++)
            {
                row[c] = slice.Values[c][r] ?? float.NaN;
            }

            table.Add(slice.TimestampsUnixMs[r], row);
        }

        return table;
    }
}
