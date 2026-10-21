<#
.SYNOPSIS
  Runs tools/menu_altitude_perf_capture.gd against an ISOLATED copy of a saved world (never touches real saves).
.EXAMPLE
  # cold: fresh isolated profile (slot copy only, no shader/pipeline/ring caches), load only
  .\tools\menu_altitude_perf_capture.ps1 -Label cold1 -Cache none -ScriptArgs '--load-only'
  # warm: reuse the profile from the previous run
  .\tools\menu_altitude_perf_capture.ps1 -Label cold1 -Cache reuse -ScriptArgs '--load-only'
  # full altitude capture with the user's caches copied
  .\tools\menu_altitude_perf_capture.ps1 -Label fly1 -Cache copy
.NOTES
  -Cache none  : new isolated profile containing only worlds\slot_N and the gameplay_*.tres preferences
  -Cache copy  : additionally copies shader_cache, vulkan, ring_cache and generated from the real profile
  -Cache reuse : keeps the existing isolated profile as left by the previous run
  Output: .godot\phase_c_p0\<OutLabel (default Label)>\ (results.json, PNGs, console.log). Isolated profile: .godot\phase_c_p0\appdata_<Label>
#>
param(
  [string]$Label = "run",
  [string]$OutLabel = "",
  [int]$Slot = 1,
  [ValidateSet("none","copy","reuse")][string]$Cache = "none",
  [string]$ScriptArgs = "",
  [string]$GodotExe = "$env:USERPROFILE\Downloads\Godot_v4.8-dev7_mono_win64\Godot_v4.8-dev7_mono_win64\Godot_v4.8-dev7_mono_win64_console.exe",
  [string]$ProjectName = "RingworldTechDemo"
)
$ErrorActionPreference = "Stop"
$project = Split-Path -Parent $PSScriptRoot
$realProfile = Join-Path $env:APPDATA "Godot\app_userdata\$ProjectName"
$base = Join-Path $project ".godot\phase_c_p0"
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
$arguments = @("--path", $project, "--script", "res://tools/menu_altitude_perf_capture.gd", "--", "--out=phase_c_p0/$OutLabel", "--slot=$Slot") + ($ScriptArgs -split " " | Where-Object { $_ })
$stdout = Join-Path $outDir "console.log"
$watch = [Diagnostics.Stopwatch]::StartNew()
& $GodotExe @arguments *> $stdout
"process wall seconds: {0:N1}" -f $watch.Elapsed.TotalSeconds | Add-Content $stdout
Select-String -Path $stdout -Pattern "Using Device|WORLD_LOADING_REPORT|P0 " | ForEach-Object { $_.Line.Substring(0, [Math]::Min(400, $_.Line.Length)) }
