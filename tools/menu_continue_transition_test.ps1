<#
.SYNOPSIS
  Runs tools/menu_continue_transition_test.gd against an ISOLATED APPDATA holding COPIES of two saved worlds (never touches real saves).
.EXAMPLE
  .	oolsmenu_continue_transition_test.ps1 -Label c6m -Case ground -Movie   # records .godotpturesvm_groundmovie.avi
  .\tools\menu_continue_transition_test.ps1 -Label c6                 # windowed GPU: the main, ground, warming and timeout cases in turn
  .\tools\menu_continue_transition_test.ps1 -Label c6 -Case main
.NOTES
  Each case gets its own isolated profile, .godot\phase_c_live\appdata_<Label>_<case>, with slot_<Primary> and slot_<Secondary> copied from
  the real profile plus the gameplay_*.tres preferences; the test refuses to run unless user:// resolves into such a profile. The primary
  slot must be the most recently played one (it is the slot the menu warm-loads). Output: .godot\phase_c_live\<Label>\console_<case>.log
  and results_<case>.json.
#>
param(
  [string]$Label = "c6",
  [ValidateSet("all","main","ground","underground","warming","timeout")][string]$Case = "all",
  [int]$Primary = 1,
  [int]$Secondary = 2,
  [switch]$Headless,
  [switch]$Movie,
  [int]$ExitGraceSeconds = 60,
  [string]$GodotExe = "$env:USERPROFILE\Downloads\Godot_v4.8-dev7_mono_win64\Godot_v4.8-dev7_mono_win64\Godot_v4.8-dev7_mono_win64_console.exe",
  [string]$ProjectName = "RingworldTechDemo"
)
$ErrorActionPreference = "Stop"
$project = Split-Path -Parent $PSScriptRoot
$realProfile = Join-Path $env:APPDATA "Godot\app_userdata\$ProjectName"
$base = Join-Path $project ".godot\phase_c_live"
$outDir = Join-Path $base $Label
New-Item -ItemType Directory -Force $outDir | Out-Null
function Invoke-Godot([string]$caseArg, [string]$logName, [string]$extraArg = "") {
  $arguments = @("--path", $project)
  if ($Headless) { $arguments += "--headless" }
  if ($Movie -and $caseArg -ne "prep") {
    # Godot Movie Maker: fixed 30 fps MJPEG, small window. Review with tools/media/ffmpeg_tools.ps1 (flicker, sheet).
    $captureDir = Join-Path $project (Join-Path ".godot" (Join-Path "captures" "${Label}_$caseArg"))
    New-Item -ItemType Directory -Force $captureDir | Out-Null
    $arguments += @("--write-movie", (Join-Path $captureDir "movie.avi"), "--fixed-fps", "30", "--resolution", "960x540")
  }
  $arguments += @("--script", "res://tools/menu_continue_transition_test.gd", "--", "--case=$caseArg")
  if ($extraArg) { $arguments += $extraArg }
  $stdout = Join-Path $outDir "console_$logName.log"
  $watch = [Diagnostics.Stopwatch]::StartNew()
  # Safety net: Godot 4.8-dev7's WorkerThreadPool can hang at exit when low-priority tasks are still queued. If the process has not exited
  # within $ExitGraceSeconds of the verdict, kill our own process.
  $process = Start-Process $GodotExe -ArgumentList $arguments -RedirectStandardOutput $stdout -RedirectStandardError "$stdout.err" -PassThru -NoNewWindow
  $verdictAt = $null
  while (-not $process.HasExited) {
    Start-Sleep -Seconds 2
    if (-not $verdictAt -and (Select-String -Path $stdout -Pattern "MENU_CONTINUE_TRANSITION=(PASS|FAIL)" -Quiet)) { $verdictAt = Get-Date }
    if ($verdictAt -and ((Get-Date) - $verdictAt).TotalSeconds -gt $ExitGraceSeconds) {
      "process did not exit within $ExitGraceSeconds s of the verdict; killing it (exit hang safety net)" | Add-Content $stdout
      Stop-Process -Id $process.Id -Force
      break
    }
  }
  $code = if ($verdictAt -and (Select-String -Path $stdout -Pattern "MENU_CONTINUE_TRANSITION=PASS" -Quiet)) { 0 } elseif ($verdictAt) { 1 } else { $process.ExitCode }
  "process wall seconds: {0:N1}  exit code: {1}" -f $watch.Elapsed.TotalSeconds, $code | Add-Content $stdout
  Copy-Item (Join-Path $base "c6\results_$caseArg.json") (Join-Path $outDir "results_$caseArg.json") -ErrorAction SilentlyContinue
  Select-String -Path $stdout -Pattern "Using Device|MENU_|CONTINUE_|PREP|FAIL|ERROR|exit code" | ForEach-Object { $_.Line.Substring(0, [Math]::Min(600, $_.Line.Length)) } | Out-Host
  return [int]$code
}

$cases = if ($Case -eq "all") { @("main", "ground", "underground", "warming", "timeout") } else { @($Case) }
$exitCode = 0
foreach ($caseName in $cases) {
  $isoRoot = Join-Path $base "appdata_${Label}_$caseName"
  $isoProfile = Join-Path $isoRoot "Godot\app_userdata\$ProjectName"
  # The previous run's vulkan cache has paths too long for Remove-Item; rd handles them with the extended prefix.
  if (Test-Path $isoRoot) { cmd /c "rd /s /q \\?\$isoRoot" }
  if (Test-Path $isoRoot) { Remove-Item -Recurse -Force $isoRoot }
  New-Item -ItemType Directory -Force (Join-Path $isoProfile "worlds") | Out-Null
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
  if ($caseName -in @("ground", "underground", "timeout")) {
    # Rewrite the isolated copy's saved player to stand on the ground, in a first process on the same profile.
    $code = Invoke-Godot "prep" "prep_$caseName" $(if ($caseName -eq "underground") { "--prep-depth=40" } else { "" })
    if ($code -ne 0) { $exitCode = $code; continue }
  }
  $code = Invoke-Godot $caseName $caseName
  if ($code -ne 0) { $exitCode = $code }
}
exit $exitCode
