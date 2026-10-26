using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace SensorDeck.Core.Protocol;

/// <summary>
/// Checks which process answers on a SensorDeck pipe before a client trusts its data. While the collector service is
/// stopped, any local program can create <c>\\.\pipe\SensorDeck</c> and feed the app forged readings (which drive
/// alerts, phone pushes and automation), so a client only accepts:
/// <list type="bullet">
/// <item>Normal pipe, service running (or starting/stopping): the pipe's server process id must be the
/// <see cref="ServiceName"/> service's process id. Nothing else is accepted while the service has a process.</item>
/// <item>Normal pipe, service stopped or not installed (developer running the collector from a terminal): the server's
/// image must be <see cref="CollectorExeName"/> under Program Files or the source tree's collector build output
/// (<see cref="DevelopmentCollectorFolder"/>, only when the client itself runs from that tree), <b>and</b> it must run
/// elevated: its token says TokenElevation, or its token can't be opened for query (expected for an elevated process
/// seen from an unelevated one) while it runs in this client's own Windows session. Other users' processes run in
/// their own session (and usually can't be opened at all); an unelevated process of this user has a readable token
/// that isn't elevated. Either way it is rejected.</item>
/// <item>Replay pipe: the server must be the exact collector process the app started (<see cref="IsProcess"/>).</item>
/// </list>
/// Everything is fail-closed: anything that can't be checked counts as "not the collector".
/// </summary>
public static class PipeServerIdentity
{
    /// <summary>Windows service name of the collector.</summary>
    public const string ServiceName = "SensorDeckCollector";

    /// <summary>File name of the collector program.</summary>
    public const string CollectorExeName = "SensorDeck.Collector.exe";

    /// <summary>What the user is told when another program answers on the pipe.</summary>
    public const string NotCollectorMessage =
        "Another program is answering on SensorDeck's connection — not the SensorDeck collector service. Data is not shown.";

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenElevationClass = 20;
    private const int ErrorAccessDenied = 5;
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const int ScStatusProcessInfo = 0;
    private const uint ServiceStopped = 1;

    /// <summary>Size of SERVICE_STATUS_PROCESS: nine DWORDs.</summary>
    private const int ServiceStatusProcessSize = 36;

    /// <summary>Offset of <c>dwCurrentState</c> in SERVICE_STATUS_PROCESS (after <c>dwServiceType</c>).</summary>
    private const int CurrentStateOffset = 4;

    /// <summary>Offset of <c>dwProcessId</c> in SERVICE_STATUS_PROCESS (after seven DWORDs).</summary>
    private const int ProcessIdOffset = 28;

    /// <summary>
    /// True if the server end of <paramref name="pipe"/> (a connected client handle on the normal
    /// <see cref="ProtocolInfo.PipeName"/> pipe) is the collector service, or, while the service isn't running, an
    /// elevated development collector (see the class summary). Otherwise false, with a reason for the user.
    /// </summary>
    /// <param name="pipe">The client's connected pipe handle (<c>NamedPipeClientStream.SafePipeHandle</c>).</param>
    /// <param name="developmentFolder">The source tree's collector build folder, or null (see <see cref="DevelopmentCollectorFolder"/>).</param>
    /// <param name="problem">Why the server was rejected; null when it was accepted.</param>
    [SupportedOSPlatform("windows")]
    public static bool IsCollectorService(SafeHandle pipe, string? developmentFolder, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        if (!GetNamedPipeServerProcessId(pipe, out var serverPid))
        {
            problem = NotCollectorMessage + " (couldn't tell which program it is)";
            return false;
        }

        if (RunningServiceProcessId() is { } servicePid)
        {
            problem = servicePid == serverPid
                ? null
                : NotCollectorMessage + $" (process {serverPid}; the service is process {servicePid})";
            return problem is null;
        }

        var image = ImagePathAndElevation(serverPid, out var elevated);
        string[] trusted =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            developmentFolder ?? "",
        ];
        if (image is not null && elevated && IsTrustedCollectorPath(image, trusted))
        {
            problem = null;
            return true;
        }

        problem = NotCollectorMessage + $" (process {serverPid}" + (image is null ? ")" : $": {image}{(elevated ? "" : ", not elevated")})");
        return false;
    }

    /// <summary>True if the server end of <paramref name="pipe"/> is process <paramref name="expectedProcessId"/> (a replay collector the app started).</summary>
    [SupportedOSPlatform("windows")]
    public static bool IsProcess(SafeHandle pipe, int expectedProcessId, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        if (GetNamedPipeServerProcessId(pipe, out var serverPid) && serverPid == (uint)expectedProcessId)
        {
            problem = null;
            return true;
        }

        problem = "Another program is answering on this replay's connection — not the collector SensorDeck started. Data is not shown.";
        return false;
    }

    /// <summary>
    /// <c>&lt;root&gt;\src\SensorDeck.Collector\bin\</c> when <paramref name="baseDirectory"/> is the build output of
    /// <paramref name="projectName"/> inside a source tree (<c>&lt;root&gt;\src\&lt;projectName&gt;\bin\…</c>), else null.
    /// An installed app or CLI never trusts a source tree.
    /// </summary>
    public static string? DevelopmentCollectorFolder(string baseDirectory, string projectName)
    {
        ArgumentNullException.ThrowIfNull(baseDirectory);
        var dir = baseDirectory.Replace('/', '\\');
        var marker = $"\\src\\{projectName}\\bin\\";
        var at = dir.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return at > 0 ? dir[..(at + 1)] + "src\\SensorDeck.Collector\\bin\\" : null;
    }

    /// <summary>
    /// True if <paramref name="imagePath"/> (a full Windows path as the kernel reports it) is a
    /// <see cref="CollectorExeName"/> inside one of <paramref name="trustedFolders"/> (empty entries are ignored).
    /// Network paths and paths with "." or ".." segments are never trusted.
    /// </summary>
    public static bool IsTrustedCollectorPath(string imagePath, IEnumerable<string> trustedFolders)
    {
        ArgumentNullException.ThrowIfNull(trustedFolders);
        if (string.IsNullOrEmpty(imagePath)
            || imagePath.StartsWith("\\\\", StringComparison.Ordinal)
            || imagePath.Contains('/', StringComparison.Ordinal)
            || imagePath.Contains("\\..\\", StringComparison.Ordinal)
            || imagePath.Contains("\\.\\", StringComparison.Ordinal))
        {
            return false;
        }

        var name = imagePath[(imagePath.LastIndexOf('\\') + 1)..];
        if (!name.Equals(CollectorExeName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var folder in trustedFolders)
        {
            if (string.IsNullOrEmpty(folder))
            {
                continue;
            }

            var prefix = folder.EndsWith('\\') ? folder : folder + "\\";
            if (imagePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The service's process id while it has one (running, starting or stopping); null if stopped, missing or unreadable.</summary>
    private static uint? RunningServiceProcessId()
    {
        var manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var service = OpenService(manager, ServiceName, ServiceQueryStatus);
            if (service == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                var status = new byte[ServiceStatusProcessSize];
                if (!QueryServiceStatusEx(service, ScStatusProcessInfo, status, status.Length, out _))
                {
                    return null;
                }

                var state = BinaryPrimitives.ReadUInt32LittleEndian(status.AsSpan(CurrentStateOffset));
                var pid = BinaryPrimitives.ReadUInt32LittleEndian(status.AsSpan(ProcessIdOffset));
                return state != ServiceStopped && pid != 0 ? pid : null;
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }

    /// <summary>
    /// The process's image path (null if it can't be opened) and whether it counts as elevated: TokenElevation when
    /// its token is readable; when the token is refused (access denied), only if it runs in this process's own
    /// session, which is where this user's elevated processes run and another user's processes never do.
    /// </summary>
    private static string? ImagePathAndElevation(uint processId, out bool elevated)
    {
        elevated = false;
        var process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var buffer = new StringBuilder(1024);
            var size = (uint)buffer.Capacity;
            var image = QueryFullProcessImageName(process, 0, buffer, ref size) ? buffer.ToString(0, (int)size) : null;

            if (OpenProcessToken(process, TokenQuery, out var token))
            {
                try
                {
                    elevated = GetTokenInformation(token, TokenElevationClass, out var isElevated, sizeof(int), out _) && isElevated != 0;
                }
                finally
                {
                    CloseHandle(token);
                }
            }
            else if (Marshal.GetLastPInvokeError() == ErrorAccessDenied)
            {
                elevated = ProcessIdToSessionId(processId, out var theirSession)
                    && ProcessIdToSessionId((uint)Environment.ProcessId, out var ourSession)
                    && theirSession == ourSession;
            }

            return image;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafeHandle pipe, out uint serverProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder exeName, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(IntPtr token, int informationClass, out int information, int informationLength, out int returnLength);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "OpenSCManagerW")]
    private static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "OpenServiceW")]
    private static extern IntPtr OpenService(IntPtr manager, string serviceName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(IntPtr service, int infoLevel, [Out] byte[] buffer, int bufferSize, out int bytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}
