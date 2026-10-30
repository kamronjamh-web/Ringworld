<#
.SYNOPSIS
  FIFO GPU queue for parallel agents sharing one GPU. Tickets live in .godot/gpu_queue/ (repo-local, shared by every session
  and runtime); the oldest live ticket holds the GPU.
.EXAMPLE
  .\tools\gpu_queue.ps1 -Acquire -Label <name>     # blocks until this label holds the GPU (prints GPU_ACQUIRED)
  .\tools\gpu_queue.ps1 -Heartbeat -Label <name>   # refresh your ticket (at least every 10 min while waiting or holding)
  .\tools\gpu_queue.ps1 -Release -Label <name>     # give the GPU up; wait >= 60 s before acquiring again if others wait
  .\tools\gpu_queue.ps1 -Status                    # show the queue (first line is the holder)
  .\tools\gpu_queue.ps1 -Label x -Wrap { .\tools\some_gpu_script.ps1 }      # acquire, run, heartbeat every 5 min, release
  .\tools\gpu_queue.ps1 -Label x -Wrap "powershell -File tools\foo.ps1"     # same, with a command string
.NOTES
  A ticket not refreshed for 15 min is stale and removed (a finished agent that forgot -Release blocks the queue only that long).
  tools/run_tests.ps1 acquires this queue itself for its GPU phase; do not wrap it.
#>
param(
    [switch]$Acquire, [switch]$Release, [switch]$Heartbeat, [switch]$Status,
    [object]$Wrap = $null,
    [string]$Label = "", [int]$TimeoutMinutes = 180
)
$ErrorActionPreference = "Stop"
$dir = Join-Path (Split-Path -Parent $PSScriptRoot) ".godot\gpu_queue"
New-Item -ItemType Directory -Force $dir | Out-Null
$staleAfter = [TimeSpan]::FromMinutes(15)   # holders heartbeat every <= 5-10 min, waiters every 15 s

function Get-Tickets {
    Get-ChildItem $dir -Filter *.ticket | ForEach-Object {
        if ((Get-Date) - $_.LastWriteTime -gt $staleAfter) { Remove-Item $_.FullName -ErrorAction SilentlyContinue; return }
        $_
    } | Sort-Object Name
}
function Get-Mine { Get-ChildItem $dir -Filter "*_$Label.ticket" -ErrorAction SilentlyContinue | Select-Object -First 1 }

function Invoke-Acquire {
    $mine = Get-Mine
    if (-not $mine) {
        $name = "{0:D20}_{1}.ticket" -f [DateTime]::UtcNow.Ticks, $Label
        $mine = New-Item -ItemType File (Join-Path $dir $name) -ErrorAction Stop
    }
    $deadline = (Get-Date).AddMinutes($TimeoutMinutes)
    while ((Get-Date) -lt $deadline) {
        $mine.LastWriteTime = Get-Date
        $head = Get-Tickets | Select-Object -First 1
        if ($head -and $head.Name -eq $mine.Name) { "GPU_ACQUIRED $Label"; return $true }
        Start-Sleep -Seconds 15
    }
    Remove-Item $mine.FullName -ErrorAction SilentlyContinue
    "GPU_TIMEOUT $Label"; return $false
}
function Invoke-Release {
    $m = Get-Mine
    if ($m) { Remove-Item $m.FullName -ErrorAction SilentlyContinue }
    "GPU_RELEASED $Label (wait >= 60 s before acquiring again)"
}

if ($Status) {
    $t = @(Get-Tickets)
    if ($t.Count -eq 0) { "GPU queue empty"; exit 0 }
    $i = 0
    foreach ($x in $t) {
        $lbl = $x.BaseName -replace '^\d+_', ''
        "{0}  {1}  last refresh {2:HH:mm:ss}" -f $(if ($i -eq 0) { "HOLDER " } else { "WAITING" }), $lbl, $x.LastWriteTime
        $i++
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
    try {
        $global:LASTEXITCODE = 0
        if ($Wrap -is [scriptblock]) { & $Wrap } else { Invoke-Expression ([string]$Wrap) }
        $code = [int]$global:LASTEXITCODE
    } catch { Write-Host "gpu_queue: wrapped command failed: $($_.Exception.Message)"; $code = 1 }
    finally {
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
