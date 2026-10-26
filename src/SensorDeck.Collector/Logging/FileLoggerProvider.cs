using System.Globalization;
using System.Text;
using System.Threading.Channels;

namespace SensorDeck.Collector.Logging;

/// <summary>
/// Minimal daily-rolling file logger: <c>collector-yyyyMMdd.log</c>, keeping the last <c>retainDays</c> files, at most
/// <see cref="MaxBytesPerDay"/> per day and <see cref="MaxTotalBytes"/> in all (a stuck error can't fill the disk).
/// Writes happen on a background task so logging never blocks the poll thread.
/// </summary>
/// <remarks>Level filtering comes from the standard "Logging" configuration section.</remarks>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _directory;
    private readonly int _retainDays;
    private readonly Channel<string> _lines = Channel.CreateBounded<string>(new BoundedChannelOptions(10_000)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
    });
    private readonly Task _writer;

    public const long MaxBytesPerDay = 20L * 1024 * 1024;
    public const long MaxTotalBytes = 100L * 1024 * 1024;

    public FileLoggerProvider(string directory, int retainDays = 7)
    {
        _directory = directory;
        _retainDays = Math.Max(1, retainDays);
        Directory.CreateDirectory(directory);
        DeleteOldFiles();
        _writer = Task.Run(WriteLoopAsync);
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
        _lines.Writer.TryComplete();
        try
        {
            _writer.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // Best effort on shutdown.
        }
    }

    internal void Enqueue(string line) => _lines.Writer.TryWrite(line);

    private async Task WriteLoopAsync()
    {
        StreamWriter? writer = null;
        var currentDay = DateOnly.MinValue;
        var capped = false;
        try
        {
            await foreach (var line in _lines.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                var today = DateOnly.FromDateTime(DateTime.Now);
                if (writer is null || today != currentDay)
                {
                    if (writer is not null)
                    {
                        await writer.DisposeAsync().ConfigureAwait(false);
                    }

                    currentDay = today;
                    var path = Path.Combine(_directory, $"collector-{today.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.log");
                    var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                    writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
                    capped = false;
                    DeleteOldFiles();
                }

                if (writer.BaseStream.Length >= MaxBytesPerDay)
                {
                    if (!capped)
                    {
                        capped = true;
                        await writer.WriteLineAsync("… log size limit for today reached; further lines are dropped until tomorrow.").ConfigureAwait(false);
                    }

                    continue;
                }

                await writer.WriteLineAsync(line).ConfigureAwait(false);
            }
        }
        catch (IOException)
        {
            // Disk full or file locked: stop file logging rather than crash the collector.
        }
        catch (UnauthorizedAccessException)
        {
        }
        finally
        {
            if (writer is not null)
            {
                await writer.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private void DeleteOldFiles()
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-_retainDays);
            var files = new DirectoryInfo(_directory).EnumerateFiles("collector-*.log").OrderBy(f => f.LastWriteTime).ToList();
            foreach (var file in files.Where(f => f.LastWriteTime < cutoff).ToList())
            {
                file.Delete();
                files.Remove(file);
            }

            // Then the oldest while over the total cap (today's file, the newest, stays).
            var total = files.Sum(f => f.Length);
            foreach (var file in files.Take(Math.Max(0, files.Count - 1)))
            {
                if (total <= MaxTotalBytes)
                {
                    break;
                }

                total -= file.Length;
                file.Delete();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cleanup is optional.
        }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var sb = new StringBuilder();
            sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
              .Append(" [").Append(Short(logLevel)).Append("] ")
              .Append(category).Append(": ")
              .Append(formatter(state, exception));
            if (exception is not null)
            {
                sb.AppendLine().Append(exception);
            }

            provider.Enqueue(sb.ToString());
        }

        private static string Short(LogLevel level) => level switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            LogLevel.Critical => "CRT",
            _ => "---",
        };
    }
}
