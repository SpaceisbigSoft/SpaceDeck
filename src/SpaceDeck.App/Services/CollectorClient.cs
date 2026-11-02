using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using SpaceDeck.Core.Model;
using SpaceDeck.Core.Protocol;

namespace SpaceDeck.App.Services;

public enum ConnectionState
{
    Connecting,
    Connected,
    Disconnected,
}

/// <summary>
/// Keeps a connection to the collector service open, reconnecting automatically, and raises events for
/// each message. Events are raised on a background thread; <see cref="SensorStore"/> marshals them to the UI.
/// </summary>
public sealed class CollectorClient : IDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    private readonly string _pipeName;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<HistoryResponse>> _pendingHistory = new();
    private NamedPipeClientStream? _pipe;
    private Task? _loop;
    private int _nextRequestId;
    private int _expectedServerProcessId;
    private string? _connectionProblem;

    public CollectorClient()
        : this(ProtocolInfo.PipeName)
    {
    }

    public CollectorClient(string pipeName)
    {
        _pipeName = pipeName;
    }

    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;

    /// <summary>The collector's latest catalog as received (before the app adds calculated sensors); null until one arrives.</summary>
    public SensorCatalog? LastCatalog => Volatile.Read(ref _lastCatalog);

    private SensorCatalog? _lastCatalog;

    public event Action<ConnectionState>? StateChanged;

    public event Action<HelloMessage>? HelloReceived;

    public event Action<SensorCatalog>? CatalogReceived;

    public event Action<ValuesMessage>? ValuesReceived;

    public event Action<SourceStatusEntry[]>? StatusReceived;

    public event Action<CollectorStatusMessage>? CollectorStatusReceived;

    /// <summary>
    /// Why the last connection was refused (another program answered on the pipe, see <see cref="PipeServerIdentity"/>);
    /// null while connected or when nothing answered. Raised only when it changes, so a squatter is reported once.
    /// </summary>
    public event Action<string?>? ConnectionProblemChanged;

    /// <summary>Same as the last <see cref="ConnectionProblemChanged"/>.</summary>
    public string? ConnectionProblem => Volatile.Read(ref _connectionProblem);

    /// <summary>
    /// Only accept a server that is this process (the replay collector the app started). Call before <see cref="Start"/>.
    /// Without it, the normal pipe accepts only the collector service (or an elevated development collector), and
    /// any other pipe accepts nothing.
    /// </summary>
    public void TrustServerProcess(int processId) => Volatile.Write(ref _expectedServerProcessId, processId);

    public void Start() => _loop ??= Task.Run(() => RunAsync(_cts.Token));

    /// <summary>Asks the collector for recent history. Throws if not connected or no reply arrives in time.</summary>
    public Task<HistoryResponse> RequestHistoryAsync(IReadOnlyList<string> sensorIds, int windowSeconds, CancellationToken cancellationToken = default) =>
        RequestHistoryAsync(new HistoryRequest(0, [.. sensorIds], windowSeconds), cancellationToken);

    /// <summary>
    /// Sends a history request (its RequestId is replaced) and waits for the answer. Disk reads can take a few
    /// seconds, so the timeout is generous. Throws if not connected or nothing arrives.
    /// </summary>
    public async Task<HistoryResponse> RequestHistoryAsync(HistoryRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var id = Interlocked.Increment(ref _nextRequestId);
        var tcs = new TaskCompletionSource<HistoryResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingHistory[id] = tcs;
        try
        {
            await SendAsync(request with { RequestId = id }, cancellationToken).ConfigureAwait(false);
            return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pendingHistory.TryRemove(id, out _);
        }
    }

    public Task SetConfigAsync(SetConfigRequest request, CancellationToken cancellationToken = default) =>
        SendAsync(request, cancellationToken);

    /// <summary>Restarts min/max for these sensors (null = all). Needs a protocol 3 collector.</summary>
    public Task ResetMinMaxAsync(IReadOnlyList<string>? sensorIds, CancellationToken cancellationToken = default) =>
        SendAsync(new ResetMinMaxRequest(sensorIds is null ? null : [.. sensorIds]), cancellationToken);

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // Shutting down.
        }

        _pipe?.Dispose();
        _cts.Dispose();
        _writeLock.Dispose();
    }

    private async Task SendAsync(IProtocolMessage message, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var pipe = _pipe ?? throw new InvalidOperationException("Not connected to the SpaceDeck collector.");
            try
            {
                // Never the caller's token: cancelling a half-written frame would desync the stream. Only shutdown (or
                // the pipe breaking) ends a write; the caller's token still cancels the wait for the lock above.
                await FrameCodec.WriteAsync(pipe, message, _cts.Token).ConfigureAwait(false);
            }
            catch (ObjectDisposedException ex)
            {
                // The connection dropped mid-write; report it like any other lost connection.
                throw new IOException("Disconnected from the SpaceDeck collector.", ex);
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            SetState(ConnectionState.Connecting);
            var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            string? rejected = null;
            try
            {
                await pipe.ConnectAsync((int)ConnectTimeout.TotalMilliseconds, token).ConfigureAwait(false);
                // Nothing is read from (or sent to) a server that isn't the collector; it is dropped and retried on
                // the normal schedule, so the real service is picked up once it starts.
                if (!IsTrustedServer(pipe, out rejected))
                {
                    rejected ??= PipeServerIdentity.NotCollectorMessage;
                }
                else
                {
                    SetConnectionProblem(null);
                    _pipe = pipe;
                    SetState(ConnectionState.Connected);
                    await ReadLoopAsync(pipe, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException or InvalidDataException
                                           or MessagePack.MessagePackSerializationException)
            {
                // Not running yet, or the connection dropped; retry below.
            }
            finally
            {
                _pipe = null;
                await pipe.DisposeAsync().ConfigureAwait(false);
                FailPendingHistory();
            }

            // Cleared again once a later attempt finds nobody answering, or the real collector.
            SetConnectionProblem(rejected);
            SetState(ConnectionState.Disconnected);
            try
            {
                await Task.Delay(RetryDelay, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ReadLoopAsync(Stream pipe, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var message = await FrameCodec.ReadAsync(pipe, token).ConfigureAwait(false);
            switch (message)
            {
                case null:
                    return;
                case HelloMessage hello:
                    HelloReceived?.Invoke(hello);
                    break;
                case CatalogMessage catalog:
                    Volatile.Write(ref _lastCatalog, catalog.Catalog);
                    CatalogReceived?.Invoke(catalog.Catalog);
                    break;
                case ValuesMessage values:
                    ValuesReceived?.Invoke(values);
                    break;
                case SourceStatusMessage status:
                    StatusReceived?.Invoke(status.Sources);
                    break;
                case CollectorStatusMessage collectorStatus:
                    CollectorStatusReceived?.Invoke(collectorStatus);
                    break;
                case HistoryResponse history:
                    if (_pendingHistory.TryRemove(history.RequestId, out var tcs))
                    {
                        tcs.TrySetResult(history);
                    }

                    break;
            }
        }
    }

    /// <summary>See <see cref="PipeServerIdentity"/> for the exact rules.</summary>
    private bool IsTrustedServer(NamedPipeClientStream pipe, out string? problem)
    {
        var expected = Volatile.Read(ref _expectedServerProcessId);
        if (expected != 0)
        {
            return PipeServerIdentity.IsProcess(pipe.SafePipeHandle, expected, out problem);
        }

        if (_pipeName == ProtocolInfo.PipeName)
        {
            var developmentFolder = PipeServerIdentity.DevelopmentCollectorFolder(AppContext.BaseDirectory, "SpaceDeck.App");
            return PipeServerIdentity.IsCollectorService(pipe.SafePipeHandle, developmentFolder, out problem);
        }

        problem = "This window's private connection has no collector to check against. Data is not shown.";
        return false;
    }

    private void SetConnectionProblem(string? problem)
    {
        if (Volatile.Read(ref _connectionProblem) == problem)
        {
            return;
        }

        Volatile.Write(ref _connectionProblem, problem);
        if (problem is not null)
        {
            // Once per change (not every retry): kept next to the app's other error log.
            LogFiles.Append(Path.Combine(AppSettings.Directory, "app-errors.log"),
                string.Create(CultureInfo.InvariantCulture, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  Refused a pipe connection on {_pipeName}: {problem}{Environment.NewLine}"));
        }

        ConnectionProblemChanged?.Invoke(problem);
    }

    private void FailPendingHistory()
    {
        foreach (var key in _pendingHistory.Keys)
        {
            if (_pendingHistory.TryRemove(key, out var tcs))
            {
                tcs.TrySetException(new IOException("Disconnected from the SpaceDeck collector."));
            }
        }
    }

    private void SetState(ConnectionState state)
    {
        if (State == state)
        {
            return;
        }

        State = state;
        StateChanged?.Invoke(state);
    }
}
