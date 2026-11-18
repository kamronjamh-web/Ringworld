<#
.SYNOPSIS
  Runner of the tree visibility harnesses on the real world: an isolated copy of the player's slot 3, the real start menu, Continue.
.EXAMPLE
  .\tools\tree_visibility_run.ps1 -Test oracle                              # OC-0 oracle: depth readback, per-instance classification, copy oracle, vertex kill
  .\tools\tree_visibility_run.ps1 -Test oracle -ScriptArgs "--names=forest_interior_eye --yaws=0 --timing=0"
  .\tools\tree_visibility_run.ps1 -Test frustum_identity -Build              # OC-3 byte A/B at rest (needs TreeFrustumCulling; SKIPs until it exists)
  .\tools\tree_visibility_run.ps1 -Test cull_verify -Build                   # OC-3 magenta count in motion (needs TreeCullVerify; SKIPs until it exists)
.NOTES
  Tests: oracle -> tools/tree_visibility_oracle.gd (TREE_VISIBILITY_ORACLE), frustum_identity -> tools/tree_frustum_identity_gpu_test.gd (TREE_FRUSTUM_IDENTITY),
  cull_verify -> tools/tree_cull_verify_sweep.gd (TREE_CULL_VERIFY_SWEEP). Verdict lines are echoed from the Godot logs; the exit code is 0 for PASS and SKIP.
  Safety: never touches the real profile except to READ it. The profile copy lives in .godot\test_runs\appdata_occl-v (one reused profile; slot 3 and the top-level
  preference files are re-copied from the real profile before every run, because the game autosaves into the copy). The scripts refuse to run when user:// is not an
  isolated profile. Stops with USER_PLAYING (exit 9) when a Godot with --remote-debug runs or .godot\mono\temp\bin\DebugPlaySaved holds an assembly (the player's optimized
  play swapped one in).
  Build: the optimized C# assembly (bin\OptRelease, as tools/play.ps1) is swapped into the Debug slot for the run and restored afterwards. It is rebuilt with -Build, or when a
  .cs file is newer than the existing one; -SkipBuild reuses it as is. The SKIP check for the OC-3 tests reads the sources, before any copy or launch (not with --baseline-only, the determinism check of the identity test).
  GPU: takes the queue itself (tools/gpu_queue.ps1 -Wrap); do not wrap this in the queue. Output: .godot\test_runs\<Label>\tv_<Test>_<stamp>\ (console.log, report.json, ...).
  Do not run two copies at once (one shared profile).
#>
param(
  [ValidateSet("oracle", "frustum_identity", "cull_verify", "blob_probe", "cadence")][string]$Test = "oracle",
  [string]$Label = "occl-v",
  [string]$ScriptArgs = "",
  [string]$Resolution = "1600x900",
  [string]$EngineArgs = "",
  [switch]$Build,
  [switch]$SkipBuild,
  [int]$TimeoutMin = 80,
  [string]$RunId = "",
  [switch]$InQueue
)
$ErrorActionPreference = "Stop"
$project = Split-Path -Parent $PSScriptRoot
$name = "RingworldTechDemo"
$spec = @{
  oracle           = @{ Script = "tree_visibility_oracle.gd";           Marker = "TREE_VISIBILITY_ORACLE";  Needs = $null }
  frustum_identity = @{ Script = "tree_frustum_identity_gpu_test.gd";   Marker = "TREE_FRUSTUM_IDENTITY";   Needs = "TreeFrustumCulling" }
  cull_verify      = @{ Script = "tree_cull_verify_sweep.gd";          Marker = "TREE_CULL_VERIFY_SWEEP";  Needs = "TreeCullVerify" }
  cadence          = @{ Script = "tree_cadence_probe.gd";               Marker = "TREE_CADENCE";            Needs = $null }
  blob_probe       = @{ Script = "tree_frustum_blob_probe.gd";         Marker = "TREE_FRUSTUM_BLOB";       Needs = "TreeFrustumCulling" }
}[$Test]
$marker = $spec.Marker
# The sweeps count one frame at a time (readback), so simulate a fixed 33 ms per frame whatever the readback costs.
if ($Test -eq "cull_verify" -and -not $EngineArgs) { $EngineArgs = "--fixed-fps 30" }

# Cheap SKIP before any copy or launch: the OC-2 switch is not in the sources yet.
if ($spec.Needs -and $ScriptArgs -notmatch "--baseline-only") {
  $hit = Get-ChildItem (Join-Path $project "scripts") -Recurse -Filter *.cs -ErrorAction SilentlyContinue | Select-String -Pattern ("\b" + $spec.Needs + "\b") -List | Select-Object -First 1
  if (-not $hit) { Write-Host "$marker=SKIP property $($spec.Needs) does not exist yet (OC-2 not landed)"; exit 0 }
}

if (-not $RunId) { $RunId = Get-Date -Format "yyyyMMdd_HHmmss" }
if (-not $InQueue) {
  $inner = "& '$PSCommandPath' -InQueue -Test $Test -Label $Label -RunId $RunId -Resolution $Resolution -TimeoutMin $TimeoutMin" +
    $(if ($Build) { " -Build" } else { "" }) + $(if ($SkipBuild) { " -SkipBuild" } else { "" }) +
    $(if ($ScriptArgs) { " -ScriptArgs '$ScriptArgs'" } else { "" }) + $(if ($EngineArgs) { " -EngineArgs '$EngineArgs'" } else { "" })
  & (Join-Path $PSScriptRoot "gpu_queue.ps1") -Label $Label -Wrap $inner
  exit $LASTEXITCODE
}

$exe = Join-Path $project "artifacts\engine-upgrade\Godot_v4.8-dev7_mono_win64\Godot_v4.8-dev7_mono_win64_console.exe"
$real = Join-Path $env:APPDATA "Godot\app_userdata\$name"
$monoTemp = Join-Path $project ".godot\mono\temp"
$isoRoot = Join-Path $project ".godot\test_runs\appdata_occl-v"
$iso = Join-Path $isoRoot "Godot\app_userdata\$name"
$out = Join-Path $project ".godot\test_runs\$Label\tv_${Test}_$RunId"
New-Item -ItemType Directory -Force $out | Out-Null

# The user may be playing: wait, do not build or launch.
$playing = @(Get-CimInstance Win32_Process -Filter "Name LIKE 'Godot%'" | Where-Object { $_.CommandLine -match '--remote-debug' })
if ($playing.Count -gt 0 -or (Test-Path (Join-Path $monoTemp "bin\DebugPlaySaved\$name.dll"))) { Write-Host "USER_PLAYING"; Write-Host "$marker=FAIL user is playing (exit 9)"; exit 9 }
if (-not (Test-Path (Join-Path $real "worlds\slot_3"))) { Write-Host "$marker=FAIL real profile has no worlds\slot_3"; exit 1 }

# ---- isolated profile: read the real one, write only under .godot\test_runs\appdata_occl-v
if ((-not (Test-Path $iso))) {
  Write-Host "first use: creating the isolated profile (copies caches, a few GB) ..."
  New-Item -ItemType Directory -Force (Join-Path $iso "worlds") | Out-Null
  foreach ($d in Get-ChildItem $real -Directory) {
    if ($d.Name -in @("worlds", "artifacts", "objectdb_snapshots", "certification_reports", "pda_debug", "terrain_generation_preview")) { continue }
    Copy-Item -Recurse $d.FullName (Join-Path $iso $d.Name)
  }
}
$isoSlot = Join-Path $iso "worlds\slot_3"
if ((Test-Path $isoSlot)) {
  if ((Resolve-Path $isoSlot).Path -notlike "*\.godot\test_runs\appdata_occl-v\*") { throw "refusing: $isoSlot" }
  Remove-Item -Recurse -Force $isoSlot
}
Copy-Item -Recurse (Join-Path $real "worlds\slot_3") $isoSlot
Get-ChildItem $real -File | Copy-Item -Destination $iso -Force

# ---- optimized assembly
$slotDll = Join-Path $monoTemp "bin\Debug\$name.dll"; $slotPdb = Join-Path $monoTemp "bin\Debug\$name.pdb"
$optDir = Join-Path $monoTemp "bin\OptRelease"
$optDll = Join-Path $optDir "$name.dll"; $optPdb = Join-Path $optDir "$name.pdb"
$stale = $true
if (Test-Path $optDll) {
  $built = (Get-Item $optDll).LastWriteTimeUtc
  $newer = Get-ChildItem $project -Recurse -Include *.cs -File -ErrorAction SilentlyContinue | Where-Object { $_.FullName -notmatch '\\(\.godot|artifacts|addons|\.git)\\' -and $_.LastWriteTimeUtc -gt $built } | Select-Object -First 1
  $stale = [bool]$newer
}
if ($Build -or (-not $SkipBuild -and $stale)) {
  $dotnetRoot = Join-Path $project "artifacts\engine-upgrade\dotnet"
  $envNames = @('DOTNET_ROOT', 'DOTNET_ROOT_X64', 'PATH', 'DOTNET_CLI_HOME', 'NUGET_PACKAGES', 'NUGET_HTTP_CACHE_PATH', 'DOTNET_CLI_TELEMETRY_OPTOUT', 'DOTNET_GENERATE_ASPNET_CERTIFICATE', 'DOTNET_ADD_GLOBAL_TOOLS_TO_PATH')
  $oldEnv = @{}; foreach ($n in $envNames) { $oldEnv[$n] = [Environment]::GetEnvironmentVariable($n, 'Process') }
  try {
    $env:DOTNET_ROOT = $dotnetRoot; $env:DOTNET_ROOT_X64 = $dotnetRoot; $env:PATH = "$dotnetRoot;$($oldEnv['PATH'])"
    $env:DOTNET_CLI_HOME = Join-Path $project 'artifacts\engine-upgrade\dotnet-cli'
    $env:NUGET_PACKAGES = Join-Path $project 'artifacts\engine-upgrade\nuget-packages'
    $env:NUGET_HTTP_CACHE_PATH = Join-Path $project 'artifacts\engine-upgrade\nuget-http-cache'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'; $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'; $env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
    Write-Host "Building optimized C# into $optDir ..."
    & (Join-Path $dotnetRoot 'dotnet.exe') build (Join-Path $project "$name.csproj") -c Debug -p:Optimize=true -p:NuGetAudit=false -v:minimal "-p:OutputPath=$optDir\" "-p:IntermediateOutputPath=$(Join-Path $monoTemp 'obj\OptRelease')\"
    $buildExit = $LASTEXITCODE
  } finally { foreach ($n in $envNames) { [Environment]::SetEnvironmentVariable($n, $oldEnv[$n], 'Process') } }
  if ($buildExit -ne 0) { Write-Host "$marker=FAIL optimized C# build failed (exit $buildExit)"; exit 1 }
}
if (-not (Test-Path $optDll)) { Write-Host "$marker=FAIL no OptRelease build (run with -Build)"; exit 1 }

$savedDir = Join-Path $monoTemp "bin\DebugSaved_treevis"
New-Item -ItemType Directory -Force $savedDir | Out-Null
Copy-Item -Force $slotDll, $slotPdb $savedDir
Copy-Item -Force $optDll $slotDll
Copy-Item -Force $optPdb $slotPdb
$code = -1
$savedAppData = $env:APPDATA
try {
  $env:APPDATA = $isoRoot
  $env:RINGWORLD_TEST_NO_GRAB = "1"
  $arguments = @("--path", $project, "--resolution", $Resolution)
  if ($EngineArgs) { $arguments += ($EngineArgs -split " " | Where-Object { $_ }) }
  $arguments += @("--script", "res://tools/$($spec.Script)", "--", "--out=$out") + ($ScriptArgs -split " " | Where-Object { $_ })
  $log = Join-Path $out "console.log"
  $proc = Start-Process -FilePath $exe -ArgumentList $arguments -RedirectStandardOutput $log -RedirectStandardError (Join-Path $out "stderr.log") -PassThru -NoNewWindow
  if (-not $proc.WaitForExit($TimeoutMin * 60 * 1000)) { Write-Host "TIMEOUT: killing own Godot $($proc.Id)"; Stop-Process -Id $proc.Id -Force }
  $code = $proc.ExitCode
} finally {
  $env:APPDATA = $savedAppData
  Copy-Item -Force (Join-Path $savedDir "$name.dll") $slotDll
  Copy-Item -Force (Join-Path $savedDir "$name.pdb") $slotPdb
  Remove-Item -Recurse -Force $savedDir
  Write-Host "Debug slot restored"
}
Write-Host "godot exit $code, output $out"
$verdicts = @(Select-String -Path (Join-Path $out "console.log"), (Join-Path $out "stderr.log") -Pattern "^(ERROR: )?$marker=" | ForEach-Object { $_.Line })
$verdicts | ForEach-Object { Write-Host $_ }
Select-String -Path (Join-Path $out "console.log") -Pattern "^(TVH|OCC) |SCRIPT ERROR|Parse Error" | ForEach-Object { $_.Line } | Select-Object -Last 40 | ForEach-Object { Write-Host $_ }
if ($verdicts.Count -eq 0) { Write-Host "$marker=FAIL no verdict line (godot exit $code)"; exit 1 }
if ($verdicts -match "=FAIL") { exit 1 }
exit 0
