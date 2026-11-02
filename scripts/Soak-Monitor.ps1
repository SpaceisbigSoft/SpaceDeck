<#
.SYNOPSIS
    Logs CPU, memory and handle counts of the SpaceDeck app and collector once a minute (Phase 12 soak test).

.DESCRIPTION
    Leave it running (e.g. 24 hours) while SpaceDeck is in normal use, then look at the CSV:
    memory, handles and GDI/USER objects should stay flat, CPU within the targets (collector < 1 %, app < 2 % idle at
    1 s polling, as cpu_percent).
    Run it in an elevated PowerShell window: the collector runs as SYSTEM, and an unelevated window usually can't read
    its CPU time, so the collector's rows would be missing (the script warns when that happens).

    CSV columns (the first eight are the same as in older logs, so they stay comparable):
      cpu_percent           CPU as a share of the whole machine (all cores = 100 %), like Task Manager's Processes tab.
      working_set_mb, private_mb, handles, threads
      cpu_one_core_percent  CPU as a share of one core (one busy core = 100 %), like Process Explorer's per-core view.
      cpu_machine_percent   The same value as cpu_percent, under a name that says what it is.
      gdi_objects, user_objects  GDI and USER objects (leaks of icons, brushes and windows show here, not in handles).
                            Read for the app only; blank for the collector (a service has none) or when unreadable.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Soak-Monitor.ps1 -Hours 24
#>
param(
    [double]$Hours = 24,
    [int]$IntervalSeconds = 60,
    [string]$OutFile = (Join-Path ([Environment]::GetFolderPath("Desktop")) "spacedeck-soak.csv")
)

$ErrorActionPreference = "Stop"
$appName = "SpaceDeck"
$names = @($appName, "SpaceDeck.Collector")
$cores = [Environment]::ProcessorCount
$end = (Get-Date).AddHours($Hours)
$last = @{}
$warned = @{}

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Warning ("This window isn't elevated. The collector service runs as SYSTEM and usually can't be read from here, " +
        "so the CSV would have no collector rows. For the soak test, stop this and run PowerShell as administrator.")
}

# GetGuiResources: GDI (0) and USER (1) object counts of a process.
if (-not ("SoakNative.User32" -as [type])) {
    Add-Type -Namespace SoakNative -Name User32 -MemberDefinition @'
[DllImport("user32.dll")]
public static extern uint GetGuiResources(System.IntPtr hProcess, uint uiFlags);
'@
}

# Never overwrite an earlier run's log (it may still be running, or not looked at yet): write next to it instead.
if ((Test-Path -LiteralPath $OutFile) -and (Get-Item -LiteralPath $OutFile).Length -gt 0) {
    $OutFile = [System.IO.Path]::Combine([System.IO.Path]::GetDirectoryName($OutFile),
        [System.IO.Path]::GetFileNameWithoutExtension($OutFile) + "-" + (Get-Date -Format "yyyyMMdd-HHmmss") +
        [System.IO.Path]::GetExtension($OutFile))
    Write-Warning "An earlier log is already there; this run writes to $OutFile instead."
}

"time,process,pid,cpu_percent,working_set_mb,private_mb,handles,threads,cpu_one_core_percent,cpu_machine_percent,gdi_objects,user_objects" |
    Set-Content -Path $OutFile -Encoding UTF8
Write-Host "Logging to $OutFile until $end (Ctrl+C to stop)."

while ((Get-Date) -lt $end) {
    $now = Get-Date
    foreach ($name in $names) {
        foreach ($p in @(Get-Process -Name $name -ErrorAction SilentlyContinue)) {
            try {
                try {
                    $cpu = $p.TotalProcessorTime.TotalSeconds
                } catch {
                    if (-not $warned.ContainsKey($name)) {
                        $warned[$name] = $true
                        Write-Warning "Can't read $name (pid $($p.Id)); run this window as administrator to include it."
                    }
                    continue
                }

                $key = "$name/$($p.Id)"
                $percent = ""
                $oneCore = ""
                if ($last.ContainsKey($key)) {
                    $prev = $last[$key]
                    $seconds = ($now - $prev.Time).TotalSeconds
                    if ($seconds -gt 0) {
                        $oneCore = [math]::Round((($cpu - $prev.Cpu) / $seconds) * 100, 2)
                        $percent = [math]::Round((($cpu - $prev.Cpu) / $seconds / $cores) * 100, 2)
                    }
                }
                $last[$key] = @{ Time = $now; Cpu = $cpu }

                $gdi = ""
                $user = ""
                if ($name -eq $appName) {
                    try {
                        $gdi = [SoakNative.User32]::GetGuiResources($p.Handle, 0)
                        $user = [SoakNative.User32]::GetGuiResources($p.Handle, 1)
                    } catch {
                        # Process handle not available (exited meanwhile): left blank.
                    }
                }

                $line = "{0},{1},{2},{3},{4},{5},{6},{7},{8},{9},{10},{11}" -f $now.ToString("s"), $name, $p.Id, $percent,
                    [math]::Round($p.WorkingSet64 / 1MB, 1), [math]::Round($p.PrivateMemorySize64 / 1MB, 1),
                    $p.HandleCount, $p.Threads.Count, $oneCore, $percent, $gdi, $user
                Add-Content -Path $OutFile -Value $line
            } finally {
                $p.Dispose() # its process handle, opened for the counters above
            }
        }
    }

    Start-Sleep -Seconds $IntervalSeconds
}

Write-Host "Done: $OutFile"
