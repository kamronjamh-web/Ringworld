<#
.SYNOPSIS
  Regenerates res://resources/menu/showcase_world.json from the launcher defaults (isolated APPDATA). Bump SHOWCASE_ID in showcase_world_definition.gd first if the change is deliberate, then run showcase_snapshot_capture.ps1.
#>
param(
  [string]$GodotExe = "$env:USERPROFILE\Downloads\Godot_v4.8-dev7_mono_win64\Godot_v4.8-dev7_mono_win64\Godot_v4.8-dev7_mono_win64_console.exe",
  [int]$TimeoutSeconds = 600
)
$ErrorActionPreference = "Stop"
$project = Split-Path -Parent $PSScriptRoot
# A fresh, isolated profile: no previewed terrain recipe and no real saves can leak into the result.
$isoRoot = Join-Path $project ".godot\showcase_tools\appdata_generate_showcase_world"
if (Test-Path $isoRoot) { Remove-Item -Recurse -Force $isoRoot }
New-Item -ItemType Directory -Force $isoRoot | Out-Null
$env:APPDATA = $isoRoot
$log = Join-Path $project ".godot\showcase_tools\generate_showcase_world.log"
$arguments = @("--path", $project, "--headless", "--script", "res://tools/generate_showcase_world.gd")
$process = Start-Process $GodotExe -ArgumentList $arguments -RedirectStandardOutput $log -RedirectStandardError "$log.err" -PassThru -NoNewWindow
if (-not $process.WaitForExit($TimeoutSeconds * 1000)) { Stop-Process -Id $process.Id -Force; "timed out after $TimeoutSeconds s" | Add-Content $log }
Select-String -Path $log -Pattern "SHOWCASE_|^FAIL|^ERROR:" | ForEach-Object { $_.Line }
exit $process.ExitCode
