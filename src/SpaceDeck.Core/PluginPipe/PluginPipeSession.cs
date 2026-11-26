using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace SpaceDeck.Core.PluginPipe;

/// <summary>
/// Reads one plugin pipe connection's lines (<c>docs/PLUGIN-PIPE.md</c>) and checks every rule, so nothing a client sends
/// reaches the collector unchecked. Pure (no I/O) so each rule is unit-tested on any platform.
/// </summary>
/// <remarks>
/// One instance per connection: it remembers whether <c>hello</c> came and the current catalog, which <c>values</c>
/// lines are checked against. Not thread-safe (a connection is read by one loop).
/// </remarks>
public sealed class PluginPipeSession
{
    // Catalog lines nest three deep (object → list → object); anything much deeper isn't ours and isn't worth parsing.
    private static readonly JsonDocumentOptions ParseOptions = new() { MaxDepth = 8 };

    /// <summary>The <c>hello</c> received, or null before it.</summary>
    public PluginHello? Hello { get; private set; }

    /// <summary>The latest valid catalog, or null before the first.</summary>
    public PluginCatalog? Catalog { get; private set; }

    /// <summary>
    /// Checks one line (UTF-8, without its <c>\n</c>; a trailing <c>\r</c> is tolerated as JSON whitespace). Returns the
    /// message, <see cref="PluginLineResult.Ignored"/>, or an error that must disconnect the client.
    /// </summary>
    public PluginLineResult Accept(ReadOnlyMemory<byte> line)
    {
        if (line.Length > PluginPipeProtocol.MaxLineBytes)
        {
            return PluginLineResult.Fail("a line is longer than 64 KiB");
        }

        if (IsBlank(line.Span))
        {
            return PluginLineResult.Ignored;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line, ParseOptions);
        }
        catch (JsonException)
        {
            // The exception's text carries a position, which would make every bad line a "new" reason in the log.
            return PluginLineResult.Fail("a line isn't valid JSON");
        }

        using (document)
        {
            try
            {
                return Interpret(document.RootElement);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                // GetString on a string that isn't valid UTF-8, or escapes a lone surrogate.
                return PluginLineResult.Fail("a line has text that isn't valid Unicode");
            }
        }
    }

    private PluginLineResult Interpret(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return PluginLineResult.Fail("a line isn't a JSON object");
        }

        if (!root.TryGetProperty("t", out var type) || type.ValueKind != JsonValueKind.String)
        {
            return PluginLineResult.Fail("a line has no \"t\" (message type)");
        }

        switch (type.GetString())
        {
            case "hello":
                return ReadHello(root);

            case "catalog":
                if (Hello is null)
                {
                    return PluginLineResult.Fail("catalog before hello");
                }

                return ReadCatalog(root);

            case "values":
                if (Hello is null)
                {
                    return PluginLineResult.Fail("values before hello");
                }

                if (Catalog is null)
                {
                    return PluginLineResult.Fail("values before a catalog");
                }

                return ReadValues(root, Catalog);

            default:
                // A newer plugin's message type: skipped, so plugins can add types without breaking older collectors.
                return PluginLineResult.Ignored;
        }
    }

    private PluginLineResult ReadHello(JsonElement root)
    {
        if (!root.TryGetProperty("v", out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var version))
        {
            return PluginLineResult.Fail("hello has no protocol version");
        }

        if (version != PluginPipeProtocol.Version)
        {
            return PluginLineResult.Fail(string.Create(CultureInfo.InvariantCulture,
                $"protocol version {version} isn't supported (this collector speaks {PluginPipeProtocol.Version})"));
        }

        var hello = new PluginHello(version, ForLog(root, "plugin"), ForLog(root, "version"), ForLog(root, "instance"));
        Hello = hello;
        return new PluginLineResult(hello, null);
    }

    private PluginLineResult ReadCatalog(JsonElement root)
    {
        if (!root.TryGetProperty("hardware", out var hardwareList) || hardwareList.ValueKind != JsonValueKind.Array)
        {
            return PluginLineResult.Fail("catalog has no \"hardware\" list");
        }

        if (!root.TryGetProperty("sensors", out var sensorList) || sensorList.ValueKind != JsonValueKind.Array)
        {
            return PluginLineResult.Fail("catalog has no \"sensors\" list");
        }

        if (hardwareList.GetArrayLength() > PluginPipeProtocol.MaxHardware)
        {
            return PluginLineResult.Fail("catalog lists more than 32 hardware entries");
        }

        if (sensorList.GetArrayLength() > PluginPipeProtocol.MaxSensors)
        {
            return PluginLineResult.Fail("catalog lists more than 512 sensors");
        }

        var hardware = new List<PluginHardware>(hardwareList.GetArrayLength());
        var hardwareIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in hardwareList.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                return PluginLineResult.Fail("catalog: a hardware entry isn't an object");
            }

            if (ReadId(item, "id") is not { } id)
            {
                return PluginLineResult.Fail("catalog: a hardware id is missing or invalid");
            }

            if (ReadName(item) is not { } name)
            {
                return PluginLineResult.Fail($"catalog: hardware '{id}' has a missing or invalid name");
            }

            if (!hardwareIds.Add(id))
            {
                return PluginLineResult.Fail($"catalog: hardware '{id}' is listed twice");
            }

            // Unknown hardware kinds are only an icon choice, so they don't fail the catalog.
            var kind = ReadString(item, "kind") switch
            {
                "cooler" => PluginHardwareKind.Cooler,
                "fans" => PluginHardwareKind.Fans,
                _ => PluginHardwareKind.Other,
            };
            hardware.Add(new PluginHardware(id, name, kind));
        }

        var sensors = new List<PluginSensor>(sensorList.GetArrayLength());
        var sensorIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in sensorList.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                return PluginLineResult.Fail("catalog: a sensor entry isn't an object");
            }

            if (ReadId(item, "id") is not { } id)
            {
                return PluginLineResult.Fail("catalog: a sensor id is missing or invalid");
            }

            if (ReadId(item, "hardware") is not { } hardwareId || !hardwareIds.Contains(hardwareId))
            {
                return PluginLineResult.Fail($"catalog: sensor '{id}' doesn't name a listed hardware id");
            }

            if (ReadName(item) is not { } name)
            {
                return PluginLineResult.Fail($"catalog: sensor '{id}' has a missing or invalid name");
            }

            if (!sensorIds.Add(id))
            {
                return PluginLineResult.Fail($"catalog: sensor '{id}' is listed twice");
            }

            // Unlike hardware, a sensor kind decides the unit and the valid range: guessing would mislabel readings.
            PluginSensorKind kind;
            switch (ReadString(item, "kind"))
            {
                case "temperature":
                    kind = PluginSensorKind.Temperature;
                    break;
                case "fan":
                    kind = PluginSensorKind.Fan;
                    break;
                case "control":
                    kind = PluginSensorKind.Control;
                    break;
                default:
                    return PluginLineResult.Fail($"catalog: sensor '{id}' has a missing or unknown kind");
            }

            sensors.Add(new PluginSensor(id, hardwareId, name, kind));
        }

        var catalog = new PluginCatalog(hardware, sensors);
        Catalog = catalog;
        return new PluginLineResult(catalog, null);
    }

    private static PluginLineResult ReadValues(JsonElement root, PluginCatalog catalog)
    {
        if (!root.TryGetProperty("values", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return PluginLineResult.Fail("values has no \"values\" list");
        }

        var count = list.GetArrayLength();
        if (count != catalog.Sensors.Count)
        {
            return PluginLineResult.Fail(string.Create(CultureInfo.InvariantCulture,
                $"values has {count} entries for {catalog.Sensors.Count} sensors"));
        }

        var values = new double?[count];
        var i = 0;
        foreach (var item in list.EnumerateArray())
        {
            switch (item.ValueKind)
            {
                case JsonValueKind.Null:
                    break;

                case JsonValueKind.Number when item.TryGetDouble(out var value) && double.IsFinite(value):
                    // An odd reading from one device is dropped, not fatal: disconnecting would blank every sensor.
                    values[i] = PluginPipeProtocol.InRange(catalog.Sensors[i].Kind, value) ? value : null;
                    break;

                default:
                    return PluginLineResult.Fail("values: an entry isn't a finite number or null");
            }

            i++;
        }

        return new PluginLineResult(new PluginValues(values), null);
    }

    private static string? ReadString(JsonElement item, string property) =>
        item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>The id if it is 1–<see cref="PluginPipeProtocol.MaxIdLength"/> characters of <c>A–Z a–z 0–9 / . _ : -</c>; else null.</summary>
    private static string? ReadId(JsonElement item, string property)
    {
        var id = ReadString(item, property);
        return id is not null && IsValidId(id) ? id : null;
    }

    internal static bool IsValidId(string id)
    {
        if (id.Length is 0 or > PluginPipeProtocol.MaxIdLength)
        {
            return false;
        }

        foreach (var c in id)
        {
            if (!(c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '/' or '.' or '_' or ':' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    private static string? ReadName(JsonElement item)
    {
        var name = ReadString(item, "name");
        return name is not null && IsValidName(name) ? name : null;
    }

    /// <summary>
    /// 1–<see cref="PluginPipeProtocol.MaxNameLength"/> printable characters, not all blank. Control and format
    /// characters (which include bidirectional overrides and zero-width characters that could disguise a name), line
    /// separators and lone surrogates make a name invalid.
    /// </summary>
    internal static bool IsValidName(string name)
    {
        var rest = name.AsSpan();
        var count = 0;
        var visible = false;
        while (!rest.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(rest, out var rune, out var used) != OperationStatus.Done)
            {
                return false;
            }

            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator
                or UnicodeCategory.ParagraphSeparator or UnicodeCategory.Surrogate)
            {
                return false;
            }

            if (++count > PluginPipeProtocol.MaxNameLength)
            {
                return false;
            }

            visible |= !Rune.IsWhiteSpace(rune);
            rest = rest[used..];
        }

        return visible;
    }

    /// <summary>A hello field made safe to log: printable characters only, at most 64; empty when missing or not a string.</summary>
    private static string ForLog(JsonElement root, string property)
    {
        if (ReadString(root, property) is not { } text)
        {
            return "";
        }

        var builder = new StringBuilder(Math.Min(text.Length, PluginPipeProtocol.MaxNameLength));
        foreach (var c in text)
        {
            if (builder.Length == PluginPipeProtocol.MaxNameLength)
            {
                break;
            }

            if (!char.IsControl(c) && !char.IsSurrogate(c) && char.GetUnicodeCategory(c) != UnicodeCategory.Format)
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    private static bool IsBlank(ReadOnlySpan<byte> line)
    {
        foreach (var b in line)
        {
            if (b != ' ' && b != '\t' && b != '\r')
            {
                return false;
            }
        }

        return true;
    }
}
