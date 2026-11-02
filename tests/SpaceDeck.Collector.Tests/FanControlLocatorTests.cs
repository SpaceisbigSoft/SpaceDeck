using System.Security.AccessControl;
using SpaceDeck.Integrations.FanControl;
using Entry = SpaceDeck.Integrations.FanControl.FanControlLocator.AclEntry;

namespace SpaceDeck.Collector.Tests;

/// <summary>The permission rule for auto-detected FanControl installs (HARD-4).</summary>
public class FanControlLocatorTests
{
    private const string Me = "S-1-5-21-1-2-3-1001";
    private const string LocalSystem = "S-1-5-18";
    private const string Administrators = "S-1-5-32-544";
    private const string TrustedInstaller = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
    private const string Users = "S-1-5-32-545";
    private const string AuthenticatedUsers = "S-1-5-11";
    private const string CreatorOwner = "S-1-3-0";

    [Fact]
    public void ProgramFilesStyleAcl_Passes()
    {
        // C:\Program Files (x86)\FanControl as inherited from Program Files (x86).
        Entry[] acl =
        [
            new(TrustedInstaller, FileSystemRights.FullControl, true, false),
            new(LocalSystem, FileSystemRights.Modify, true, false),
            new(LocalSystem, (FileSystemRights)0x10000000, true, true),
            new(Administrators, FileSystemRights.FullControl, true, false),
            new(Users, FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize, true, false),
            new(Users, unchecked((FileSystemRights)0xA0000000), true, true), // GR|GE, inherit-only
            new(CreatorOwner, (FileSystemRights)0x10000000, true, true), // GA, inherit-only
            new("S-1-15-2-1", FileSystemRights.ReadAndExecute, true, false), // ALL APPLICATION PACKAGES
        ];

        Assert.Null(FanControlLocator.FirstUntrustedWriter(acl, Me));
    }

    /// <summary>GENERIC_READ | GENERIC_EXECUTE ("(GR,GE)" in icacls).</summary>
    private const FileSystemRights GenericReadExecute = unchecked((FileSystemRights)0xA0000000);

    private const FileSystemRights ReadExecute = FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize; // "(RX)"
    private const string AllApplicationPackages = "S-1-15-2-1";
    private const string AllRestrictedApplicationPackages = "S-1-15-2-2";

    [Fact]
    public void RealProgramFilesX86FanControlFolderAcl_Passes()
    {
        // icacls "C:\Program Files (x86)\FanControl" on the user's PC. (IO) entries apply to children only; CREATOR OWNER
        // is one of them and must not cause a rejection.
        Entry[] acl =
        [
            new(TrustedInstaller, FileSystemRights.FullControl, true, false),
            new(TrustedInstaller, FileSystemRights.FullControl, true, true),
            new(LocalSystem, FileSystemRights.FullControl, true, false),
            new(LocalSystem, FileSystemRights.FullControl, true, true),
            new(Administrators, FileSystemRights.FullControl, true, false),
            new(Administrators, FileSystemRights.FullControl, true, true),
            new(Users, ReadExecute, true, false),
            new(Users, GenericReadExecute, true, true),
            new(CreatorOwner, FileSystemRights.FullControl, true, true),
            new(AllApplicationPackages, ReadExecute, true, false),
            new(AllApplicationPackages, GenericReadExecute, true, true),
            new(AllRestrictedApplicationPackages, ReadExecute, true, false),
            new(AllRestrictedApplicationPackages, GenericReadExecute, true, true),
        ];

        Assert.Null(FanControlLocator.FirstUntrustedWriter(acl, Me));
    }

    [Fact]
    public void RealProgramFilesX86FanControlExeAcl_Passes()
    {
        // icacls "C:\Program Files (x86)\FanControl\FanControl.exe" on the user's PC.
        Entry[] acl =
        [
            new(LocalSystem, FileSystemRights.FullControl, true, false),
            new(Administrators, FileSystemRights.FullControl, true, false),
            new(Users, ReadExecute, true, false),
            new(AllApplicationPackages, ReadExecute, true, false),
            new(AllRestrictedApplicationPackages, ReadExecute, true, false),
        ];

        Assert.Null(FanControlLocator.FirstUntrustedWriter(acl, Me));

        // Owner of an elevated install: Administrators (or TrustedInstaller); both are accepted.
        Assert.True(FanControlLocator.IsTrustedSid(Administrators, Me));
        Assert.True(FanControlLocator.IsTrustedSid(TrustedInstaller, Me));
    }

    [Fact]
    public void UserProfileAcl_Passes()
    {
        Entry[] acl =
        [
            new(LocalSystem, FileSystemRights.FullControl, true, false),
            new(Administrators, FileSystemRights.FullControl, true, false),
            new(Me, FileSystemRights.FullControl, true, false),
        ];

        Assert.Null(FanControlLocator.FirstUntrustedWriter(acl, Me));
    }

    [Fact]
    public void DriveRootFolder_WithInheritedAuthenticatedUsersModify_IsRejected()
    {
        Entry[] acl =
        [
            new(Administrators, FileSystemRights.FullControl, true, false),
            new(LocalSystem, FileSystemRights.FullControl, true, false),
            new(Users, FileSystemRights.ReadAndExecute, true, false),
            new(AuthenticatedUsers, FileSystemRights.Modify | FileSystemRights.Synchronize, true, false),
        ];

        Assert.Equal(AuthenticatedUsers, FanControlLocator.FirstUntrustedWriter(acl, Me));
    }

    [Theory]
    [InlineData(FileSystemRights.WriteData)]
    [InlineData(FileSystemRights.AppendData)]
    [InlineData(FileSystemRights.Delete)]
    [InlineData(FileSystemRights.DeleteSubdirectoriesAndFiles)]
    [InlineData(FileSystemRights.ChangePermissions)]
    [InlineData(FileSystemRights.TakeOwnership)]
    [InlineData((FileSystemRights)0x40000000)] // GENERIC_WRITE
    public void AnyModifyRight_ForOthers_IsRejected(FileSystemRights rights)
    {
        Assert.Equal("S-1-1-0", FanControlLocator.FirstUntrustedWriter([new Entry("S-1-1-0", rights, true, false)], Me));
    }

    [Fact]
    public void ReadRights_DenyEntries_AndInheritOnlyEntries_DontReject()
    {
        Entry[] acl =
        [
            new("S-1-1-0", FileSystemRights.ReadAndExecute | FileSystemRights.ReadPermissions, true, false),
            new(AuthenticatedUsers, FileSystemRights.Modify, false, false), // deny
            new(AuthenticatedUsers, FileSystemRights.Modify, true, true), // inherit-only: new children only
            new("S-1-3-4", FileSystemRights.FullControl, true, false), // OWNER RIGHTS (the owner is checked separately)
        ];

        Assert.Null(FanControlLocator.FirstUntrustedWriter(acl, Me));
    }

    [Fact]
    public void AnotherUsersFullControl_IsRejected_AndTrustedSidsAreExactlyTheFour()
    {
        Assert.Equal("S-1-5-21-1-2-3-1002",
            FanControlLocator.FirstUntrustedWriter([new Entry("S-1-5-21-1-2-3-1002", FileSystemRights.FullControl, true, false)], Me));
        Assert.True(FanControlLocator.IsTrustedSid(Me, Me));
        Assert.True(FanControlLocator.IsTrustedSid(LocalSystem, Me));
        Assert.True(FanControlLocator.IsTrustedSid(Administrators, Me));
        Assert.True(FanControlLocator.IsTrustedSid(TrustedInstaller, Me));
        Assert.False(FanControlLocator.IsTrustedSid(Users, Me));
        Assert.False(FanControlLocator.IsTrustedSid(null, Me));
        Assert.False(FanControlLocator.IsTrustedSid(Users, null));
    }
}
