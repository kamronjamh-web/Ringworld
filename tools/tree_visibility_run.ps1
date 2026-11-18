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
  Build: the optimized C# assembly (bin\OptRelease, as tools/play.ps1) is swapped into the Debug slot for the run and restored afterwards. The build and its git-based staleness
  check (a tracked or untracked .cs file newer than the assembly) run BEFORE the GPU ticket, as do the script preflight and the prune; it is rebuilt with -Build, -SkipBuild reuses
  it as is. The SKIP check for the OC-3 tests reads the sources, before any copy or launch (not with --baseline-only, the determinism check of the identity test).
  GPU: takes the queue itself (tools/gpu_queue.ps1 -Wrap, one ticket label per run: tree_vis_<Label>_<RunId>); do not wrap this in the queue. Godot runs under the stall / load-failure
  watchdog (-StallMinutes, default 10: no output for that long kills it). Output: .godot\test_runs\<Label>\tv_<Test>_<stamp>\ (console.log, report.json, ...).
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
  [int]$StallMinutes = 10,
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
$allScripts = @("tree_visibility_oracle.gd", "tree_frustum_identity_gpu_test.gd", "tree_cull_verify_sweep.gd", "tree_cadence_probe.gd", "tree_frustum_blob_probe.gd")
function Quote([string]$s) { return "'" + $s.Replace("'", "''") + "'" }
# The sweeps count one frame at a time (readback), so simulate a fixed 33 ms per frame whatever the readback costs.
if ($Test -eq "cull_verify" -and -not $EngineArgs) { $EngineArgs = "--fixed-fps 30" }

$real = Join-Path $env:APPDATA "Godot\app_userdata\$name"
$monoTemp = Join-Path $project ".godot\mono\temp"
Import-Module (Join-Path $PSScriptRoot "testing\GpuJob.psm1") -Force -DisableNameChecking

if (-not $RunId) { $RunId = Get-Date -Format "yyyyMMdd_HHmmss" }
if (-not $InQueue) {
  # The user may be playing: wait, do not build or launch.
  if (Test-UserPlaying) { Write-Host "USER_PLAYING"; Write-Host "$marker=FAIL user is playing (exit 9)"; exit 9 }

  # Cheap SKIP before any copy or launch: the OC-2 switch is not in the sources yet.
  if ($spec.Needs -and $ScriptArgs -notmatch "--baseline-only") {
    $hit = Get-ChildItem (Join-Path $project "scripts") -Recurse -Filter *.cs -ErrorAction SilentlyContinue | Select-String -Pattern ("\b" + $spec.Needs + "\b") -List | Select-Object -First 1
    if (-not $hit) { Write-Host "$marker=SKIP property $($spec.Needs) does not exist yet (OC-2 not landed)"; exit 0 }
  }
  if (-not (Test-Path (Join-Path $real "worlds\slot_3"))) { Write-Host "$marker=FAIL real profile has no worlds\slot_3"; exit 1 }

  # Pre-launch check (no GPU, no ticket): the driver and its preloads must parse. The engine exits 0 on a parse error and would idle with the ticket held.
  $pfRes = @(Invoke-GodotPreflight -EntryScripts @("res://tools/$($spec.Script)") 6>&1)
  $pfLines = @($pfRes | Where-Object { $_ -is [System.Management.Automation.InformationRecord] } | ForEach-Object { "$($_.MessageData)" })
  $pfLines | ForEach-Object { Write-Host $_ }
  if ($pfRes[-1] -ne $true) {
    $first = ($pfLines | Where-Object { $_ -like 'PREFLIGHT problem:*' } | Select-Object -First 1) -replace '^PREFLIGHT problem:\s*', ''
    Write-Host "$marker=FAIL preflight: $first"; exit 2
  }
  # Build (staleness check, lock) and prune need no GPU: before the ticket.
  $buildRc = Invoke-OptimizedBuild -Build:$Build -SkipBuild:$SkipBuild -Marker $marker
  if ($buildRc -ne 0) { exit $buildRc }
  & (Join-Path $PSScriptRoot "prune_test_output.ps1") -Apply -Quiet -Exclude "appdata_occl-v", $Label

  $inner = "& $(Quote $PSCommandPath) -InQueue -Test $Test -Label $(Quote $Label) -RunId $RunId -Resolution $Resolution -TimeoutMin $TimeoutMin -StallMinutes $StallMinutes" +
    $(if ($Build) { " -Build" } else { "" }) + $(if ($SkipBuild) { " -SkipBuild" } else { "" }) +
    $(if ($ScriptArgs) { " -ScriptArgs $(Quote $ScriptArgs)" } else { "" }) + $(if ($EngineArgs) { " -EngineArgs $(Quote $EngineArgs)" } else { "" })
  # One ticket per run: gpu_queue finds tickets by label, so a shared label let a second run match the first one's ticket.
  # frustum_identity, cull_verify and blob_probe check correctness or pictures and may share the GPU; oracle and cadence time things and stay exclusive.
  $qClass = if ($Test -in @("frustum_identity", "cull_verify", "blob_probe")) { "shared" } else { "exclusive" }
  & (Join-Path $PSScriptRoot "gpu_queue.ps1") -Label "tree_vis_$($Label)_$RunId" -Class $qClass -Wrap $inner
  exit $LASTEXITCODE
}

# ------------------------------------------------------------------ inside the queue (GPU needed only from here)
$exe = Join-Path $project "artifacts\engine-upgrade\Godot_v4.8-dev7_mono_win64\Godot_v4.8-dev7_mono_win64_console.exe"
$isoRoot = Join-Path $project ".godot\test_runs\appdata_occl-v"
$iso = Join-Path $isoRoot "Godot\app_userdata\$name"
$out = Join-Path $project ".godot\test_runs\$Label\tv_${Test}_$RunId"
New-Item -ItemType Directory -Force $out | Out-Null

if (Test-UserPlaying) { Write-Host "USER_PLAYING"; Write-Host "$marker=FAIL user is playing (exit 9)"; exit 9 }
$otherRun = @(Get-CimInstance Win32_Process -Filter "Name like 'Godot%'" -ErrorAction SilentlyContinue | Where-Object { $cl = $_.CommandLine; $cl -and @($allScripts | Where-Object { $cl.Contains($_) }).Count -gt 0 })
if ($otherRun.Count -gt 0) { Write-Host "$marker=FAIL another tree visibility Godot is still running (pid $($otherRun[0].ProcessId)) on the shared profile; not touching it"; exit 2 }
$realSlot = Join-Path $real "worlds\slot_3"
if (-not (Test-Path $realSlot)) { Write-Host "$marker=FAIL real profile has no worlds\slot_3"; exit 1 }

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
New-Item -ItemType Directory -Force (Join-Path $iso "worlds") | Out-Null
if ([System.IO.Path]::GetFullPath($isoSlot) -notlike "*\.godot\test_runs\appdata_occl-v\*") { throw "refusing: $isoSlot" }
# Mirror the live slot into the profile: only what changed is copied (the real slot is the source and is never written).
& robocopy $realSlot $isoSlot /MIR /R:1 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { Write-Host "$marker=FAIL robocopy of the world failed (exit $LASTEXITCODE)"; exit 2 }
$srcStat = Get-ChildItem -LiteralPath $realSlot -Recurse -File -Force | Measure-Object -Property Length -Sum
$dstStat = Get-ChildItem -LiteralPath $isoSlot -Recurse -File -Force | Measure-Object -Property Length -Sum
if ($srcStat.Count -ne $dstStat.Count -or $srcStat.Sum -ne $dstStat.Sum) {
  Write-Host "$marker=FAIL world restore mismatch: real slot $($srcStat.Count) files / $($srcStat.Sum) bytes, profile $($dstStat.Count) files / $($dstStat.Sum) bytes"; exit 2
}
Write-Host "world restore: real slot_3 $($srcStat.Count) files / $($srcStat.Sum) bytes, profile $($dstStat.Count) files / $($dstStat.Sum) bytes (match)"
Get-ChildItem $real -File | Copy-Item -Destination $iso -Force

# ---- optimized assembly (built by the outer part)
$slotDll = Join-Path $monoTemp "bin\Debug\$name.dll"; $slotPdb = Join-Path $monoTemp "bin\Debug\$name.pdb"
$optDir = Join-Path $monoTemp "bin\OptRelease"
$optDll = Join-Path $optDir "$name.dll"; $optPdb = Join-Path $optDir "$name.pdb"
if (-not (Test-Path $optDll)) { Write-Host "$marker=FAIL no OptRelease build (run with -Build)"; exit 1 }

$savedDir = Join-Path $monoTemp "bin\DebugSaved_treevis"
New-Item -ItemType Directory -Force $savedDir | Out-Null
Copy-Item -Force $slotDll, $slotPdb $savedDir
Copy-Item -Force $optDll $slotDll
Copy-Item -Force $optPdb $slotPdb
$code = -1
$job = $null
try {
  $arguments = @("--path", $project, "--resolution", $Resolution)
  if ($EngineArgs) { $arguments += ($EngineArgs -split " " | Where-Object { $_ }) }
  $arguments += @("--script", "res://tools/$($spec.Script)", "--", "--out=$out") + ($ScriptArgs -split " " | Where-Object { $_ })
  $job = Invoke-GodotJob -Exe $exe -Arguments $arguments -WorkDir $project -StdOutLog (Join-Path $out "console.log") -StdErrLog (Join-Path $out "stderr.log") `
    -Env @{ APPDATA = $isoRoot; RINGWORLD_TEST_NO_GRAB = "1" } -TimeoutS ($TimeoutMin * 60) -StallS ($StallMinutes * 60) -EntryScript "res://tools/$($spec.Script)"
  if ($job.Status -ne "exited") { Write-Host "GODOT_JOB $($job.Status): $($job.Detail)" }
  $code = if ($null -ne $job.ExitCode) { $job.ExitCode } else { -1 }
} finally {
  # Restore only when nobody rebuilt the Debug slot meanwhile (another agent's newer build must not be clobbered).
  if ((Get-FileHash $slotDll).Hash -eq (Get-FileHash $optDll).Hash) {
    Copy-Item -Force (Join-Path $savedDir "$name.dll") $slotDll
    Copy-Item -Force (Join-Path $savedDir "$name.pdb") $slotPdb
    Write-Host "Debug slot restored"
  } else { Write-Host "Debug slot was rebuilt by someone else during the run; left as is" }
  Remove-Item -Recurse -Force $savedDir -ErrorAction SilentlyContinue
}
Write-Host "godot exit $code, output $out"
$verdicts = @(Select-String -Path (Join-Path $out "console.log"), (Join-Path $out "stderr.log") -Pattern "^(ERROR: )?$marker=" | ForEach-Object { $_.Line })
$verdicts | ForEach-Object { Write-Host $_ }
Select-String -Path (Join-Path $out "console.log") -Pattern "^(TVH|OCC) |SCRIPT ERROR|Parse Error" | ForEach-Object { $_.Line } | Select-Object -Last 40 | ForEach-Object { Write-Host $_ }
if ($verdicts.Count -eq 0) {
  if ($job -and $job.Status -ne "exited") { Write-Host "$marker=FAIL $($job.Status): $($job.Detail)"; exit 1 }
  Write-Host "$marker=FAIL no verdict line (godot exit $code)"; exit 1
}
if ($verdicts -match "=FAIL") { exit 1 }
exit 0
