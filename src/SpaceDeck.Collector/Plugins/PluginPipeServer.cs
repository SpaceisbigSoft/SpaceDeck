using System.IO.Pipes;
using System.Runtime.InteropServices;
using SpaceDeck.Collector.Pipe;
using SpaceDeck.Core.PluginPipe;
using SpaceDeck.Integrations.FanControl;

namespace SpaceDeck.Collector.Plugins;

/// <summary>
/// Hosts <c>\\.\pipe\SpaceDeck.Plugins</c> (<c>docs/PLUGIN-PIPE.md</c>): SpaceDeck's FanControl plugin connects and sends
/// readings, which feed <see cref="PluginBridgeSource"/>. Readings only: nothing received is ever acted on.
/// </summary>
/// <remarks>
/// <para>Access is the app pipe's (<see cref="PipeServer.CreateServer"/>): any signed-in user may connect, so the client
/// process is checked (<see cref="IPluginClientVerifier"/>) before a byte is read, and everything it sends is checked by
/// <see cref="PluginPipeSession"/>. At most <see cref="MaxInstances"/> instances exist: FanControl's app and service can
/// overlap briefly while control moves between them; the newest connection with a valid catalog feeds the source and the
/// older one is closed.</para>
/// <para>A client is also cut off for lines over 64 KiB, <see cref="ReadTimeout"/> without a line, or more lines than the
/// rate limit allows. Every rejection reason is logged once; nothing a client does can throw into the host.</para>
/// </remarks>
public sealed partial class PluginPipeServer : BackgroundService
{
    internal const int MaxInstances = 2;

    /// <summary>No line for this long disconnects (the plugin sends values every second).</summary>
    internal static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Lines a second a client may send on average.</summary>
    internal const int LinesPerSecond = 5;

    /// <summary>
    /// Lines a client may send at once. Above <see cref="LinesPerSecond"/> so lines that queued in the pipe while the
    /// collector was busy (verifying the client takes a moment) arrive together without cutting the plugin off.
    /// </summary>
    internal const int LineBurst = 10;

    /// <summary>Distinct rejection reasons remembered for "log once" (bounds the memory a stream of odd clients can cost).</summary>
    private const int MaxLoggedReasons = 1000;

    private readonly PluginBridgeSource _bridge;
    private readonly IPluginClientVerifier _verifier;
    private readonly ILogger<PluginPipeServer> _logger;
    private readonly string _pipeName;
    private readonly TimeProvider _time;

    /// <summary>Guards <see cref="_current"/>, the bridge's feeding connection with it, and <see cref="_loggedReasons"/>.</summary>
    private readonly Lock _gate = new();
    private readonly HashSet<string> _loggedReasons = new(StringComparer.Ordinal);
    private Connection? _current;
    private long _nextConnectionId;

    /// <summary>Pipe instances of ours that exist (listening or connected).</summary>
    private int _instances;

    /// <param name="pipeName">Tests pass a unique name; the collector uses <see cref="PluginPipeProtocol.PipeName"/>.</param>
    /// <param name="timeProvider">Clock for the rate limit.</param>
    public PluginPipeServer(PluginBridgeSource bridge, IPluginClientVerifier verifier, ILogger<PluginPipeServer> logger,
        string? pipeName = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(logger);
        _bridge = bridge;
        _verifier = verifier;
        _logger = logger;
        _pipeName = pipeName ?? PluginPipeProtocol.PipeName;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>One accepted pipe connection; cancelling <see cref="Cancellation"/> closes it (shutdown, or a newer plugin took over).</summary>
    private sealed class Connection(long id, CancellationTokenSource cancellation)
    {
        public long Id { get; } = id;

        public CancellationTokenSource Cancellation { get; } = cancellation;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var connections = new List<Task>();
        var started = false;
        LogListening(_logger, _pipeName);

        while (!stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream server;

            // FirstPipeInstance whenever we hold no instance at all, so another process can't have taken the name in a gap.
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
                // Usually both instances are connected (FanControl's app and service overlapping) until the older one is
                // replaced or times out: expected, so it isn't logged. Anything else is logged once.
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

    /// <summary>Waits; false if the service is stopping.</summary>
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

    /// <summary>Checks the client, then reads its lines until it leaves, breaks a rule, is replaced or the service stops.</summary>
    private async Task ServeAsync(long id, NamedPipeServerStream pipe, CancellationToken stoppingToken)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var connection = new Connection(id, cancellation);
        try
        {
            var rejected = CheckClient(pipe, out var processId);
            if (rejected is null)
            {
                rejected = await ReadLinesAsync(connection, pipe, cancellation.Token).ConfigureAwait(false);
            }

            if (rejected is null)
            {
                LogClientLeft(_logger, id);
            }
            else if (FirstTime("rejected", rejected))
            {
                // A client that keeps reconnecting with the same problem is logged once, then turned away quietly.
                LogClientRejected(_logger, id, processId, rejected);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // Replaced by a newer connection, service stopping, or the client went away mid-read.
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
                }

                _bridge.Disconnect(id);
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

    /// <summary>Null if the client process is accepted; otherwise why not.</summary>
    private string? CheckClient(NamedPipeServerStream pipe, out int processId)
    {
        processId = 0;
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid))
        {
            return "couldn't tell which process connected";
        }

        processId = (int)pid;
        return _verifier.Check(processId, FanControlLocator.TryGetImagePath(processId));
    }

    /// <summary>
    /// Reads lines until the client closes the pipe (null) or breaks a rule (the reason). Lines are split here, with the
    /// 64 KiB cap enforced before a line is complete, so a client can't make the service buffer more than that.
    /// </summary>
    private async Task<string?> ReadLinesAsync(Connection connection, NamedPipeServerStream pipe, CancellationToken token)
    {
        var session = new PluginPipeSession();
        var buffer = new byte[PluginPipeProtocol.MaxLineBytes + 1]; // the longest line plus its '\n'
        var start = 0;
        var end = 0;

        // Token bucket for the rate limit: starts full, refills at LinesPerSecond, holds at most LineBurst.
        var tokens = (double)LineBurst;
        var refilled = _time.GetTimestamp();

        // Re-armed after every line, so the timeout means "no complete line for 30 s" (a trickle of bytes doesn't count).
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(ReadTimeout);

        while (true)
        {
            var newline = Array.IndexOf(buffer, (byte)'\n', start, end - start);
            if (newline < 0)
            {
                if (start > 0)
                {
                    Buffer.BlockCopy(buffer, start, buffer, 0, end - start);
                    end -= start;
                    start = 0;
                }

                if (end > PluginPipeProtocol.MaxLineBytes)
                {
                    return "a line is longer than 64 KiB";
                }

                int read;
                try
                {
                    read = await pipe.ReadAsync(buffer.AsMemory(end), timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    return "no line for 30 seconds";
                }

                if (read == 0)
                {
                    return null; // the client closed the pipe; a partial last line is dropped
                }

                end += read;
                continue;
            }

            var line = buffer.AsMemory(start, newline - start);
            start = newline + 1;
            timeout.CancelAfter(ReadTimeout);

            var now = _time.GetTimestamp();
            tokens = Math.Min(LineBurst, tokens + (_time.GetElapsedTime(refilled, now).TotalSeconds * LinesPerSecond));
            refilled = now;
            if (tokens < 1)
            {
                return "more than 5 lines a second";
            }

            tokens--;

            // Parsed before the next read: the line is a view of the buffer, which the next read overwrites.
            var result = session.Accept(line);
            if (result.Error is { } error)
            {
                return error;
            }

            switch (result.Message)
            {
                case PluginHello hello:
                    LogHello(_logger, connection.Id, hello.Plugin, hello.PluginVersion, hello.Instance);
                    break;

                case PluginCatalog catalog:
                    TakeOver(connection, catalog);
                    break;

                case PluginValues values:
                    _bridge.ApplyValues(connection.Id, values.Values);
                    break;
            }
        }
    }

    /// <summary>
    /// A valid catalog makes this connection the one that feeds the source (the newest is the live FanControl); an older
    /// one is closed. The bridge update and the swap happen under one lock, so two connections sending catalogs at once
    /// can't leave the bridge listening to the one that was closed.
    /// </summary>
    private void TakeOver(Connection connection, PluginCatalog catalog)
    {
        Connection? replaced = null;
        lock (_gate)
        {
            _bridge.ApplyCatalog(connection.Id, catalog);
            if (_current != connection)
            {
                replaced = _current;
                _current = connection;
            }
        }

        LogCatalog(_logger, connection.Id, catalog.Hardware.Count, catalog.Sensors.Count);
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

    /// <summary>True the first time <paramref name="kind"/> with <paramref name="reason"/> is seen: log it then, not again.</summary>
    private bool FirstTime(string kind, string reason)
    {
        lock (_gate)
        {
            return _loggedReasons.Count < MaxLoggedReasons && _loggedReasons.Add(kind + "|" + reason);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafeHandle pipe, out uint clientProcessId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Listening for FanControl plugins on named pipe '{PipeName}'.")]
    private static partial void LogListening(ILogger logger, string pipeName);

    [LoggerMessage(Level = LogLevel.Error, Message = "Named pipe '{PipeName}' is already owned by another process; FanControl plugin readings are off. Is another collector running?")]
    private static partial void LogPipeTaken(ILogger logger, Exception ex, string pipeName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to create or connect a plugin pipe instance (further identical errors are not logged).")]
    private static partial void LogCreateFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Plugin client {Id} (process {ProcessId}) disconnected: {Reason}. Further identical rejections are not logged.")]
    private static partial void LogClientRejected(ILogger logger, long id, int processId, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Plugin client {Id}: {Plugin} {PluginVersion} (instance {Instance}).")]
    private static partial void LogHello(ILogger logger, long id, string plugin, string pluginVersion, string instance);

    [LoggerMessage(Level = LogLevel.Information, Message = "Plugin client {Id} sent a catalog: {HardwareCount} hardware, {SensorCount} sensors.")]
    private static partial void LogCatalog(ILogger logger, long id, int hardwareCount, int sensorCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "Plugin client {OldId} is replaced by client {NewId}.")]
    private static partial void LogReplaced(ILogger logger, long oldId, long newId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Plugin client {Id} disconnected.")]
    private static partial void LogClientLeft(ILogger logger, long id);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Plugin client {Id} failed (further identical errors are not logged).")]
    private static partial void LogConnectionFailed(ILogger logger, Exception ex, long id);
}
