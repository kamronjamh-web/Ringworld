param(
  [string]$Label = "run",
  [string]$ScriptArgs = "",
  [int]$TimeoutMin = 40,
  [string]$Lock = "",          # optional path of the shared FIFO queue script gpu_queue.ps1 (acquire / release with label storm_s2)
  [string]$Profile = "storm_s2",
  [switch]$Movie               # record the run with Godot Movie Maker (30 fps PNG sequence next to the screenshots: <out>\movie\frame00000000.png ...)
)
# Runs tools/wet_world_capture.gd (windowed) in the fixed isolated profile .godot\appdata_<Profile>, output in .godot\captures\wet_world\<Label>.
# The profile is created once (shader and Vulkan caches copied from the real profile) and reused afterwards.
#   .\tools\wet_world_capture.ps1 -Label timeline -ScriptArgs "--mode=timeline"
#   .\tools\wet_world_capture.ps1 -Label movie -ScriptArgs "--mode=movie" -Movie
$ErrorActionPreference = "Stop"
$project = Split-Path $PSScriptRoot -Parent
$exe = Join-Path $project "artifacts\engine-upgrade\Godot_v4.8-dev7_mono_win64\Godot_v4.8-dev7_mono_win64_console.exe"
$savedAppData = $env:APPDATA
$realProfile = Join-Path $env:APPDATA "Godot\app_userdata\RingworldTechDemo"
$isoRoot = Join-Path $project ".godot\appdata_$Profile"
$isoProfile = Join-Path $isoRoot "Godot\app_userdata\RingworldTechDemo"
$outDir = Join-Path $project ".godot\captures\wet_world\$Label"
& (Join-Path $project "tools\prune_test_output.ps1") -Apply -Quiet -Exclude "appdata_$Profile" | Out-Host
# $Lock is the FIFO queue script (gpu_queue.ps1); the label storm_s2 is the queue ticket and the lock owner. Waits 60 s after a previous release (stamp file next to the queue script).
$stamp = ""
if ($Lock -ne "") {
  $stamp = Join-Path (Split-Path $Lock -Parent) "s2\last_release.txt"
  if (Test-Path $stamp) { $age = ((Get-Date) - (Get-Item $stamp).LastWriteTime).TotalSeconds; if ($age -lt 60) { Start-Sleep -Seconds ([int](61 - $age)) } }
  & $Lock -Acquire -Label storm_s2
}
try {
  if (-not (Test-Path $isoProfile)) {
    New-Item -ItemType Directory -Force $isoProfile | Out-Null
    foreach ($dir in "shader_cache", "vulkan") {
      if (Test-Path (Join-Path $realProfile $dir)) { Copy-Item -Recurse (Join-Path $realProfile $dir) (Join-Path $isoProfile $dir) }
    }
  }
  New-Item -ItemType Directory -Force $outDir | Out-Null
  $log = Join-Path $outDir "console.log"; $errLog = Join-Path $outDir "stderr.log"
  $outAbs = $outDir.Replace([string][char]92, "/")
  $argList = @("--path", $project, "--resolution", "1280x720")
  if ($Movie) {
    $movieDir = Join-Path $outDir "movie"
    New-Item -ItemType Directory -Force $movieDir | Out-Null
    $argList += @("--write-movie", (Join-Path $movieDir "frame.png"), "--fixed-fps", "30")
  }
  $argList += @("--script", "res://tools/wet_world_capture.gd", "--", "--out=$outAbs")
  $argList += ($ScriptArgs -split " " | Where-Object { $_ })
  $env:APPDATA = $isoRoot
  $p = Start-Process -PassThru -NoNewWindow -FilePath $exe -ArgumentList $argList -RedirectStandardOutput $log -RedirectStandardError $errLog
  $env:APPDATA = $savedAppData
  $deadline = (Get-Date).AddMinutes($TimeoutMin)
  while (-not $p.HasExited -and (Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 5
    if (Select-String -Path $log -Pattern "WET_WORLD_CAPTURE=PASS" -Quiet) { Start-Sleep -Seconds 8; break }
    if (Select-String -Path $errLog -Pattern "WET_WORLD_CAPTURE=FAIL" -Quiet) { Start-Sleep -Seconds 3; break }
  }
  if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force }   # only the process started here
  "done: $outDir"
} finally {
  $env:APPDATA = $savedAppData
  if ($Lock -ne "") { & $Lock -Release -Label storm_s2; Set-Content $stamp (Get-Date -Format o) }
}
