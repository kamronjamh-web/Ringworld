<#
.SYNOPSIS
  Runs tools/menu_warm_world_lifecycle_test.gd against an ISOLATED APPDATA holding COPIES of two saved worlds (never touches real saves).
.EXAMPLE
  .\tools\menu_warm_world_lifecycle_test.ps1 -Label d1 -Sequence          # windowed GPU: phases 1 (fresh), 2 (snapshot), 3 (stale) on one profile
  .\tools\menu_warm_world_lifecycle_test.ps1 -Label d1 -Phase 2 -Cache reuse
  .\tools\menu_warm_world_lifecycle_test.ps1 -Label run1 -Headless        # phases 1 and 4 (no pixels, so no snapshot checks)
.NOTES
  The isolated profile is .godot\phase_c_live\appdata_<Label>. It receives slot_<Primary> and slot_<Secondary> from the real profile
  plus the gameplay_*.tres preferences; the test refuses to run unless user:// resolves into such a profile. The primary slot must be the
  most recently played one (it is the slot the menu warm-loads). Output: .godot\phase_c_live\<Label>\console_phase<N>.log and
  results_phase<N>.json; screenshots land in .godot\phase_c_live\d1\. Phase 2 and 3 need the snapshot written by phase 1 (same profile).
#>
param(
  [string]$Label = "run",
  [int]$Primary = 1,
  [int]$Secondary = 2,
  [switch]$Headless,
  [int]$Phase = 1,
  [switch]$Sequence,
  [int]$ExitGraceSeconds = 60,
  [ValidateSet("none","copy","reuse")][string]$Cache = "none",
  [string]$GodotExe = "$env:USERPROFILE\Downloads\Godot_v4.8-dev7_mono_win64\Godot_v4.8-dev7_mono_win64\Godot_v4.8-dev7_mono_win64_console.exe",
  [string]$ProjectName = "RingworldTechDemo"
)
$ErrorActionPreference = "Stop"
$project = Split-Path -Parent $PSScriptRoot
$realProfile = Join-Path $env:APPDATA "Godot\app_userdata\$ProjectName"
$base = Join-Path $project ".godot\phase_c_live"
$isoRoot = Join-Path $base "appdata_$Label"
$isoProfile = Join-Path $isoRoot "Godot\app_userdata\$ProjectName"
$outDir = Join-Path $base $Label
New-Item -ItemType Directory -Force $outDir | Out-Null
if ($Cache -ne "reuse" -or -not (Test-Path $isoProfile)) {
  if (Test-Path $isoRoot) { Remove-Item -Recurse -Force $isoRoot }
  New-Item -ItemType Directory -Force (Join-Path $isoProfile "worlds") | Out-Null
  # The test expects the primary world at slot 1 and the secondary at slot 2 of the isolated profile.
  Copy-Item -Recurse (Join-Path $realProfile "worlds\slot_$Primary") (Join-Path $isoProfile "worlds\slot_1")
  Copy-Item -Recurse (Join-Path $realProfile "worlds\slot_$Secondary") (Join-Path $isoProfile "worlds\slot_2")
  # slot_N directories keep their own world.json slot number; fix it so the copies agree with their folder names.
  foreach ($pair in @(@(1, $Primary), @(2, $Secondary))) {
    $meta = Join-Path $isoProfile "worlds\slot_$($pair[0])\world.json"
    $text = Get-Content -Raw $meta
    $text = $text -replace '"slot":\s*\d+', ('"slot": ' + $pair[0])
    Set-Content -NoNewline -Path $meta -Value $text
  }
  # Phase 1 is the fresh boot: drop any menu snapshot the real slots carry so it starts on the starfield.
  Get-ChildItem (Join-Path $isoProfile "worlds") -Recurse -Filter "menu_snapshot.*" | Remove-Item -Force
  Get-ChildItem $realProfile -Filter "*.tres" | Copy-Item -Destination $isoProfile
  if ($Cache -eq "copy") {
    foreach ($dir in "shader_cache","vulkan","ring_cache","generated") {
      if (Test-Path (Join-Path $realProfile $dir)) { Copy-Item -Recurse (Join-Path $realProfile $dir) (Join-Path $isoProfile $dir) }
    }
  }
}
$env:APPDATA = $isoRoot
$phases = if ($Sequence) { @(1, 2, 3, 4) } else { @($Phase) }
if ($Headless -and $Sequence) { $phases = @(1, 4) }
$exitCode = 0
foreach ($phaseNumber in $phases) {
  if ($phaseNumber -eq 4) {
    # Phase 4 (showcase) needs a profile with no saved worlds: a fresh isolated APPDATA with only the preference resources.
    $emptyRoot = Join-Path $base "appdata_${Label}_empty"
    $emptyProfile = Join-Path $emptyRoot "Godot\app_userdata\$ProjectName"
    if (Test-Path $emptyRoot) { Remove-Item -Recurse -Force $emptyRoot }
    New-Item -ItemType Directory -Force $emptyProfile | Out-Null
    Get-ChildItem $realProfile -Filter "*.tres" | Copy-Item -Destination $emptyProfile
    $env:APPDATA = $emptyRoot
  } else { $env:APPDATA = $isoRoot }
  $arguments = @("--path", $project)
  if ($Headless) { $arguments += "--headless" }
  $arguments += @("--script", "res://tools/menu_warm_world_lifecycle_test.gd", "--", "--phase=$phaseNumber")
  $stdout = Join-Path $outDir "console_phase$phaseNumber.log"
  $watch = [Diagnostics.Stopwatch]::StartNew()
  # Safety net: Godot 4.8-dev7's WorkerThreadPool can hang at exit when low-priority tasks are still queued (WorldSession drains the
  # pool on exit to avoid it). If the process still has not exited within $ExitGraceSeconds of the verdict, kill our own process.
  $process = Start-Process $GodotExe -ArgumentList $arguments -RedirectStandardOutput $stdout -RedirectStandardError "$stdout.err" -PassThru -NoNewWindow
  $verdictAt = $null
  while (-not $process.HasExited) {
    Start-Sleep -Seconds 2
    if (-not $verdictAt -and (Select-String -Path $stdout -Pattern "MENU_WARM_WORLD_LIFECYCLE=(PASS|FAIL|SKIP)" -Quiet)) { $verdictAt = Get-Date }
    if ($verdictAt -and ((Get-Date) - $verdictAt).TotalSeconds -gt $ExitGraceSeconds) {
      "process did not exit within $ExitGraceSeconds s of the verdict; killing it (exit hang safety net)" | Add-Content $stdout
      Stop-Process -Id $process.Id -Force
      break
    }
  }
  $code = if ($process.HasExited -and $verdictAt -and (Select-String -Path $stdout -Pattern "LIFECYCLE=PASS" -Quiet)) { 0 } elseif (Select-String -Path $stdout -Pattern "LIFECYCLE=SKIP" -Quiet) { 0 } elseif ($verdictAt) { 1 } else { $process.ExitCode }
  "process wall seconds: {0:N1}  exit code: {1}" -f $watch.Elapsed.TotalSeconds, $code | Add-Content $stdout
  Copy-Item (Join-Path $base "d1\results_phase$phaseNumber.json") (Join-Path $outDir "results_phase$phaseNumber.json") -ErrorAction SilentlyContinue
  Select-String -Path $stdout -Pattern "Using Device|MENU_|WARM_WORLD|WORLD_LOADING_REPORT|FAIL|ERROR|exit code" | ForEach-Object { $_.Line.Substring(0, [Math]::Min(600, $_.Line.Length)) }
  if ($code -ne 0) { $exitCode = $code; break }
}
exit $exitCode
