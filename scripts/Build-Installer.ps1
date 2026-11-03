#Requires -Version 5.1
<#
.SYNOPSIS
    Builds the SpaceDeck installer: artifacts\installer\SpaceDeck-<version>-x64.msi and SHA256SUMS.txt.

.DESCRIPTION
    1. Publishes the self-contained app and collector (scripts\Publish-Release.ps1) into artifacts\publish.
    2. Builds installer\SpaceDeck.Installer.wixproj (WiX v5, restored from NuGet) from those folders.
    3. Writes the MSI and a SHA256SUMS.txt (the checksum auto-update checks) to artifacts\installer.
    Windows only (WiX builds MSIs with Windows' own installer libraries).

.PARAMETER Version
    Release version, e.g. 0.1.0 or 0.2.0-beta.1. The MSI itself uses the numeric part (0.2.0).

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Build-Installer.ps1 -Version 0.1.0
#>
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [switch]$SkipPublish
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $repoRoot "artifacts\publish"
$outDir = Join-Path $repoRoot "artifacts\installer"
$numeric = ($Version -split '[-+]')[0]
if ($numeric -notmatch '^\d+\.\d+\.\d+$') { throw "Version must start with major.minor.build, e.g. 0.1.0 (got '$Version')." }

if (-not $SkipPublish) {
    & (Join-Path $PSScriptRoot "Publish-Release.ps1") -Version $Version -OutDir $publishDir
}
foreach ($exe in @("App\SpaceDeck.exe", "Collector\SpaceDeck.Collector.exe", "Collector\spacedeck-cli.exe")) {
    if (-not (Test-Path -LiteralPath (Join-Path $publishDir $exe))) { throw "Missing $exe in $publishDir; run without -SkipPublish." }
}

if (Test-Path -LiteralPath $outDir) { Remove-Item -LiteralPath $outDir -Recurse -Force }
New-Item -ItemType Directory -Path $outDir | Out-Null

$project = Join-Path $repoRoot "installer\SpaceDeck.Installer.wixproj"
Write-Host "Building the installer ($numeric)..."
dotnet build $project -c Release --nologo "-p:SpaceDeckVersion=$numeric" "-p:SpaceDeckPublishDir=$publishDir\" "-p:OutputPath=$outDir\"
if ($LASTEXITCODE -ne 0) { throw "The installer build failed." }

# Keep only the MSI (WiX also writes a .wixpdb with build details).
Get-ChildItem -LiteralPath $outDir -Recurse -File | Where-Object { $_.Extension -ne ".msi" } | Remove-Item -Force
$msi = Get-ChildItem -LiteralPath $outDir -Filter "*.msi" -Recurse | Select-Object -First 1
if (-not $msi) { throw "No MSI was produced." }
if ($msi.DirectoryName -ne $outDir) { Move-Item -LiteralPath $msi.FullName -Destination $outDir; $msi = Get-Item (Join-Path $outDir $msi.Name) }

$hash = (Get-FileHash -LiteralPath $msi.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
# Same format as sha256sum: "<hash>  <file name>"
Set-Content -LiteralPath (Join-Path $outDir "SHA256SUMS.txt") -Value "$hash  $($msi.Name)" -Encoding ascii

Write-Host ("{0}  {1:N0} MB" -f $msi.FullName, ($msi.Length / 1MB))
Write-Host "SHA-256 $hash"
