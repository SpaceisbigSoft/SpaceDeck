#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Stops and removes the SensorDeck collector service and its installed files.
    Settings and logs in %ProgramData%\SensorDeck are kept unless -RemoveData is given.
#>
param(
    [string]$InstallDir = (Join-Path $env:ProgramFiles "SensorDeck\Collector"),
    [switch]$RemoveData
)

$ErrorActionPreference = "Stop"
$serviceName = "SensorDeckCollector"

# Deletes junctions, symbolic links and mount points under $Path without following them. Windows PowerShell 5.1's
# Remove-Item -Recurse follows junctions, so a planted link would otherwise make it delete files elsewhere.
function Remove-Links([string]$Path) {
    foreach ($entry in [System.IO.Directory]::EnumerateFileSystemEntries($Path)) {
        $attributes = [System.IO.File]::GetAttributes($entry)
        if ($attributes -band [System.IO.FileAttributes]::ReparsePoint) {
            if ($attributes -band [System.IO.FileAttributes]::Directory) {
                [System.IO.Directory]::Delete($entry) # removes the link only
            } else {
                [System.IO.File]::Delete($entry)
            }
        } elseif ($attributes -band [System.IO.FileAttributes]::Directory) {
            Remove-Links $entry
        }
    }
}

function Remove-Folder([string]$Path) {
    $item = Get-Item -LiteralPath $Path -Force
    if ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
        [System.IO.Directory]::Delete($Path) # a link: remove it, not what it points to
        return
    }

    Remove-Links $Path
    Remove-Item -LiteralPath $Path -Recurse -Force
}

$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -ne "Stopped") {
        Write-Host "Stopping $serviceName..."
        Stop-Service -Name $serviceName -Force
        $service.WaitForStatus("Stopped", [TimeSpan]::FromSeconds(30))
    }

    Write-Host "Removing service $serviceName..."
    sc.exe delete $serviceName | Out-Null
} else {
    Write-Host "Service $serviceName is not installed."
}

if (Test-Path -LiteralPath $InstallDir) {
    Write-Host "Removing $InstallDir..."
    Remove-Folder $InstallDir
}

if ($RemoveData) {
    $data = Join-Path $env:ProgramData "SensorDeck"
    if (Test-Path -LiteralPath $data) {
        Write-Host "Removing $data..."
        Remove-Folder $data
    }
}

Write-Host "Done."
