using System.IO.Pipes;
using System.Threading.Channels;
using SpaceDeck.Collector.Polling;
using SpaceDeck.Collector.Storage;
using SpaceDeck.Core.Model;
using SpaceDeck.Core.Protocol;

namespace SpaceDeck.Collector.Pipe;

/// <summary>
/// One connected UI client.
/// </summary>
/// <remarks>
/// Outgoing messages go through two lanes so a slow client can't stall the poll thread or lose a catalog:
/// control messages (hello, catalog, status, history) are queued in order and never dropped; value frames
/// keep only the latest one. Value frames are only sent after the catalog they belong to.
/// <para>Any signed-in user can connect, so a session also guards the service against misbehaving clients: frames it
/// reads are small (<see cref="MaxClientFrameBytes"/>), a client may have only <see cref="MaxPendingHistory"/> history
/// answers outstanding (further requests wait until one is sent, so answers can't pile up), all clients together at
/// most <see cref="MaxPendingHistoryTotal"/> (so 32 clients can't hold gigabytes of answers), and a client that stops reading is dropped after <see cref="WriteTimeout"/> instead of
/// queueing answers in memory forever.</para>
/// </remarks>
internal sealed partial class ClientSession
{
    /// <summary>Largest frame a client may send. Client messages are small (≤ 512 history ids, ≤ 10,000 reset ids).</summary>
    internal const int MaxClientFrameBytes = 1024 * 1024;

    /// <summary>
    /// History requests a client may have outstanding (asked, answer not yet sent). The app usually has one or two;
    /// more wait (the read loop stops reading) instead of disconnecting it, so a burst of clicks doesn't drop the app.
    /// </summary>
    internal const int MaxPendingHistory = 8;

    /// <summary>
    /// History answers being prepared or waiting to be sent, across all clients (each can be several MB). Twice
    /// <see cref="MaxPendingHistory"/>: the app never has more than that many of its own outstanding, so it never waits
    /// on the CLI or a second app (a replay window talks to its own collector process). Requests over the limit wait
    /// for a slot; none is dropped.
    /// </summary>
    internal const int MaxPendingHistoryTotal = 2 * MaxPendingHistory;

    /// <summary>See <see cref="MaxPendingHistoryTotal"/>.</summary>
    private static readonly SemaphoreSlim GlobalHistorySlots = new(MaxPendingHistoryTotal, MaxPendingHistoryTotal);

    /// <summary>A client that hasn't taken a frame for this long has stopped reading and is disconnected.</summary>
    internal static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(30);

    private static readonly string ServerVersion =
        typeof(ClientSession).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    private readonly int _id;
    private readonly NamedPipeServerStream _pipe;
    private readonly SensorHub _hub;
    private readonly ILogger _logger;
    private readonly Channel<IProtocolMessage> _control = Channel.CreateUnbounded<IProtocolMessage>(new UnboundedChannelOptions { SingleReader = true });
    private readonly SemaphoreSlim _wake = new(0);
    private ValuesMessage? _latestValues;
    private int _sentCatalogVersion = -1;
    private readonly SemaphoreSlim _historySlots = new(MaxPendingHistory, MaxPendingHistory);

    /// <summary>Guards <see cref="_globalSlotsHeld"/> and <see cref="_closed"/>.</summary>
    private readonly Lock _historyLock = new();

    /// <summary><see cref="GlobalHistorySlots"/> this session holds: answers being prepared or not yet sent.</summary>
    private int _globalSlotsHeld;

    /// <summary>Set when the session ends: its global slots are given back, and no new ones are taken.</summary>
    private bool _closed;

    public ClientSession(int id, NamedPipeServerStream pipe, SensorHub hub, ILogger logger)
    {
        _id = id;
        _pipe = pipe;
        _hub = hub;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        LogConnected(_logger, _id);

        // Hello first, then subscribe, then the snapshot: anything published in between is queued after Hello,
        // and the snapshot is never older than what a subscription delivered.
        EnqueueControl(new HelloMessage(ProtocolInfo.Version, ServerVersion, _hub.PollIntervalMs));
        _hub.CatalogChanged += OnCatalog;
        _hub.ValuesPublished += OnValues;
        _hub.StatusChanged += OnStatus;
        _hub.CollectorStatusChanged += OnCollectorStatus;

        try
        {
            var (catalog, values, statuses) = _hub.Snapshot();
            if (_hub.CollectorStatus is { } collectorStatus)
            {
                EnqueueControl(collectorStatus);
            }

            EnqueueControl(new SourceStatusMessage(statuses));
            EnqueueControl(new CatalogMessage(catalog));
            if (values is not null)
            {
                OnValues(values);
            }

            var writer = WriteLoopAsync(cts.Token);
            var reader = ReadLoopAsync(cts.Token);
            await Task.WhenAny(writer, reader).ConfigureAwait(false);
            await cts.CancelAsync().ConfigureAwait(false);

            try
            {
                await Task.WhenAll(writer, reader).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
            {
                // Normal disconnect or shutdown.
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // Client went away.
        }
        catch (Exception ex)
        {
            LogSessionFailed(_logger, ex, _id);
        }
        finally
        {
            _hub.CatalogChanged -= OnCatalog;
            _hub.ValuesPublished -= OnValues;
            _hub.StatusChanged -= OnStatus;
            _hub.CollectorStatusChanged -= OnCollectorStatus;
            ReleaseAllGlobalHistorySlots(); // answers that will never be sent
            await _pipe.DisposeAsync().ConfigureAwait(false);
            _wake.Dispose();
            _historySlots.Dispose();
            LogDisconnected(_logger, _id);
        }
    }

    private void OnCatalog(SensorCatalog catalog) => EnqueueControl(new CatalogMessage(catalog));

    private void OnStatus(SourceStatusEntry[] statuses) => EnqueueControl(new SourceStatusMessage(statuses));

    private void OnCollectorStatus(CollectorStatusMessage status) => EnqueueControl(status);

    private void OnValues(ValuesMessage values)
    {
        Interlocked.Exchange(ref _latestValues, values);
        Wake();
    }

    private void EnqueueControl(IProtocolMessage message)
    {
        _control.Writer.TryWrite(message);
        Wake();
    }

    private void Wake()
    {
        try
        {
            // Cap the count: one pending wake is enough, the loop drains everything each time.
            if (_wake.CurrentCount == 0)
            {
                _wake.Release();
            }
        }
        catch (ObjectDisposedException)
        {
            // Session is closing.
        }
    }

    private async Task WriteLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            await _wake.WaitAsync(token).ConfigureAwait(false);

            while (_control.Reader.TryRead(out var message))
            {
                if (message is CatalogMessage catalog)
                {
                    // The connect-time snapshot can land behind a newer catalog the event delivered (published between
                    // Snapshot() and the enqueue). Versions only grow within a collector run, so never step back: values
                    // carry the newer version and would all be dropped.
                    if (catalog.Catalog.Version < _sentCatalogVersion)
                    {
                        continue;
                    }

                    _sentCatalogVersion = catalog.Catalog.Version;
                }

                await WriteFrameAsync(message, token).ConfigureAwait(false);
                if (message is HistoryResponse)
                {
                    _historySlots.Release();
                    ReturnGlobalHistorySlot();
                }
            }

            var values = Interlocked.Exchange(ref _latestValues, null);
            if (values is not null && values.CatalogVersion == _sentCatalogVersion)
            {
                await WriteFrameAsync(values, token).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Writes one frame; a client that doesn't take it within <see cref="WriteTimeout"/> ends the session.</summary>
    private async Task WriteFrameAsync(IProtocolMessage message, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(WriteTimeout);
        try
        {
            await FrameCodec.WriteAsync(_pipe, message, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            LogNotReading(_logger, _id);
            throw new IOException("The client stopped reading.");
        }
    }

    private async Task ReadLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var message = await FrameCodec.ReadAsync(_pipe, MaxClientFrameBytes, token).ConfigureAwait(false);
            switch (message)
            {
                case null:
                    return; // client disconnected

                case HistoryRequest request:
                    // Asking faster than the answers are sent: wait for a slot instead of queueing more answers in
                    // the service's memory. Cancelled when the session ends.
                    await _historySlots.WaitAsync(token).ConfigureAwait(false);

                    // Disk reads can take a moment: answer off the read loop so other requests keep flowing.
                    _ = Task.Run(() => AnswerHistoryAsync(request, token), token);
                    break;

                case ResetMinMaxRequest reset:
                    LogResetRequested(_logger, _id, reset.SensorIds?.Length);
                    _hub.RequestResetMinMax(reset.SensorIds is null ? null : [.. reset.SensorIds.Where(id => id is not null).Take(10_000)]);
                    break;

                case SetConfigRequest config:
                    LogConfigRequested(_logger, _id, config.PollIntervalMs, config.StorageEnabled, config.NetworkEnabled, config.ControllersEnabled, config.Persist);
                    _hub.RequestConfig(config);
                    break;

                default:
                    // Server-to-client message types sent by a client are ignored.
                    break;
            }
        }
    }

    /// <summary>At most two history queries run at once across all clients (disk reads are heavy).</summary>
    private static readonly SemaphoreSlim HistoryGate = new(2);

    private async Task AnswerHistoryAsync(HistoryRequest request, CancellationToken token)
    {
        // A slot is held from here until the answer is sent (WriteLoopAsync) or the session ends.
        try
        {
            await GlobalHistorySlots.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return; // session closing
        }

        lock (_historyLock)
        {
            if (_closed)
            {
                GlobalHistorySlots.Release();
                return;
            }

            _globalSlotsHeld++;
        }

        try
        {
            await HistoryGate.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            ReturnGlobalHistorySlot(); // no answer will come
            return; // session closing
        }

        try
        {
            EnqueueControl(HistoryQuery.Answer(_hub, request, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) // any failure must still answer, or the client waits
        {
            LogHistoryFailed(_logger, ex, _id);
            EnqueueControl(new HistoryResponse(request.RequestId, [], [], []));
        }
        finally
        {
            HistoryGate.Release();
        }
    }

    /// <summary>Gives back one of this session's global history slots (an answer was sent, or none will come).</summary>
    private void ReturnGlobalHistorySlot()
    {
        lock (_historyLock)
        {
            if (_globalSlotsHeld > 0)
            {
                _globalSlotsHeld--;
                GlobalHistorySlots.Release();
            }
        }
    }

    /// <summary>The session ended: every slot it holds goes back, and requests still being answered take no new one.</summary>
    private void ReleaseAllGlobalHistorySlots()
    {
        lock (_historyLock)
        {
            _closed = true;
            if (_globalSlotsHeld > 0)
            {
                GlobalHistorySlots.Release(_globalSlotsHeld);
                _globalSlotsHeld = 0;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Client {Id} stopped reading; disconnecting it.")]
    private static partial void LogNotReading(ILogger logger, int id);

    [LoggerMessage(Level = LogLevel.Information, Message = "Client {Id} reset min/max ({Count} sensors; empty = all).")]
    private static partial void LogResetRequested(ILogger logger, int id, int? count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Client {Id}: history query failed.")]
    private static partial void LogHistoryFailed(ILogger logger, Exception ex, int id);

    [LoggerMessage(Level = LogLevel.Information, Message = "Client {Id} connected.")]
    private static partial void LogConnected(ILogger logger, int id);

    [LoggerMessage(Level = LogLevel.Information, Message = "Client {Id} disconnected.")]
    private static partial void LogDisconnected(ILogger logger, int id);

    [LoggerMessage(Level = LogLevel.Information, Message = "Client {Id} requested config: interval {IntervalMs}, storage {Storage}, network {Network}, controllers {Controllers}, save {Persist}.")]
    private static partial void LogConfigRequested(ILogger logger, int id, int? intervalMs, bool? storage, bool? network, bool? controllers, bool persist);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Client {Id} session failed.")]
    private static partial void LogSessionFailed(ILogger logger, Exception ex, int id);
}
