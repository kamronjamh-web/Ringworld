<#
.SYNOPSIS
  Runs tools/terrain_altitude_lod_flight_test.gd (WO-E live flight invariant + perf capture) against an ISOLATED
  copy of a saved world. Never touches real saves: APPDATA is redirected to .godot\terrain_altitude_lod\appdata_<Label>.
.EXAMPLE
  # full invariant run on High (preset 2), copying the user's shader/ring caches so the world loads quickly
  .\tools\terrain_altitude_lod_flight_test.ps1 -Label high -Preset 2 -Cache copy
  # perf + screenshots only, moving leg at 16 km, reusing the profile left by the previous run
  .\tools\terrain_altitude_lod_flight_test.ps1 -Label high -Preset 2 -Cache reuse -ScriptArgs '--alts=16000,23000,60000 --pitches=-25,-70 --shots --move=16000 --quiet-only'
.NOTES
  -Preset 0 Low, 1 Medium, 2 High, 3 Desktop High. ALWAYS pass it: a saved slot may carry a different TerrainQuality.
  -Cache none | copy | reuse as for tools\menu_altitude_perf_capture.ps1.
  Output: .godot\terrain_altitude_lod\<OutLabel>\ (results.json, PNGs, console.log). Exit code 1 when a pose fails.
#>
param(
  [string]$Label = "run",
  [string]$OutLabel = "",
  [int]$Slot = 1,
  [int]$Preset = 2,
  [ValidateSet("none","copy","reuse")][string]$Cache = "copy",
  [string]$ScriptArgs = "",
  [string]$GodotExe = "$env:USERPROFILE\Downloads\Godot_v4.8-dev7_mono_win64\Godot_v4.8-dev7_mono_win64\Godot_v4.8-dev7_mono_win64_console.exe",
  [string]$ProjectName = "RingworldTechDemo"
)
$ErrorActionPreference = "Stop"
$project = Split-Path -Parent $PSScriptRoot
$realProfile = Join-Path $env:APPDATA "Godot\app_userdata\$ProjectName"
$base = Join-Path $project ".godot\terrain_altitude_lod"
$isoRoot = Join-Path $base "appdata_$Label"
$isoProfile = Join-Path $isoRoot "Godot\app_userdata\$ProjectName"
if (-not $OutLabel) { $OutLabel = $Label }
$outDir = Join-Path $base $OutLabel
New-Item -ItemType Directory -Force $outDir | Out-Null
if ($Cache -ne "reuse" -or -not (Test-Path $isoProfile)) {
  if (Test-Path $isoRoot) { Remove-Item -Recurse -Force $isoRoot }
  New-Item -ItemType Directory -Force (Join-Path $isoProfile "worlds") | Out-Null
  Copy-Item -Recurse (Join-Path $realProfile "worlds\slot_$Slot") (Join-Path $isoProfile "worlds\slot_$Slot")
  Get-ChildItem $realProfile -Filter "*.tres" | Copy-Item -Destination $isoProfile
  if ($Cache -eq "copy") {
    foreach ($dir in "shader_cache","vulkan","ring_cache","generated") {
      if (Test-Path (Join-Path $realProfile $dir)) { Copy-Item -Recurse (Join-Path $realProfile $dir) (Join-Path $isoProfile $dir) }
    }
  }
}
$env:APPDATA = $isoRoot
$arguments = @("--path", $project, "--script", "res://tools/terrain_altitude_lod_flight_test.gd", "--", "--out=$OutLabel", "--slot=$Slot", "--preset=$Preset") + ($ScriptArgs -split " " | Where-Object { $_ })
$stdout = Join-Path $outDir "console.log"
$watch = [Diagnostics.Stopwatch]::StartNew()
& $GodotExe @arguments *> $stdout
$code = $LASTEXITCODE
"process wall seconds: {0:N1} exit={1}" -f $watch.Elapsed.TotalSeconds, $code | Add-Content $stdout
Select-String -Path $stdout -Pattern "Using Device|FLIGHT" | ForEach-Object { $_.Line.Substring(0, [Math]::Min(500, $_.Line.Length)) }
exit $code
