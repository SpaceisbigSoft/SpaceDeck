#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Publishes the SpaceDeck collector and installs (or updates) it as a Windows Service.

.DESCRIPTION
    Builds src/SpaceDeck.Collector, copies it to "C:\Program Files\SpaceDeck\Collector",
    and registers the "SpaceDeckCollector" service (automatic start, LocalSystem).
    Safe to re-run after code changes: it stops the service, republishes, and starts it again.

    The service runs as SYSTEM, so the script also makes sure normal users can't change its files:
    the install folder and %ProgramData%\SpaceDeck (settings, logs, history) get an ACL where only
    SYSTEM and Administrators can write, and links planted in the data folder (including hard links) are
    removed. A data folder a normal user created first is taken over for Administrators.

.EXAMPLE
    .\scripts\Install-Collector.ps1
#>
param(
    [string]$Configuration = "Release",
    [string]$InstallDir = (Join-Path $env:ProgramFiles "SpaceDeck\Collector")
)

$ErrorActionPreference = "Stop"
$serviceName = "SpaceDeckCollector"
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "src\SpaceDeck.Collector\SpaceDeck.Collector.csproj"
$InstallDir = [System.IO.Path]::GetFullPath($InstallDir)
$exe = Join-Path $InstallDir "SpaceDeck.Collector.exe"
$dataDir = Join-Path $env:ProgramData "SpaceDeck"

# SIDs, so the script works on non-English Windows: SYSTEM, Administrators, Users.
$aclGrants = @("*S-1-5-18:(OI)(CI)F", "*S-1-5-32-544:(OI)(CI)F", "*S-1-5-32-545:(OI)(CI)RX")

# True when $File has more than one name (an NTFS hard link). fsutil lists every name of the file, one per line.
# A file it can't inspect counts as not linked here; the collector checks again (and refuses) when it starts.
function Test-HardLink([string]$File) {
    $ErrorActionPreference = "Continue" # local to this function: fsutil's error text must not stop the script
    # Blank lines dropped: a stray empty line must never make a single-name file look linked (it would be deleted).
    $names = @(fsutil.exe hardlink list $File 2>$null | Where-Object { "$_".Trim().Length -gt 0 })
    return ($LASTEXITCODE -eq 0 -and $names.Count -gt 1)
}

# Deletes junctions, symbolic links and mount points under $Path without following them, so a link a normal
# user planted can't send the recursive commands below to files outside the folder. With -SetAsideHardLinks, files
# with more than one name (hard links aren't reparse points) lose this name too: the ACL commands below would
# otherwise change the permissions of the file they point to, and the service would write its logs into it. Only the
# name in this folder goes; the file itself is kept under its other names.
function Remove-Links([string]$Path, [switch]$SetAsideHardLinks) {
    foreach ($entry in @([System.IO.Directory]::GetFileSystemEntries($Path))) {
        $attributes = [System.IO.File]::GetAttributes($entry)
        if ($attributes -band [System.IO.FileAttributes]::ReparsePoint) {
            Write-Host "Removing link $entry"
            if ($attributes -band [System.IO.FileAttributes]::Directory) {
                [System.IO.Directory]::Delete($entry) # removes the link only
            } else {
                [System.IO.File]::Delete($entry)
            }
        } elseif ($attributes -band [System.IO.FileAttributes]::Directory) {
            Remove-Links $entry -SetAsideHardLinks:$SetAsideHardLinks
        } elseif ($SetAsideHardLinks -and (Test-HardLink $entry)) {
            Write-Host "Removing hard link $entry (the file keeps its other names)"
            [System.IO.File]::Delete($entry)
        }
    }
}

# Files and folders under $Path owned by the admin running this script (e.g. logs or history from an elevated test run
# of the collector) are handed to Administrators. Otherwise the service, which only trusts SYSTEM, Administrators and
# TrustedInstaller, would set them aside as *.untrusted. Links aren't followed; entries owned by anyone else are left.
function Grant-OwnItemsToAdministrators([string]$Path) {
    $me = [System.Security.Principal.WindowsIdentity]::GetCurrent().User
    foreach ($entry in [System.IO.Directory]::EnumerateFileSystemEntries($Path)) {
        $attributes = [System.IO.File]::GetAttributes($entry)
        if ($attributes -band [System.IO.FileAttributes]::ReparsePoint) { continue }
        try {
            $owner = (Get-Acl -LiteralPath $entry).GetOwner([System.Security.Principal.SecurityIdentifier])
        } catch {
            $owner = $null
        }
        if ($null -ne $owner -and $owner.Equals($me)) {
            icacls.exe $entry /setowner "*S-1-5-32-544" /Q | Out-Null
        }
        if ($attributes -band [System.IO.FileAttributes]::Directory) { Grant-OwnItemsToAdministrators $entry }
    }
}

# Gives every entry under $Path the permissions inherited from $Path (icacls /reset), entry by entry, skipping links and
# anything set aside as *.untrusted: the collector locks those folders so their owner can't plant links in them, and a
# reset would hand that owner write access again. Returns $false when icacls failed for some entry.
function Reset-InheritedAcl([string]$Path) {
    $ok = $true
    foreach ($entry in @([System.IO.Directory]::GetFileSystemEntries($Path))) {
        if ([System.IO.Path]::GetFileName($entry) -like "*.untrusted*") { continue }
        $attributes = [System.IO.File]::GetAttributes($entry)
        if ($attributes -band [System.IO.FileAttributes]::ReparsePoint) { continue }
        icacls.exe $entry /reset /C /Q | Out-Null
        if ($LASTEXITCODE -ne 0) { $ok = $false }
        if (($attributes -band [System.IO.FileAttributes]::Directory) -and -not (Reset-InheritedAcl $entry)) { $ok = $false }
    }
    return $ok
}

# Owner Administrators; SYSTEM and Administrators full control, Users read; everything inside inherits that.
# -AdoptContents also makes Administrators the owner of everything inside (for files we just published). Without
# it, files someone else owns keep their owner, and the collector sets them aside as *.untrusted when it starts.
# -SetAsideHardLinks: see Remove-Links (used for the data folder, which the service writes to).
function Set-AdminOnlyAcl([string]$Path, [switch]$AdoptContents, [switch]$SetAsideHardLinks) {
    if (Test-Path -LiteralPath $Path) {
        $item = Get-Item -LiteralPath $Path -Force
        if ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
            throw "$Path is a link (junction or symbolic link). Delete it and run this script again."
        }

        # A folder a normal user created first (anyone may create folders in ProgramData) is taken over for
        # Administrators: the collector won't use a folder it doesn't trust, and only an administrator can fix that.
        # takeown works even if the folder's ACL shuts Administrators out. Only the folder itself; what's inside keeps
        # its owner (the collector sets aside entries owned by other users as *.untrusted).
        takeown.exe /F $Path /A | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "takeown failed for $Path." }

        # Links out first, so the ACL change below (which propagates into the tree) can't be sent elsewhere.
        Remove-Links $Path -SetAsideHardLinks:$SetAsideHardLinks
    } else {
        New-Item -ItemType Directory -Path $Path | Out-Null
    }

    # The folder itself first, so nobody else can add anything while its contents are fixed.
    icacls.exe $Path /setowner "*S-1-5-32-544" /Q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "icacls /setowner failed for $Path." }
    icacls.exe $Path /inheritance:r /grant:r $aclGrants /Q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "icacls failed for $Path." }

    Remove-Links $Path -SetAsideHardLinks:$SetAsideHardLinks
    if (Get-ChildItem -LiteralPath $Path -Force | Select-Object -First 1) {
        if ($AdoptContents) {
            icacls.exe (Join-Path $Path "*") /setowner "*S-1-5-32-544" /T /C /Q | Out-Null
        } else {
            Grant-OwnItemsToAdministrators $Path
        }
        if (-not (Reset-InheritedAcl $Path)) { Write-Warning "Some files in $Path kept their own permissions; check them with icacls." }
    }
}

$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($existing -and $existing.Status -ne "Stopped") {
    Write-Host "Stopping $serviceName..."
    Stop-Service -Name $serviceName -Force
    $existing.WaitForStatus("Stopped", [TimeSpan]::FromSeconds(30))
}

# Renamed from SensorDeck (2026-11): remove the old service and its install folder, and copy the old data folder
# (collector settings, history, logs) to the new one the first time. The old data folder is left as it was.
$legacyService = "SensorDeckCollector"
$legacyDataDir = Join-Path $env:ProgramData "SensorDeck"
$legacyInstallDir = Join-Path $env:ProgramFiles "SensorDeck"
$old = Get-Service -Name $legacyService -ErrorAction SilentlyContinue
if ($old) {
    Write-Host "Removing the old $legacyService service (SensorDeck is now SpaceDeck)..."
    if ($old.Status -ne "Stopped") {
        Stop-Service -Name $legacyService -Force
        $old.WaitForStatus("Stopped", [TimeSpan]::FromSeconds(30))
    }
    sc.exe delete $legacyService | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Couldn't remove the old $legacyService service." }
}
$dataDirEmpty = -not (Test-Path -LiteralPath $dataDir) -or -not (Get-ChildItem -LiteralPath $dataDir -Force | Select-Object -First 1)
if ((Test-Path -LiteralPath $legacyDataDir) -and $dataDirEmpty) {
    Write-Host "Copying $legacyDataDir to $dataDir..."
    # /XJ /XJD /XJF: links are skipped, never followed. Permissions aren't copied; Set-AdminOnlyAcl below sets them.
    robocopy.exe $legacyDataDir $dataDir /E /XJ /XJD /XJF /COPY:DAT /R:1 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "Copying $legacyDataDir failed (robocopy exit code $LASTEXITCODE)." }
}
if (Test-Path -LiteralPath $legacyInstallDir) {
    Write-Host "Removing the old install folder $legacyInstallDir..."
    Remove-Item -LiteralPath $legacyInstallDir -Recurse -Force
}

$programFiles = [System.IO.Path]::GetFullPath($env:ProgramFiles).TrimEnd('\') + '\'
if (-not $InstallDir.StartsWith($programFiles, [StringComparison]::OrdinalIgnoreCase)) {
    Write-Warning ("$InstallDir is outside Program Files. The service runs these files as SYSTEM: make sure normal users " +
        "can't rename or replace any folder above it, or they could swap in their own program.")
}

Write-Host "Publishing collector ($Configuration) to $InstallDir..."
dotnet publish $project -c $Configuration -o $InstallDir --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

if (-not $InstallDir.StartsWith($programFiles, [StringComparison]::OrdinalIgnoreCase)) {
    # Program Files is already admin-only; anywhere else, lock the folder down ourselves.
    Write-Host "Restricting write access to $InstallDir..."
    Set-AdminOnlyAcl $InstallDir -AdoptContents
}

Write-Host "Securing $dataDir..."
Set-AdminOnlyAcl $dataDir -SetAsideHardLinks

# Always quoted: the path has spaces ("Program Files"), and an unquoted service path can run C:\Program.exe.
$binaryPath = "`"$exe`""
if (-not $existing) {
    Write-Host "Creating service $serviceName..."
    New-Service -Name $serviceName `
        -BinaryPathName $binaryPath `
        -DisplayName "SpaceDeck Collector" `
        -Description "Reads hardware sensors (LibreHardwareMonitor on PawnIO) for the SpaceDeck dashboard. Read-only: never controls fans." `
        -StartupType Automatic | Out-Null

    # Restart automatically if it crashes: after 5 s, 5 s, then 30 s; reset the failure count daily.
    sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/5000/restart/30000 | Out-Null
} else {
    # Re-runs may use another -InstallDir: point the service at what was just published.
    $service = Get-CimInstance Win32_Service -Filter "Name='$serviceName'"
    if ($service.PathName -ne $binaryPath) {
        Write-Host "Updating the service path to $binaryPath..."
        $result = Invoke-CimMethod -InputObject $service -MethodName Change -Arguments @{ PathName = $binaryPath }
        if ($result.ReturnValue -ne 0) { throw "Couldn't update the service path (error $($result.ReturnValue))." }
    }
}

Write-Host "Starting $serviceName..."
Start-Service -Name $serviceName
Get-Service -Name $serviceName | Format-Table -AutoSize Name, Status, StartType

Write-Host "Logs: $dataDir\logs"
Write-Host "Test: dotnet run --project src\SpaceDeck.Cli -- connect"
