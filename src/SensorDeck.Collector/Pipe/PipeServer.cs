using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Options;
using SensorDeck.Collector.Polling;

namespace SensorDeck.Collector.Pipe;

/// <summary>
/// Accepts UI clients on <c>\\.\pipe\SensorDeck</c> and runs a <see cref="ClientSession"/> for each.
/// </summary>
/// <remarks>
/// Access: any signed-in (authenticated) user may connect and read/write messages; only SYSTEM, administrators
/// and the account running the collector can create pipe instances. The first instance is created with
/// FirstPipeInstance so another process can't squat on the name before us; after that a listening or connected
/// instance always holds the name. At most <see cref="MaxSessions"/> clients are served at once; others are disconnected right away.
/// </remarks>
public sealed partial class PipeServer : BackgroundService
{
    /// <summary>Clients served at once (the app, the CLI…). Bounds what a flood of connections can cost.</summary>
    internal const int MaxSessions = 32;

    private readonly SensorHub _hub;
    private readonly ILogger<PipeServer> _logger;
    private readonly string _pipeName;
    private int _nextSessionId;

    public PipeServer(SensorHub hub, IOptions<CollectorOptions> options, ILogger<PipeServer> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _hub = hub;
        _logger = logger;
        _pipeName = options.Value.PipeName;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var sessions = new List<Task>();
        var first = true;
        var full = false;

        // A client turned away because all slots were taken. It is closed only once the next listening instance
        // exists, so the pipe name is never without an instance of ours (another process could take it over).
        NamedPipeServerStream? rejected = null;
        LogListening(_logger, _pipeName);

        while (!stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try
            {
                server = CreateServer(_pipeName, first);
                first = false;
            }
            catch (UnauthorizedAccessException ex) when (first)
            {
                LogPipeTaken(_logger, ex, _pipeName);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Transient (e.g. instance limit); back off briefly.
                LogCreateFailed(_logger, ex);
                await Task.Delay(1000, stoppingToken).ConfigureAwait(false);
                continue;
            }

            if (rejected is not null)
            {
                await rejected.DisposeAsync().ConfigureAwait(false);
                rejected = null;
            }

            try
            {
                await server.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await server.DisposeAsync().ConfigureAwait(false);
                break;
            }
            catch (IOException ex)
            {
                LogCreateFailed(_logger, ex);
                await server.DisposeAsync().ConfigureAwait(false);
                continue;
            }

            sessions.RemoveAll(t => t.IsCompleted);
            if (sessions.Count >= MaxSessions)
            {
                if (!full)
                {
                    full = true;
                    LogTooManyClients(_logger, MaxSessions);
                }

                rejected = server;
                continue;
            }

            full = false;
            var id = Interlocked.Increment(ref _nextSessionId);
            var session = new ClientSession(id, server, _hub, _logger);
            sessions.Add(session.RunAsync(stoppingToken));
        }

        if (rejected is not null)
        {
            await rejected.DisposeAsync().ConfigureAwait(false);
        }

        try
        {
            await Task.WhenAll(sessions).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Sessions log their own failures; shutdown proceeds regardless.
        }
    }

    internal static NamedPipeServerStream CreateServer(string pipeName, bool firstInstance)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        // The account running the collector (SYSTEM as a service; you when debugging) must be able to add instances.
        using (var identity = WindowsIdentity.GetCurrent())
        {
            if (identity.User is { } user)
            {
                security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
            }
        }

        var options = PipeOptions.Asynchronous | (firstInstance ? PipeOptions.FirstPipeInstance : PipeOptions.None);
        return NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            options,
            inBufferSize: 0,
            outBufferSize: 0,
            security);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Listening on named pipe '{PipeName}'.")]
    private static partial void LogListening(ILogger logger, string pipeName);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Named pipe '{PipeName}' is already owned by another process. Is another collector running?")]
    private static partial void LogPipeTaken(ILogger logger, Exception ex, string pipeName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Max} clients are connected; turning further clients away until one leaves.")]
    private static partial void LogTooManyClients(ILogger logger, int max);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to create or connect a pipe instance.")]
    private static partial void LogCreateFailed(ILogger logger, Exception ex);
}
