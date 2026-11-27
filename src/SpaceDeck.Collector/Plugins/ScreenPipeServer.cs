using System.IO.Pipes;
using SpaceDeck.Collector.Pipe;
using SpaceDeck.Core.Screens;

namespace SpaceDeck.Collector.Plugins;

/// <summary>
/// Hosts <c>\\.\pipe\SpaceDeck.Screen</c> (<c>docs/SCREEN-PIPE.md</c> §3): SpaceDeck's FanControl plugin connects and
/// reads cooler screen frames, which it sends to the cooler. Collector → plugin only; nothing the client sends is accepted.
/// </summary>
/// <remarks>
/// <para>Access and instances as the plugin pipe (<see cref="PluginPipeServer"/>): the app pipe's ACL, FirstPipeInstance,
/// at most <see cref="MaxInstances"/> instances, and the same client check (<see cref="PluginPipeClient"/>, FanControl in
/// a folder no untrusted account can write to) before a byte is written. One client at a time: a newly verified connection
/// replaces the older one (the newest is the live FanControl).</para>
/// <para>Frames come from <see cref="ScreenRelay.Mailbox"/>: each is written once, the newest per screen, never one older
/// than 5 s, and at most one message every <see cref="MinMessageGap"/> in all (the plugin disconnects above 4 a second on
/// average). A client that doesn't take a frame within <see cref="WriteTimeout"/>, or writes anything, is disconnected.</para>
/// </remarks>
public sealed partial class ScreenPipeServer : BackgroundService
{
    internal const int MaxInstances = 2;

    /// <summary>A frame not taken by the client within this long ends the connection (the plugin reads all the time).</summary>
    internal static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(10);

    /// <summary>At most 4 messages a second, evenly: under the plugin's limit (4 a second on average, bursts of 8).</summary>
    internal static readonly TimeSpan MinMessageGap = TimeSpan.FromMilliseconds(250);

    private const int MaxLoggedReasons = 1000;

    private readonly ScreenRelay _relay;
    private readonly IPluginClientVerifier _verifier;
    private readonly ILogger<ScreenPipeServer> _logger;
    private readonly string _pipeName;
    private readonly TimeProvider _time;

    /// <summary>Guards <see cref="_current"/> and <see cref="_loggedReasons"/>.</summary>
    private readonly Lock _gate = new();
    private readonly HashSet<string> _loggedReasons = new(StringComparer.Ordinal);
    private Connection? _current;
    private long _nextConnectionId;
    private int _instances;

    /// <param name="pipeName">Tests pass a unique name; the collector uses <see cref="ScreenPipeProtocol.PipeName"/>.</param>
    public ScreenPipeServer(ScreenRelay relay, IPluginClientVerifier verifier, ILogger<ScreenPipeServer> logger,
        string? pipeName = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(relay);
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(logger);
        _relay = relay;
        _verifier = verifier;
        _logger = logger;
        _pipeName = pipeName ?? ScreenPipeProtocol.PipeName;
        _time = timeProvider ?? TimeProvider.System;
        relay.Mailbox.FrameReady += WakeCurrent;
    }

    /// <summary>One verified connection. <see cref="Wake"/> is released when a frame is waiting.</summary>
    private sealed class Connection(long id, CancellationTokenSource cancellation) : IDisposable
    {
        public long Id { get; } = id;

        public CancellationTokenSource Cancellation { get; } = cancellation;

        public SemaphoreSlim Wake { get; } = new(0, 1);

        public void Signal()
        {
            try
            {
                if (Wake.CurrentCount == 0)
                {
                    Wake.Release();
                }
            }
            catch (Exception ex) when (ex is SemaphoreFullException or ObjectDisposedException)
            {
                // Already signalled, or the connection just ended.
            }
        }

        public void Dispose() => Wake.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var connections = new List<Task>();
        var started = false;
        LogListening(_logger, _pipeName);

        while (!stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            var first = Volatile.Read(ref _instances) == 0;
            try
            {
                server = PipeServer.CreateServer(_pipeName, first, MaxInstances);
            }
            catch (UnauthorizedAccessException ex) when (!started)
            {
                LogPipeTaken(_logger, ex, _pipeName);
                break;
            }
            catch (Exception ex)
            {
                if (Volatile.Read(ref _instances) < MaxInstances && FirstTime("create", ex.Message))
                {
                    LogCreateFailed(_logger, ex);
                }

                if (!await DelayAsync(TimeSpan.FromMilliseconds(500), stoppingToken).ConfigureAwait(false))
                {
                    break;
                }

                continue;
            }

            started = true;
            Interlocked.Increment(ref _instances);
            try
            {
                await server.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await server.DisposeAsync().ConfigureAwait(false);
                Interlocked.Decrement(ref _instances);
                if (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                if (FirstTime("connect", ex.Message))
                {
                    LogCreateFailed(_logger, ex);
                }

                if (!await DelayAsync(TimeSpan.FromMilliseconds(500), stoppingToken).ConfigureAwait(false))
                {
                    break;
                }

                continue;
            }

            connections.RemoveAll(t => t.IsCompleted);
            var id = Interlocked.Increment(ref _nextConnectionId);
            connections.Add(Task.Run(() => ServeAsync(id, server, stoppingToken), CancellationToken.None));
        }

        try
        {
            await Task.WhenAll(connections).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Connections log their own failures; shutdown proceeds regardless.
        }
    }

    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Checks the client, takes over from the previous one, then writes frames until it leaves or is replaced.</summary>
    private async Task ServeAsync(long id, NamedPipeServerStream pipe, CancellationToken stoppingToken)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        using var connection = new Connection(id, cancellation);
        var processId = 0;
        try
        {
            var rejected = PluginPipeClient.Check(pipe, _verifier, out processId);
            if (rejected is null)
            {
                TakeOver(connection);
                rejected = await RunAsync(connection, pipe, cancellation.Token).ConfigureAwait(false);
            }

            if (rejected is null)
            {
                LogClientLeft(_logger, id);
            }
            else if (FirstTime("rejected", rejected))
            {
                LogClientRejected(_logger, id, processId, rejected);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // Replaced by a newer connection, service stopping, or the client went away mid-write.
            LogClientLeft(_logger, id);
        }
        catch (Exception ex)
        {
            if (FirstTime("failed", $"{ex.GetType().FullName}: {ex.Message}"))
            {
                LogConnectionFailed(_logger, ex, id);
            }
        }
        finally
        {
            lock (_gate)
            {
                if (_current == connection)
                {
                    _current = null;
                    _relay.SetConnected(false);
                }
            }

            try
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A broken pipe can fail to flush on close; it is gone either way.
            }

            Interlocked.Decrement(ref _instances);
        }
    }

    /// <summary>This verified connection gets the frames from now on; an older one is closed.</summary>
    private void TakeOver(Connection connection)
    {
        Connection? replaced;
        lock (_gate)
        {
            replaced = _current;
            _current = connection;
            _relay.SetConnected(true);
        }

        LogClientConnected(_logger, connection.Id);
        connection.Signal(); // frames may already be waiting
        if (replaced is not null)
        {
            LogReplaced(_logger, replaced.Id, connection.Id);
            try
            {
                replaced.Cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // It ended on its own meanwhile.
            }
        }
    }

    private void WakeCurrent()
    {
        Connection? current;
        lock (_gate)
        {
            current = _current;
        }

        current?.Signal();
    }

    /// <summary>
    /// Writes frames until the client closes the pipe (null) or breaks a rule (the reason). A read runs alongside: the
    /// client never writes, so it only ends when the client goes away (or sends something it mustn't).
    /// </summary>
    private async Task<string?> RunAsync(Connection connection, NamedPipeServerStream pipe, CancellationToken token)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var reader = WatchForCloseAsync(pipe, stop.Token);
        var writer = WriteFramesAsync(connection, pipe, stop.Token);
        var finished = await Task.WhenAny(reader, writer).ConfigureAwait(false);
        await stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(reader, writer).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // The other half, stopped because this one finished.
        }

        token.ThrowIfCancellationRequested(); // replaced or stopping: not a rejection
        return finished.IsCompletedSuccessfully ? await finished.ConfigureAwait(false) : null;
    }

    /// <summary>Null when the client closed the pipe; a reason when it wrote to it.</summary>
    private static async Task<string?> WatchForCloseAsync(NamedPipeServerStream pipe, CancellationToken token)
    {
        var buffer = new byte[1];
        try
        {
            var read = await pipe.ReadAsync(buffer, token).ConfigureAwait(false);
            return read == 0 ? null : "the client wrote to the screen pipe (it is collector → plugin only)";
        }
        catch (IOException)
        {
            return null; // broken pipe: the client went away
        }
    }

    private async Task<string?> WriteFramesAsync(Connection connection, NamedPipeServerStream pipe, CancellationToken token)
    {
        var lastWrite = long.MinValue;
        while (true)
        {
            await connection.Wake.WaitAsync(token).ConfigureAwait(false);
            while (true)
            {
                if (lastWrite != long.MinValue)
                {
                    var since = _time.GetElapsedTime(lastWrite);
                    if (since < MinMessageGap)
                    {
                        await Task.Delay(MinMessageGap - since, _time, token).ConfigureAwait(false);
                    }
                }

                // Taken after the pacing wait, so a frame that was replaced meanwhile goes out as its newest version.
                if (_relay.Mailbox.Take() is not { } frame)
                {
                    break;
                }

                var message = ScreenPipeProtocol.Encode(frame.ScreenId, frame.Jpeg);
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    timeout.CancelAfter(WriteTimeout);
                    try
                    {
                        await pipe.WriteAsync(message, timeout.Token).ConfigureAwait(false);
                        await pipe.FlushAsync(timeout.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested)
                    {
                        return "the client didn't take a frame for 10 seconds";
                    }
                }

                lastWrite = _time.GetTimestamp();
                _relay.CountSent();
            }
        }
    }

    private bool FirstTime(string kind, string reason)
    {
        lock (_gate)
        {
            return _loggedReasons.Count < MaxLoggedReasons && _loggedReasons.Add(kind + "|" + reason);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Listening for FanControl's screen client on named pipe '{PipeName}'.")]
    private static partial void LogListening(ILogger logger, string pipeName);

    [LoggerMessage(Level = LogLevel.Error, Message = "Named pipe '{PipeName}' is already owned by another process; cooler screens are off. Is another collector running?")]
    private static partial void LogPipeTaken(ILogger logger, Exception ex, string pipeName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to create or connect a screen pipe instance (further identical errors are not logged).")]
    private static partial void LogCreateFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Screen client {Id} (process {ProcessId}) disconnected: {Reason}. Further identical rejections are not logged.")]
    private static partial void LogClientRejected(ILogger logger, long id, int processId, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Screen client {Id} connected.")]
    private static partial void LogClientConnected(ILogger logger, long id);

    [LoggerMessage(Level = LogLevel.Information, Message = "Screen client {OldId} is replaced by client {NewId}.")]
    private static partial void LogReplaced(ILogger logger, long oldId, long newId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Screen client {Id} disconnected.")]
    private static partial void LogClientLeft(ILogger logger, long id);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Screen client {Id} failed (further identical errors are not logged).")]
    private static partial void LogConnectionFailed(ILogger logger, Exception ex, long id);
}
