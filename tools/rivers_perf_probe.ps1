<#
.SYNOPSIS
  Runner of tools/rivers_perf_probe.gd: rivers world against a generation-1 control at one pose, streaming window and still windows, with a summary table.
.EXAMPLE
  .\tools\rivers_perf_probe.ps1                                   # rivers twice, gen-1 once, play-test seed and spot
  .\tools\rivers_perf_probe.ps1 -Scenario rivers -Runs 1 -Cases base,water_hidden,coarse_water_hidden,base_repeat
  .\tools\rivers_perf_probe.ps1 -Scenario rivers -Runs 1 -Mode ablate    # GPU cost per proxy level and node group, allocation by node
  .\tools\rivers_perf_probe.ps1 -Scenario rivers -Runs 1 -Mode levels    # top-down stills with one level's water at a time
.NOTES
  Scenarios: rivers (generation 1 plus the water.rivers review feature), gen1 (generation 1 alone). Both are NEW certified worlds with -Seed, loaded through the real loading screen.
  Profile: one isolated profile, .godot\test_runs\appdata_<Label>, reused across runs. It gets the user's preference files (*.tres), render resolution forced to Native with
  adaptive resolution off (-KeepRenderResolution leaves the user's file alone), and on first use a copy of the real profile's shader and Vulkan caches (-Cache none skips that).
  The world folder is emptied before every run, so no run sees another run's world cache.
  Output: .godot\test_runs\<Label>\<RunId>\ with <scenario>_<n>\result.json and console.log, environment.json, summary.json and summary.md. test_runs is not pruned by
  tools/prune_test_output.ps1 (only its appdata_* profiles are), so results survive a prune.
  Takes the GPU queue itself (tools/gpu_queue.ps1 -Wrap). Sets RINGWORLD_PDA_DISABLE_PRELOAD=1 and keeps the display on while it runs (a display that powers down blocks
  presentation). Other Godot processes found at the start or the end are recorded in environment.json; a window under 5 fps is marked invalid by the probe.
  Exit code: 0 when every run passed with valid windows, 1 otherwise.
#>
param(
  [string[]]$Scenario = @("rivers", "gen1"),
  [int]$Runs = 0,
  [string]$Seed = "876374626",
  [ValidateSet("timed", "levels", "ablate")][string]$Mode = "timed",
  [string]$Cases = "base,base_repeat",
  [int]$StillSeconds = 60,
  [int]$WindowSeconds = 20,
  [switch]$Stills,
  [string]$ProbeArgs = "",
  [string]$Label = "rivers_perf",
  [string]$RunId = "",
  [ValidateSet("copy", "none")][string]$Cache = "copy",
  [switch]$KeepRenderResolution,
  [switch]$NoPrune,
  [int]$TimeoutSeconds = 1500,
  [string]$ProjectRoot = "",
  [string]$ProjectName = "RingworldTechDemo",
  [switch]$InQueue
)
$ErrorActionPreference = "Stop"
if (-not $ProjectRoot) { $ProjectRoot = Split-Path -Parent $PSScriptRoot }
$project = (Resolve-Path $ProjectRoot).Path
if (-not $RunId) { $RunId = Get-Date -Format "yyyyMMdd_HHmmss" }
$Scenario = @($Scenario | ForEach-Object { $_ -split "," } | Where-Object { $_ })
foreach ($kind in $Scenario) { if ($kind -notin @("rivers", "gen1")) { throw "unknown scenario '$kind' (rivers, gen1)" } }

if (-not $InQueue) {
  # Re-enter through the GPU queue so the whole measurement holds the GPU once.
  $inner = "& '$PSCommandPath' -InQueue -Scenario " + ($Scenario -join ",") + " -Runs $Runs -Seed $Seed -Mode $Mode -Cases '$Cases' -StillSeconds $StillSeconds -WindowSeconds $WindowSeconds" +
    " -Label $Label -RunId $RunId -Cache $Cache -TimeoutSeconds $TimeoutSeconds -ProjectRoot '$project' -ProjectName $ProjectName" +
    $(if ($Stills) { " -Stills" } else { "" }) + $(if ($KeepRenderResolution) { " -KeepRenderResolution" } else { "" }) + $(if ($NoPrune) { " -NoPrune" } else { "" }) +
    $(if ($ProbeArgs) { " -ProbeArgs '$ProbeArgs'" } else { "" })
  & (Join-Path $project "tools\gpu_queue.ps1") -Label $Label -Wrap $inner
  exit $LASTEXITCODE
}

$manifest = Get-Content (Join-Path $project "tools\test_manifest.json") -Raw | ConvertFrom-Json
$engine = Join-Path $project ([string]$manifest.engine)
$probe = Join-Path $PSScriptRoot "rivers_perf_probe.gd"
$realRoaming = if ($env:APPDATA -and $env:APPDATA -notlike "*\.godot\*") { $env:APPDATA } else { Join-Path $env:USERPROFILE "AppData\Roaming" }
$realProfile = Join-Path $realRoaming "Godot\app_userdata\$ProjectName"
$isoRoot = Join-Path $project ".godot\test_runs\appdata_$Label"
$isoProfile = Join-Path $isoRoot "Godot\app_userdata\$ProjectName"
$runDir = Join-Path $project ".godot\test_runs\$Label\$RunId"
New-Item -ItemType Directory -Force $runDir | Out-Null
if (-not $NoPrune) { & (Join-Path $project "tools\prune_test_output.ps1") -Apply -Quiet -Exclude (Split-Path -Leaf $isoRoot) | Out-Host }

# ---- isolated profile
if (-not (Test-Path $isoProfile)) {
  New-Item -ItemType Directory -Force (Join-Path $isoProfile "worlds") | Out-Null
  if ($Cache -eq "copy") {
    foreach ($name in @("shader_cache", "vulkan")) {
      $source = Join-Path $realProfile $name
      if (Test-Path $source) { Copy-Item -Recurse -Force $source (Join-Path $isoProfile $name) }
    }
  }
}
Get-ChildItem $realProfile -Filter "*.tres" -File | Copy-Item -Destination $isoProfile -Force
if (-not $KeepRenderResolution) {
  # Native output resolution, no FSR, no adaptive resolution: the frame cost must not depend on the scaler.
  $resolution = Join-Path $isoProfile "gameplay_render_resolution_defaults.tres"
  if (Test-Path $resolution) {
    (Get-Content $resolution) -replace '^Preset = \d+', 'Preset = 0' -replace '^AdaptiveResolutionEnabled = true', 'AdaptiveResolutionEnabled = false' | Set-Content $resolution
  }
}

function Get-GodotProcesses { @(Get-Process -Name "Godot*" -ErrorAction SilentlyContinue | ForEach-Object { [ordered]@{ id = $_.Id; name = $_.ProcessName; started = $_.StartTime.ToString("o"); title = $_.MainWindowTitle } }) }
function Get-GpuMemoryMiB {
  try { return [int]((& nvidia-smi --query-gpu=memory.used --format=csv,noheader,nounits 2>$null | Select-Object -First 1).Trim()) } catch { return $null }
}

# Keep the display on for the length of the measurement (ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED); restored in finally.
if (-not ("RiversPerfProbe.Power" -as [type])) {
  Add-Type -Namespace RiversPerfProbe -Name Power -MemberDefinition '[DllImport("kernel32.dll")] public static extern uint SetThreadExecutionState(uint flags);'
}

$environment = [ordered]@{
  run_id = $RunId; label = $Label; machine = $env:COMPUTERNAME; seed = $Seed; mode = $Mode; cases = $Cases
  commit = "$(git -C $project rev-parse --short HEAD)$(if (git -C $project status --porcelain --untracked-files=no) { '+dirty' } else { '' })"
  engine = [string]$manifest.engine; started = (Get-Date).ToString("o")
  other_godot_at_start = Get-GodotProcesses; gpu_memory_mib_at_start = Get-GpuMemoryMiB
  render_resolution_forced_native = (-not $KeepRenderResolution); runs = @()
}

function Invoke-Probe([string]$Kind, [string]$OutDir) {
  New-Item -ItemType Directory -Force $OutDir | Out-Null
  $worlds = Join-Path $isoProfile "worlds"
  if (Test-Path $worlds) { Get-ChildItem $worlds -Force | Remove-Item -Recurse -Force }
  New-Item -ItemType Directory -Force $worlds | Out-Null
  Set-Content (Join-Path $isoRoot ".keep_alive") (Get-Date -Format o)
  $log = Join-Path $OutDir "console.log"
  $rivers = if ($Kind -eq "rivers") { "1" } else { "0" }
  $godotArgs = @("--path", "`"$project`"", "--resolution", "2560x1440", "--position", "0,0", "--script", "`"$probe`"", "--",
    "--rivers=$rivers", "--seed=$Seed", "--out=`"$OutDir`"", "--cases=$Cases", "--still-seconds=$StillSeconds", "--window=$WindowSeconds",
    "--stills=$(if ($Stills) { 1 } else { 0 })")
  if ($Mode -eq "levels") { $godotArgs += "--level-stills=1" }
  if ($Mode -eq "ablate") { $godotArgs += "--ablate=1" }
  if ($ProbeArgs) { $godotArgs += ($ProbeArgs -split " " | Where-Object { $_ }) }
  $oldAppData = $env:APPDATA
  $oldPreload = $env:RINGWORLD_PDA_DISABLE_PRELOAD
  $env:APPDATA = $isoRoot
  $env:RINGWORLD_PDA_DISABLE_PRELOAD = "1"
  $exit = -1
  try {
    $proc = Start-Process -FilePath $engine -ArgumentList $godotArgs -PassThru -NoNewWindow -RedirectStandardOutput $log -RedirectStandardError "$log.err"
    if (-not $proc.WaitForExit($TimeoutSeconds * 1000)) { $proc.Kill(); "timed out after $TimeoutSeconds s (this process only)" | Add-Content $log } else { $exit = $proc.ExitCode }
  } finally { $env:APPDATA = $oldAppData; $env:RINGWORLD_PDA_DISABLE_PRELOAD = $oldPreload }
  if (Test-Path "$log.err") { Get-Content "$log.err" | Add-Content $log; Remove-Item "$log.err" }
  "exit=$exit" | Add-Content $log
  $json = Join-Path $OutDir "result.json"
  if (-not (Test-Path $json)) { return [ordered]@{ exit = $exit; result = $null } }
  $result = Get-Content $json -Raw | ConvertFrom-Json
  # The slowdown report stays in the profile (16 MB); keep its proxy build counters beside the result.
  if ($result.PSObject.Properties.Name -contains "slowdown_report" -and (Test-Path ([string]$result.slowdown_report))) {
    try {
      $report = Get-Content ([string]$result.slowdown_report) -Raw | ConvertFrom-Json
      $report.proxyBuildCounters | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $OutDir "proxy_counters.json")
    } catch { }
  }
  return [ordered]@{ exit = $exit; result = $result }
}

function Get-Scope($Window, [string]$Name) {
  if ($null -eq $Window.cpu_scopes) { return $null }
  $property = $Window.cpu_scopes.PSObject.Properties[$Name]
  if ($property) { return $property.Value } else { return $null }
}
function Format-Number($Value, [string]$Format = "0.0") { if ($null -eq $Value) { return "-" } return ([double]$Value).ToString($Format, [Globalization.CultureInfo]::InvariantCulture) }

function Get-WindowRow([string]$RunName, $Window) {
  $coordinator = Get-Scope $Window "Terrain LOD coordinator update (inclusive)"
  $publish = Get-Scope $Window "Terrain proxy install ocean publish"
  $copy = Get-Scope $Window "Terrain proxy build start sampler copy"
  $trees = Get-Scope $Window "Trees install"
  $build = Get-Scope $Window "Terrain proxy build"
  $seconds = [double]$Window.seconds
  return [ordered]@{
    run = $RunName; case = [string]$Window.case; valid = [bool]$Window.valid; fps = [double]$Window.fps
    frame_p50_ms = $Window.frame_ms.p50; frame_p95_ms = $Window.frame_ms.p95; frame_max_ms = $Window.frame_ms.max
    gpu_root_p50_ms = $Window.viewport_gpu_ms.root.p50
    coordinator_ms_per_frame = $(if ($coordinator) { $coordinator.ms_per_frame }); coordinator_p95_ms = $(if ($coordinator) { $coordinator.p95 }); coordinator_max_ms = $(if ($coordinator) { $coordinator.max })
    ocean_publish_ms_per_frame = $(if ($publish) { $publish.ms_per_frame }); ocean_publish_mean_ms = $(if ($publish) { $publish.mean }); ocean_publish_p95_ms = $(if ($publish) { $publish.p95 })
    sampler_copy_mean_ms = $(if ($copy) { $copy.mean }); sampler_copy_count = $(if ($copy) { $copy.n }); sampler_copy_ms_per_frame = $(if ($copy) { $copy.ms_per_frame })
    trees_install_ms_per_frame = $(if ($trees) { $trees.ms_per_frame })
    proxy_build_mean_ms = $(if ($build) { $build.mean }); proxy_build_p95_ms = $(if ($build) { $build.p95 }); proxy_build_batches = $(if ($build) { $build.n })
    alloc_mb_per_s = [double]$Window.gc_delta.allocated_bytes / 1e6 / [math]::Max(0.001, $seconds); gc_count = $Window.gc_delta.gen0_collections; gc_gen2 = $Window.gc_delta.gen2_collections
    gc_pause_ms = $Window.gc_delta.total_gc_pause_ms
  }
}

$failed = 0
$rows = New-Object System.Collections.Generic.List[object]
$lines = New-Object System.Collections.Generic.List[string]
try {
  [void][RiversPerfProbe.Power]::SetThreadExecutionState([uint32]"0x80000003")
  foreach ($kind in $Scenario) {
    $count = if ($Runs -gt 0) { $Runs } elseif ($kind -eq "rivers") { 2 } else { 1 }
    for ($n = 1; $n -le $count; $n++) {
      $name = "${kind}_$n"
      Write-Host "run $name (seed $Seed, mode $Mode)"
      $outcome = Invoke-Probe $kind (Join-Path $runDir $name)
      $result = $outcome.result
      $record = [ordered]@{ name = $name; exit = $outcome.exit; other_godot_after = Get-GodotProcesses; gpu_memory_mib_after = Get-GpuMemoryMiB }
      if ($null -eq $result) { $failed++; $record.error = "no result.json"; $environment.runs += $record; continue }
      if ($outcome.exit -ne 0) { $failed++ }
      $record.load_s = [math]::Round([double]$result.open_to_ready_ms / 1000, 1)
      if ($result.PSObject.Properties.Name -contains "settle") {
        $record.settle_idle = $result.settle.idle
        $record.settle_since_teleport_s = $(if ($result.settle.PSObject.Properties.Name -contains "since_teleport_s") { [math]::Round([double]$result.settle.since_teleport_s, 1) } else { [math]::Round([double]$result.settle.seconds, 1) })
      }
      if ($result.PSObject.Properties.Name -contains "render") { $record.render = $result.render }
      $record.graphics_preset = $result.graphics_preset
      $record.gate = @($result.load_marks | Where-Object { $_.name -like "streaming_settle*" } | ForEach-Object { $_.name })
      $environment.runs += $record
      foreach ($window in @($result.windows)) { $rows.Add((Get-WindowRow $name $window)) }
    }
  }
} finally {
  [void][RiversPerfProbe.Power]::SetThreadExecutionState([uint32]"0x80000000")
}
$environment.finished = (Get-Date).ToString("o")
$environment.other_godot_at_end = Get-GodotProcesses
$environment | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $runDir "environment.json")
[ordered]@{ environment = $environment; windows = $rows } | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $runDir "summary.json")

$lines.Add("# Rivers perf probe $RunId")
$lines.Add("")
$lines.Add("Machine $($environment.machine), commit $($environment.commit), seed $Seed, mode $Mode, cases $Cases. Other Godot processes at start: $(@($environment.other_godot_at_start).Count).")
$lines.Add("")
$lines.Add("| run | load s | settle s (idle) | gate | preset | render |")
$lines.Add("|---|---|---|---|---|---|")
foreach ($record in $environment.runs) {
  $render = if ($record.Contains("render")) { "$($record.render.window[0])x$($record.render.window[1]) x $(Format-Number $record.render.scaling_3d_scale '0.###')" } else { "-" }
  $lines.Add("| $($record.name) | $($record.load_s) | $($record.settle_since_teleport_s) ($($record.settle_idle)) | $($record.gate -join ', ') | $($record.graphics_preset) | $render |")
}
if ($rows.Count -gt 0) {
  $lines.Add("")
  $lines.Add("| run | window | valid | fps | frame p50 / p95 / max ms | GPU ms | coordinator ms/frame (p95) | ocean publish ms/frame (per call, p95) | sampler copy ms (count) | Trees install ms/frame | proxy build mean / p95 ms | alloc MB/s (GCs, pause ms) |")
  $lines.Add("|---|---|---|---|---|---|---|---|---|---|---|---|")
  foreach ($row in $rows) {
    $lines.Add("| $($row.run) | $($row.case) | $($row.valid) | $(Format-Number $row.fps) | $(Format-Number $row.frame_p50_ms) / $(Format-Number $row.frame_p95_ms) / $(Format-Number $row.frame_max_ms) | $(Format-Number $row.gpu_root_p50_ms '0.00') | " +
      "$(Format-Number $row.coordinator_ms_per_frame '0.00') ($(Format-Number $row.coordinator_p95_ms)) | $(Format-Number $row.ocean_publish_ms_per_frame '0.00') ($(Format-Number $row.ocean_publish_mean_ms), $(Format-Number $row.ocean_publish_p95_ms)) | " +
      "$(Format-Number $row.sampler_copy_mean_ms '0.00') ($($row.sampler_copy_count)) | $(Format-Number $row.trees_install_ms_per_frame '0.00') | $(Format-Number $row.proxy_build_mean_ms '0') / $(Format-Number $row.proxy_build_p95_ms '0') | " +
      "$(Format-Number $row.alloc_mb_per_s) ($($row.gc_count), $(Format-Number $row.gc_pause_ms '0')) |")
  }
}
$lines | Set-Content (Join-Path $runDir "summary.md")
$lines | Out-Host
Write-Host "RIVERS_PERF_PROBE_SUMMARY=$(Join-Path $runDir 'summary.md')"
$invalid = @($rows | Where-Object { -not $_.valid }).Count
if ($failed -gt 0 -or $invalid -gt 0) { Write-Host "RIVERS_PERF_PROBE=FAIL runs failed: $failed, invalid windows: $invalid"; exit 1 }
Write-Host "RIVERS_PERF_PROBE=PASS"
exit 0
