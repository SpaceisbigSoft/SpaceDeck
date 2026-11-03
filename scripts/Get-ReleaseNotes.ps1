#Requires -Version 7
<#
.SYNOPSIS
    Writes the GitHub release notes for one version: its CHANGELOG.md section, how to install, and the compare link.

.EXAMPLE
    pwsh ./scripts/Get-ReleaseNotes.ps1 -Version 0.1.2 -OutFile notes.md

.EXAMPLE
    pwsh ./scripts/Get-ReleaseNotes.ps1 -ListVersions    # every version in CHANGELOG.md, newest first
#>
param(
    [string]$Version = "",
    [string]$OutFile = "",
    [switch]$ListVersions
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$repoUrl = "https://github.com/SpaceisbigSoft/SpaceDeck"
$lines = Get-Content -LiteralPath (Join-Path $repoRoot "CHANGELOG.md") -Encoding utf8

# "## [0.1.2] - 2026-11-03" headings, in file order (newest first).
$headings = @()
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match '^## \[(?<v>[^\]]+)\]') { $headings += [pscustomobject]@{ Version = $Matches.v; Line = $i } }
}

if ($ListVersions) {
    $headings | ForEach-Object { $_.Version }
    return
}

$index = [array]::FindIndex([object[]]$headings, [Predicate[object]]{ param($h) $h.Version -eq $Version })
if ($index -lt 0) { throw "CHANGELOG.md has no section for $Version." }

$start = $headings[$index].Line + 1
$end = if ($index + 1 -lt $headings.Count) { $headings[$index + 1].Line } else { $lines.Count }
# GitHub shows every line break in release notes, so lines wrapped in CHANGELOG.md are joined back up: an indented
# line continues the list item above it, and a plain line continues the paragraph above it.
$joined = [System.Collections.Generic.List[string]]::new()
foreach ($line in $lines[$start..($end - 1)]) {
    $text = $line.TrimEnd()
    $startsBlock = $text -match '^\s*([-*+] |\d+\. |#|>|\||```)' -or $text -eq ''
    if ($joined.Count -gt 0 -and $joined[-1] -ne '' -and -not $startsBlock -and -not $joined[-1].StartsWith('#')) {
        $joined[-1] = $joined[-1] + ' ' + $text.Trim()
    } else {
        $joined.Add($text)
    }
}
$section = ($joined -join "`n").Trim()
if (-not $section) { throw "The CHANGELOG.md section for $Version is empty." }

$previous = if ($index + 1 -lt $headings.Count) { $headings[$index + 1].Version } else { $null }
$compare = if ($previous) { "$repoUrl/compare/v$previous...v$Version" } else { "$repoUrl/commits/v$Version" }

$notes = @"
$section

---

**Install:** install the [PawnIO](https://pawnio.eu) driver first (once), then run ``SpaceDeck-$Version-x64.msi`` from the assets below. Already have SpaceDeck? Use **About → Updates → Download and install**, or run the new installer over the old one; settings and history are kept. ``SHA256SUMS.txt`` lists the installer's checksum.

**Full changelog:** $compare
"@

if ($OutFile) { Set-Content -LiteralPath $OutFile -Value $notes -Encoding utf8NoBOM } else { $notes }
