using System.Globalization;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SensorDeck.Core.Formatting;
using SensorDeck.Core.Model;
using SensorDeck.Core.Protocol;
using SensorDeck.Core.Replay;
using SensorDeck.Integrations.FanControl;
using SensorDeck.Sources.Lhm;
using SensorDeck.Sources.Lhm.SystemChecks;

// sensordeck-cli — diagnostics for the sensor sources. Run from an elevated terminal.
//   sensordeck-cli list   [--storage] [--network]
//   sensordeck-cli watch  [--interval 1000] [--kind voltage,fan,temperature] [--storage] [--network]
//   sensordeck-cli json   [--storage] [--network]          (redirect to a file for bug reports)
//   sensordeck-cli connect [--kind ...] [--pipe name] [--no-verify]   (talks to the collector service; doesn't need admin)
//   sensordeck-cli fancontrol [list]          [--exe path]   (doesn't need admin)
//   sensordeck-cli fancontrol switch <config> [--exe path]
//   sensordeck-cli diff <before.json> <after.json>   (compare hardware reports, e.g. across an LHM update)

Console.OutputEncoding = Encoding.UTF8;

string[] optionsWithValues = ["--interval", "--kind", "--exe", "--pipe"];
var positionals = Positionals();
var command = positionals.FirstOrDefault()?.ToLowerInvariant() ?? "list";
var options = new LhmOptions
{
    Storage = HasFlag("--storage"),
    Network = HasFlag("--network"),
    Controller = HasFlag("--controllers"),
};
var interval = int.TryParse(GetValue("--interval"), out var ms) ? Math.Clamp(ms, 250, 10_000) : 1000;
var kindFilter = ParseKinds(GetValue("--kind"));

if (command is "help" or "-h" or "/?")
{
    PrintUsage();
    return 0;
}

if (command == "connect")
{
    return ConnectCommandAsync(GetValue("--pipe") ?? ProtocolInfo.PipeName, kindFilter, verify: !HasFlag("--no-verify")).GetAwaiter().GetResult();
}

if (command == "diff")
{
    return DiffCommand(positionals.Skip(1).ToList());
}

if (command == "fancontrol")
{
    return FanControlCommand(positionals.Skip(1).ToList(), GetValue("--exe"));
}

if (command is not ("list" or "watch" or "json"))
{
    Console.Error.WriteLine($"Unknown command '{command}'.");
    PrintUsage();
    return 2;
}

using var source = new LhmSource(options);
if (command != "json")
{
    Console.WriteLine("Opening sensors (this can take a few seconds)…");
}

source.Open();

if (command != "json")
{
    PrintStatus(source);
}

switch (command)
{
    case "list":
        // A second update fills sensors that need two samples (loads, throughput).
        Thread.Sleep(500);
        source.Update();
        Console.WriteLine(Render(source, kindFilter));
        break;

    case "watch":
        Watch(source, interval, kindFilter);
        break;

    case "json":
        Thread.Sleep(500);
        source.Update();
        Console.WriteLine(ToJson(source));
        break;
}

return source.Status is SourceStatus.Error ? 1 : 0;

// ---------------------------------------------------------------------------------------------

bool HasFlag(string flag) => args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));

List<string> Positionals()
{
    var result = new List<string>();
    for (var i = 0; i < args.Length; i++)
    {
        if (args[i].StartsWith("--", StringComparison.Ordinal))
        {
            if (optionsWithValues.Contains(args[i], StringComparer.OrdinalIgnoreCase))
            {
                i++; // skip the option's value
            }

            continue;
        }

        result.Add(args[i]);
    }

    return result;
}

string? GetValue(string name)
{
    var i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static HashSet<SensorKind>? ParseKinds(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return null;
    }

    var set = new HashSet<SensorKind>();
    foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        if (Enum.TryParse<SensorKind>(part, ignoreCase: true, out var kind))
        {
            set.Add(kind);
        }
        else
        {
            Console.Error.WriteLine($"Ignoring unknown sensor kind '{part}'. Known: {string.Join(", ", Enum.GetNames<SensorKind>())}");
        }
    }

    return set.Count > 0 ? set : null;
}

static void PrintUsage()
{
    Console.WriteLine("""
        sensordeck-cli — SensorDeck sensor diagnostics (run elevated)

          list    Print every sensor once, grouped by hardware.
          watch   Live view, refreshed every --interval ms (default 1000). Ctrl+C to stop.
          json    Dump everything as JSON (redirect to a file for bug reports).

          diff <before> <after>      Compare two hardware reports (json above, or Diagnostics → Save hardware
                                     report) or .sdrec recordings: added/removed/renamed sensors and readings
                                     that moved a lot. Exit code 3 when sensors disappeared or changed kind.

          connect                    Connect to the running collector service and show its live values.
          fancontrol                 Find FanControl and list its saved configs.
          fancontrol switch <name>   Ask the running FanControl to load <name>.json.

        Options:
          --kind voltage,fan,temperature   Only show these sensor kinds (list/watch/connect).
          --interval <ms>                  Refresh interval for watch (250–10000, default 1000).
          --pipe <name>                    Pipe to connect to (connect; default SensorDeck).
          --no-verify                      connect: skip the check that the collector service (or an elevated
                                           SensorDeck.Collector.exe) is the program answering, e.g. for a replay pipe.
          --storage                        Include drives (can wake sleeping HDDs).
          --network                        Include network adapters.
          --controllers                    Include USB fan/AIO controllers LHM supports. Off by default:
                                           FanControl or vendor software may already own them.
          --exe <path>                     Path to FanControl.exe, if it isn't found automatically.
        """);
}

static void PrintStatus(LhmSource source)
{
    var pawn = source.PawnIo;
    Console.WriteLine($"PawnIO : {pawn?.State}{(pawn?.ServiceName is { } n ? $" (service '{n}')" : "")}");
    Console.WriteLine($"Admin  : {(Elevation.IsElevated() ? "yes" : "NO — run from an elevated terminal")}");
    Console.WriteLine($"Source : {source.Status}{(source.StatusMessage is { } m ? $" — {m}" : "")}");
    Console.WriteLine($"Found  : {source.Catalog.Hardware.Count} hardware, {source.Catalog.Sensors.Count} sensors");
    Console.WriteLine();
}

static string Render(LhmSource source, HashSet<SensorKind>? kinds)
{
    var catalog = source.Catalog;
    var readings = new SensorReading[catalog.Sensors.Count];
    source.Read(readings);

    var byHardware = catalog.Sensors
        .Select((s, i) => (Info: s, Reading: readings[i]))
        .Where(x => kinds is null || kinds.Contains(x.Info.Kind))
        .ToLookup(x => x.Info.HardwareId);

    var depth = new Dictionary<string, int>();
    var sb = new StringBuilder();
    var culture = CultureInfo.CurrentCulture;

    foreach (var hw in catalog.Hardware)
    {
        var level = hw.ParentId is { } p && depth.TryGetValue(p, out var d) ? d + 1 : 0;
        depth[hw.Id] = level;
        var indent = new string(' ', level * 2);

        var sensors = byHardware[hw.Id].ToList();
        if (kinds is not null && sensors.Count == 0)
        {
            continue;
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}■ {hw.Name}  [{hw.Kind}]");

        foreach (var group in sensors.GroupBy(x => x.Info.Kind))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}  {group.Key}");
            foreach (var (info, reading) in group)
            {
                var value = UnitFormatter.Format(info.Kind, reading.Value, culture: culture);
                var min = UnitFormatter.Format(info.Kind, reading.Min, culture: culture);
                var max = UnitFormatter.Format(info.Kind, reading.Max, culture: culture);
                var rail = info.NominalValue is { } nominal
                    ? $"  [{VoltageRails.Evaluate(reading.Value, nominal)} vs {nominal.ToString(culture)} V]"
                    : "";
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    {Truncate(info.Name, 28),-28} {value,14}   min {min,14}   max {max,14}{rail}");
            }
        }

        sb.AppendLine();
    }

    return sb.ToString();
}

static void Watch(LhmSource source, int intervalMs, HashSet<SensorKind>? kinds)
{
    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
    };

    var redirected = Console.IsOutputRedirected;
    if (!redirected)
    {
        Console.Clear();
        Console.CursorVisible = false;
    }

    try
    {
        while (!cts.IsCancellationRequested)
        {
            source.Update();
            source.TryRefreshCatalog();
            var frame = $"SensorDeck watch — {DateTime.Now:T} — every {intervalMs} ms — Ctrl+C to stop{System.Environment.NewLine}{System.Environment.NewLine}{Render(source, kinds)}";

            if (redirected)
            {
                Console.WriteLine(frame);
            }
            else
            {
                // Overwrite in place to avoid flicker; pad lines so shorter values don't leave stale characters.
                Console.SetCursorPosition(0, 0);
                var width = Math.Max(1, Console.WindowWidth - 1);
                var lines = frame.Split(System.Environment.NewLine);
                var maxLines = Math.Max(1, Console.WindowHeight - 1);
                var sb = new StringBuilder();
                foreach (var line in lines.Take(maxLines))
                {
                    sb.AppendLine(line.Length > width ? line[..width] : line.PadRight(width));
                }

                for (var i = lines.Length; i < maxLines; i++)
                {
                    sb.AppendLine(new string(' ', width));
                }

                Console.Write(sb.ToString());
            }

            cts.Token.WaitHandle.WaitOne(intervalMs);
        }
    }
    finally
    {
        if (!redirected)
        {
            Console.CursorVisible = true;
            Console.WriteLine();
        }
    }
}

static string ToJson(LhmSource source)
{
    var catalog = source.Catalog;
    var readings = new SensorReading[catalog.Sensors.Count];
    source.Read(readings);
    var sensorsByHw = catalog.Sensors.Select((s, i) => (s, r: readings[i])).ToLookup(x => x.s.HardwareId);

    var dump = new
    {
        generatedAt = DateTimeOffset.Now,
        tool = "sensordeck-cli",
        os = System.Environment.OSVersion.VersionString,
        elevated = Elevation.IsElevated(),
        pawnIo = source.PawnIo,
        source = new { name = source.Name, status = source.Status, message = source.StatusMessage },
        hardware = catalog.Hardware.Select(h => new
        {
            h.Id,
            h.Name,
            h.Kind,
            h.ParentId,
            sensors = sensorsByHw[h.Id].Select(x => new
            {
                x.s.Id,
                x.s.Name,
                x.s.Kind,
                x.s.Unit,
                x.r.Value,
                x.r.Min,
                x.r.Max,
                x.s.NominalValue,
            }),
        }),
    };

    return JsonSerializer.Serialize(dump, new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals, // a sensor can read NaN
        Converters = { new JsonStringEnumConverter() },
    });
}

static async Task<int> ConnectCommandAsync(string pipeName, HashSet<SensorKind>? kinds, bool verify)
{
    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
    };

    await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
    try
    {
        await pipe.ConnectAsync(3000, cts.Token).ConfigureAwait(false);
    }
    catch (TimeoutException)
    {
        Console.Error.WriteLine($@"Couldn't connect to \\.\pipe\{pipeName}. Is the collector running? (scripts\Install-Collector.ps1, or 'dotnet run --project src/SensorDeck.Collector' from an elevated terminal)");
        return 1;
    }
    catch (UnauthorizedAccessException)
    {
        Console.Error.WriteLine("The collector refused the connection (access denied).");
        return 1;
    }

    // Same rule as the app (PipeServerIdentity): the service's own process, or, while the service is stopped, an elevated
    // SensorDeck.Collector.exe from Program Files or this source tree's build output. Anything else could be forging data.
    var developmentFolder = PipeServerIdentity.DevelopmentCollectorFolder(AppContext.BaseDirectory, "SensorDeck.Cli");
    if (verify && !PipeServerIdentity.IsCollectorService(pipe.SafePipeHandle, developmentFolder, out var problem))
    {
        Console.Error.WriteLine($"{problem}{Environment.NewLine}Use --no-verify to connect anyway (e.g. to a replay pipe).");
        return 1;
    }

    HelloMessage? hello = null;
    SourceStatusEntry[] statuses = [];
    SensorCatalog catalog = SensorCatalog.Empty;
    var historyRequested = false;
    var redirected = Console.IsOutputRedirected;
    var lastHistory = "";
    if (!redirected)
    {
        Console.Clear();
    }

    try
    {
        while (!cts.IsCancellationRequested)
        {
            var message = await FrameCodec.ReadAsync(pipe, cts.Token).ConfigureAwait(false);
            switch (message)
            {
                case null:
                    Console.WriteLine("Collector closed the connection.");
                    return 0;

                case HelloMessage h:
                    hello = h;
                    break;

                case SourceStatusMessage st:
                    statuses = st.Sources;
                    break;

                case CatalogMessage c:
                    catalog = c.Catalog;
                    if (!historyRequested && catalog.Sensors.Count > 0)
                    {
                        // Exercise the history path once: ask for the last 5 minutes of the first temperature sensor.
                        var probe = catalog.Sensors.FirstOrDefault(s => s.Kind == SensorKind.Temperature) ?? catalog.Sensors[0];
                        await FrameCodec.WriteAsync(pipe, new HistoryRequest(1, [probe.Id], 300), cts.Token).ConfigureAwait(false);
                        historyRequested = true;
                    }

                    break;

                case HistoryResponse hr:
                    var points = hr.Values.Length > 0 ? hr.Values[0].Count(v => v.HasValue) : 0;
                    lastHistory = $"History check: {points} samples over the last 5 min for {hr.SensorIds.FirstOrDefault()}";
                    break;

                case ValuesMessage v when v.CatalogVersion == catalog.Version:
                    var frame = RenderRemote(hello, statuses, catalog, v, kinds, lastHistory);
                    WriteFrame(frame, redirected);
                    break;
            }
        }
    }
    catch (OperationCanceledException)
    {
        // Ctrl+C
    }
    catch (IOException)
    {
        Console.WriteLine("Lost connection to the collector.");
        return 1;
    }
    catch (Exception ex) when (ex is InvalidDataException or MessagePack.MessagePackSerializationException)
    {
        Console.WriteLine($"Protocol error from the collector: {ex.Message}");
        return 1;
    }
    finally
    {
        if (!redirected)
        {
            Console.CursorVisible = true;
            Console.WriteLine();
        }
    }

    return 0;
}

static string RenderRemote(HelloMessage? hello, SourceStatusEntry[] statuses, SensorCatalog catalog, ValuesMessage values, HashSet<SensorKind>? kinds, string historyLine)
{
    var culture = CultureInfo.CurrentCulture;
    var sb = new StringBuilder();
    sb.AppendLine(CultureInfo.InvariantCulture, $"SensorDeck collector v{hello?.ServerVersion} — protocol {hello?.ProtocolVersion} — every {hello?.PollIntervalMs} ms — frame #{values.Sequence} — Ctrl+C to stop");
    foreach (var s in statuses)
    {
        sb.AppendLine(CultureInfo.InvariantCulture, $"Source {s.Source}: {s.Status}{(s.Message is { } m ? " — " + m : "")}");
    }

    sb.AppendLine(CultureInfo.InvariantCulture, $"Catalog v{catalog.Version}: {catalog.Hardware.Count} hardware, {catalog.Sensors.Count} sensors");
    if (historyLine.Length > 0)
    {
        sb.AppendLine(historyLine);
    }

    sb.AppendLine();

    // Default view: the things a dashboard shows first.
    kinds ??= [SensorKind.Temperature, SensorKind.Fan, SensorKind.Control, SensorKind.Voltage];
    var hardwareNames = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var h in catalog.Hardware)
    {
        hardwareNames.TryAdd(h.Id, h.Name);
    }
    string? lastHardware = null;
    for (var i = 0; i < catalog.Sensors.Count && i < values.Values.Length; i++)
    {
        var info = catalog.Sensors[i];
        if (!kinds.Contains(info.Kind))
        {
            continue;
        }

        if (info.HardwareId != lastHardware)
        {
            lastHardware = info.HardwareId;
            sb.AppendLine(CultureInfo.InvariantCulture, $"■ {hardwareNames.GetValueOrDefault(info.HardwareId, info.HardwareId)}");
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"    {Truncate(info.Name, 28),-28} {UnitFormatter.Format(info.Kind, values.Values[i], culture: culture),14}");
    }

    return sb.ToString();
}

static void WriteFrame(string frame, bool redirected)
{
    if (redirected)
    {
        Console.WriteLine(frame);
        return;
    }

    Console.CursorVisible = false;
    Console.SetCursorPosition(0, 0);
    var width = Math.Max(1, Console.WindowWidth - 1);
    var maxLines = Math.Max(1, Console.WindowHeight - 1);
    var lines = frame.Split(System.Environment.NewLine);
    var sb = new StringBuilder();
    foreach (var line in lines.Take(maxLines))
    {
        sb.AppendLine(line.Length > width ? line[..width] : line.PadRight(width));
    }

    for (var i = lines.Length; i < maxLines; i++)
    {
        sb.AppendLine(new string(' ', width));
    }

    Console.Write(sb.ToString());
}

static int FanControlCommand(List<string> rest, string? exePath)
{
    var sub = rest.FirstOrDefault()?.ToLowerInvariant() ?? "list";
    var install = FanControlLocator.Find(exePath, out var rejected);
    if (install is null)
    {
        if (rejected is not null)
        {
            Console.Error.WriteLine($"Found but not used automatically: {rejected}.");
        }

        Console.Error.WriteLine("FanControl.exe wasn't found. Start FanControl, or pass --exe \"C:\\path\\to\\FanControl.exe\".");
        return 1;
    }

    Console.WriteLine($"FanControl : {install.ExePath}");
    Console.WriteLine($"Found by   : {install.FoundBy}");
    Console.WriteLine($"Running    : {(install.IsRunning ? "yes" : "no")}");
    Console.WriteLine();

    var configs = FanControlConfigs.List(install);

    switch (sub)
    {
        case "list":
            if (configs.Count == 0)
            {
                Console.WriteLine("No saved configs found next to FanControl.exe. In FanControl, use File → Save as… to save one per profile.");
                return 0;
            }

            Console.WriteLine("Saved configs:");
            foreach (var c in configs)
            {
                var note = c.LooksLikeConfig ? "" : "   (not recognized as a FanControl config)";
                Console.WriteLine($"  {c.Name,-28} {c.LastWriteTime:g}   {c.FullPath}{note}");
            }

            Console.WriteLine();
            Console.WriteLine("Switch with: sensordeck-cli fancontrol switch <name>");
            return 0;

        case "switch":
            // Allow unquoted names with spaces: "switch 100 Percent".
            var name = string.Join(' ', rest.Skip(1));
            if (string.IsNullOrWhiteSpace(name))
            {
                Console.Error.WriteLine("Which config? Usage: sensordeck-cli fancontrol switch <name>");
                return 2;
            }

            var config = FanControlConfigs.Find(install, name);
            if (config is null)
            {
                Console.Error.WriteLine($"No config named '{name}'. Known: {string.Join(", ", configs.Select(c => c.Name))}");
                return 1;
            }

            var result = FanControlSwitcher.Switch(install, config);
            Console.WriteLine(result.Message);
            if (result.Outcome is SwitchOutcome.Sent)
            {
                Console.WriteLine("FanControl doesn't report back; check its window title or tray tooltip to confirm the switch.");
            }

            return result.Outcome is SwitchOutcome.Sent or SwitchOutcome.StartedWithConfig ? 0 : 1;

        default:
            Console.Error.WriteLine($"Unknown fancontrol command '{sub}'. Use 'list' or 'switch <name>'.");
            return 2;
    }
}

static int DiffCommand(List<string> files)
{
    if (files.Count != 2)
    {
        Console.Error.WriteLine("Usage: sensordeck-cli diff <before.json> <after.json>");
        return 2;
    }

    try
    {
        var diff = HardwareDiff.Compare(SensorRecording.Load(files[0]), SensorRecording.Load(files[1]));
        Console.WriteLine(diff.ToText());
        return diff.HasBreakingChanges ? 3 : 0;
    }
    catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }
}

static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
