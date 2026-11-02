using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace SpaceDeck.Collector;

/// <summary>
/// Locks down the collector's data folder (<c>%ProgramData%\SpaceDeck</c>) before the service (LocalSystem) reads or
/// writes anything in it.
/// </summary>
/// <remarks>
/// <para>%ProgramData% lets every user create files and folders, and a folder created there inherits that. A normal user
/// could then plant <c>collector.json</c> (pipe name, logging), or put a junction where <c>logs</c> or <c>history</c>
/// should be and make SYSTEM create, append to or delete files anywhere on the disk.</para>
/// <para>So the folder gets an explicit, protected ACL (SYSTEM and Administrators full control, Users read), and
/// anything inside it that is a link, or isn't owned by SYSTEM, Administrators, TrustedInstaller or the account running
/// the collector, is taken out of use: links are deleted (only the link, never what it points to), files and folders
/// are renamed to <c>*.untrusted</c> (nothing is deleted, so the owner can still get the data back). Hard links aren't
/// reparse points and carry the owner of the file they point to, so a file with more than one name counts as a link
/// too: this name is deleted, and the file itself stays under its other names. A folder set aside as *.untrusted is
/// locked (its owner can still read it, but no longer write to it or change its ACL, and the data folder's ACL no longer
/// propagates into it), and links are removed from it too, at every start. Other entries owned by SYSTEM,
/// Administrators or TrustedInstaller are never changed. A folder that can't be secured is reported and not used.</para>
/// <para>Once the ACL is on, other users can't create anything in the folder, so files the collector opens later (logs,
/// disk history, collector.json) can't be swapped for a link.</para>
/// </remarks>
internal static partial class DataDirectorySecurity
{
    /// <summary>The data folder is shallow (logs, history); deeper trees aren't walked.</summary>
    private const int MaxDepth = 8;

    /// <summary>Appended to files and folders set aside because someone else owns them. Such entries are skipped later.</summary>
    internal const string UntrustedSuffix = ".untrusted";

    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);
    private static readonly SecurityIdentifier TrustedInstaller = new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

    /// <summary>OWNER RIGHTS: when an ACL names it, an owner gets only what it grants (not the implicit right to change the ACL).</summary>
    private static readonly SecurityIdentifier OwnerRights = new("S-1-3-4");

    /// <summary>
    /// Creates or secures <paramref name="directory"/>. Returns null when it is safe to use, otherwise why it isn't.
    /// Paths of entries taken out of use (links deleted, others renamed) are added to <paramref name="removed"/>.
    /// </summary>
    /// <remarks>Needs SYSTEM or an elevated administrator.</remarks>
    public static string? Secure(string directory, ICollection<string> removed)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        ArgumentNullException.ThrowIfNull(removed);
        try
        {
            var info = new DirectoryInfo(directory);
            if (!info.Exists)
            {
                info.Create(CreateSecurity());
                info.Refresh();
            }

            // Checked even right after creating it: someone may have created it in between.
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return $"{directory} is a link (junction or symbolic link). Remove the link (what it points to is kept) and restart the collector.";
            }

            var owner = OwnerOf(info);
            if (!IsTrusted(owner))
            {
                return $"{directory} is owned by {owner?.Value ?? "an unknown account"}, not by SYSTEM or Administrators, so nothing in it was used or changed. Run Install-Collector.ps1 as administrator (it makes Administrators the owner) or take ownership of the folder, then restart the collector.";
            }

            // Links and foreign entries out of the way first, so the ACL change below (which propagates its inheritable
            // entries into the tree) only walks our own files. Then the folder's ACL, so nobody else can add anything,
            // and a second pass for whatever appeared in between.
            RemoveUntrusted(info, removed, 0);
            info.SetAccessControl(CreateSecurity());
            RemoveUntrusted(info, removed, 0);
            return null;
        }
        // Anything at all: this runs before the service has a logger, and an exception escaping here would stop the
        // service at every start. Not secured means not used, which is the safe outcome.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return $"Couldn't secure {directory}: {ex.Message}";
        }
    }

    private static DirectorySecurity CreateSecurity()
    {
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(LocalSystem, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(Administrators, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    private static void RemoveUntrusted(DirectoryInfo directory, ICollection<string> removed, int depth)
    {
        foreach (var entry in directory.GetFileSystemInfos()) // a snapshot: entries are renamed and deleted below
        {
            if (RemoveIfLink(entry, removed))
            {
                // A link (any name, *.untrusted too): gone.
            }
            else if (entry.Name.Contains(UntrustedSuffix, StringComparison.OrdinalIgnoreCase))
            {
                // Set aside earlier: nothing in it is used, but its owner must not be able to plant links in it.
                if (entry is DirectoryInfo setAside)
                {
                    LockSetAside(setAside, removed, depth);
                }
            }
            else if (!IsTrusted(OwnerOf(entry)))
            {
                // Perhaps a collector.json or history folder created unelevated: set aside rather than lost (renamed
                // within the folder, so nothing is followed or deleted). Nothing reads *.untrusted.
                var path = entry.FullName;
                var target = QuarantineName(path);
                if (entry is DirectoryInfo dir)
                {
                    dir.MoveTo(target);
                    removed.Add(path);
                    LockSetAside(new DirectoryInfo(target), removed, depth);
                }
                else
                {
                    ((FileInfo)entry).MoveTo(target);
                    removed.Add(path);
                }
            }
            else if (entry is DirectoryInfo sub && depth < MaxDepth)
            {
                RemoveUntrusted(sub, removed, depth + 1);
            }
        }
    }

    /// <summary>
    /// Deletes <paramref name="entry"/> when it is a link: a junction or symbolic link (only the link goes, never what it
    /// points to), or a file with more than one name. Returns false, changing nothing, for anything else.
    /// </summary>
    /// <remarks>
    /// A hard link: SYSTEM would append its logs or history to someone else's file, and the ACL that
    /// <see cref="Secure"/> sets, which propagates into the folder, would rewrite that file's permissions. Deleting this
    /// name removes only the link; the file stays under its other names.
    /// </remarks>
    private static bool RemoveIfLink(FileSystemInfo entry, ICollection<string> removed)
    {
        var path = entry.FullName;
        if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            // A non-recursive delete removes a junction itself, not its target.
            if (entry is DirectoryInfo link)
            {
                link.Delete(recursive: false);
            }
            else
            {
                entry.Delete();
            }
        }
        else if (entry is FileInfo linked && HasOtherNames(linked))
        {
            linked.Delete();
        }
        else
        {
            return false;
        }

        removed.Add(path);
        return true;
    }

    /// <summary>
    /// A folder set aside as *.untrusted keeps its owner (so it can be given back), and an owner may change a folder's
    /// ACL, so it could keep planting links in it. Each folder in the set-aside tree is therefore locked first (SYSTEM
    /// and Administrators full control, everyone else, its owner included, read only; protected, so the data folder's
    /// ACL no longer propagates into it), then the links in it are removed. Nothing else in it is changed or deleted.
    /// </summary>
    private static void LockSetAside(DirectoryInfo directory, ICollection<string> removed, int depth)
    {
        LockFolder(directory.FullName);
        foreach (var entry in directory.GetFileSystemInfos())
        {
            if (!RemoveIfLink(entry, removed) && entry is DirectoryInfo sub && depth < MaxDepth)
            {
                LockSetAside(sub, removed, depth + 1);
            }
        }
    }

    /// <summary>
    /// Sets the set-aside ACL on the folder <paramref name="path"/> itself, without following a link and without
    /// propagating anything to what's inside (so a hard link in it can't pass the change on to its target).
    /// </summary>
    private static void LockFolder(string path)
    {
        using var handle = CreateFile(path, FileReadAttributes | WriteDac, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero,
            FileMode.Open, FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw new IOException($"Couldn't lock {path}: {Marshal.GetPInvokeErrorMessage(Marshal.GetLastPInvokeError())}");
        }

        // The handle is what gets changed, so check it is still a plain folder (not swapped for a link meanwhile).
        var info = new byte[ByHandleFileInformationSize];
        if (!GetFileInformationByHandle(handle, info))
        {
            throw new IOException($"Couldn't lock {path}: {Marshal.GetPInvokeErrorMessage(Marshal.GetLastPInvokeError())}");
        }

        var attributes = (FileAttributes)BinaryPrimitives.ReadInt32LittleEndian(info);
        if (!attributes.HasFlag(FileAttributes.Directory) || attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException($"{path} changed while it was being secured.");
        }

        // SetKernelObjectSecurity sets exactly this object's DACL; unlike SetAccessControl it never propagates.
        if (!SetKernelObjectSecurity(handle, DaclSecurityInformation, CreateSetAsideSecurity().GetSecurityDescriptorBinaryForm()))
        {
            throw new IOException($"Couldn't lock {path}: {Marshal.GetPInvokeErrorMessage(Marshal.GetLastPInvokeError())}");
        }
    }

    /// <summary>The data folder's ACL plus OWNER RIGHTS read-only, which takes away the owner's implicit right to change the ACL.</summary>
    private static DirectorySecurity CreateSetAsideSecurity()
    {
        var security = CreateSecurity();
        security.AddAccessRule(new FileSystemAccessRule(OwnerRights, FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    /// <summary>Where an entry that is taken out of use is renamed to (next to it, never overwriting anything).</summary>
    private static string QuarantineName(string path)
    {
        var target = path + UntrustedSuffix;
        if (Path.Exists(target))
        {
            target += "-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
        }

        return target;
    }

    /// <summary>
    /// True when <paramref name="file"/> has more than one name (an NTFS hard link). The collector never creates hard
    /// links, so another name means someone else made it.
    /// </summary>
    /// <remarks>
    /// False when it can't tell: the name is then deleted only if it really is a link, never a file that may be the only
    /// copy. One owned by someone else is still set aside by the owner check.
    /// </remarks>
    private static bool HasOtherNames(FileInfo file)
    {
        // Attributes only (no read/write/delete access): such an open is never refused for sharing, so a file another
        // program holds open exclusively is still checked, and nothing here can make the folder unusable.
        using var handle = CreateFile(file.FullName, FileReadAttributes, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero,
            FileMode.Open, FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            return false;
        }

        var info = new byte[ByHandleFileInformationSize];
        return GetFileInformationByHandle(handle, info)
            && BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(NumberOfLinksOffset)) > 1;
    }

    /// <summary>Size of BY_HANDLE_FILE_INFORMATION: attributes, three FILETIMEs, then seven DWORDs.</summary>
    private const int ByHandleFileInformationSize = 52;

    /// <summary>Offset of <c>nNumberOfLinks</c> in BY_HANDLE_FILE_INFORMATION (after attributes, 3 FILETIMEs, serial, size high/low).</summary>
    private const int NumberOfLinksOffset = 40;

    private const uint FileReadAttributes = 0x80;
    private const uint WriteDac = 0x00040000;
    private const uint DaclSecurityInformation = 0x4;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;

    // FileMode.Open is OPEN_EXISTING (3); FileShare values match FILE_SHARE_*.
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, FileShare shareMode, IntPtr securityAttributes,
        FileMode creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, [Out] byte[] fileInformation);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetKernelObjectSecurity(SafeFileHandle handle, uint securityInformation, byte[] securityDescriptor);

    private static SecurityIdentifier? OwnerOf(FileSystemInfo entry)
    {
        try
        {
            FileSystemSecurity security = entry is DirectoryInfo dir
                ? dir.GetAccessControl(AccessControlSections.Owner)
                : ((FileInfo)entry).GetAccessControl(AccessControlSections.Owner);
            return security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            return null; // can't even read the owner: not ours
        }
    }

    private static bool IsTrusted(SecurityIdentifier? owner)
    {
        if (owner is null)
        {
            return false;
        }

        if (owner.Equals(LocalSystem) || owner.Equals(Administrators) || owner.Equals(TrustedInstaller))
        {
            return true;
        }

        // An elevated administrator running the collector from a terminal may own what it created.
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User is { } user && owner.Equals(user);
    }

    [LoggerMessage(Level = LogLevel.Critical, Message = "The data folder {Directory} can't be used safely, so settings, logs and disk history are off: {Problem}")]
    public static partial void LogUnsafe(ILogger logger, string directory, string problem);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Took {Path} out of use: a link was deleted (not its target), anything else renamed to *.untrusted, because it was a link or not owned by SYSTEM or Administrators. Take ownership and rename it back if it is yours.")]
    public static partial void LogRemoved(ILogger logger, string path);
}
