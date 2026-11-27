#Requires -Version 5.1
<#
.SYNOPSIS
    Builds SpaceDeck's FanControl plugin and zips it for a release: FanControl.LianLi-SpaceDeck-<version>.zip.

.DESCRIPTION
    The cooler screen, the Lian Li cooler readings and the screen ring need SpaceDeck's FanControl plugin, which the
    MSI doesn't install (FanControl loads its own plugins). This clones the plugin fork's branch into a temporary
    folder (outside this repo, so its Directory.*.props and global.json don't apply), builds it as the fork's own CI
    does - the Lighting variant named FanControl.LianLi-SpaceDeck - and writes the zip (the DLL and a read-me) to
    OutDir. When OutDir has a SHA256SUMS.txt (Build-Installer.ps1 writes one), the zip's checksum is added to it.
    Needs git and the .NET 9 SDK (the fork's global.json pins SDK 9).

    The plugin's version is 2.0.<commits on the branch>, so one commit always gets one version.

.PARAMETER OutDir
    Where the zip goes. Default: artifacts\installer (beside the MSI).

.PARAMETER Branch
    The fork's branch to build. Default: spacedeck (what ships).

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Build-FanControlPlugin.ps1 -OutDir artifacts\plugin-test
#>
param(
    [string]$OutDir = "",
    [string]$Branch = "spacedeck"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutDir) { $OutDir = Join-Path $repoRoot "artifacts\installer" }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$OutDir = (Resolve-Path -LiteralPath $OutDir).Path

$temp = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() }
$work = Join-Path $temp ("spacedeck-fancontrol-plugin-" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
$clone = Join-Path $work "source"
$built = Join-Path $work "built"
$staging = Join-Path $work "zip"

git clone --quiet --branch $Branch https://github.com/SpaceisbigSoft/SpaceDeck.FanControl.LianLi.git $clone
if ($LASTEXITCODE -ne 0) { throw "Cloning the FanControl plugin failed." }
$count = (git -C $clone rev-list --count HEAD).Trim()
$sha = (git -C $clone rev-parse --short HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or -not $count -or -not $sha) { throw "Reading the FanControl plugin's commit failed." }
$pluginVersion = "2.0.$count"

Push-Location -LiteralPath $clone
try {
    dotnet build src/FanControl.LianLi/FanControl.LianLi.csproj -c Release -o $built `
        -p:EnableLighting=true -p:AssemblyName=FanControl.LianLi-SpaceDeck `
        "-p:Version=$pluginVersion" "-p:InformationalVersion=$pluginVersion+$sha"
    if ($LASTEXITCODE -ne 0) { throw "Building the FanControl plugin failed." }
} finally {
    Pop-Location
}

New-Item -ItemType Directory -Force -Path $staging | Out-Null
Copy-Item -LiteralPath (Join-Path $built "FanControl.LianLi-SpaceDeck.dll") -Destination $staging
@(
    "SpaceDeck's FanControl plugin $pluginVersion (SpaceisbigSoft/SpaceDeck.FanControl.LianLi, $Branch $sha)"
    ""
    "Needed for SpaceDeck's cooler screen, Lian Li cooler readings and screen ring lighting (Lian Li GA II LCD)."
    ""
    "Install: in FanControl, use the Install plugin button and choose FanControl.LianLi-SpaceDeck.dll."
    "FanControl loads it at once. If you had the upstream Lian Li plugin, this one takes over its devices;"
    "check that your fan curves still point at the Lian Li fans and pump afterwards."
    ""
    "Screen ring in OpenRGB: Settings -> SDK Client, add 127.0.0.1 port 6743, tick Save Connection."
) | Set-Content -LiteralPath (Join-Path $staging "READ ME.txt") -Encoding utf8

$zipName = "FanControl.LianLi-SpaceDeck-$pluginVersion.zip"
$zip = Join-Path $OutDir $zipName
if (Test-Path -LiteralPath $zip) { throw "$zip exists already; a release file is never overwritten." }
Compress-Archive -Path (Join-Path $staging "*") -DestinationPath $zip
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()

$sums = Join-Path $OutDir "SHA256SUMS.txt"
if (Test-Path -LiteralPath $sums) {
    # Same format as sha256sum: "<hash>  <file name>"
    Add-Content -LiteralPath $sums -Value "$hash  $zipName" -Encoding ascii
}

Write-Host "$zip"
Write-Host "SHA-256 $hash"
