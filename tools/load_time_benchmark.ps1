<#
.SYNOPSIS
  L0 load-time harness (docs/load-time-plan.md): end-to-end world load with per-stage timestamps, repeated N times, median reported.
.EXAMPLE
  .\tools\load_time_benchmark.ps1 -Scenario reload,new -Runs 3          # warm caches, VSync off
  .\tools\load_time_benchmark.ps1 -Scenario new-rivers -Runs 1
  .\tools\load_time_benchmark.ps1 -Scenario reload -CacheMode cold -Runs 3   # empty shader/pipeline caches every run
.NOTES
  Scenarios: new (certified launcher world, fresh slot), new-rivers (same with the water.rivers review feature; needs the T2R-10 code),
  reload (copy of real slot -Slot, no rivers unless the slot has them).
  -CacheMode warm : one isolated profile (.godot\load_bench\appdata_<Label>) reused across runs and invocations; the first invocation adds one discarded warm-up run.
  -CacheMode cold : the profile is recreated before every run (no shader, Vulkan or ring caches).
  Worlds are recreated per run (reload copies the slot again; new worlds start without a slot), so no run sees another run's world cache.
  Takes the GPU queue itself (tools/gpu_queue.ps1 -Wrap). Output: .godot\audit_baseline\<machine>\load\<stamp>\<scenario>\ (run_N\run.json + console.log, summary.json, summary.md) and <stamp>\summary.md.
#>
param(
  [string[]]$Scenario = @("reload", "new"),
  [int]$Runs = 3,
  [ValidateSet("off", "on")][string]$VSync = "off",
  [ValidateSet("warm", "cold")][string]$CacheMode = "warm",
  [int]$Slot = 1,
  [string]$Label = "loadbench",
  [string]$Stamp = "",
  [int]$PostSeconds = 10,
  [int]$TimeoutSeconds = 420,
  [switch]$InQueue,
  [string]$ProjectName = "RingworldTechDemo"
)
$ErrorActionPreference = "Stop"
$project = Split-Path -Parent $PSScriptRoot
if (-not $Stamp) { $Stamp = Get-Date -Format "yyyyMMdd_HHmmss" }
$Scenario = @($Scenario | ForEach-Object { $_ -split "," } | Where-Object { $_ })

if (-not $InQueue) {
  # Re-enter through the GPU queue so the whole benchmark holds the GPU once.
  $inner = "& '$PSCommandPath' -InQueue -Scenario " + ($Scenario -join ",") + " -Runs $Runs -VSync $VSync -CacheMode $CacheMode -Slot $Slot -Label $Label -Stamp $Stamp -PostSeconds $PostSeconds -TimeoutSeconds $TimeoutSeconds"
  & (Join-Path $PSScriptRoot "gpu_queue.ps1") -Label "load_bench" -Wrap $inner
  exit $LASTEXITCODE
}

$manifest = Get-Content (Join-Path $PSScriptRoot "test_manifest.json") -Raw | ConvertFrom-Json
$engine = Join-Path $project ([string]$manifest.engine)
$machine = $env:COMPUTERNAME
$realRoaming = if ($env:APPDATA -and $env:APPDATA -notlike "*\.godot\*") { $env:APPDATA } else { Join-Path $env:USERPROFILE "AppData\Roaming" }
$realProfile = Join-Path $realRoaming "Godot\app_userdata\$ProjectName"
$isoRoot = Join-Path $project ".godot\load_bench\appdata_$Label"
$isoProfile = Join-Path $isoRoot "Godot\app_userdata\$ProjectName"
$stampDir = Join-Path $project ".godot\audit_baseline\$machine\load\$Stamp"
& (Join-Path $PSScriptRoot "prune_test_output.ps1") -Apply -Quiet -Exclude (Split-Path -Leaf $isoRoot) | Out-Host

function Reset-Profile {
  if (Test-Path $isoRoot) { Remove-Item -Recurse -Force $isoRoot }
  New-Item -ItemType Directory -Force (Join-Path $isoProfile "worlds") | Out-Null
  Get-ChildItem $realProfile -Filter "*.tres" -File | Copy-Item -Destination $isoProfile
}

function Initialize-World([string]$Kind) {
  $worlds = Join-Path $isoProfile "worlds"
  if (Test-Path $worlds) { Get-ChildItem $worlds -Force | Remove-Item -Recurse -Force }
  New-Item -ItemType Directory -Force $worlds | Out-Null
  if ($Kind -eq "reload") { Copy-Item -Recurse (Join-Path $realProfile "worlds\slot_$Slot") (Join-Path $worlds "slot_$Slot") }
  Set-Content (Join-Path $isoRoot ".keep_alive") (Get-Date -Format o)
}

function Invoke-Run([string]$Kind, [string]$OutDir) {
  New-Item -ItemType Directory -Force $OutDir | Out-Null
  if ($CacheMode -eq "cold" -or -not (Test-Path $isoProfile)) { Reset-Profile }
  Initialize-World $Kind
  $log = Join-Path $OutDir "console.log"
  $godotArgs = @("--path", "`"$project`"", "--script", "res://tools/load_time_benchmark.gd", "--",
    "--scenario=$Kind", "--out=`"$OutDir`"", "--vsync=$VSync", "--slot=$Slot", "--post-seconds=$PostSeconds")
  $old = $env:APPDATA
  $env:APPDATA = $isoRoot
  try {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $proc = Start-Process -FilePath $engine -ArgumentList $godotArgs -PassThru -NoNewWindow -RedirectStandardOutput $log -RedirectStandardError "$log.err"
    if (-not $proc.WaitForExit($TimeoutSeconds * 1000)) { $proc.Kill(); Write-Host "  timed out after $TimeoutSeconds s (this process only)" }
    "process wall seconds: {0:N1}" -f $watch.Elapsed.TotalSeconds | Add-Content $log
  } finally { $env:APPDATA = $old }
  if (Test-Path "$log.err") { Get-Content "$log.err" | Add-Content $log; Remove-Item "$log.err" }
  $json = Join-Path $OutDir "run.json"
  if (-not (Test-Path $json)) { return $null }
  $run = Get-Content $json -Raw | ConvertFrom-Json
  if ($run.PSObject.Properties.Name -contains "error") { Write-Host "  load failed: $($run.error)"; return $null }
  return $run
}

function Get-Median([double[]]$Values) {
  if ($Values.Count -eq 0) { return $null }
  $s = @($Values | Sort-Object)
  $n = $s.Count
  if ($n % 2 -eq 1) { return [double]$s[($n - 1) / 2] }
  return ([double]$s[$n / 2 - 1] + [double]$s[$n / 2]) / 2
}

function Add-Series($Table, [string]$Key, $Value) {
  if ($null -eq $Value) { return }
  if (-not $Table.Contains($Key)) { $Table[$Key] = New-Object System.Collections.Generic.List[double] }
  $Table[$Key].Add([double]$Value)
}

function Get-Medians($Table) {
  $o = [ordered]@{}
  foreach ($k in $Table.Keys) { $o[$k] = [math]::Round((Get-Median $Table[$k].ToArray()), 1) }
  return $o
}

function Write-Summary([string]$Kind, $RunList, [string]$KindDir) {
  $total = New-Object System.Collections.Generic.List[double]
  $stage = [ordered]@{}; $stageStart = [ordered]@{}; $stageEnd = [ordered]@{}; $stagePipe = [ordered]@{}
  $ops = [ordered]@{}; $opEnd = [ordered]@{}; $marks = [ordered]@{}; $post = [ordered]@{}
  foreach ($run in $RunList) {
    $r = $run.report
    $total.Add([double]$r.elapsed_ms)
    foreach ($s in $r.stages) {
      Add-Series $stage $s.stage $s.milliseconds; Add-Series $stageStart $s.stage $s.start_ms
      Add-Series $stageEnd $s.stage $s.end_ms; Add-Series $stagePipe $s.stage $s.pipeline_compilations
    }
    foreach ($p in $r.operations_ms.PSObject.Properties) { Add-Series $ops $p.Name $p.Value }
    foreach ($p in $r.operation_spans_ms.PSObject.Properties) { Add-Series $opEnd $p.Name $p.Value.end_ms }
    $seen = @{}
    # A mark that repeats (graphics preset applied, global terrain queued after a restart) keeps every occurrence as name, name#2, ...
    foreach ($m in $r.marks) { $n = 1 + [int]$seen[$m.name]; $seen[$m.name] = $n; Add-Series $marks $(if ($n -eq 1) { $m.name } else { "$($m.name)#$n" }) $m.at_ms }
    Add-Series $post "post_entry_pipeline_compiles" (($run.post_entry.pipeline_compilations.PSObject.Properties | Measure-Object Value -Sum).Sum)
    Add-Series $post "post_entry_frame_max_ms" $run.post_entry.frame_max_ms
    Add-Series $post "loading_frame_p95_ms" $r.frame_p95_ms
    Add-Series $post "loading_frame_max_ms" $r.frame_max_ms
  }
  $first = $RunList[0].report.settings
  $mStage = Get-Medians $stage; $mStart = Get-Medians $stageStart; $mEnd = Get-Medians $stageEnd; $mPipe = Get-Medians $stagePipe
  $mOps = Get-Medians $ops; $mOpEnd = Get-Medians $opEnd; $mMarks = Get-Medians $marks; $mPost = Get-Medians $post
  $totalMedian = [math]::Round((Get-Median $total.ToArray()), 1)
  $summary = [ordered]@{
    scenario = $Kind; machine = $machine; gpu = $first.gpu; vsync = $first.vsync_name; vsync_requested = $VSync; max_fps = $first.max_fps
    processor_count = $first.processor_count; cache_mode = $CacheMode; runs = $RunList.Count; rivers = $first.rivers; existing_world = $first.existing_world
    caches_present_at_start = $first.caches_present; commit = $script:Commit; stamp = $Stamp
    total_ms_median = $totalMedian; total_ms_runs = @($total | ForEach-Object { [math]::Round($_, 1) })
    stages_ms_median = $mStage; stage_start_ms_median = $mStart; stage_end_ms_median = $mEnd
    stage_pipeline_compiles_median = $mPipe; operations_ms_median = $mOps; operation_end_ms_median = $mOpEnd
    marks_at_ms_median = $mMarks; other_median = $mPost
  }
  $summary | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $KindDir "summary.json")
  $lines = @("### $Kind (runs: $($RunList.Count), cache: $CacheMode, VSync: $($first.vsync_name), rivers: $($first.rivers), GPU: $($first.gpu))", "",
    "Total (median): **$totalMedian ms**; runs: $($summary.total_ms_runs -join ', ')", "",
    "| Stage | median ms | start | end | pipeline compiles |", "|---|---|---|---|---|")
  foreach ($k in $mStage.Keys) { $lines += "| $k | $($mStage[$k]) | $($mStart[$k]) | $($mEnd[$k]) | $($mPipe[$k]) |" }
  $lines += @("", "| Operation | median ms | ends at |", "|---|---|---|")
  foreach ($k in $mOps.Keys) { $lines += "| $k | $($mOps[$k]) | $($mOpEnd[$k]) |" }
  $lines += @("", "| Mark | at ms (median) |", "|---|---|")
  foreach ($k in ($mMarks.Keys | Sort-Object { $mMarks[$_] })) { $lines += "| $k | $($mMarks[$k]) |" }
  $lines += @("", "| Other | median |", "|---|---|")
  foreach ($k in $mPost.Keys) { $lines += "| $k | $($mPost[$k]) |" }
  $lines += ""
  $lines | Set-Content (Join-Path $KindDir "summary.md")
  return $lines
}

$dirty = if (git -C $project status --porcelain --untracked-files=no) { "+dirty" } else { "" }
$script:Commit = "$(git -C $project rev-parse --short HEAD)$dirty"
New-Item -ItemType Directory -Force $stampDir | Out-Null
$all = @("# Load time benchmark $Stamp", "", "Machine $machine, commit $script:Commit, VSync $VSync, cache $CacheMode, $Runs run(s) per scenario, median reported.", "")

# One discarded warm-up run when the warm profile has no caches yet (first invocation): fills the shader and pipeline caches.
if ($CacheMode -eq "warm" -and -not (Test-Path (Join-Path $isoProfile "shader_cache"))) {
  Write-Host "warm-up run ($($Scenario[0]), discarded)"
  Invoke-Run $Scenario[0] (Join-Path $stampDir "_warmup") | Out-Null
}
foreach ($kind in $Scenario) {
  $kindDir = Join-Path $stampDir $kind
  $runList = @()
  for ($i = 1; $i -le $Runs; $i++) {
    Write-Host "$kind run $i/$Runs"
    $run = Invoke-Run $kind (Join-Path $kindDir "run_$i")
    if ($run) { $runList += $run; Write-Host ("  elapsed {0:N0} ms" -f $run.report.elapsed_ms) }
  }
  if ($runList.Count -eq 0) { $all += @("### $kind", "", "No run completed; see $kindDir\run_*\console.log", ""); continue }
  $all += Write-Summary $kind $runList $kindDir
}
$all | Set-Content (Join-Path $stampDir "summary.md")
$all | Out-Host
Write-Host "Output: $stampDir"
Write-Host "LOAD_BENCHMARK=PASS"
& (Join-Path $PSScriptRoot "prune_test_output.ps1") -Apply -Quiet -Exclude (Split-Path -Leaf $isoRoot) | Out-Host
