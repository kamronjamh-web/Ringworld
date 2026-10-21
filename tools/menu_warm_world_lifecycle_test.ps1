<#
.SYNOPSIS
  Runs tools/menu_warm_world_lifecycle_test.gd against an ISOLATED APPDATA holding COPIES of two saved worlds (never touches real saves).
.EXAMPLE
  .\tools\menu_warm_world_lifecycle_test.ps1 -Label run1                 # windowed GPU run, writes .godot\phase_c_live\live_menu.png
  .\tools\menu_warm_world_lifecycle_test.ps1 -Label run1 -Headless
.NOTES
  The isolated profile is .godot\phase_c_live\appdata_<Label>. It receives slot_<Primary> and slot_<Secondary> from the real profile
  plus the gameplay_*.tres preferences; the test refuses to run unless user:// resolves into such a profile. The primary slot must be the
  most recently played one (it is the slot the menu warm-loads). Output: .godot\phase_c_live\<Label>\console.log and results.json copy.
#>
param(
  [string]$Label = "run",
  [int]$Primary = 1,
  [int]$Secondary = 2,
  [switch]$Headless,
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
  Get-ChildItem $realProfile -Filter "*.tres" | Copy-Item -Destination $isoProfile
  if ($Cache -eq "copy") {
    foreach ($dir in "shader_cache","vulkan","ring_cache","generated") {
      if (Test-Path (Join-Path $realProfile $dir)) { Copy-Item -Recurse (Join-Path $realProfile $dir) (Join-Path $isoProfile $dir) }
    }
  }
}
$env:APPDATA = $isoRoot
$arguments = @("--path", $project)
if ($Headless) { $arguments += "--headless" }
$arguments += @("--script", "res://tools/menu_warm_world_lifecycle_test.gd")
$stdout = Join-Path $outDir "console.log"
$watch = [Diagnostics.Stopwatch]::StartNew()
& $GodotExe @arguments *> $stdout
$code = $LASTEXITCODE
"process wall seconds: {0:N1}  exit code: {1}" -f $watch.Elapsed.TotalSeconds, $code | Add-Content $stdout
Copy-Item (Join-Path $base "results.json") (Join-Path $outDir "results.json") -ErrorAction SilentlyContinue
Select-String -Path $stdout -Pattern "Using Device|MENU_WARM|WARM_WORLD|WORLD_LOADING_REPORT|FAIL|ERROR|exit code" | ForEach-Object { $_.Line.Substring(0, [Math]::Min(600, $_.Line.Length)) }
exit $code
