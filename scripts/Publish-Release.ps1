#Requires -Version 5.1
<#
.SYNOPSIS
    Publishes self-contained release builds of SpaceDeck (no .NET install needed on the target PC).

.DESCRIPTION
    Produces two folders, the input for the installer (Phase 11):

      <OutDir>\App        SpaceDeck.exe (the app) with its own .NET runtime, WPF and ASP.NET Core
      <OutDir>\Collector  SpaceDeck.Collector.exe (the service) and spacedeck-cli.exe, sharing one .NET runtime

    The CLI shares the collector's folder: both use the same libraries (Core + LibreHardwareMonitor), so every file
    they have in common is identical. Debug symbols are embedded in the DLLs (stack traces keep line numbers) and only
    English resources ship.

.PARAMETER Version
    Release version, e.g. 0.1.0 or 0.2.0-beta.1. Defaults to the <Version> in Directory.Build.props.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Publish-Release.ps1 -Version 0.1.0
#>
param(
    [string]$Version = "",
    [string]$OutDir = "",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutDir) { $OutDir = Join-Path $repoRoot "artifacts/publish" }
$OutDir = [System.IO.Path]::GetFullPath($OutDir)

$common = @(
    "-c", "Release",
    "-r", $Runtime,
    "--self-contained", "true",
    "-p:DebugType=embedded",
    "-p:SatelliteResourceLanguages=en",
    "--nologo"
)
if ($Version) {
    # "0.2.0-beta.1" -> assembly/file version 0.2.0; the full text becomes the product (informational) version.
    $numeric = ($Version -split '[-+]')[0]
    $common += @("-p:Version=$Version", "-p:AssemblyVersion=$numeric", "-p:FileVersion=$numeric")
}

if (Test-Path -LiteralPath $OutDir) { Remove-Item -LiteralPath $OutDir -Recurse -Force }

$targets = @(
    @{ Project = "src/SpaceDeck.App/SpaceDeck.App.csproj";             Dest = "App" },
    @{ Project = "src/SpaceDeck.Collector/SpaceDeck.Collector.csproj"; Dest = "Collector" },
    @{ Project = "src/SpaceDeck.Cli/SpaceDeck.Cli.csproj";             Dest = "Collector" }
)

foreach ($t in $targets) {
    $project = Join-Path $repoRoot $t.Project
    $dest = Join-Path $OutDir $t.Dest
    Write-Host "Publishing $($t.Project) -> $dest"
    dotnet publish $project @common -o $dest
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $($t.Project)." }
}

foreach ($d in @("App", "Collector")) {
    $folder = Join-Path $OutDir $d
    $size = (Get-ChildItem -LiteralPath $folder -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB
    Write-Host ("{0,-10} {1,6:N0} MB  {2}" -f $d, $size, $folder)
}
