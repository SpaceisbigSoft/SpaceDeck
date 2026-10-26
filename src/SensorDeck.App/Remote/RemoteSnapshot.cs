using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using SensorDeck.App.Services;
using SensorDeck.App.Themes;
using SensorDeck.App.ViewModels;
using SensorDeck.Core.Dashboards;
using SensorDeck.Core.Formatting;
using SensorDeck.Core.Model;

namespace SensorDeck.App.Remote;

/// <summary>
/// Builds the JSON the web client needs, on the UI thread (sensors and dashboards live there): the full state
/// (skin, dashboards, sensor names, tile extras) and the compact per-reading value frame.
/// </summary>
internal static class RemoteSnapshot
{
    private static readonly JsonSerializerOptions TileOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public static string State(SensorStore store, IReadOnlyList<DashboardDocumentViewModel> dashboards, FanProfileService fans, RemoteSettings remote)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("tempUnit", store.TemperatureUnit == TemperatureUnit.Fahrenheit ? "F" : "C");
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            w.WriteNumber("serverNowMs", now);
            w.WriteNumber("bootMs", now - Environment.TickCount64);
            w.WriteBoolean("controls", remote.AllowControl);
            w.WriteString("clock", Appearance.ClockFormat);

            WriteSkin(w);

            w.WriteStartArray("profiles");
            foreach (var config in fans.Configs)
            {
                w.WriteStringValue(config.Name);
            }

            w.WriteEndArray();
            w.WriteString("profile", fans.LastProfile);

            w.WriteStartObject("sensors");
            foreach (var s in store.Sensors)
            {
                w.WriteStartObject(s.Id);
                w.WriteString("n", s.Name);
                w.WriteString("g", s.GroupName);
                w.WriteString("k", s.Kind.ToString());
                w.WriteString("u", s.Unit);
                if (s.Nominal is { } nominal)
                {
                    Number(w, "nom", nominal);
                }

                Number(w, "max", s.ScaleMaximum);
                w.WriteEndObject();
            }

            w.WriteEndObject();

            w.WriteStartArray("dashboards");
            foreach (var dashboard in dashboards)
            {
                w.WriteStartObject();
                w.WriteString("id", dashboard.Id);
                w.WriteString("name", dashboard.Name);
                w.WritePropertyName("tiles");
                JsonSerializer.Serialize(w, dashboard.Document.Tiles, TileOptions);
                w.WriteStartObject("extras");
                foreach (var tile in dashboard.Document.Tiles)
                {
                    WriteExtras(w, tile, store);
                }

                w.WriteEndObject();
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>One frame per reading: id → [value, text, level, min text, max text].</summary>
    public static string Values(SensorStore store, long timestampUnixMs)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteNumber("t", timestampUnixMs);
            w.WriteStartObject("v");
            foreach (var s in store.Sensors)
            {
                w.WriteStartArray(s.Id);
                if (s.Value is { } v && float.IsFinite(v))
                {
                    w.WriteNumberValue(v);
                }
                else
                {
                    w.WriteNullValue();
                }

                w.WriteStringValue(s.DisplayValue);
                w.WriteNumberValue((int)s.Level);
                w.WriteStringValue(s.DisplayMin);
                w.WriteStringValue(s.DisplayMax);
                w.WriteEndArray();
            }

            w.WriteEndObject();
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>The last <paramref name="seconds"/> of each sensor's live history: id → [[t…], [v…]].</summary>
    public static string History(SensorStore store, IEnumerable<string> ids, int seconds)
    {
        var since = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (Math.Clamp(seconds, 10, 3600) * 1000L);
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            foreach (var id in ids.Distinct(StringComparer.Ordinal).Take(64))
            {
                if (!store.SensorsById.TryGetValue(id, out var sensor))
                {
                    continue;
                }

                var times = new List<long>();
                var values = new List<float>();
                sensor.History.ForEach((t, v) =>
                {
                    if (t >= since)
                    {
                        times.Add(t);
                        values.Add(v);
                    }
                });

                w.WriteStartArray(id);
                w.WriteStartArray();
                foreach (var t in times)
                {
                    w.WriteNumberValue(t);
                }

                w.WriteEndArray();
                w.WriteStartArray();
                foreach (var v in values)
                {
                    if (float.IsFinite(v))
                    {
                        w.WriteNumberValue(v);
                    }
                    else
                    {
                        w.WriteNullValue();
                    }
                }

                w.WriteEndArray();
                w.WriteEndArray();
            }

            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>Things a tile shows that the desktop works out from live sensors: core cells, throughput, fan duty.</summary>
    private static void WriteExtras(Utf8JsonWriter w, TileSpec tile, SensorStore store)
    {
        switch (tile.Type)
        {
            case WidgetType.CoreMap:
                var layout = CoreMap.Describe(store.Sensors, tile.Metric);
                w.WriteStartObject(tile.Id);
                Number(w, "low", layout.Low);
                Number(w, "high", layout.High);
                w.WriteStartArray("groups");
                foreach (var group in layout.Groups)
                {
                    w.WriteStartObject();
                    w.WriteString("label", group.Label);
                    w.WriteStartArray("cells");
                    foreach (var cell in group.Cells)
                    {
                        w.WriteStartArray();
                        w.WriteStringValue(cell.Sensor.Id);
                        w.WriteStringValue(cell.Label);
                        w.WriteEndArray();
                    }

                    w.WriteEndArray();
                    w.WriteEndObject();
                }

                w.WriteEndArray();
                w.WriteEndObject();
                break;

            case WidgetType.Throughput:
                w.WriteStartArray(tile.Id);
                foreach (var s in store.Sensors.Where(s => s.Kind == SensorKind.Throughput && !s.IsHidden
                                                            && s.HardwareKind is HardwareKind.Network or HardwareKind.Storage))
                {
                    w.WriteStringValue(s.Id);
                }

                w.WriteEndArray();
                break;

            case WidgetType.Section:
                var plan = Sections.Plan(tile, store.Sensors, boardName: store.MotherboardName);
                w.WriteStartObject(tile.Id);
                w.WriteString("title", plan.Kind.Title);
                if (plan.HardwareName is { } hardwareName)
                {
                    w.WriteString("name", hardwareName);
                }

                w.WriteStartArray("rows");
                foreach (var row in plan.Rows)
                {
                    w.WriteStartObject();
                    w.WriteString("label", row.Label);
                    w.WriteBoolean("heading", row.IsHeading);
                    w.WriteBoolean("gap", row.GapBefore);
                    w.WriteStartArray("ids");
                    foreach (var sensor in row.Values)
                    {
                        if (sensor is null)
                        {
                            w.WriteNullValue();
                        }
                        else
                        {
                            w.WriteStringValue(sensor.Id);
                        }
                    }

                    w.WriteEndArray();
                    w.WriteEndObject();
                }

                w.WriteEndArray();
                w.WriteEndObject();
                break;

            case WidgetType.Fans:
                var rows = FanRow.Build(store.Sensors).ToList();
                w.WriteStartObject(tile.Id);
                foreach (var id in tile.Sensors)
                {
                    if (rows.FirstOrDefault(r => r.Fan.Id == id)?.Duty is { } duty)
                    {
                        w.WriteString(id, duty.Id);
                    }
                }

                w.WriteEndObject();
                break;
        }
    }

    /// <summary>JSON has no NaN/Infinity: write null instead of throwing.</summary>
    private static void Number(Utf8JsonWriter w, string name, double value)
    {
        if (double.IsFinite(value))
        {
            w.WriteNumber(name, value);
        }
        else
        {
            w.WriteNull(name);
        }
    }

    private static void WriteSkin(Utf8JsonWriter w)
    {
        w.WriteStartObject("skin");
        foreach (var (name, key) in new[]
        {
            ("bg", "Skin.Background"), ("surface", "Skin.Surface"), ("text", "Skin.Text"), ("muted", "Skin.TextMuted"),
            ("faint", "Skin.TextFaint"), ("accent", "Skin.Accent"), ("onAccent", "Skin.OnAccent"), ("track", "Skin.Track"),
            ("rule", "Skin.Rule"), ("grid", "Skin.Grid"), ("ok", "Skin.Ok"), ("warning", "Skin.Warning"), ("critical", "Skin.Critical"),
            ("cardBg", "Skin.Card.Background"), ("cardBorder", "Skin.Card.Border"),
        })
        {
            w.WriteString(name, Css(SkinManager.Brush(key)));
        }

        w.WriteStartArray("series");
        for (var i = 0; i < 6; i++)
        {
            w.WriteStringValue(Css(SkinManager.Brush($"Skin.Series.{i}")));
        }

        w.WriteEndArray();
        w.WriteString("fontDisplay", Font("Skin.Font.Display"));
        w.WriteString("fontBody", Font("Skin.Font.Body"));
        w.WriteString("fontMono", Font("Skin.Font.Mono"));
        var border = Application.Current?.TryFindResource("Skin.Card.BorderThickness") is Thickness t ? t : new Thickness(0, 1, 0, 0);
        w.WriteString("cardBorderWidth", string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{border.Top:0.#}px {border.Right:0.#}px {border.Bottom:0.#}px {border.Left:0.#}px"));
        w.WriteNumber("radius", Application.Current?.TryFindResource("Skin.Card.Radius") is CornerRadius r ? r.TopLeft : 0);
        w.WriteEndObject();

        static string Font(string key) =>
            Application.Current?.TryFindResource(key) is FontFamily f ? f.Source : "Segoe UI";
    }

    private static string Css(Brush brush) =>
        brush is SolidColorBrush { Color: var c }
            ? $"rgba({c.R},{c.G},{c.B},{(c.A / 255.0).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)})"
            : "gray";
}
