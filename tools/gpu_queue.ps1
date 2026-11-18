<#
.SYNOPSIS
  FIFO GPU queue for parallel agents sharing one GPU. Tickets live in .godot/gpu_queue/ (repo-local, shared by every session
  and runtime); the oldest live ticket holds the GPU, except that "shared" tickets may hold beside each other (see Classes).
.EXAMPLE
  .\tools\gpu_queue.ps1 -Acquire -Label <name>     # blocks until this label holds the GPU (prints GPU_ACQUIRED)
  .\tools\gpu_queue.ps1 -Acquire -Label <name> -Class shared [-Weight light]   # correctness / picture job, may share the GPU
  .\tools\gpu_queue.ps1 -Heartbeat -Label <name>   # refresh your ticket (at least every 10 min while waiting or holding)
  .\tools\gpu_queue.ps1 -Release -Label <name>     # give the GPU up; wait >= 60 s before acquiring again if others wait
  .\tools\gpu_queue.ps1 -Status                    # show the queue (first line is a holder; several holders are possible)
  .\tools\gpu_queue.ps1 -Label x -Wrap { .\tools\some_gpu_script.ps1 }      # acquire, run, heartbeat every 5 min, release
  .\tools\gpu_queue.ps1 -Label x -Wrap "powershell -File tools\foo.ps1"     # same, with a command string
.NOTES
  Classes (-Class, applies to -Acquire and -Wrap; default exclusive):
    exclusive  timing and performance measurements: alone on the GPU. This is the default, so callers that pass nothing are unchanged.
    shared     correctness and picture jobs. -Weight world (default) boots a world; -Weight light is a fixture job without a world.
  Rule, from the FIFO list of live tickets only (no overtaking):
    exclusive holds when it is the oldest ticket.
    shared holds when every older ticket is shared, at most 2 tickets are older (3 holders), and, if it is world weight,
      at most 1 older ticket is world weight (2 world jobs). A shared world job that holds beside another holder also waits
      while the GPU has less than 4500 MiB free (RINGWORLD_GPU_SHARED_MIN_FREE_MB overrides; skipped when alone or without nvidia-smi).
  Callers must not pass -Class shared for anything that measures time (frame times, GPU ms, benchmarks): use the default.
  The class is the ticket file's content (one line: "shared world", "shared light", "exclusive"); an empty ticket is exclusive,
  so older script instances still see a plain FIFO and treat new tickets by name order only.
  A ticket not refreshed for 15 min is stale and removed (a finished agent that forgot -Release blocks the queue only that long).
  tools/run_tests.ps1 acquires this queue itself for its GPU phase; do not wrap it.
#>
param(
    [switch]$Acquire, [switch]$Release, [switch]$Heartbeat, [switch]$Status,
    [object]$Wrap = $null,
    [string]$Label = "", [int]$TimeoutMinutes = 180,
    [ValidateSet("exclusive", "shared")][string]$Class = "exclusive",
    [ValidateSet("world", "light")][string]$Weight = "world"
)
$ErrorActionPreference = "Stop"
$dir = Join-Path (Split-Path -Parent $PSScriptRoot) ".godot\gpu_queue"
if ($env:RINGWORLD_GPU_QUEUE_DIR) { $dir = $env:RINGWORLD_GPU_QUEUE_DIR }   # tests only
New-Item -ItemType Directory -Force $dir | Out-Null
$staleAfter = [TimeSpan]::FromMinutes(15)   # holders heartbeat every <= 5-10 min, waiters every 15 s
$historyPath = Join-Path $dir "history.jsonl"
$maxHolders = 3
$maxWorldHolders = 2
$minFreeMb = 4500
if ($env:RINGWORLD_GPU_SHARED_MIN_FREE_MB) { $parsed = 0; if ([int]::TryParse($env:RINGWORLD_GPU_SHARED_MIN_FREE_MB, [ref]$parsed)) { $minFreeMb = $parsed } }
$ticketContent = if ($Class -eq "shared") { "shared $Weight" } else { "exclusive" }

# One JSON line per queue event (enqueue, acquire, release, timeout) for later wait analysis; never changes the queue's behaviour or output.
function Write-History([string]$kind, $waitedSeconds = $null, [string]$cls = $Class, [string]$wt = $Weight) {
    try {
        $o = [ordered]@{ time = [DateTime]::UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"); event = $kind; label = $Label; pid = $PID; class = $cls; weight = $wt }
        if ($null -ne $waitedSeconds) { $o["waited_s"] = [math]::Round([double]$waitedSeconds, 1) }
        $line = ($o | ConvertTo-Json -Compress) + "`n"
        for ($i = 1; $i -le 4; $i++) {
            try { [System.IO.File]::AppendAllText($historyPath, $line); break } catch { Start-Sleep -Milliseconds (40 * $i) }
        }
    } catch { }
}

# Class and weight of a ticket from its content; empty or unreadable is exclusive.
function Read-TicketClass([string]$path) {
    $text = ""
    try { $text = [System.IO.File]::ReadAllText($path) } catch { }
    $w = @($text.Trim().ToLowerInvariant() -split '\s+')
    if ($w.Count -ge 2 -and $w[0] -eq "shared" -and ($w[1] -eq "world" -or $w[1] -eq "light")) { return @("shared", $w[1]) }
    return @("exclusive", "world")
}

# Live tickets in FIFO order, each with its class, weight and whether it holds under the rule.
function Get-Queue {
    $files = @(Get-ChildItem $dir -Filter *.ticket | ForEach-Object {
        if ((Get-Date) - $_.LastWriteTime -gt $staleAfter) { Remove-Item $_.FullName -ErrorAction SilentlyContinue; return }
        $_
    } | Sort-Object Name)
    $out = @()
    $olderAllShared = $true; $olderWorld = 0
    for ($i = 0; $i -lt $files.Count; $i++) {
        $cw = Read-TicketClass $files[$i].FullName
        $holds = $false
        if ($cw[0] -eq "exclusive") { $holds = ($i -eq 0) }
        else {
            $holds = $olderAllShared -and $i -lt $maxHolders -and ($cw[1] -ne "world" -or $olderWorld -lt $maxWorldHolders)
        }
        if ($cw[0] -ne "shared") { $olderAllShared = $false }
        if ($cw[0] -eq "shared" -and $cw[1] -eq "world") { $olderWorld++ }
        $out += [pscustomobject]@{ File = $files[$i]; Name = $files[$i].Name; Class = $cw[0]; Weight = $cw[1]; Holds = $holds; Index = $i }
    }
    return $out
}
function Get-Mine { Get-ChildItem $dir -Filter "*_$Label.ticket" -ErrorAction SilentlyContinue | Select-Object -First 1 }

# Free VRAM in MiB of the first GPU, or $null when nvidia-smi is missing or fails.
function Get-FreeVramMb {
    try {
        $raw = & nvidia-smi --query-gpu=memory.total,memory.used --format=csv,noheader,nounits 2>$null
        if ($LASTEXITCODE -ne 0 -or -not $raw) { return $null }
        $parts = (@($raw)[0] -split ',') | ForEach-Object { $_.Trim() }
        if ($parts.Count -lt 2) { return $null }
        return ([double]$parts[0] - [double]$parts[1])
    } catch { return $null }
}

function Invoke-Acquire {
    $mine = Get-Mine
    if (-not $mine) {
        $name = "{0:D20}_{1}.ticket" -f [DateTime]::UtcNow.Ticks, $Label
        $path = Join-Path $dir $name
        $tmp = "$path.tmp"   # written complete, then renamed, so no reader sees a half-made ticket as exclusive
        [System.IO.File]::WriteAllText($tmp, $ticketContent)
        Move-Item $tmp $path -ErrorAction Stop
        $mine = Get-Item $path
        Write-History "enqueue"
    } else {
        $have = Read-TicketClass $mine.FullName
        $haveText = if ($have[0] -eq "shared") { "shared $($have[1])" } else { "exclusive" }
        if ($haveText -ne $ticketContent) { [System.IO.File]::WriteAllText($mine.FullName, $ticketContent) }
    }
    $t0 = Get-Date
    $deadline = (Get-Date).AddMinutes($TimeoutMinutes)
    $vramNoted = $false
    while ((Get-Date) -lt $deadline) {
        $mine.LastWriteTime = Get-Date
        $me = Get-Queue | Where-Object { $_.Name -eq $mine.Name } | Select-Object -First 1
        if ($me -and $me.Holds) {
            $ok = $true
            if ($Class -eq "shared" -and $Weight -eq "world" -and $me.Index -gt 0) {
                $free = Get-FreeVramMb
                if ($null -ne $free -and $free -lt $minFreeMb) {
                    $ok = $false
                    if (-not $vramNoted) { $vramNoted = $true; Write-Host ("GPU_QUEUE waiting for free VRAM: {0:N0} MiB free, shared world job needs {1} MiB" -f $free, $minFreeMb) }
                }
            }
            if ($ok) { Write-History "acquire" ((Get-Date) - $t0).TotalSeconds; "GPU_ACQUIRED $Label"; return $true }
        }
        Start-Sleep -Seconds 15
    }
    Remove-Item $mine.FullName -ErrorAction SilentlyContinue
    Write-History "timeout" ((Get-Date) - $t0).TotalSeconds
    "GPU_TIMEOUT $Label"; return $false
}
function Invoke-Release {
    $m = Get-Mine
    if ($m) {
        $have = Read-TicketClass $m.FullName
        Remove-Item $m.FullName -ErrorAction SilentlyContinue
        Write-History "release" $null $have[0] $have[1]
    }
    "GPU_RELEASED $Label (wait >= 60 s before acquiring again)"
}

if ($Status) {
    $t = @(Get-Queue)
    if ($t.Count -eq 0) { "GPU queue empty"; exit 0 }
    foreach ($x in $t) {
        $lbl = $x.File.BaseName -replace '^\d+_', ''
        $cls = if ($x.Class -eq "shared") { "shared $($x.Weight)" } else { "exclusive" }
        "{0}  {1,-12}  {2}  last refresh {3:HH:mm:ss}" -f $(if ($x.Holds) { "HOLDER " } else { "WAITING" }), $cls, $lbl, $x.File.LastWriteTime
    }
    exit 0
}

if ($null -ne $Wrap) {
    if ($Label -eq "") { $Label = "wrap_$PID" }
    if (-not (Invoke-Acquire)) { exit 1 }
    $hb = Start-Job -ArgumentList $dir, $Label -ScriptBlock {
        param($d, $l)
        while ($true) { Start-Sleep -Seconds 300; Get-ChildItem $d -Filter "*_$l.ticket" -ErrorAction SilentlyContinue | ForEach-Object { $_.LastWriteTime = Get-Date } }
    }
    $code = 0
    $prevNoGrab = $env:RINGWORLD_TEST_NO_GRAB; $env:RINGWORLD_TEST_NO_GRAB = '1'   # TestRunMouseRelease autoload
    try {
        $global:LASTEXITCODE = 0
        if ($Wrap -is [scriptblock]) { & $Wrap } else { Invoke-Expression ([string]$Wrap) }
        $code = [int]$global:LASTEXITCODE
    } catch { Write-Host "gpu_queue: wrapped command failed: $($_.Exception.Message)"; $code = 1 }
    finally {
        $env:RINGWORLD_TEST_NO_GRAB = $prevNoGrab
        Stop-Job $hb -ErrorAction SilentlyContinue; Remove-Job $hb -Force -ErrorAction SilentlyContinue
        Invoke-Release
    }
    exit $code
}

if ($Label -eq "") { throw "-Label is required" }
if ($Heartbeat) { $m = Get-Mine; if ($m) { $m.LastWriteTime = Get-Date; "GPU_HEARTBEAT $Label" }; exit 0 }
if ($Release) { Invoke-Release; exit 0 }
if ($Acquire) { if (Invoke-Acquire) { exit 0 } else { exit 1 } }
throw "Specify one of -Acquire, -Release, -Heartbeat, -Status, -Wrap"
