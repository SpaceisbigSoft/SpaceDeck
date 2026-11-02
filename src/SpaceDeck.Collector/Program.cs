using System.Globalization;
using Microsoft.Extensions.Options;
using SpaceDeck.Collector;
using SpaceDeck.Collector.Logging;
using SpaceDeck.Collector.Pipe;
using SpaceDeck.Collector.Polling;
using SpaceDeck.Collector.Storage;
using SpaceDeck.Sources.Lhm.SystemChecks;

// SpaceDeck collector: reads sensors on one thread and serves them to UI clients over \\.\pipe\SpaceDeck.
// Runs as a Windows Service (installed by scripts\Install-Collector.ps1) or as a console app when debugging:
//   dotnet run --project src/SpaceDeck.Collector        (from an elevated terminal for full sensors)
// Demo/replay mode (no admin, no hardware access; the app starts this itself for Diagnostics → Open replay):
//   SpaceDeck.Collector.exe --replay recording.sdrec --pipe SpaceDeck-replay [--parent-pid 1234]

var builder = Host.CreateApplicationBuilder(args);
var switchMappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
{
    ["--replay"] = "Collector:ReplayFile",
    ["--pipe"] = "Collector:PipeName",
    ["--parent-pid"] = "Collector:ParentProcessId",
};

// Replay settings come from the command line only: collector.json must never be able to switch the service
// (LocalSystem) into replay mode or make it open a file of someone else's choosing.
var commandLine = new ConfigurationBuilder().AddCommandLine(args, switchMappings).Build();
var replayFile = commandLine[$"{CollectorOptions.SectionName}:{nameof(CollectorOptions.ReplayFile)}"];
int? parentProcessId = int.TryParse(commandLine[$"{CollectorOptions.SectionName}:{nameof(CollectorOptions.ParentProcessId)}"],
    NumberStyles.None, CultureInfo.InvariantCulture, out var pid) ? pid : null;
var isReplay = !string.IsNullOrWhiteSpace(replayFile);

// Lock the data folder down before anything in it is read (collector.json) or written (logs, history). Only an
// elevated collector can; an unelevated debug run can't write there anyway.
string? dataDirectoryProblem = null;
var removedEntries = new List<string>();
if (!isReplay && Elevation.IsElevated())
{
    dataDirectoryProblem = DataDirectorySecurity.Secure(CollectorConfigFile.DataDirectory, removedEntries);
    if (dataDirectoryProblem is not null)
    {
        Console.Error.WriteLine(dataDirectoryProblem);
    }
}

var dataDirectoryUsable = dataDirectoryProblem is null;

// Optional settings file; see docs/PROTOCOL.md for the keys. The command line comes last so it wins.
string? configProblem = null;
if (dataDirectoryUsable)
{
    try
    {
        // Tried on its own first: a file the loader can't read or bind (a typo, a duplicate key, "PollIntervalMs":
        // "fast") would otherwise stop the service from starting at all, before it can log why. The collector then runs
        // on its defaults and logs the problem.
        var probe = new ConfigurationBuilder()
            .AddJsonFile(CollectorConfigFile.DefaultPath, optional: true, reloadOnChange: false)
            .Build();
        try
        {
            _ = probe.GetSection(CollectorOptions.SectionName).Get<CollectorOptions>();
        }
        finally
        {
            (probe as IDisposable)?.Dispose();
        }

        builder.Configuration.AddJsonFile(CollectorConfigFile.DefaultPath, optional: true, reloadOnChange: false);
    }
    catch (Exception ex) when (ex is InvalidDataException or FormatException or InvalidOperationException or IOException
                                   or UnauthorizedAccessException or System.Text.Json.JsonException)
    {
        configProblem = ex.Message;
        Console.Error.WriteLine($"collector.json ignored: {ex.Message}");
    }
}

builder.Configuration.AddCommandLine(args, switchMappings);

builder.Services.AddWindowsService(options => options.ServiceName = "SpaceDeckCollector");
builder.Services.Configure<CollectorOptions>(builder.Configuration.GetSection(CollectorOptions.SectionName));
builder.Services.PostConfigure<CollectorOptions>(o =>
{
    o.ReplayFile = replayFile;
    o.ParentProcessId = parentProcessId;
});

var collectorOptions = builder.Configuration.GetSection(CollectorOptions.SectionName).Get<CollectorOptions>() ?? new CollectorOptions();
collectorOptions.ReplayFile = replayFile;
collectorOptions.ParentProcessId = parentProcessId;

// A replay must not touch the real collector's files (history, logs, settings), which may also be admin-only. Each
// replay window gets its own folder (by the app's process id), so two replays never append to the same log file.
var dataDirectory = isReplay
    ? Path.Combine(Path.GetTempPath(), "SpaceDeck-replay", (parentProcessId ?? Environment.ProcessId).ToString(CultureInfo.InvariantCulture))
    : CollectorConfigFile.DataDirectory;

DiskHistory? disk = null;
if (collectorOptions.DiskHistoryEnabled && !isReplay && dataDirectoryUsable)
{
    try
    {
        disk = new DiskHistory(new DiskHistoryOptions
        {
            Directory = Path.Combine(dataDirectory, "history"),
            RawHours = Math.Clamp(collectorOptions.DiskRawHours, 1, 24 * 7),
            MinuteDays = Math.Clamp(collectorOptions.DiskMinuteDays, 1, 366),
            MaxBytes = Math.Clamp(collectorOptions.DiskHistoryMaxMB, 50, 100_000) * 1024L * 1024L,
        });
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"Disk history disabled: {ex.Message}");
    }
}

var persistBlockedReason = dataDirectoryProblem is null ? null : $"Settings can't be saved: {dataDirectoryProblem}";
builder.Services.AddSingleton(new SensorHub(collectorOptions.HistoryCapacity(), collectorOptions.PollIntervalMs, disk));
builder.Services.AddSingleton<ISensorSourceFactory, DefaultSensorSourceFactory>();
builder.Services.AddHostedService(sp => new PollingService(
    sp.GetRequiredService<SensorHub>(),
    sp.GetRequiredService<ISensorSourceFactory>(),
    sp.GetRequiredService<IOptions<CollectorOptions>>(),
    sp.GetRequiredService<ILogger<PollingService>>(),
    configPath: null,
    persistBlockedReason: persistBlockedReason));
builder.Services.AddHostedService<PipeServer>();
if (parentProcessId is { } parentPid)
{
    builder.Services.AddHostedService(sp => new ParentProcessWatcher(parentPid, sp.GetRequiredService<IHostApplicationLifetime>()));
}

FileLoggerProvider? fileLogger = null;
if (dataDirectoryUsable)
{
    try
    {
        fileLogger = new FileLoggerProvider(Path.Combine(dataDirectory, "logs"));
        builder.Logging.AddProvider(fileLogger);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"File logging disabled: {ex.Message}");
    }
}

using var host = builder.Build();
var startupLogger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("SpaceDeck.Collector.Startup");
if (dataDirectoryProblem is not null)
{
    DataDirectorySecurity.LogUnsafe(startupLogger, CollectorConfigFile.DataDirectory, dataDirectoryProblem);
}

foreach (var path in removedEntries)
{
    DataDirectorySecurity.LogRemoved(startupLogger, path);
}

if (configProblem is not null)
{
    CollectorConfigFile.LogIgnored(startupLogger, CollectorConfigFile.DefaultPath, configProblem);
}

await host.RunAsync();
disk?.Dispose(); // writes the last minute and closes files

// Instances passed to AddProvider aren't disposed by the host; flush queued log lines ourselves.
fileLogger?.Dispose();
