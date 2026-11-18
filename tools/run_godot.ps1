<#
.SYNOPSIS
  Ad-hoc Godot launcher for agents: parse-checks the entry script first, takes the GPU queue for windowed runs, and supervises the
  process (stall watchdog, hard timeout, load-failure kill) through tools/testing/GpuJob.psm1. Use it instead of
  `& godot.exe ... *> log` (empty logs under $ErrorActionPreference='Stop', hangs after a script parse error).
.EXAMPLE
  .\tools\run_godot.ps1 -Headless -Script res://tools/some_test.gd -Marker SOME_TEST
  .\tools\run_godot.ps1 -Script res://tools/some_capture.gd -UserArgs "--out foo" -AppData .\.godot\appdata_mytask -Label mytask
  .\tools\run_godot.ps1 -Scene res://scenes/foo.tscn -TimeoutMin 5 -NoQueue
.NOTES
  Exit codes: 0 process exited 0 (with -Marker: a PASS or SKIP line and no FAIL line), 1 anything else (non-zero exit, load-failed,
  stalled, timeout, marker FAIL), 2 preflight or usage problem (nothing was launched), 9 the user is playing (windowed runs only).
  -Class shared [-Weight light] lets a correctness or picture run share the GPU (see gpu_queue.ps1); never for timing runs (default exclusive).
  Windowed runs hold the GPU queue (tools\gpu_queue.ps1) and heartbeat the ticket once a minute. Without -AppData the real profile
  is used. RINGWORLD_TEST_NO_GRAB=1 is set for the child.
#>
param(
    [string]$Script = '', [string]$Scene = '', [switch]$Headless,
    [string]$EngineArgs = '', [string]$UserArgs = '',
    [string]$Log = '', [string]$AppData = '',
    [double]$TimeoutMin = 30, [double]$StallMinutes = 10,
    [string]$Marker = '', [string]$Label = '', [switch]$NoQueue,
    [ValidateSet('exclusive', 'shared')][string]$Class = 'exclusive', [ValidateSet('world', 'light')][string]$Weight = 'world'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Import-Module (Join-Path $PSScriptRoot 'testing\GpuJob.psm1') -Force -DisableNameChecking

if (($Script -eq '') -eq ($Scene -eq '')) { Write-Host 'run_godot: give exactly one of -Script or -Scene'; exit 2 }
$target = if ($Script) { $Script } else { $Scene }
$name = [System.IO.Path]::GetFileNameWithoutExtension(($target -replace '^res://', ''))
$stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
if ($Log -eq '') { $Log = Join-Path $root ".godot\captures\run_godot\${name}_$stamp.log" }
elseif (-not [System.IO.Path]::IsPathRooted($Log)) { $Log = Join-Path (Get-Location).Path $Log }
if ($Label -eq '') { $Label = "run_godot_${name}_$stamp" }

# --- preflight: parse check of the entry script (no GPU, no queue)
if ($Script) {
    $pf = Test-GodotScriptParse -Script $Script
    if (-not $pf.Ok) {
        Write-Host "PREFLIGHT=FAIL $Script ($($pf.Seconds) s)"
        foreach ($e in $pf.Errors) { Write-Host "  $e" }
        Write-Host 'run_godot: nothing was launched (exit 2)'
        exit 2
    }
    Write-Host "preflight ok: $Script parses ($($pf.Seconds) s)"
} else { Write-Host 'preflight: scene run, no script parse check' }

if (-not $Headless -and (Test-UserPlaying)) { Write-Host 'USER_PLAYING'; exit 9 }

$engine = Join-Path $root 'artifacts\engine-upgrade\Godot_v4.8-dev7_mono_win64\Godot_v4.8-dev7_mono_win64_console.exe'
$godotArgs = @('--path', $root)
if ($Headless) { $godotArgs += '--headless' }
if ($EngineArgs.Trim()) { $godotArgs += @($EngineArgs.Trim() -split '\s+') }
if ($Script) { $godotArgs += @('--script', $Script) } else { $godotArgs += $Scene }
if ($UserArgs.Trim()) { $godotArgs += '--'; $godotArgs += @($UserArgs.Trim() -split '\s+') }

$envMap = @{ RINGWORLD_TEST_NO_GRAB = '1' }
if ($AppData) {
    $ad = if ([System.IO.Path]::IsPathRooted($AppData)) { $AppData } else { Join-Path (Get-Location).Path $AppData }
    New-Item -ItemType Directory -Force $ad | Out-Null
    $envMap['APPDATA'] = $ad
    Write-Host "APPDATA: $ad"
} else { Write-Host 'note: no -AppData, the real profile is in use' }

$queueScript = Join-Path $PSScriptRoot 'gpu_queue.ps1'
$queueDir = Join-Path $root '.godot\gpu_queue'
if ($env:RINGWORLD_GPU_QUEUE_DIR) { $queueDir = $env:RINGWORLD_GPU_QUEUE_DIR }
$useQueue = (-not $Headless) -and (-not $NoQueue)
$hbState = @{ last = [DateTime]::UtcNow }
$onTick = {
    param($proc)
    if (([DateTime]::UtcNow - $hbState.last).TotalSeconds -ge 60) {
        $hbState.last = [DateTime]::UtcNow
        Get-ChildItem $queueDir -Filter "*_$Label.ticket" -ErrorAction SilentlyContinue | ForEach-Object { $_.LastWriteTime = Get-Date }
    }
}

$exit = 1
$acquired = $false
try {
    if ($useQueue) {
        Write-Host "GPU queue: acquiring as $Label"
        & $queueScript -Acquire -Label $Label -Class $Class -Weight $Weight | Out-Host
        if ($LASTEXITCODE -ne 0) { Write-Host 'run_godot: could not acquire the GPU queue'; exit 1 }
        $acquired = $true
    }
    Write-Host "launch: $($godotArgs -join ' ')"
    $job = Invoke-GodotJob -Exe $engine -Arguments $godotArgs -WorkDir $root -Log $Log -Env $envMap `
        -TimeoutS ($TimeoutMin * 60) -StallS ($StallMinutes * 60) -EntryScript $(if ($Script) { $Script } else { '' }) -OnTick $onTick
    Write-Host "status=$($job.Status) exit_code=$($job.ExitCode) seconds=$($job.Seconds) detail=$($job.Detail)"
    $markerOk = $true
    if ($Marker) {
        $ml = @($job.Lines | Where-Object { $_ -match "\b$([regex]::Escape($Marker))=(PASS|FAIL|SKIP)\b" })
        foreach ($l in $ml) { Write-Host $l }
        $hasFail = @($ml | Where-Object { $_ -match "$([regex]::Escape($Marker))=FAIL" }).Count -gt 0
        $hasOk = @($ml | Where-Object { $_ -match "$([regex]::Escape($Marker))=(PASS|SKIP)" }).Count -gt 0
        $markerOk = ($hasOk -and -not $hasFail)
        if (-not $ml) { Write-Host "no $Marker=PASS|FAIL|SKIP line found" }
    }
    $firstErr = $job.Lines | Where-Object { $_ -match '^(SCRIPT ERROR|ERROR:|Parse Error)' -and $_ -notmatch 'GDExtension dynamic library not found|Error loading extension|FileAccess::exists\(path\)' } | Select-Object -First 1
    if ($firstErr) { Write-Host "first error: $firstErr" }
    Write-Host "log: $Log"
    $exit = if ($job.Status -eq 'exited' -and $job.ExitCode -eq 0 -and $markerOk) { 0 } else { 1 }
} finally {
    if ($acquired) { & $queueScript -Release -Label $Label | Out-Host }
}
exit $exit
