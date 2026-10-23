<#
.SYNOPSIS
  Regenerates the bundled hi-res showcase picture (resources/menu/showcase_snapshot.png and .json). Needs a GPU: opens a window, warm-loads the showcase in an isolated profile, captures the world at cruise. Run whenever showcase_id changes.
#>
param(
  [string]$GodotExe = "$env:USERPROFILE\Downloads\Godot_v4.8-dev7_mono_win64\Godot_v4.8-dev7_mono_win64\Godot_v4.8-dev7_mono_win64_console.exe",
  [int]$TimeoutSeconds = 600,
  [string]$Resolution = "2560x1440"
)
$ErrorActionPreference = "Stop"
$project = Split-Path -Parent $PSScriptRoot
# A fresh, isolated profile: no previewed terrain recipe and no real saves can leak into the result.
$isoRoot = Join-Path $project ".godot\showcase_tools\appdata_showcase_snapshot_capture"
if (Test-Path $isoRoot) { Remove-Item -Recurse -Force $isoRoot }
New-Item -ItemType Directory -Force $isoRoot | Out-Null
$env:APPDATA = $isoRoot
$log = Join-Path $project ".godot\showcase_tools\showcase_snapshot_capture.log"
$arguments = @("--path", $project, "--resolution", "$Resolution", "--borderless", "--position", "0,0", "--script", "res://tools/showcase_snapshot_capture.gd")
$process = Start-Process $GodotExe -ArgumentList $arguments -RedirectStandardOutput $log -RedirectStandardError "$log.err" -PassThru -NoNewWindow
if (-not $process.WaitForExit($TimeoutSeconds * 1000)) { Stop-Process -Id $process.Id -Force; "timed out after $TimeoutSeconds s" | Add-Content $log }
Select-String -Path $log -Pattern "SHOWCASE_|^FAIL|^ERROR:" | ForEach-Object { $_.Line }
exit $process.ExitCode
