using SpaceDeck.Collector.Storage;
using SpaceDeck.Core.Model;
using SpaceDeck.Core.Protocol;

namespace SpaceDeck.Collector.Polling;

/// <summary>
/// The collector's shared state: the current catalog, the latest values, source health and history.
/// The poll thread publishes; pipe clients subscribe and take snapshots.
/// </summary>
/// <remarks>Events are raised on the poll thread. Handlers must be quick and must not throw.</remarks>
public sealed class SensorHub
{
    private readonly Lock _lock = new();
    private SensorCatalog _catalog = SensorCatalog.Empty;
    private ValuesMessage? _latestValues;
    private SourceStatusEntry[] _statuses = [];
    private readonly Lock _configLock = new();
    private SetConfigRequest? _pendingConfig;
    private CollectorStatusMessage? _collectorStatus;
    private int _pollIntervalMs;

    // Min/max resets waiting for the poll thread, merged: "all sensors", or a set of ids (bounded, so a client
    // sending resets faster than the poll thread takes them can't grow memory).
    private const int MaxPendingResetIds = 10_000;
    private readonly HashSet<string> _pendingResetIds = new(StringComparer.Ordinal);
    private bool _pendingResetAll;

    public SensorHub(int historyCapacity, int pollIntervalMs, DiskHistory? disk = null)
    {
        History = new HistoryStore(historyCapacity);
        Disk = disk;
        _pollIntervalMs = CollectorOptions.ClampInterval(pollIntervalMs);
    }

    public HistoryStore History { get; }

    /// <summary>Long history on disk (null when disabled or unavailable).</summary>
    public DiskHistory? Disk { get; }

    /// <summary>Queues a min/max reset (null = all sensors); the poll thread applies it on its next read.</summary>
    public void RequestResetMinMax(string[]? sensorIds)
    {
        lock (_configLock)
        {
            if (sensorIds is null)
            {
                _pendingResetAll = true;
                _pendingResetIds.Clear();
                return;
            }

            if (_pendingResetAll)
            {
                return; // already covered
            }

            foreach (var id in sensorIds)
            {
                if (_pendingResetIds.Count >= MaxPendingResetIds)
                {
                    break; // more ids than any catalog has: the rest can't be real sensors
                }

                if (id is not null)
                {
                    _pendingResetIds.Add(id);
                }
            }
        }
    }

    /// <summary>Takes the queued resets: all sensors, or these ids (false and empty when nothing is queued).</summary>
    internal (bool All, string[] Ids) TakeResetRequests()
    {
        lock (_configLock)
        {
            var taken = (_pendingResetAll, _pendingResetIds.ToArray());
            _pendingResetAll = false;
            _pendingResetIds.Clear();
            return taken;
        }
    }

    public int PollIntervalMs => Volatile.Read(ref _pollIntervalMs);

    public event Action<SensorCatalog>? CatalogChanged;

    public event Action<ValuesMessage>? ValuesPublished;

    public event Action<SourceStatusEntry[]>? StatusChanged;

    /// <summary>The collector's settings changed (or were first published).</summary>
    public event Action<CollectorStatusMessage>? CollectorStatusChanged;

    /// <summary>Current settings/run mode, or null before the poll thread has started.</summary>
    public CollectorStatusMessage? CollectorStatus
    {
        get
        {
            lock (_lock)
            {
                return _collectorStatus;
            }
        }
    }

    /// <summary>Consistent view of the current state, for a newly connected client.</summary>
    public (SensorCatalog Catalog, ValuesMessage? Values, SourceStatusEntry[] Statuses) Snapshot()
    {
        lock (_lock)
        {
            return (_catalog, _latestValues, _statuses);
        }
    }

    /// <summary>Queues a settings change; the poll thread applies it before its next read.</summary>
    /// <remarks>Requests that arrive before the poll thread picks them up are merged (later fields win), so two quick
    /// partial changes (e.g. two switches) are both applied.</remarks>
    public void RequestConfig(SetConfigRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_configLock)
        {
            _pendingConfig = _pendingConfig is not { } p
                ? request
                : new SetConfigRequest(
                    request.PollIntervalMs ?? p.PollIntervalMs,
                    request.StorageEnabled ?? p.StorageEnabled,
                    request.NetworkEnabled ?? p.NetworkEnabled,
                    request.ControllersEnabled ?? p.ControllersEnabled,
                    request.Persist || p.Persist);
        }
    }

    /// <summary>True while a settings change is waiting (the poll loop wakes early for it).</summary>
    internal bool HasPendingConfig
    {
        get
        {
            lock (_configLock)
            {
                return _pendingConfig is not null;
            }
        }
    }

    internal SetConfigRequest? TakeConfigRequest()
    {
        lock (_configLock)
        {
            var request = _pendingConfig;
            _pendingConfig = null;
            return request;
        }
    }

    internal void SetPollInterval(int ms) => Volatile.Write(ref _pollIntervalMs, CollectorOptions.ClampInterval(ms));

    internal void PublishCatalog(SensorCatalog catalog)
    {
        lock (_lock)
        {
            _catalog = catalog;
            _latestValues = null;
        }

        string[] ids = [.. catalog.Sensors.Select(s => s.Id)];
        History.SetSensors(ids);
        try
        {
            Disk?.SetSensors(ids);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or FormatException or ArgumentException)
        {
            // Disk history is best effort (a damaged file included); polling must go on.
        }
        Raise(CatalogChanged, catalog);
    }

    internal void PublishValues(ValuesMessage values)
    {
        lock (_lock)
        {
            if (values.CatalogVersion != _catalog.Version)
            {
                return;
            }

            _latestValues = values;
        }

        History.Append(values.TimestampUnixMs, values.Values);
        try
        {
            Disk?.Append(values.TimestampUnixMs, values.Values);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or FormatException or ArgumentException)
        {
            // Disk history is best effort (a damaged file included); polling must go on.
        }
        Raise(ValuesPublished, values);
    }

    internal void PublishCollectorStatus(CollectorStatusMessage status)
    {
        lock (_lock)
        {
            _collectorStatus = status;
        }

        Raise(CollectorStatusChanged, status);
    }

    internal void PublishStatus(SourceStatusEntry[] statuses)
    {
        lock (_lock)
        {
            if (_statuses.SequenceEqual(statuses))
            {
                return;
            }

            _statuses = statuses;
        }

        Raise(StatusChanged, statuses);
    }

    private static void Raise<T>(Action<T>? handlers, T arg)
    {
        if (handlers is null)
        {
            return;
        }

        // Invoke one by one so a failing subscriber can't stop the others (or the poll loop).
        foreach (var handler in handlers.GetInvocationList().Cast<Action<T>>())
        {
            try
            {
                handler(arg);
            }
            catch
            {
                // Subscribers are pipe sessions; a broken one is cleaned up by its own read/write loop.
            }
        }
    }
}
