using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using SpaceDeck.Core.Model;
using SpaceDeck.Core.Protocol;
using SpaceDeck.Core.Replay;

namespace SpaceDeck.App.Services;

/// <summary>
/// Records the raw sensor stream from the collector into an <c>.sdrec</c> file that can be replayed later
/// (Diagnostics → Open replay) or sent to someone testing SpaceDeck on other hardware.
/// </summary>
public sealed partial class ReplayRecorder : ObservableObject, IDisposable
{
    /// <summary>Stops by itself after this long (about 10–40 MB uncompressed in memory when replayed).</summary>
    public static readonly TimeSpan MaxDuration = TimeSpan.FromMinutes(60);

    private readonly CollectorClient _client;
    private readonly Lock _lock = new();
    private SensorRecordingWriter? _writer;
    private SensorCatalog? _catalog;
    private string? _path;

    [ObservableProperty]
    private bool _isRecording;

    public ReplayRecorder(CollectorClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        client.CatalogReceived += OnCatalog;
        client.ValuesReceived += OnValues;
    }

    /// <summary>Where recordings go by default.</summary>
    public static string Directory => Path.Combine(AppSettings.Directory, "replays");

    /// <summary>Raised (on a background thread) when recording stopped by itself (time limit or write error); the argument is the file.</summary>
    public event Action<string>? AutoStopped;

    public TimeSpan Elapsed
    {
        get
        {
            lock (_lock)
            {
                return TimeSpan.FromMilliseconds(_writer?.DurationMs ?? 0);
            }
        }
    }

    /// <summary>Starts recording to <paramref name="path"/>. Throws IOException / UnauthorizedAccessException if it can't be created.</summary>
    public void Start(string path, string? description)
    {
        lock (_lock)
        {
            StopLocked();
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            _writer = new SensorRecordingWriter(stream, description);
            _path = path;
            _catalog = _client.LastCatalog;
            try
            {
                if (_catalog is not null)
                {
                    _writer.WriteCatalog(_catalog);
                }
            }
            catch (IOException)
            {
                StopLocked();
                throw;
            }
        }

        IsRecording = true;
    }

    /// <summary>Stops and closes the file. Returns its path and how many samples it holds (null if nothing was recording).</summary>
    public (string Path, int Frames)? Stop()
    {
        (string, int)? result;
        lock (_lock)
        {
            result = StopLocked();
        }

        IsRecording = false;
        return result;
    }

    public void Dispose()
    {
        _client.CatalogReceived -= OnCatalog;
        _client.ValuesReceived -= OnValues;
        Stop();
    }

    private (string, int)? StopLocked()
    {
        if (_writer is null)
        {
            return null;
        }

        var frames = _writer.FrameCount;
        try
        {
            _writer.Dispose();
        }
        catch (IOException)
        {
            // Disk full or similar; the file may be cut short but stays readable up to the last whole line.
        }

        _writer = null;
        var path = _path!;
        _path = null;
        return (path, frames);
    }

    private void OnCatalog(SensorCatalog catalog)
    {
        string? stoppedPath;
        lock (_lock)
        {
            _catalog = catalog;
            stoppedPath = Write(w => w.WriteCatalog(catalog));
        }

        Stopped(stoppedPath);
    }

    private void OnValues(ValuesMessage values)
    {
        string? stoppedPath;
        lock (_lock)
        {
            if (_writer is null || _catalog is null || values.CatalogVersion != _catalog.Version)
            {
                return;
            }

            stoppedPath = Write(w => w.WriteValues(values.TimestampUnixMs, values.Values));
            if (stoppedPath is null && _writer is not null && TimeSpan.FromMilliseconds(_writer.DurationMs) >= MaxDuration)
            {
                stoppedPath = StopLocked()?.Item1;
            }
        }

        Stopped(stoppedPath);
    }

    private void Stopped(string? path)
    {
        if (path is not null)
        {
            IsRecording = false;
            AutoStopped?.Invoke(path);
        }
    }

    /// <summary>Writes; on an I/O error (e.g. disk full) stops and returns the file's path, keeping what was written.</summary>
    private string? Write(Action<SensorRecordingWriter> write)
    {
        if (_writer is null)
        {
            return null;
        }

        try
        {
            write(_writer);
            return null;
        }
        catch (IOException)
        {
            return StopLocked()?.Item1;
        }
    }
}
