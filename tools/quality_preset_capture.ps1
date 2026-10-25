<#
.SYNOPSIS
  Runs tools/quality_preset_capture.gd (quality-preset perf / hitch capture, docs/quality-presets-plan.md WO-0) against an ISOLATED
  profile. Never touches real saves: APPDATA is redirected to .godot\quality_presets\appdata_<Label>. Windowed GPU run.
.EXAMPLE
  # Very High at 1080p, default poses, copying the user's shader/vulkan caches
  .\tools\quality_preset_capture.ps1 -Label qp -OutLabel vh_1080 -Preset 3 -Res 1920x1080
  # Same, against an optimized (Optimize=true) C# assembly
  .\tools\quality_preset_capture.ps1 -Label qp -OutLabel vh_1080_rel -Preset 3 -Res 1920x1080 -CSharpConfig Release
  # A1000 gate run: two poses, one ablation label, power-validated windows, GPU lock held
  .\tools\quality_preset_capture.ps1 -Label qp -OutLabel vh_a1000 -Preset 3 -Poses forest_noon,alt_1km -Labels sun_map_off -A1000 -GpuLock .\gpu.lock
.NOTES
  -Preset 0 Low, 1 Medium, 2 High, 3 Very High, 4 Ultra (QualityPreset.UltraVideo), 5 Cinematic5090
  (RingTerrainLodCoordinator.QualityPreset order).
  -Cache none (empty profile) | copy (shader_cache + vulkan from the real profile; the harness builds its own fixture world) | reuse (keep an existing
  profile, build it as copy when missing).
  Output: .godot\quality_presets\<OutLabel>\ (report.json, power_log.csv, stall_*.csv, console.log, stderr.log).
  -CSharpConfig Debug (default) uses whatever .godot\mono\temp\bin\Debug\<Project>.dll is already built; no build, no swap.
  -CSharpConfig Release: the Godot mono EDITOR binary always loads bin\Debug\<Project>.dll, so "Release" here means the Debug configuration
  built with -p:Optimize=true into bin\OptRelease\ and temporarily swapped into the Debug slot (dll + pdb). The previous slot is saved to
  bin\DebugSaved\ and restored afterwards (only if nobody rebuilt Debug during the run). The engine is still the editor build, NOT an
  export template, so engine-side cost is unchanged. The build is skipped with -SkipBuild (reuses an existing bin\OptRelease build).
  The configuration is recorded in the report via --release-note=csharp_build=<cfg>;optimize=<bool>.
  Exit code 0 only when console.log contains QP_BASELINE=PASS (Godot 4.8-dev7 can hang or exit nonzero on quit).
  Analyse with: python -I tools\quality_preset_report.py <out dir> [--deltas]; python -I tools\quality_preset_probe_report.py <out dir> [context prefix].
#>
param(
  [string]$Label = "qp",
  [string]$OutLabel = "",
  [ValidateRange(0, 5)][int]$Preset = 3,
  [string]$Res = "1920x1080",
  [switch]$Maximized,
  [string[]]$Poses = @(),
  [string[]]$Labels = @(),
  [string[]]$FirstPoseLabels = @(),
  [string[]]$Flights = @(),
  [double]$Seconds = 0,
  [double]$Warmup = -1,
  [double]$BootSeconds = -1,
  [switch]$A1000,
  [switch]$Snap,
  [ValidateSet("none","copy","reuse")][string]$Cache = "copy",
  [string]$ScriptArgs = "",
  [string]$GpuLock = "",
  [string]$GodotExe = "",
  [int]$TimeoutMin = 120,
  [string]$ProjectName = "RingworldTechDemo",
  [ValidateSet("Debug","Release")][string]$CSharpConfig = "Debug",
  [switch]$SkipBuild,
  # Drive an exported build (-GodotExe <export>\<name>.exe from export preset "Windows Desktop Release (perf harness)"): no --path, no C# swap.
  [switch]$ExportedBuild
)
$ErrorActionPreference = "Stop"
$project = Split-Path -Parent $PSScriptRoot
if (-not $GodotExe) { $GodotExe = Join-Path $project "artifacts\engine-upgrade\Godot_v4.8-dev7_mono_win64\Godot_v4.8-dev7_mono_win64_console.exe" }
if (-not $OutLabel) { $OutLabel = $Label }
$savedAppData = $env:APPDATA
$realProfile = Join-Path $env:APPDATA "Godot\app_userdata\$ProjectName"
$base = Join-Path $project ".godot\quality_presets"
$isoRoot = Join-Path $base "appdata_$Label"
$isoProfile = Join-Path $isoRoot "Godot\app_userdata\$ProjectName"
$outDir = Join-Path $base $OutLabel
$terrainDll = Join-Path $project "addons\terrain_3d\bin\~libterrain.ringworld.windows.debug.x86_64.dll"
# Bound .godot test output (stale isolated profiles and old captures); keeps the profile this run may reuse.
& (Join-Path $PSScriptRoot "prune_test_output.ps1") -Apply -Quiet -Exclude (Split-Path -Leaf $isoRoot) | Out-Host

$monoTemp = Join-Path $project ".godot\mono\temp"
$slotDir = Join-Path $monoTemp "bin\Debug"
$savedDir = Join-Path $monoTemp "bin\DebugSaved"
$optDir = Join-Path $monoTemp "bin\OptRelease"
$slotDll = Join-Path $slotDir "$ProjectName.dll"
$slotPdb = Join-Path $slotDir "$ProjectName.pdb"
$optDll = Join-Path $optDir "$ProjectName.dll"
$optPdb = Join-Path $optDir "$ProjectName.pdb"
$swapped = $false
$optHash = ""

$lockHeld = $false
if ($GpuLock) {
  New-Item -ItemType File $GpuLock -ErrorAction Stop | Out-Null
  $lockHeld = $true
}
try {
  if ($Cache -ne "reuse" -or -not (Test-Path $isoProfile)) {
    if (Test-Path $isoRoot) { Remove-Item -Recurse -Force $isoRoot }
    New-Item -ItemType Directory -Force $isoProfile | Out-Null
    if ($Cache -eq "copy") {
      foreach ($dir in "shader_cache","vulkan") {
        if (Test-Path (Join-Path $realProfile $dir)) { Copy-Item -Recurse (Join-Path $realProfile $dir) (Join-Path $isoProfile $dir) }
      }
    }
  }
  New-Item -ItemType Directory -Force $outDir | Out-Null
  $log = Join-Path $outDir "console.log"
  $errLog = Join-Path $outDir "stderr.log"

  # C# build configuration, recorded in the report so Debug and Release captures are never confused.
  $buildNote = "csharp_build=$CSharpConfig;optimize=$(if ($CSharpConfig -eq 'Release') { 'true' } else { 'false' })"

  if ($ExportedBuild) {
    if (-not $PSBoundParameters.ContainsKey("GodotExe")) { throw "-ExportedBuild needs -GodotExe <exported exe>." }
    $buildNote = "exported_build=" + (Split-Path -Leaf $GodotExe)
  }
  if ($CSharpConfig -eq "Release" -and -not $ExportedBuild) {
    if (-not $SkipBuild) {
      # Pinned SDK environment, same block as tools/use_godot_48.ps1.
      $dotnetRoot = Join-Path $project "artifacts\engine-upgrade\dotnet"
      if (-not (Test-Path (Join-Path $dotnetRoot "dotnet.exe"))) { throw "Pinned .NET SDK missing at $dotnetRoot (see tools/use_godot_48.ps1)." }
      $envNames = @("DOTNET_ROOT","DOTNET_ROOT_X64","PATH","DOTNET_CLI_HOME","NUGET_PACKAGES","NUGET_HTTP_CACHE_PATH","DOTNET_CLI_TELEMETRY_OPTOUT","DOTNET_GENERATE_ASPNET_CERTIFICATE","DOTNET_ADD_GLOBAL_TOOLS_TO_PATH")
      $oldEnv = @{}
      foreach ($name in $envNames) { $oldEnv[$name] = [Environment]::GetEnvironmentVariable($name, "Process") }
      try {
        $env:DOTNET_ROOT = $dotnetRoot
        $env:DOTNET_ROOT_X64 = $dotnetRoot
        $env:PATH = "$dotnetRoot;$($oldEnv['PATH'])"
        $env:DOTNET_CLI_HOME = Join-Path $project "artifacts\engine-upgrade\dotnet-cli"
        $env:NUGET_PACKAGES = Join-Path $project "artifacts\engine-upgrade\nuget-packages"
        $env:NUGET_HTTP_CACHE_PATH = Join-Path $project "artifacts\engine-upgrade\nuget-http-cache"
        $env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
        $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = "false"
        $env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = "false"
        "Building optimized C# assembly into $optDir"
        # Godot.NET.Sdk sets OutputPath unconditionally in Sdk.props; global (command-line) properties override it.
        & (Join-Path $dotnetRoot "dotnet.exe") build (Join-Path $project "$ProjectName.csproj") -c Debug -p:Optimize=true -p:NuGetAudit=false -v:minimal "-p:OutputPath=$optDir\" "-p:IntermediateOutputPath=$(Join-Path $monoTemp 'obj\OptRelease')\"
        $buildExit = $LASTEXITCODE
      } finally {
        foreach ($name in $envNames) { [Environment]::SetEnvironmentVariable($name, $oldEnv[$name], "Process") }
      }
      if ($buildExit -ne 0) { throw "Optimized C# build failed (exit $buildExit)." }
    }
    if (-not (Test-Path $optDll)) { throw "Missing $optDll (run without -SkipBuild first)." }

    # Swap the optimized assembly into the Debug slot the editor binary loads; the finally block restores it.
    New-Item -ItemType Directory -Force $savedDir | Out-Null
    New-Item -ItemType Directory -Force $slotDir | Out-Null
    if (Test-Path $slotDll) { Copy-Item -Force $slotDll (Join-Path $savedDir "$ProjectName.dll") }
    if (Test-Path $slotPdb) { Copy-Item -Force $slotPdb (Join-Path $savedDir "$ProjectName.pdb") }
    $optHash = (Get-FileHash -Algorithm SHA256 $optDll).Hash
    $swapped = $true
    Copy-Item -Force $optDll $slotDll
    if (Test-Path $optPdb) { Copy-Item -Force $optPdb $slotPdb }
    "Swapped optimized assembly into $slotDll (sha256 $optHash)"
  }

  $argList = if ($ExportedBuild) { @() } else { @("--path", $project) }
  if (-not $Maximized) { $argList += @("--resolution", $Res) }
  # Official export templates disable --script and scene-path overrides. The perf-harness export preset carries the
  # "qp_perf_harness" feature, whose main_scene override (project.godot) is tools/quality_preset_capture_entry.tscn.
  if (-not $ExportedBuild) { $argList += @("--script", "res://tools/quality_preset_capture.gd") }
  $argList += @("--", "--out=$outDir", "--preset=$Preset", "--release-note=$($buildNote -replace ' ','_')")
  if ($Maximized) { $argList += "--maximized" } else { $argList += "--res=$Res" }
  # Comma lists: PowerShell binds an unquoted a,b as an array, so accept arrays and join them (a quoted "a,b" also works).
  if ($Poses) { $argList += "--poses=$($Poses -join ',')" }
  if ($Labels) { $argList += "--labels=$($Labels -join ',')" }
  if ($FirstPoseLabels) { $argList += "--first-pose-labels=$($FirstPoseLabels -join ',')" }
  if ($Flights) { $argList += "--flights=$($Flights -join ',')" }
  if ($PSBoundParameters.ContainsKey("Seconds")) { $argList += "--seconds=$Seconds" }
  if ($PSBoundParameters.ContainsKey("Warmup")) { $argList += "--warmup=$Warmup" }
  if ($PSBoundParameters.ContainsKey("BootSeconds")) { $argList += "--boot-seconds=$BootSeconds" }
  if ($A1000) { $argList += "--a1000" }
  if ($Snap) { $argList += "--snap" }
  $argList += ($ScriptArgs -split " " | Where-Object { $_ })

  $env:APPDATA = $isoRoot
  $watch = [Diagnostics.Stopwatch]::StartNew()
  $p = Start-Process -PassThru -NoNewWindow -FilePath $GodotExe -ArgumentList $argList -RedirectStandardOutput $log -RedirectStandardError $errLog
  $env:APPDATA = $savedAppData
  $done = $p.WaitForExit($TimeoutMin * 60 * 1000)
  if (-not $done) { Stop-Process -Id $p.Id -Force; "TIMEOUT after $TimeoutMin min" | Add-Content $log }
  "wall_s={0:N0} exit={1}" -f $watch.Elapsed.TotalSeconds, $(if ($done) { $p.ExitCode } else { -1 })
  Start-Sleep -Seconds 2
  if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force }
} finally {
  $env:APPDATA = $savedAppData
  if ($swapped) {
    # Godot has exited or been killed above (or we threw before launch); safe to put the Debug assembly back.
    try {
      $slotHash = if (Test-Path $slotDll) { (Get-FileHash -Algorithm SHA256 $slotDll).Hash } else { "" }
      if ($slotHash -eq $optHash) {
        $savedDll = Join-Path $savedDir "$ProjectName.dll"
        $savedPdb = Join-Path $savedDir "$ProjectName.pdb"
        if (Test-Path $savedDll) { Copy-Item -Force $savedDll $slotDll }
        if (Test-Path $savedPdb) { Copy-Item -Force $savedPdb $slotPdb }
        "Restored Debug assembly into the Debug slot from $savedDir"
      } else {
        "NOTE: Debug slot changed during the run; not restoring"
      }
    } catch { "WARNING: Debug slot restore failed: $($_.Exception.Message)" }
  }
  if ($lockHeld) { Remove-Item $GpuLock -ErrorAction SilentlyContinue }
}

Select-String -Path $log -Pattern "Using Device|QP_BASELINE|QP_FIRST|QP_POSE|QP_SUMMARY|QP_FLIGHT" | ForEach-Object { $_.Line.Substring(0, [Math]::Min(700, $_.Line.Length)) }
Select-String -Path $errLog -Pattern "QP_BASELINE=FAIL|SCRIPT ERROR|Parse Error" | Select-Object -First 10 | ForEach-Object { $_.Line.Substring(0, [Math]::Min(400, $_.Line.Length)) }

# The engine can delete the Terrain3D debug DLL copy on a failed load; restore it from HEAD as raw bytes.
if (-not (Test-Path $terrainDll)) {
  & cmd.exe /c "git -C `"$project`" show HEAD:addons/terrain_3d/bin/~libterrain.ringworld.windows.debug.x86_64.dll > `"$terrainDll`""
  "NOTE: restored missing $terrainDll from git HEAD"
}

$passed = (Test-Path $log) -and [bool](Select-String -Path $log -Pattern "QP_BASELINE=PASS" -Quiet)
exit $(if ($passed) { 0 } else { 1 })
