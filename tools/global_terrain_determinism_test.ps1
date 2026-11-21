<#
.SYNOPSIS
  Two-launch determinism test for the global terrain: the same saved world, reopened twice, must publish identical per-segment morph hashes.
.EXAMPLE
  .\tools\global_terrain_determinism_test.ps1 -Label gtdet
.NOTES
  Isolated APPDATA (.godot\gt_determinism\appdata_<Label>_<pid>, deleted at the end). Launch 1 creates a certified runtime-generation world in
  slot 1 and saves it (its hashes are reported for information only); launches 2 and 3 reopen the saved slot through the loading screen and
  must match. Regression for the load-timing race where segments sampled before the surface height field attached took the 1 m bilinear path.
  Takes the GPU queue itself unless a parent run already holds it (gpu_queue.ps1 nested mode). Prints GLOBAL_TERRAIN_DETERMINISM=PASS|FAIL.
#>
param(
  [string]$Label = "gtdet",
  [int]$TimeoutSeconds = 420,
  [switch]$InQueue,
  [string]$ProjectName = "RingworldTechDemo"
)
$ErrorActionPreference = "Stop"
$project = Split-Path -Parent $PSScriptRoot

if (-not $InQueue) {
  $inner = "& '$PSCommandPath' -InQueue -Label '$Label' -TimeoutSeconds $TimeoutSeconds"
  & (Join-Path $PSScriptRoot "gpu_queue.ps1") -Label "gt_determinism" -Wrap $inner
  exit $LASTEXITCODE
}

$manifest = Get-Content (Join-Path $PSScriptRoot "test_manifest.json") -Raw | ConvertFrom-Json
$engine = Join-Path $project ([string]$manifest.engine)
$isoRoot = Join-Path $project ".godot\gt_determinism\appdata_${Label}_$PID"
$isoProfile = Join-Path $isoRoot "Godot\app_userdata\$ProjectName"
$logDir = Join-Path $project ".godot\gt_determinism\logs_${Label}_$PID"

function Remove-Scratch {
  # The profile's vulkan cache has paths too long for Remove-Item; rd handles them with the extended prefix.
  if (Test-Path $isoRoot) { cmd /c "rd /s /q \\?\$isoRoot" | Out-Null }
  if (Test-Path $isoRoot) { Remove-Item -Recurse -Force $isoRoot -ErrorAction SilentlyContinue }
}

function Invoke-Launch([string]$Mode, [string]$Name) {
  $log = Join-Path $logDir "$Name.log"
  $godotArgs = @("--path", "`"$project`"", "--script", "res://tools/global_terrain_determinism_test.gd", "--", "--mode=$Mode")
  $old = $env:APPDATA
  $env:APPDATA = $isoRoot
  try {
    $proc = Start-Process -FilePath $engine -ArgumentList $godotArgs -PassThru -NoNewWindow -RedirectStandardOutput $log -RedirectStandardError "$log.err"
    if (-not $proc.WaitForExit($TimeoutSeconds * 1000)) { $proc.Kill(); Write-Host "  $Name timed out after $TimeoutSeconds s (this process only)" }
  } finally { $env:APPDATA = $old }
  if (Test-Path "$log.err") { Get-Content "$log.err" | Add-Content $log; Remove-Item "$log.err" }
  $line = Select-String -Path $log -Pattern "^GLOBAL_TERRAIN_DETERMINISM_HASHES=(.+)$" | Select-Object -Last 1
  $ok = Select-String -Path $log -Pattern "GLOBAL_TERRAIN_DETERMINISM_LAUNCH=PASS" -Quiet
  if (-not $line -or -not $ok) {
    Write-Host "launch '$Name' did not produce hashes; log tail:"
    Get-Content $log -Tail 25 | Out-Host
    return $null
  }
  return $line.Matches[0].Groups[1].Value.Trim()
}

# "<total>|<seg0>,<seg1>,..." lines; returns whether the totals match and the indices of the differing segments.
function Compare-Hashes([string]$First, [string]$Second) {
  $partsFirst = $First.Split('|')
  $partsSecond = $Second.Split('|')
  $segFirst = $partsFirst[1].Split(',')
  $segSecond = $partsSecond[1].Split(',')
  $diff = @()
  for ($i = 0; $i -lt [Math]::Max($segFirst.Count, $segSecond.Count); $i++) { if ($segFirst[$i] -ne $segSecond[$i]) { $diff += $i } }
  return @{ TotalEqual = ($partsFirst[0] -eq $partsSecond[0]); Segments = $diff; Count = $segFirst.Count }
}

$verdict = "FAIL"
try {
  & (Join-Path $PSScriptRoot "prune_test_output.ps1") -Apply -Quiet | Out-Host
  Remove-Scratch
  New-Item -ItemType Directory -Force (Join-Path $isoProfile "worlds") | Out-Null
  New-Item -ItemType Directory -Force $logDir | Out-Null
  Set-Content (Join-Path $isoRoot "profile_created.txt") (Get-Date -Format o)   # keeps the profile young for other runners' prune

  Write-Host "launch 1/3: create + save"
  $create = Invoke-Launch "create" "launch1_create"
  Write-Host "launch 2/3: reopen saved slot"
  $reloadA = if ($create) { Invoke-Launch "reload" "launch2_reload" } else { $null }
  Write-Host "launch 3/3: reopen saved slot"
  $reloadB = if ($reloadA) { Invoke-Launch "reload" "launch3_reload" } else { $null }
  if ($create) { Write-Host "GLOBAL_TERRAIN_DETERMINISM_HASHES_CREATE=$create" }
  if ($reloadA) { Write-Host "GLOBAL_TERRAIN_DETERMINISM_HASHES_RELOAD1=$reloadA" }
  if ($reloadB) { Write-Host "GLOBAL_TERRAIN_DETERMINISM_HASHES_RELOAD2=$reloadB" }
  if ($reloadA -and $reloadB) {
    $r = Compare-Hashes $reloadA $reloadB
    if ($r.TotalEqual -and $r.Segments.Count -eq 0) { $verdict = "PASS" }
    else { Write-Host "reload 1 vs reload 2: total equal=$($r.TotalEqual); differing segments ($($r.Segments.Count) of $($r.Count)): $($r.Segments -join ',')" }
    $c = Compare-Hashes $create $reloadA
    Write-Host "info: create vs reload 1 differing segments: $(if ($c.Segments.Count) { $c.Segments -join ',' } else { 'none' })"
  }
} finally {
  Remove-Scratch
  if ($verdict -eq "PASS" -and (Test-Path $logDir)) { Remove-Item -Recurse -Force $logDir -ErrorAction SilentlyContinue }
  & (Join-Path $PSScriptRoot "prune_test_output.ps1") -Apply -Quiet | Out-Host
}
Write-Host "GLOBAL_TERRAIN_DETERMINISM=$verdict"
if ($verdict -ne "PASS") { Write-Host "logs kept at $logDir"; exit 1 }
exit 0
