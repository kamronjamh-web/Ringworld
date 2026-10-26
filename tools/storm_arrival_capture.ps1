param(
  [string]$Label = "run",
  [string]$ScriptArgs = "",
  [int]$TimeoutMin = 35,
  [string]$Lock = "",          # GPU lock file shared with parallel agents (created atomically, deleted afterwards)
  [string]$Profile = "storm_s3"
)
# Runs tools/storm_arrival_capture.gd (windowed) in the fixed isolated profile .godot\appdata_<Profile>, output in .godot\captures\storm_arrival\<Label>.
# The profile is created once (shader and Vulkan caches copied from the real profile) and reused afterwards.
#   .\tools\storm_arrival_capture.ps1 -Label mist -ScriptArgs "--mode=mist" -Lock <gpu.lock>
#   .\tools\storm_arrival_capture.ps1 -Label rainbow -ScriptArgs "--mode=rainbow --elev=16" -Lock <gpu.lock>
$ErrorActionPreference = "Stop"
$project = Split-Path $PSScriptRoot -Parent
$exe = Join-Path $project "artifacts\engine-upgrade\Godot_v4.8-dev7_mono_win64\Godot_v4.8-dev7_mono_win64_console.exe"
$savedAppData = $env:APPDATA
$realProfile = Join-Path $env:APPDATA "Godot\app_userdata\RingworldTechDemo"
$isoRoot = Join-Path $project ".godot\appdata_$Profile"
$isoProfile = Join-Path $isoRoot "Godot\app_userdata\RingworldTechDemo"
$outDir = Join-Path $project ".godot\captures\storm_arrival\$Label"
& (Join-Path $project "tools\prune_test_output.ps1") -Apply -Quiet -Exclude "appdata_$Profile" | Out-Host
if ($Lock -ne "") {
  while ($true) { try { New-Item -ItemType File $Lock -ErrorAction Stop | Out-Null; break } catch { Start-Sleep -Seconds 15 } }
  "storm_arrival_capture $Label $(Get-Date -Format s)" | Set-Content $Lock
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
  $argList = @("--path", $project, "--resolution", "1280x720", "--script", "res://tools/storm_arrival_capture.gd", "--", "--out=$outAbs")
  $argList += ($ScriptArgs -split " " | Where-Object { $_ })
  $env:APPDATA = $isoRoot
  $p = Start-Process -PassThru -NoNewWindow -FilePath $exe -ArgumentList $argList -RedirectStandardOutput $log -RedirectStandardError $errLog
  $env:APPDATA = $savedAppData
  $deadline = (Get-Date).AddMinutes($TimeoutMin)
  while (-not $p.HasExited -and (Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 5
    if (Select-String -Path $log -Pattern "STORM_ARRIVAL_CAPTURE=PASS" -Quiet) { Start-Sleep -Seconds 8; break }
    if (Select-String -Path $errLog -Pattern "STORM_ARRIVAL_CAPTURE=FAIL" -Quiet) { Start-Sleep -Seconds 3; break }
  }
  if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force }   # only the process started here
  "done: $outDir"
} finally {
  $env:APPDATA = $savedAppData
  if ($Lock -ne "") { Remove-Item $Lock -Force -ErrorAction SilentlyContinue }
}
