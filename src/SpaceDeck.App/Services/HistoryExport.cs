using System.Globalization;
using System.IO;
using System.Text;
using SpaceDeck.Core.Protocol;

namespace SpaceDeck.App.Services;

/// <summary>CSV files of sensor history: one row per sample, one column per sensor, values in base units.</summary>
public static class HistoryExport
{
    /// <summary>Header cell: "CPU Package [°C] (Intel Core i9-14900K)".</summary>
    public static string ColumnName(SensorItem sensor)
    {
        ArgumentNullException.ThrowIfNull(sensor);
        var unit = string.IsNullOrEmpty(sensor.Unit) ? "" : $" [{sensor.Unit}]";
        return $"{sensor.Name}{unit} ({sensor.GroupName})";
    }

    public static string Escape(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{text.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : text;
    }

    public static string Cell(float? value) =>
        value is { } v && !float.IsNaN(v) ? v.ToString("G7", CultureInfo.InvariantCulture) : "";

    public static string Time(long unixMs) =>
        DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

    /// <summary>
    /// Writes every stored sample of <paramref name="sensors"/> between two times (paging through the collector's
    /// answers), corrected like the app shows them. Returns the number of rows written.
    /// </summary>
    public static async Task<int> ExportAsync(
        CollectorClient client,
        IReadOnlyList<SensorItem> sensors,
        long fromUnixMs,
        long toUnixMs,
        string path,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(sensors);
        var ids = sensors.Select(s => s.Id).ToArray();
        var rows = 0;
        await using var writer = new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        await writer.WriteLineAsync(string.Join(",", new[] { "Time" }.Concat(sensors.Select(s => Escape(ColumnName(s)))))).ConfigureAwait(false);

        long? from = fromUnixMs;
        while (from is { } start && start <= toUnixMs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = await client.RequestHistoryAsync(new HistoryRequest(0, ids, 0, start, toUnixMs), cancellationToken).ConfigureAwait(false);
            var columns = new float?[sensors.Count][];
            for (var c = 0; c < sensors.Count; c++)
            {
                float?[] raw = c < response.Values.Length ? response.Values[c] : [];
                columns[c] = sensors[c].CorrectRaw(raw);
            }

            var line = new StringBuilder();
            for (var r = 0; r < response.TimestampsUnixMs.Length; r++)
            {
                line.Clear().Append(Time(response.TimestampsUnixMs[r]));
                for (var c = 0; c < sensors.Count; c++)
                {
                    line.Append(',').Append(r < columns[c].Length ? Cell(columns[c][r]) : "");
                }

                await writer.WriteLineAsync(line.ToString()).ConfigureAwait(false);
                rows++;
            }

            if (response.TimestampsUnixMs.Length > 0)
            {
                progress?.Report(response.TimestampsUnixMs[^1]);
            }

            // Only ever forward: a reply that doesn't advance would otherwise loop forever.
            from = response.NextFromUnixMs is { } next && next > start ? next : null;
        }

        return rows;
    }
}
