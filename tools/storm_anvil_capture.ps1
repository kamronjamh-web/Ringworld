<#
.SYNOPSIS
  Runs tools/storm_anvil_capture.gd (storm anvil shape captures, docs/storm-revamp-s4-design.md WO-A) against an ISOLATED profile.
  Windowed GPU run; takes the shared GPU lock (-GpuLock) for the duration.
.EXAMPLE
  .\tools\storm_anvil_capture.ps1 -OutLabel a1_old -Shots side30 -GpuLock <scratchpad>\gpu.lock
.NOTES
  Output: .godot\captures\storm_anvils\<OutLabel>\*.png, console.log, stderr.log. Profile: .godot\storm_anvils\appdata_<Label> (cache reuse).
  -Env sets extra environment variables for the run, e.g. -Env @{RING_STORM_SHAPE_LEGACY="1"}.
#>
param(
  [string]$Label = "storm_anvils",
  [Parameter(Mandatory)][string]$OutLabel,
  [string]$Shots = "side30",
  [string]$Res = "1600x900",
  [hashtable]$Env = @{},
  [string]$ScriptArgs = "",
  [string]$GpuLock = "",
  [int]$TimeoutMin = 20,
  [string]$ProjectName = "RingworldTechDemo"
)
$ErrorActionPreference = "Stop"
$project = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $project "artifacts\engine-upgrade\Godot_v4.8-dev7_mono_win64\Godot_v4.8-dev7_mono_win64_console.exe"
$savedAppData = $env:APPDATA
$realProfile = Join-Path $env:APPDATA "Godot\app_userdata\$ProjectName"
$base = Join-Path $project ".godot\storm_anvils"
$isoRoot = Join-Path $base "appdata_$Label"
$isoProfile = Join-Path $isoRoot "Godot\app_userdata\$ProjectName"
$outDir = Join-Path $project ".godot\captures\storm_anvils\$OutLabel"
& (Join-Path $PSScriptRoot "prune_test_output.ps1") -Apply -Quiet -Exclude (Split-Path -Leaf $isoRoot) | Out-Host
$lockHeld = $false
if ($GpuLock) {
  while ($true) {
    try { New-Item -ItemType File $GpuLock -ErrorAction Stop | Out-Null; $lockHeld = $true; break }
    catch { Start-Sleep -Seconds 20 }
  }
  "storm_anvils $OutLabel $(Get-Date -Format s)" | Set-Content $GpuLock
}
$savedEnv = @{}
try {
  if (-not (Test-Path $isoProfile)) {
    New-Item -ItemType Directory -Force $isoProfile | Out-Null
    foreach ($dir in "shader_cache","vulkan") {
      if (Test-Path (Join-Path $realProfile $dir)) { Copy-Item -Recurse (Join-Path $realProfile $dir) (Join-Path $isoProfile $dir) }
    }
  }
  New-Item -ItemType Directory -Force $outDir | Out-Null
  $log = Join-Path $outDir "console.log"; $errLog = Join-Path $outDir "stderr.log"
  $outRes = "res://.godot/captures/storm_anvils/$OutLabel"
  $argList = @("--path", $project, "--resolution", $Res, "--script", "res://tools/storm_anvil_capture.gd", "--", "--out=$outRes", "--shots=$Shots")
  $argList += ($ScriptArgs -split " " | Where-Object { $_ })
  foreach ($k in $Env.Keys) { $savedEnv[$k] = [Environment]::GetEnvironmentVariable($k, "Process"); [Environment]::SetEnvironmentVariable($k, [string]$Env[$k], "Process") }
  $env:APPDATA = $isoRoot
  $p = Start-Process -PassThru -NoNewWindow -FilePath $exe -ArgumentList $argList -RedirectStandardOutput $log -RedirectStandardError $errLog
  $env:APPDATA = $savedAppData
  $done = $p.WaitForExit($TimeoutMin * 60 * 1000)
  if (-not $done) { Stop-Process -Id $p.Id -Force; "TIMEOUT after $TimeoutMin min" | Add-Content $log }
  "exit=$(if ($done) { $p.ExitCode } else { -1 })"
  Start-Sleep -Seconds 2
  if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force }
} finally {
  $env:APPDATA = $savedAppData
  foreach ($k in $savedEnv.Keys) { [Environment]::SetEnvironmentVariable($k, $savedEnv[$k], "Process") }
  if ($lockHeld) { Remove-Item $GpuLock -ErrorAction SilentlyContinue }
}
Select-String -Path $log -Pattern "Using Device|STORM_ANVIL" | ForEach-Object { $_.Line.Substring(0, [Math]::Min(300, $_.Line.Length)) }
Select-String -Path $errLog -Pattern "STORM_ANVIL=FAIL|SCRIPT ERROR|Parse Error|SHADER ERROR" | Select-Object -First 10 | ForEach-Object { $_.Line.Substring(0, [Math]::Min(400, $_.Line.Length)) }
$terrainDll = Join-Path $project "addons\terrain_3d\bin\~libterrain.ringworld.windows.debug.x86_64.dll"
if (-not (Test-Path $terrainDll)) {
  & cmd.exe /c "git -C `"$project`" show HEAD:addons/terrain_3d/bin/~libterrain.ringworld.windows.debug.x86_64.dll > `"$terrainDll`""
  "NOTE: restored missing $terrainDll from git HEAD"
}
exit $(if ((Test-Path $log) -and (Select-String -Path $log -Pattern "STORM_ANVIL=PASS" -Quiet)) { 0 } else { 1 })
