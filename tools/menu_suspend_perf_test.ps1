<#
.SYNOPSIS
  Runs tools/menu_suspend_perf_test.gd (stationary 16 km frame-time sampling) against an ISOLATED APPDATA holding COPIES of two saved worlds (never touches real saves).
.EXAMPLE
  .\tools\menu_suspend_perf_test.ps1 -Label c5before     # windowed GPU
  .\tools\menu_suspend_perf_test.ps1 -Label c5after
.NOTES
  The isolated profile is .godot\phase_c_live\appdata_<Label>. It receives slot_<Primary> and slot_<Secondary> from the real profile
  plus the gameplay_*.tres preferences; the test refuses to run unless user:// resolves into such a profile. The primary slot must be the
  most recently played one (it is the slot the menu warm-loads). Output: .godot\phase_c_live\<Label>\console.log and results.json.
#>
param(
  [string]$Label = "c5perf",
  [int]$Primary = 1,
  [int]$Secondary = 2,
  [switch]$Headless,
  [int]$Seconds = 30,
  [int]$Settle = 25,
  [int]$ExitGraceSeconds = 60,
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
# The previous run's vulkan cache has paths too long for Remove-Item; rd handles them with the extended prefix.
if (Test-Path $isoRoot) { cmd /c "rd /s /q \\?\$isoRoot" }
if (Test-Path $isoRoot) { Remove-Item -Recurse -Force $isoRoot }
New-Item -ItemType Directory -Force (Join-Path $isoProfile "worlds") | Out-Null
# The test expects the primary world at slot 1 and the secondary at slot 2 of the isolated profile.
Copy-Item -Recurse (Join-Path $realProfile "worlds\slot_$Primary") (Join-Path $isoProfile "worlds\slot_1")
Copy-Item -Recurse (Join-Path $realProfile "worlds\slot_$Secondary") (Join-Path $isoProfile "worlds\slot_2")
foreach ($pair in @(@(1, $Primary), @(2, $Secondary))) {
  $meta = Join-Path $isoProfile "worlds\slot_$($pair[0])\world.json"
  $text = Get-Content -Raw $meta
  $text = $text -replace '"slot":\s*\d+', ('"slot": ' + $pair[0])
  Set-Content -NoNewline -Path $meta -Value $text
}
# Start from the starfield cover: drop any menu snapshot the real slots carry.
Get-ChildItem (Join-Path $isoProfile "worlds") -Recurse -Filter "menu_snapshot.*" | Remove-Item -Force
Get-ChildItem $realProfile -Filter "*.tres" | Copy-Item -Destination $isoProfile
$env:APPDATA = $isoRoot
$arguments = @("--path", $project)
if ($Headless) { $arguments += "--headless" }
$arguments += @("--script", "res://tools/menu_suspend_perf_test.gd", "--", "--label=$Label", "--seconds=$Seconds", "--settle=$Settle")
$stdout = Join-Path $outDir "console.log"
$watch = [Diagnostics.Stopwatch]::StartNew()
# Safety net: Godot 4.8-dev7's WorkerThreadPool can hang at exit when low-priority tasks are still queued. If the process has not exited
# within $ExitGraceSeconds of the verdict, kill our own process.
$process = Start-Process $GodotExe -ArgumentList $arguments -RedirectStandardOutput $stdout -RedirectStandardError "$stdout.err" -PassThru -NoNewWindow
$verdictAt = $null
while (-not $process.HasExited) {
  Start-Sleep -Seconds 2
  if (-not $verdictAt -and (Select-String -Path $stdout -Pattern "MENU_SUSPEND_PERF=DONE" -Quiet)) { $verdictAt = Get-Date }
  if ($verdictAt -and ((Get-Date) - $verdictAt).TotalSeconds -gt $ExitGraceSeconds) {
    "process did not exit within $ExitGraceSeconds s of the verdict; killing it (exit hang safety net)" | Add-Content $stdout
    Stop-Process -Id $process.Id -Force
    break
  }
}
$code = if ($verdictAt -and (Select-String -Path $stdout -Pattern "MENU_SUSPEND_PERF=DONE" -Quiet)) { 0 } elseif ($verdictAt) { 1 } else { $process.ExitCode }
"process wall seconds: {0:N1}  exit code: {1}" -f $watch.Elapsed.TotalSeconds, $code | Add-Content $stdout
Copy-Item (Join-Path $base "c5\$Label\*") $outDir -ErrorAction SilentlyContinue
Select-String -Path $stdout -Pattern "Using Device|MENU_|FAIL|ERROR|exit code" | ForEach-Object { $_.Line.Substring(0, [Math]::Min(600, $_.Line.Length)) }
exit $code
