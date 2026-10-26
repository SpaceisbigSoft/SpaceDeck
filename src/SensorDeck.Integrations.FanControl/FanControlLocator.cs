using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace SensorDeck.Integrations.FanControl;

/// <summary>Where FanControl is installed and whether it's running.</summary>
/// <param name="ExePath">Full path to FanControl.exe.</param>
/// <param name="IsRunning">True if a FanControl process is running.</param>
/// <param name="FoundBy">How the install was found (running process, configured path, known location).</param>
public sealed record FanControlInstall(string ExePath, bool IsRunning, string FoundBy)
{
    public string Directory => Path.GetDirectoryName(ExePath)!;
}

/// <summary>Finds FanControl.exe. FanControl ships as a portable zip, so it can live anywhere.</summary>
public static class FanControlLocator
{
    public const string ProcessName = "FanControl";
    public const string ExeName = "FanControl.exe";

    /// <summary>NT SERVICE\TrustedInstaller, the owner of Windows' own and many installed files.</summary>
    private const string TrustedInstallerSid = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

    private const string LocalSystemSid = "S-1-5-18";
    private const string AdministratorsSid = "S-1-5-32-544";

    /// <summary>OWNER RIGHTS: stands for the object's owner, which is checked separately.</summary>
    private const string OwnerRightsSid = "S-1-3-4";

    /// <summary>GENERIC_ALL / GENERIC_WRITE as they can appear in an ACE (FileSystemRights has no names for them).</summary>
    private const FileSystemRights GenericAll = (FileSystemRights)0x10000000;
    private const FileSystemRights GenericWrite = (FileSystemRights)0x40000000;

    /// <summary>
    /// Rights that let an account replace the exe or plant files beside it: write/append data (on a folder: create
    /// files/subfolders), delete, delete children, change permissions, take ownership. Read and execute are fine.
    /// </summary>
    private const FileSystemRights ModifyRights = FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.Delete
        | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership
        | GenericAll | GenericWrite;

    /// <summary>
    /// Finds FanControl. Order: a path the user configured, the running process, then common install folders.
    /// Returns null if it can't be found.
    /// </summary>
    public static FanControlInstall? Find(string? configuredExePath = null) => Find(configuredExePath, out _);

    /// <summary>
    /// Finds FanControl like <see cref="Find(string)"/>. A path the user configured is always used as-is. Anything
    /// found automatically (running process, known folders) is used only if its exe and folder are owned by, and can
    /// only be modified by, this user, SYSTEM, Administrators or TrustedInstaller (see <see cref="UntrustedReason"/>):
    /// otherwise another account could have put its own "FanControl.exe" there, and SensorDeck would run it as this
    /// user (FanControl asks for administrator rights, so its UAC prompt would look expected).
    /// </summary>
    /// <param name="rejected">Why a FanControl found automatically wasn't used (the first one); null if none was rejected.</param>
    public static FanControlInstall? Find(string? configuredExePath, out string? rejected)
    {
        rejected = null;
        var running = GetRunningExePath(out var anyRunning);

        if (!string.IsNullOrWhiteSpace(configuredExePath) && File.Exists(configuredExePath))
        {
            return new FanControlInstall(Path.GetFullPath(configuredExePath), anyRunning, "configured path");
        }

        if (running is not null)
        {
            if (UntrustedReason(running) is not { } why)
            {
                return new FanControlInstall(running, true, "running process");
            }

            rejected = why;
        }

        foreach (var candidate in KnownLocations())
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            if (UntrustedReason(candidate) is not { } reason)
            {
                return new FanControlInstall(candidate, anyRunning, "known install folder");
            }

            rejected ??= reason;
        }

        return null;
    }

    /// <summary>True if any FanControl process is running in any session we can see.</summary>
    public static bool IsRunning()
    {
        var processes = Process.GetProcessesByName(ProcessName);
        try
        {
            return processes.Length > 0;
        }
        finally
        {
            foreach (var p in processes)
            {
                p.Dispose();
            }
        }
    }

    private static IEnumerable<string> KnownLocations()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // A folder Windows can't resolve comes back as "": never build a relative path from it (it would be resolved
        // against the working directory).
        var programs = string.IsNullOrEmpty(localAppData) ? "" : Path.Combine(localAppData, "Programs");
        foreach (var root in new[] { programFiles, programFilesX86, programs })
        {
            if (!string.IsNullOrEmpty(root))
            {
                yield return Path.Combine(root, "FanControl", ExeName);
            }
        }

        if (!string.IsNullOrEmpty(userProfile))
        {
            yield return Path.Combine(userProfile, "Desktop", "FanControl", ExeName);
            yield return Path.Combine(userProfile, "Documents", "FanControl", ExeName);
            yield return Path.Combine(userProfile, "Downloads", "FanControl", ExeName);
        }

        // A folder in the drive root: any local user can create it, and it inherits "Authenticated Users: Modify" from
        // C:\, so it is usually rejected by UntrustedReason unless the user locked it down (or set the path in Settings).
        yield return Path.Combine("C:\\", "FanControl", ExeName);
    }

    /// <summary>
    /// Null if the exe and its folder are owned by this user, SYSTEM, Administrators or TrustedInstaller and no other
    /// account can modify them (<see cref="FirstUntrustedWriter"/>); otherwise why not, for the user. Anything that can't
    /// be read counts as untrusted.
    /// </summary>
    private static string? UntrustedReason(string exePath)
    {
        try
        {
            using var me = WindowsIdentity.GetCurrent();
            var user = me.User?.Value;
            var file = new FileInfo(exePath);
            if (file.Directory is not { } folder)
            {
                return $"{exePath}: couldn't check its folder";
            }

            var problem = Untrusted(file.GetAccessControl(), user, "the exe") ?? Untrusted(folder.GetAccessControl(), user, "its folder");
            return problem is null ? null : $"{exePath}: {problem}, so another account could replace it";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return $"{exePath}: couldn't read its permissions";
        }

        static string? Untrusted(FileSystemSecurity acl, string? user, string what)
        {
            var owner = (acl.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier)?.Value;
            if (!IsTrustedSid(owner, user))
            {
                return $"{what} is owned by {AccountName(owner)}";
            }

            return FirstUntrustedWriter(Entries(acl), user) is { } writer ? $"{AccountName(writer)} can change {what}" : null;
        }
    }

    /// <summary>One access-control entry, reduced to what <see cref="FirstUntrustedWriter"/> needs.</summary>
    /// <param name="Sid">The account's SID string (e.g. <c>S-1-5-11</c>).</param>
    /// <param name="IsInheritOnly">Applies only to new children, not to the object itself.</param>
    internal readonly record struct AclEntry(string Sid, FileSystemRights Rights, bool IsAllow, bool IsInheritOnly);

    /// <summary>
    /// The first account other than <paramref name="userSid"/>, SYSTEM, Administrators, TrustedInstaller (or OWNER RIGHTS,
    /// i.e. the owner, which is checked on its own) that an allow entry gives any of <see cref="ModifyRights"/>; null if
    /// none. Inherit-only entries don't apply to the object itself and are skipped. Deny entries are ignored, so they
    /// never make an object pass.
    /// </summary>
    internal static string? FirstUntrustedWriter(IEnumerable<AclEntry> entries, string? userSid)
    {
        ArgumentNullException.ThrowIfNull(entries);
        foreach (var entry in entries)
        {
            if (entry.IsAllow
                && !entry.IsInheritOnly
                && (entry.Rights & ModifyRights) != 0
                && entry.Sid != OwnerRightsSid
                && !IsTrustedSid(entry.Sid, userSid))
            {
                return entry.Sid;
            }
        }

        return null;
    }

    /// <summary>This user, SYSTEM, Administrators or TrustedInstaller.</summary>
    internal static bool IsTrustedSid(string? sid, string? userSid) =>
        sid is not null && (sid == userSid || sid is LocalSystemSid or AdministratorsSid or TrustedInstallerSid);

    private static IEnumerable<AclEntry> Entries(FileSystemSecurity acl)
    {
        foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.IdentityReference is SecurityIdentifier sid)
            {
                yield return new AclEntry(sid.Value, rule.FileSystemRights, rule.AccessControlType == AccessControlType.Allow,
                    (rule.PropagationFlags & PropagationFlags.InheritOnly) != 0);
            }
        }
    }

    /// <summary>"BUILTIN\Users" for a SID string, or the SID itself if it has no name.</summary>
    private static string AccountName(string? sid)
    {
        if (sid is null)
        {
            return "an unknown account";
        }

        try
        {
            return new SecurityIdentifier(sid).Translate(typeof(NTAccount)).Value;
        }
        catch (SystemException)
        {
            return sid; // IdentityNotMappedException, or a SID string Windows can't parse
        }
    }

    /// <summary>
    /// Gets the running FanControl's exe path. FanControl usually runs elevated, and an unelevated process
    /// can't read an elevated one's MainModule, but QueryFullProcessImageName with limited rights still works.
    /// </summary>
    private static string? GetRunningExePath(out bool anyRunning)
    {
        var processes = Process.GetProcessesByName(ProcessName);
        anyRunning = processes.Length > 0;
        try
        {
            foreach (var process in processes)
            {
                var path = TryGetImagePath(process.Id);
                if (path is not null && File.Exists(path))
                {
                    return path;
                }
            }

            return null;
        }
        finally
        {
            foreach (var p in processes)
            {
                p.Dispose();
            }
        }
    }

    private static string? TryGetImagePath(int processId)
    {
        const uint ProcessQueryLimitedInformation = 0x1000;
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)processId);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var buffer = new StringBuilder(1024);
            var size = (uint)buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString(0, (int)size) : null;
        }
        catch (Win32Exception)
        {
            return null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder exeName, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
