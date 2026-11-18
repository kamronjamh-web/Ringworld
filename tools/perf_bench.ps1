<#
.SYNOPSIS
  Reusable real-world performance benchmark: a frozen copy of the player's slot 3 world, the real start menu and Continue, data-driven scenarios, A-A rounds,
  per-pass GPU, a report and a before/after delta table with a noise floor. Details and metric definitions: docs/perf-bench.md.
.EXAMPLE
  .\tools\perf_bench.ps1 -Label baseline_a785e4b -Quick                       # about 5 min: save spot, plains, forest interior, 9 km noon, 240 m/s flight
  .\tools\perf_bench.ps1 -Label after_fix -Quick -Baseline baseline_a785e4b   # adds the delta table (before / after / delta / noise / verdict)
  .\tools\perf_bench.ps1 -Label full_x                                        # the full list, about 20 min
  .\tools\perf_bench.ps1 -Label t -Scenarios save_spot,fly_240                # picked scenarios (names in tools/fixtures/perf_bench_scenarios.json)
  .\tools\perf_bench.ps1 -Label t -Quick -VsyncOn -Stills -Baseline x         # also the VSync-on pass, one capture-mode still per standing pose (SSIM vs the baseline's)
  .\tools\perf_bench.ps1 -Label t -Quick -Tails                               # full windows everywhere (default: standing windows stop early once settled; tails then informational)
  .\tools\perf_bench.ps1 -Label t -Scenarios plains -Variants .\my_variants.json  # in-session A/B (A-X-A) of run-time toggles at the standing scenarios; see docs/perf-bench.md
  .\tools\perf_bench.ps1 -Snapshot -SnapshotOnly                              # freeze the player's current slot 3 as a new benchmark world and stop
  .\tools\perf_bench.ps1 -Label x -ReportFrom .godot\perf_bench\x\<run id>    # rebuild report.json / report.md of a finished run (no game)
.NOTES
  World: never the live slot. The first run (or -Snapshot) copies worlds\slot_3 (and the top-level settings .tres files) of the real profile to
  %APPDATA%\Godot\app_userdata\RingworldTechDemo\bench_worlds\slot3_<date>\ ; every run mirrors that snapshot (robocopy /MIR, only what the game changed is copied) into the isolated profile
  .godot\appdata_perf_bench (one reused profile, the game autosaves into the copy). -World <snapshot dir or world dir> overrides it. The real profile is only READ,
  and only the snapshot step writes (below bench_worlds). Settings: the snapshot's frozen copy of the user's *.tres files is used (-LiveSettings: the live files now);
  the values that were used are recorded in report.json (settings_files) and report.md.
  Safety: exits 9 (USER_PLAYING) when a Godot with --remote-debug runs or .godot\mono\temp\bin\DebugPlaySaved holds an assembly. Takes the GPU queue (tools/gpu_queue.ps1 -Wrap; do not wrap this in the queue)
  only for the Godot part: the staleness check (git ls-files), the optimized build (under .godot\mono\temp\opt_build.lock) and the prune run before the ticket, the
  world mirror (robocopy /MIR), assembly swap and launch inside it, the report and baseline deltas after it. The optimized C# assembly (bin\OptRelease, as tools/play.ps1) is
  swapped into the Debug slot and restored in a finally block. It is rebuilt when a .cs file is newer (or with -Build); -SkipBuild reuses it. Never kills a Godot it did not start.
  Output: .godot\perf_bench\<Label>\<run id>\ (report.json, report.md, driver_report.json, console.log, power_log.csv, stills\). Verdict line PERF_BENCH=PASS / FAIL.
  Exit: 0 pass, 1 fail (a scenario failed, no verdict, a stalled / timed-out / load-failed Godot, or -FailOnRegression and a regression), 2 setup problem or failed pre-launch check, 9 user playing.
  Pre-launch check: before the build and the ticket, res://tools/perf_bench.gd (and its preloads) is parse-checked headless (tools/testing/GpuJob.psm1, no GPU, no queue); a parse error prints PERF_BENCH=FAIL preflight: ... and exits 2.
  Watchdog: Godot runs under Invoke-GodotJob and is killed (its own process tree only) when it prints nothing for -StallMinutes (default 10; job_status stalled), after -TimeoutMin active minutes (timeout) or when the entry script fails to load (load-failed).
  run_context.json carries job_status and job_detail; a status other than exited is a FAIL, but a driver_report.json that exists is still turned into a report first.
  VSync: all windows are VSync off (GPU-bound numbers, comparable with the perf-pass doc); -VsyncOn adds one extra pass of the scenarios marked vsync_pass with VSync as shipped.
  If the monitor is off, VSync-on runs at ~4 fps: the pass is skipped with a note (and the VSync-off runs are unaffected).
  Adaptive windows: standing windows stop early (never later than window_s) once the median is stable (docs/perf-bench.md); such scenarios carry window_actual_s / window_stop / tails_info,
  and their tail metrics (p99, max, hitches, GC) are informational and not compared against a baseline. -Tails runs full windows everywhere.
  Stills (-Stills) are taken in still-capture mode (time scale 0, cloud march held, TAA off); -NoCapture restores the old quiet stills (no clouds, grass, debris; wind off).
#>
param(
  [string]$Label = "",
  [switch]$Quick,
  [string[]]$Scenarios = @(),
  [switch]$VsyncOn,
  [switch]$Stills,
  [switch]$NoCapture,
  [switch]$Tails,
  [string]$Baseline = "",
  [switch]$FailOnRegression,
  [string]$World = "",
  [switch]$Snapshot,
  [switch]$SnapshotOnly,
  [switch]$LiveSettings,
  [string]$Resolution = "1600x900",
  [string]$EngineArgs = "",
  [string]$ScenarioFile = "",
  [string]$SettingsOverlay = "",
  [int]$Rounds = 0,
  [double]$WindowScale = 1.0,
  [switch]$Build,
  [switch]$SkipBuild,
  [int]$TimeoutMin = 90,
  [double]$StallMinutes = 10,
  [string]$RunId = "",
  [string]$ReportFrom = "",
  [string]$Variants = "",
  [switch]$InQueue
)
$ErrorActionPreference = "Stop"
$project = Split-Path -Parent $PSScriptRoot
$name = "RingworldTechDemo"
$real = Join-Path $env:APPDATA "Godot\app_userdata\$name"
$benchWorlds = Join-Path $real "bench_worlds"
# $scenarioPath, not $scenarioFile: PowerShell names are case-insensitive, so that would overwrite the -ScenarioFile parameter.
$scenarioPath = Join-Path $PSScriptRoot "fixtures\perf_bench_scenarios.json"
if ($ScenarioFile) {
  $scenarioPath = if ([System.IO.Path]::IsPathRooted($ScenarioFile)) { $ScenarioFile } else { Join-Path (Split-Path -Parent $PSScriptRoot) $ScenarioFile }
  if (-not (Test-Path -LiteralPath $scenarioPath)) { Write-Host "PERF_BENCH=FAIL scenario file not found: $scenarioPath"; exit 2 }
}
$benchRoot = Join-Path $project ".godot\perf_bench"
$isoRoot = Join-Path $project ".godot\appdata_perf_bench"
$iso = Join-Path $isoRoot "Godot\app_userdata\$name"
$marker = "PERF_BENCH"

function ConvertTo-Hash($o) {
  if ($null -eq $o) { return $null }
  if ($o -is [System.Management.Automation.PSCustomObject]) { $h = @{}; foreach ($p in $o.PSObject.Properties) { $h[$p.Name] = ConvertTo-Hash $p.Value }; return $h }
  if ($o -is [System.Collections.IEnumerable] -and $o -isnot [string]) { return , @($o | ForEach-Object { ConvertTo-Hash $_ }) }
  return $o
}
function Read-JsonHash([string]$path) { return ConvertTo-Hash (Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json) }
function Quote([string]$s) { return "'" + $s.Replace("'", "''") + "'" }
# The scenarios perf_bench.gd _select returns for a --select value, in its order (all / quick / the listed names, in the listed order, a repeated name repeats).
function Select-BenchScenarios($spec, [string]$select) {
  $all = @($spec.scenarios)
  if ($select -eq "all") { return $all }
  if ($select -eq "quick") { return @($all | Where-Object { $_.ContainsKey("quick") -and [bool]$_.quick }) }
  $res = @()
  foreach ($n in ($select -split "," | Where-Object { $_ })) { foreach ($s in $all) { if ([string]$s.name -eq $n) { $res += $s } } }
  return $res
}
function Get-BenchPlanNames($spec, [string]$select) { return @(Select-BenchScenarios $spec $select | ForEach-Object { [string]$_.name }) }
function Get-UserPlaying {
  $playing = @(Get-CimInstance Win32_Process -Filter "Name LIKE 'Godot%'" | Where-Object { $_.CommandLine -match '--remote-debug' })
  return ($playing.Count -gt 0 -or (Test-Path (Join-Path $project ".godot\mono\temp\bin\DebugPlaySaved\$name.dll")))
}
function Get-GitInfo {
  $ErrorActionPreference = "Continue"   # 5.1 turns native stderr (git CRLF warnings) into terminating errors otherwise
  $head = (& git -C $project rev-parse --short HEAD) 2>$null
  $branch = (& git -C $project rev-parse --abbrev-ref HEAD) 2>$null
  $dirty = @(& git -C $project status --porcelain) 2>$null
  $diff = (& git -C $project diff HEAD) 2>$null | Out-String
  $sha = [System.BitConverter]::ToString([System.Security.Cryptography.SHA256]::Create().ComputeHash([System.Text.Encoding]::UTF8.GetBytes($diff))).Replace("-", "").Substring(0, 16).ToLower()
  return @{ head = "$head".Trim(); branch = "$branch".Trim(); dirty_files = @($dirty | ForEach-Object { "$_".Trim() }); dirty_diff_sha16 = $sha }
}
function Get-SettingsValues([string]$dir) {
  $files = @{}
  if (-not (Test-Path $dir)) { return $files }
  foreach ($f in Get-ChildItem $dir -File -Filter *.tres) {
    $values = @{}
    foreach ($line in Get-Content -LiteralPath $f.FullName -Encoding UTF8) {
      if ($line -match '^\s*"?([A-Za-z0-9_]+)"?\s*(?:=|:)\s*(.+?),?\s*$' -and $line -notmatch '^\s*(script|\[)' -and $Matches[2] -ne '{') { $values[$Matches[1]] = $Matches[2] }
    }
    $files[$f.Name] = @{ sha256 = (Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash.Substring(0, 16).ToLower(); values = $values }
  }
  return $files
}

# ------------------------------------------------------------------ the frozen world
function New-BenchSnapshot {
  $liveSlot = Join-Path $real "worlds\slot_3"
  if (-not (Test-Path $liveSlot)) { Write-Host "$marker=FAIL the real profile has no worlds\slot_3 to snapshot"; exit 2 }
  $base = "slot3_" + (Get-Date -Format "yyyy-MM-dd")
  $dir = Join-Path $benchWorlds $base
  $i = 2
  while (Test-Path $dir) { $dir = Join-Path $benchWorlds ("{0}_{1}" -f $base, $i); $i++ }
  Write-Host "Freezing slot 3 as $dir (reads the live slot, writes only below bench_worlds) ..."
  New-Item -ItemType Directory -Force $dir | Out-Null
  Copy-Item -Recurse $liveSlot (Join-Path $dir "world")
  New-Item -ItemType Directory -Force (Join-Path $dir "settings") | Out-Null
  Get-ChildItem $real -File -Filter *.tres | Copy-Item -Destination (Join-Path $dir "settings")
  $state = Get-Content -LiteralPath (Join-Path $dir "world\world_state_surface_runtime_v1.json") -Raw | ConvertFrom-Json
  $recipe = $state.SurfaceGenerationRecipeJson | ConvertFrom-Json
  $worldJson = Get-Content -LiteralPath (Join-Path $dir "world\world.json") -Raw | ConvertFrom-Json
  $meta = [ordered]@{
    name = (Split-Path $dir -Leaf); created_local = (Get-Date).ToString("s"); created_utc = (Get-Date).ToUniversalTime().ToString("s") + "Z"
    source = "worlds/slot_3 of the real profile (read only)"; world_name = "$($worldJson.name)"
    generator_id = "$($recipe.generator_id)"; seed = [long]$recipe.seed; terrain_generator_version = $state.TerrainGeneratorVersion; terrain_definition_hash = "$($state.TerrainDefinitionHash)"
    saved_unix = $state.SavedUnix; save_pose_at_snapshot = $state.Player
    world_state_sha256 = (Get-FileHash -LiteralPath (Join-Path $dir "world\world_state_surface_runtime_v1.json") -Algorithm SHA256).Hash.ToLower()
    world_json_sha256 = (Get-FileHash -LiteralPath (Join-Path $dir "world\world.json") -Algorithm SHA256).Hash.ToLower()
    git_head = (Get-GitInfo).head
    note = "Frozen benchmark world of tools/perf_bench.ps1. Never edit. settings\ holds the user's top-level *.tres files as of the snapshot."
  }
  $meta | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $dir "snapshot.json") -Encoding UTF8
  Write-Host "snapshot: generator $($meta.generator_id), seed $($meta.seed), $($meta.created_local)"
  return $dir
}
function Resolve-BenchWorld {
  if ($World) {
    $w = (Resolve-Path -LiteralPath $World).Path
    if (Test-Path (Join-Path $w "snapshot.json")) { return @{ dir = $w; world = (Join-Path $w "world"); settings = (Join-Path $w "settings"); meta = (Read-JsonHash (Join-Path $w "snapshot.json")) } }
    if (Test-Path (Join-Path $w "world.json")) { return @{ dir = $w; world = $w; settings = ""; meta = @{ name = (Split-Path $w -Leaf); note = "plain world directory (-World)" } } }
    Write-Host "$marker=FAIL -World $w is neither a snapshot (snapshot.json) nor a world directory (world.json)"; exit 2
  }
  $found = @()
  if (Test-Path $benchWorlds) { $found = @(Get-ChildItem $benchWorlds -Directory | Where-Object { Test-Path (Join-Path $_.FullName "snapshot.json") } | Sort-Object Name) }
  if ($Snapshot -or $found.Count -eq 0) { $dir = New-BenchSnapshot } else { $dir = $found[$found.Count - 1].FullName }
  return @{ dir = $dir; world = (Join-Path $dir "world"); settings = (Join-Path $dir "settings"); meta = (Read-JsonHash (Join-Path $dir "snapshot.json")) }
}

# ------------------------------------------------------------------ report
$headline = @("fps", "wall_p50", "wall_p95", "wall_p99", "wall_max", "hitch33_count", "hitch33_worst_ms", "setup_p50", "process_p50", "render_cpu_p50", "gpu_total_p50", "draws_p50", "prims_p50", "vram_mb")
$headlineTitle = @{ fps = "fps"; wall_p50 = "frame p50 ms"; wall_p95 = "frame p95 ms"; wall_p99 = "frame p99 ms"; wall_max = "frame max ms"; hitch33_count = "hitches > 33 ms"; hitch33_worst_ms = "worst hitch ms";
  setup_p50 = "frame setup p50 ms"; process_p50 = "process p50 ms"; render_cpu_p50 = "render CPU p50 ms"; gpu_total_p50 = "GPU p50 ms (all viewports)"; draws_p50 = "draw calls"; prims_p50 = "primitives"; vram_mb = "VRAM MB" }

function Get-Median($vals) {
  $s = @($vals | Sort-Object)
  $n = $s.Count
  if ($n -eq 0) { return 0.0 }
  if ($n % 2 -eq 1) { return [double]$s[($n - 1) / 2] }
  return ([double]$s[$n / 2 - 1] + [double]$s[$n / 2]) / 2.0
}
# floor: the smallest delta that can be called a change (absolute), added to the observed A-A spread by max().
function Get-NoiseFloor([string]$metric, [double]$before) {
  $b = [math]::Abs($before)
  if ($metric -eq "fps") { return [math]::Max(0.5, 0.03 * $b) }
  if ($metric -in @("wall_p95", "wall_p99", "wall_max", "hitch33_worst_ms", "render_cpu_p95", "process_p95", "setup_p95", "gpu_total_p95")) { return [math]::Max(1.0, 0.25 * $b) }
  if ($metric -eq "hitch33_count") { return [math]::Max(3.0, 0.3 * $b) }
  if ($metric -in @("draws_p50", "prims_p50")) { return [math]::Max(1.0, 0.02 * $b) }
  if ($metric -eq "vram_mb") { return 64.0 }   # driver/streaming state moves VRAM by 60 MB between identical runs
  if ($metric -in @("alloc_mb_s", "gc_pause_ms", "gc_gen1", "gc_gen2")) { return [math]::Max(10.0, 0.6 * $b) }
  return 0.3   # ms-valued metrics: medians, per-pass GPU and CPU scope times
}
$informational = @("frames", "speed_mps", "distance_m")
# Window-length dependent: informational (not compared against a baseline) when a window stopped early (adaptive, tails_info).
$tailInfo = @("wall_p99", "wall_max", "hitch33_count", "hitch33_worst_ms", "gc_gen1", "gc_gen2", "gc_pause_ms")

function Get-Metrics($scenario) {
  $metrics = @{}
  $keys = @{}
  foreach ($r in $scenario.rounds) { foreach ($k in $r.m.Keys) { $keys[$k] = $true } }
  foreach ($k in $keys.Keys) {
    $vals = @(); foreach ($r in $scenario.rounds) { if ($r.m.ContainsKey($k)) { $vals += [double]$r.m[$k] } else { $vals += 0.0 } }
    $min = ($vals | Measure-Object -Minimum).Minimum; $max = ($vals | Measure-Object -Maximum).Maximum
    $metrics[$k] = @{ values = $vals; value = (Get-Median $vals); noise = ($max - $min) }
  }
  return $metrics
}
function Format-Num($v, [string]$metric) {
  if ($metric -in @("draws_p50", "hitch33_count", "frames", "gc_gen1", "gc_gen2")) { return ("{0:N0}" -f $v) }
  if ($metric -eq "prims_p50") { return ("{0:N2}M" -f ($v / 1e6)) }
  if ($metric -in @("vram_mb", "speed_mps", "distance_m")) { return ("{0:N0}" -f $v) }
  if ($metric -eq "fps") { return ("{0:N1}" -f $v) }
  return ("{0:N2}" -f $v)
}

function Get-PowerSummary([string]$csv) {
  if (-not (Test-Path $csv)) { return @{ available = $false } }
  $lines = @(Get-Content -LiteralPath $csv)
  if ($lines.Count -lt 3) { return @{ available = $false } }
  $head = @($lines[0].Split(",") | ForEach-Object { $_.Trim() })
  $idx = @{}; for ($i = 0; $i -lt $head.Count; $i++) { $idx[$head[$i]] = $i }
  $rows = @()
  foreach ($l in $lines[1..($lines.Count - 1)]) {
    $c = @($l.Split(",") | ForEach-Object { $_.Trim() })
    if ($c.Count -lt $head.Count) { continue }
    $num = { param($key) $x = $c[$idx[$key]] -replace '[^0-9.\-]', ''; if ($x -eq "") { $null } else { [double]$x } }
    $rows += [pscustomobject]@{ pstate = $c[$idx["pstate"]]; gr = (& $num "clocks.current.graphics [MHz]"); mem = (& $num "clocks.current.memory [MHz]"); power = (& $num "power.draw [W]"); temp = (& $num "temperature.gpu"); util = (& $num "utilization.gpu [%]"); reasons = $c[$idx["clocks_event_reasons.active"]] }
  }
  $busy = @($rows | Where-Object { $_.util -ge 50 })
  if ($busy.Count -eq 0) { $busy = $rows }
  $pst = @{}; foreach ($r in $busy) { $pst[$r.pstate] = 1 + [int]$pst[$r.pstate] }
  $reasons = @{}; foreach ($r in $busy) { $reasons[$r.reasons] = 1 + [int]$reasons[$r.reasons] }
  return @{ available = $true; samples = $rows.Count; busy_samples = $busy.Count; pstate_share = $pst; event_reasons = $reasons
    power_w_median = (Get-Median ($busy | ForEach-Object { $_.power })); power_w_max = ($busy | Measure-Object power -Maximum).Maximum
    gr_mhz_median = (Get-Median ($busy | ForEach-Object { $_.gr })); mem_mhz_median = (Get-Median ($busy | ForEach-Object { $_.mem })); temp_c_max = ($busy | Measure-Object temp -Maximum).Maximum }
}

function Get-Machine([hashtable]$driver) {
  $smi = @{}
  try {
    $q = & nvidia-smi --query-gpu=name,driver_version,pstate,power.limit,enforced.power.limit,display_active,memory.total --format=csv,noheader 2>$null
    $p = @("$q".Split(",") | ForEach-Object { $_.Trim() })
    $smi = @{ gpu = $p[0]; driver = $p[1]; pstate_now = $p[2]; power_limit_w = $p[3]; enforced_power_limit_w = $p[4]; display_active = $p[5]; vram_total = $p[6] }
  } catch { }
  $cpu = Get-CimInstance Win32_Processor | Select-Object -First 1
  $ram = [math]::Round((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB)
  return @{ nvidia_smi = $smi; cpu = "$($cpu.Name)".Trim(); ram_gb = $ram; os = [System.Environment]::OSVersion.VersionString; computer = $env:COMPUTERNAME
    godot_adapter = $driver.machine.adapter; vulkan_api = $driver.machine.api; engine = $driver.machine.engine; display_on = $driver.machine.display_active; refresh_hz = $driver.machine.refresh_hz }
}

function Build-Report([string]$out, [hashtable]$ctx) {
  $drv = Read-JsonHash (Join-Path $out "driver_report.json")
  $rep = [ordered]@{
    label = $ctx.label; run_id = $ctx.run_id; started_local = $ctx.started; mode = $ctx.mode; args = $ctx.args; git = $ctx.git; world = $ctx.world_meta
    settings_source = $ctx.settings_source; settings_files = $ctx.settings
    resolution = $ctx.resolution; state = $drv.state; boot_s = $drv.boot_s; spawn_pose_after_continue = $drv.spawn_pose
    machine = (Get-Machine $drv); power = (Get-PowerSummary (Join-Path $out "power_log.csv"))
    scenarios = [ordered]@{}; stills = $drv.stills; skipped = $drv.skipped; errors = $drv.errors; godot_exit = $ctx.godot_exit
  }
  # Driver kinds whose results are not in "scenarios": carried through unchanged (ab_matrices get a report.md section, reflection_probes none).
  $probes = @($drv["reflection_probes"] | Where-Object { $null -ne $_ }); $matrices = @($drv["ab_matrices"] | Where-Object { $null -ne $_ })
  if ($drv.ContainsKey("reflection_probes")) { $rep["reflection_probes"] = $drv["reflection_probes"] }
  if ($drv.ContainsKey("ab_matrices")) { $rep["ab_matrices"] = $drv["ab_matrices"] }
  foreach ($s in $drv.scenarios) {
    $entry = [ordered]@{ name = $s.name; kind = $s.kind; pose_name = $s.pose_name; pose = $s.pose; vsync = $s.vsync; window_s = $s.window_s; rounds = @($s.rounds).Count; setup = $s.setup }
    if ($s.ContainsKey("error")) { $entry["error"] = $s.error }
    elseif ($s.kind -eq "ssr_fade") { $entry["ssr_fade"] = $s.ssr_fade.summary }
    else {
      $entry["metrics"] = Get-Metrics $s
      $entry["busy_fraction"] = @($s.rounds | ForEach-Object { $_.busy_fraction })
      $entry["monitor_dropped"] = @($s.rounds | ForEach-Object { $_.monitor_dropped })
      $entry["sun_elevation_deg"] = @($s.rounds | ForEach-Object { $_.sun_elevation_deg })
      # Adaptive windows (absent in reports of older drivers: fixed, no tails_info).
      $entry["window_actual_s"] = @($s.rounds | ForEach-Object { [math]::Round([double]$_.seconds, 1) })
      $entry["window_stop"] = @($s.rounds | ForEach-Object { if ($_.ContainsKey("adaptive") -and $_.adaptive -is [System.Collections.IDictionary]) { [string]$_.adaptive.stop } else { "fixed" } })
      $entry["tails_info"] = (@($s.rounds | Where-Object { $_.ContainsKey("tails_info") -and [bool]$_.tails_info }).Count -gt 0)
    }
    $rep.scenarios[$s.name] = $entry
  }
  $verdict = "PASS"; $why = @()
  $expected = @($drv.selected | Where-Object { $null -ne $_ })   # absent when the driver stopped before selecting (plan mismatch)
  foreach ($n in $expected) { if (-not $rep.scenarios.Contains($n) -and @($probes + $matrices | Where-Object { $_.name -eq $n }).Count -eq 0) { $verdict = "FAIL"; $why += "scenario $n did not run" } }
  foreach ($n in $rep.scenarios.Keys) { if ($rep.scenarios[$n].Contains("error")) { $verdict = "FAIL"; $why += "$n : $($rep.scenarios[$n].error)" } }
  foreach ($e in @($probes + $matrices)) { if ($e.ContainsKey("error")) { $verdict = "FAIL"; $why += "$($e.name) : $($e.error)" } }
  foreach ($st in @($drv.stills)) { if ($st.ContainsKey("error")) { $verdict = "FAIL"; $why += "still $($st.name): $($st.error)" } }
  foreach ($e in @($drv.errors)) { $verdict = "FAIL"; $why += "$e" }
  if (-not $drv.ContainsKey("selected") -or @($drv.scenarios).Count + $probes.Count + $matrices.Count -eq 0) { $verdict = "FAIL"; $why += "driver reported no scenarios" }
  $rep["verdict"] = $verdict; $rep["verdict_reasons"] = $why
  return $rep
}

function Add-Baseline($rep, [string]$baselineReportPath) {
  $base = Read-JsonHash $baselineReportPath
  $cmp = [ordered]@{ baseline_label = $base.label; baseline_run_id = $base.run_id; baseline_git = $base.git; warnings = @(); scenarios = [ordered]@{}; counts = @{ improved = 0; regressed = 0; within_noise = 0 } }
  if ($base.world.name -ne $rep.world.name -or $base.world.seed -ne $rep.world.seed) { $cmp.warnings += "WORLD MISMATCH: baseline $($base.world.name) vs now $($rep.world.name); deltas are not comparable" }
  if ($base.resolution -ne $rep.resolution) { $cmp.warnings += "RESOLUTION MISMATCH: baseline $($base.resolution) vs now $($rep.resolution)" }
  if ($base.machine.nvidia_smi.gpu -ne $rep.machine.nvidia_smi.gpu) { $cmp.warnings += "GPU MISMATCH: baseline $($base.machine.nvidia_smi.gpu) vs now $($rep.machine.nvidia_smi.gpu)" }
  $sameSettings = $true
  foreach ($f in $rep.settings_files.Keys) { if (-not $base.settings_files.ContainsKey($f) -or $base.settings_files[$f].sha256 -ne $rep.settings_files[$f].sha256) { $sameSettings = $false } }
  if (-not $sameSettings) { $cmp.warnings += "SETTINGS DIFFER between the baseline and this run (see settings_files); deltas include the settings change" }
  if ($base.git.head -eq $rep.git.head -and $base.git.dirty_diff_sha16 -ne $rep.git.dirty_diff_sha16) { $cmp.warnings += "same git HEAD but the uncommitted changes differ (dirty_diff_sha16 $($base.git.dirty_diff_sha16) -> $($rep.git.dirty_diff_sha16))" }
  foreach ($n in $rep.scenarios.Keys) {
    $cur = $rep.scenarios[$n]
    if (-not $base.scenarios.ContainsKey($n) -or -not $cur.Contains("metrics") -or -not $base.scenarios[$n].ContainsKey("metrics")) { $cmp.warnings += "scenario $n has no baseline counterpart"; continue }
    $bm = $base.scenarios[$n].metrics
    $skipTails = ($cur.Contains("tails_info") -and [bool]$cur.tails_info) -or ($base.scenarios[$n].ContainsKey("tails_info") -and [bool]$base.scenarios[$n].tails_info)
    if ($skipTails) { $cmp.warnings += "${n}: adaptive window, tails informational (p99, max, hitches, GC not compared)" }
    $rows = @()
    foreach ($k in $cur.metrics.Keys) {
      if ($k -in $informational -or -not $bm.ContainsKey($k)) { continue }
      if ($skipTails -and $k -in $tailInfo) { continue }
      $b = [double]$bm[$k].value; $a = [double]$cur.metrics[$k].value
      $noise = [math]::Max([math]::Max([double]$bm[$k].noise, [double]$cur.metrics[$k].noise), (Get-NoiseFloor $k $b))
      $delta = $a - $b
      $verdict = "within noise"
      if ([math]::Abs($delta) -gt $noise) {
        $better = if ($k -eq "fps") { $delta -gt 0 } else { $delta -lt 0 }
        $verdict = if ($better) { "improved" } else { "regressed" }
      }
      $rows += [pscustomobject]@{ metric = $k; before = $b; after = $a; delta = $delta; noise = $noise; verdict = $verdict }
    }
    foreach ($r in $rows) { $cmp.counts[($r.verdict -replace " ", "_")] += 1 }
    $cmp.scenarios[$n] = @($rows)
  }
  $rep["baseline"] = $cmp
}

function Add-StillsCompare($rep, [string]$out, [string]$baselineOut) {
  $res = @()
  $tool = Join-Path $project "tools\media\ffmpeg_tools.ps1"
  foreach ($st in @($rep.stills)) {
    if ($st.ContainsKey("error")) { continue }
    $bp = Join-Path $baselineOut "stills\$($st.name).png"
    $row = @{ name = $st.name; still = $st.path; baseline_still = $bp }
    if (-not (Test-Path $bp)) { $row["note"] = "baseline has no still for this pose"; $res += $row; continue }
    $heat = Join-Path $out "stills\$($st.name)_vs_baseline_heatmap.png"
    try {
      $text = (& $tool compare -Path $st.path -Reference $bp -Output $heat 2>&1 | Out-String)
      $row["ffmpeg_output"] = $text.Trim()
      if ($text -match '(?i)ssim[^0-9\r\n]*([0-9]*\.[0-9]+)') { $row["ssim"] = [double]$Matches[1] }
      if ($text -match '(?i)psnr[^0-9\r\n]*([0-9]*\.?[0-9]+)') { $row["psnr"] = [double]$Matches[1] }
      $row["heatmap"] = $heat
    } catch { $row["note"] = "compare failed: $($_.Exception.Message)" }
    $res += $row
  }
  $rep["stills_compare"] = @($res)
}

function Write-ReportMd($rep, [string]$path) {
  $L = New-Object System.Collections.Generic.List[string]
  $L.Add("# Perf bench: $($rep.label) ($($rep.run_id))"); $L.Add("")
  $L.Add("Verdict: **$($rep.verdict)**" + $(if ($rep.verdict_reasons.Count -gt 0) { " (" + ($rep.verdict_reasons -join "; ") + ")" } else { "" })); $L.Add("")
  $L.Add("## Setup"); $L.Add("")
  $m = $rep.machine; $p = $rep.power
  $L.Add("- Machine: $($m.nvidia_smi.gpu), driver $($m.nvidia_smi.driver), power limit $($m.nvidia_smi.enforced_power_limit_w); $($m.cpu); $($m.ram_gb) GB RAM; $($m.os); Vulkan $($m.vulkan_api); $($m.engine); display on: $($m.display_on); $($m.refresh_hz) Hz")
  if ($p.available) { $L.Add("- GPU state while busy ($($p.busy_samples) samples): P-state share $(($p.pstate_share.Keys | ForEach-Object { "$_ $([math]::Round(100.0 * $p.pstate_share[$_] / $p.busy_samples))%" }) -join ', '), power median $([math]::Round($p.power_w_median,1)) W (max $($p.power_w_max)), graphics clock median $($p.gr_mhz_median) MHz, memory clock median $($p.mem_mhz_median) MHz, max temperature $($p.temp_c_max) C, throttle reasons $(($p.event_reasons.Keys | ForEach-Object { $_ }) -join ' / ')") }
  $s = $rep.state
  $L.Add("- Resolution $($rep.resolution) (window $($s.window), 3D scale $($s.scale_3d)), preset $($s.preset), shadow atlas $($s.atlas), SSAO $($s.ssao), SSR $($s.ssr), AA: msaa $($s.msaa) / taa $($s.taa) / screen $($s.screen_aa), VSync off for the windows, max_fps 0, monitor detailed GPU $($s.monitor.detailed_gpu)")
  if ($s.trees) { $L.Add("- Tree LOD in the game: near detail $($s.trees.near_detail_start_m) to $($s.trees.near_detail_end_m) m, mid detail from $($s.trees.mid_detail_start_m) m, billboards $($s.trees.billboard_start_m) to $($s.trees.billboard_end_m) m, tree sun shadows to $($s.trees.sun_shadow_m) m") }
  $w = $rep.world
  $L.Add("- World: snapshot **$($w.name)** (frozen $($w.created_local)), generator $($w.generator_id), seed $($w.seed), terrain generator v$($w.terrain_generator_version); never the live slot")
  $g = $rep.git
  $L.Add("- Git: $($g.branch) @ **$($g.head)**, $(@($g.dirty_files).Count) uncommitted entries (diff sha $($g.dirty_diff_sha16))" + $(if (@($g.dirty_files).Count -gt 0) { ": " + (($g.dirty_files | Select-Object -First 12) -join ", ") } else { "" }))
  $L.Add("- Mode: $($rep.mode); boot to play $([math]::Round($rep.boot_s,1)) s"); $L.Add("")
  $L.Add("Settings used ($($rep.settings_source)):"); $L.Add("")
  foreach ($f in ($rep.settings_files.Keys | Sort-Object)) {
    $vals = $rep.settings_files[$f].values
    if ($vals.Count -eq 0) { $L.Add("- ``$f`` ($($rep.settings_files[$f].sha256)): shipped defaults (no overrides)"); continue }
    $L.Add("- ``$f`` ($($rep.settings_files[$f].sha256)): " + (($vals.Keys | Sort-Object | ForEach-Object { "$_=$($vals[$_])" }) -join ", "))
  }
  $L.Add("")
  # summary
  $L.Add("## Summary per scenario"); $L.Add("")
  $L.Add("Median of the A-A rounds; the number after +/- is the spread between the rounds (the noise floor of that metric). Windows: VSync off, clocks running, streaming idle before measuring."); $L.Add("")
  $L.Add("| scenario | rounds x s | fps | frame p50 | p95 | p99 | max | hitches > 33 ms (worst) | setup p50 | process p50 | render CPU p50 | GPU p50 | draws | prims | VRAM MB |"); $L.Add("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|")
  $anyAdaptive = $false
  foreach ($n in $rep.scenarios.Keys) {
    $sc = $rep.scenarios[$n]
    if ($sc.Contains("error")) { $L.Add("| $n | | FAILED: $($sc.error) |||||||||||||"); continue }
    if (-not $sc.Contains("metrics")) { continue }
    $mm = $sc.metrics
    $cell = { param($k) "{0} +/- {1}" -f (Format-Num $mm[$k].value $k), (Format-Num $mm[$k].noise $k) }
    $info = if ($sc.Contains("tails_info") -and [bool]$sc.tails_info) { " (info)" } else { "" }
    $rx = "$($sc.rounds) x $([math]::Round($sc.window_s))"
    if ($sc.Contains("window_stop") -and @($sc.window_stop | Where-Object { $_ -ne "fixed" }).Count -gt 0) {
      $act = @($sc.window_actual_s | Measure-Object -Minimum -Maximum)
      $rx = "$($sc.rounds) x " + ("{0:N1}..{1:N1} of {2:N0} (adaptive)" -f $act.Minimum, $act.Maximum, $sc.window_s)
      $anyAdaptive = $true
    }
    $L.Add("| $n | $rx | $(& $cell 'fps') | $(& $cell 'wall_p50') | $(& $cell 'wall_p95') | $(& $cell 'wall_p99')$info | $(& $cell 'wall_max')$info | $(Format-Num $mm.hitch33_count.value 'hitch33_count') ($(Format-Num $mm.hitch33_worst_ms.value 'x') ms)$info | $(& $cell 'setup_p50') | $(& $cell 'process_p50') | $(& $cell 'render_cpu_p50') | $(& $cell 'gpu_total_p50') | $(Format-Num $mm.draws_p50.value 'draws_p50') | $(Format-Num $mm.prims_p50.value 'prims_p50') | $(Format-Num $mm.vram_mb.value 'vram_mb') |")
  }
  $L.Add("")
  if ($anyAdaptive) { $L.Add("Adaptive windows: a standing window stops once the median is stable (never longer than the cap, the number after of); (info) tails of such scenarios are informational and not compared against a baseline. ``-Tails`` runs full windows."); $L.Add("") }
  $flights = @($rep.scenarios.Keys | Where-Object { $rep.scenarios[$_].kind -eq "fly" -and -not $rep.scenarios[$_].Contains("error") })
  if ($flights.Count -gt 0) {
    $L.Add("Flights: " + (($flights | ForEach-Object { "$_ mean speed $([math]::Round($rep.scenarios[$_].metrics.speed_mps.value)) m/s, $([math]::Round($rep.scenarios[$_].metrics.distance_m.value / 1000.0, 1)) km flown; GC gen2 $([math]::Round($rep.scenarios[$_].metrics.gc_gen2.value)), allocated $([math]::Round($rep.scenarios[$_].metrics.alloc_mb_s.value)) MB/s" }) -join " | ")); $L.Add("")
  }
  $ssrNames = @($rep.scenarios.Keys | Where-Object { $rep.scenarios[$_].Contains("ssr_fade") })
  if ($ssrNames.Count -gt 0) {
    $L.Add("## SSR fade"); $L.Add("")
    foreach ($n in $ssrNames) {
      $f = $rep.scenarios[$n].ssr_fade
      $L.Add("- ${n}: max frame $("{0:N2}" -f [double]$f.max_frame_ms) ms, median $("{0:N2}" -f [double]$f.median_frame_ms) ms, frames where ssr_enabled flipped: $((@($f.flip_frame_ms) | ForEach-Object { "{0:N2}" -f [double]$_ }) -join ', ') ms, toggles $($f.toggle_count_before) -> $($f.toggle_count_after); weight stills: $(@($f.stills).Count) ($(@($f.stills) -join ', ')); frame dir: $($f.frame_dir)")
    }
    $L.Add("")
  }
  # per-pass GPU and key scopes matrices
  $names = @($rep.scenarios.Keys | Where-Object { -not $rep.scenarios[$_].Contains("error") -and $rep.scenarios[$_].Contains("metrics") })
  foreach ($grp in @(@{ prefix = "gpu."; title = "GPU per pass (ms per frame, RingPerformanceGpu detailed intervals; nested stages overlap their parents, never sum)"; min = 0.01 },
      @{ prefix = "vp."; title = "GPU per viewport (p50 ms)"; min = 0.01 }, @{ prefix = "cpu."; title = "Key CPU scopes on the main thread (ms per frame)"; min = -1 }, @{ prefix = "worker."; title = "Worker-thread scopes (ms of work per frame, not frame-critical)"; min = 0.3 })) {
    $keys = @{}
    foreach ($n in $names) { foreach ($k in $rep.scenarios[$n].metrics.Keys) { if ($k.StartsWith($grp.prefix) -and -not $k.EndsWith(".cpu_p50") -and -not $k.StartsWith("gpu_sample.")) { $v = $rep.scenarios[$n].metrics[$k].value; if ($v -gt $keys[$k]) { $keys[$k] = $v } } } }
    $rows = @($keys.Keys | Where-Object { $keys[$_] -ge $grp.min } | Sort-Object { -$keys[$_] } | Select-Object -First 30)
    if ($rows.Count -eq 0) { continue }
    $L.Add("## $($grp.title)"); $L.Add("")
    $L.Add("| " + $grp.prefix.TrimEnd(".") + " | " + ($names -join " | ") + " |"); $L.Add("|---|" + (($names | ForEach-Object { "---" }) -join "|") + "|")
    foreach ($k in $rows) { $L.Add("| $(($k.Substring($grp.prefix.Length)) -replace '\.gpu_p50$', '') | " + (($names | ForEach-Object { $mt = $rep.scenarios[$_].metrics[$k]; if ($mt) { "{0:N2}" -f $mt.value } else { "-" } }) -join " | ") + " |") }
    $L.Add("")
  }
  # baseline
  if ($rep.Contains("baseline")) {
    $b = $rep.baseline
    $L.Add("## Deltas against baseline $($b.baseline_label) ($($b.baseline_run_id), git $($b.baseline_git.head))"); $L.Add("")
    foreach ($wn in $b.warnings) { $L.Add("- WARNING: $wn") }
    $L.Add("- Verdicts over every compared metric: improved $($b.counts.improved), regressed $($b.counts.regressed), within noise $($b.counts.within_noise). Noise = the larger of the two A-A spreads and a per-metric floor (0.3 ms for medians and passes, 1 ms or 25 % for tails, 3 % for fps)."); $L.Add("")
    foreach ($n in $b.scenarios.Keys) {
      $L.Add("### $n"); $L.Add("")
      $L.Add("| metric | before | after | delta | noise floor | verdict |"); $L.Add("|---|---|---|---|---|---|")
      $rows = @($b.scenarios[$n])
      $pick = @($rows | Where-Object { $_.metric -in $headline })
      $pick = @($headline | ForEach-Object { $mname = $_; $pick | Where-Object { $_.metric -eq $mname } })
      $extra = @($rows | Where-Object { $_.metric -notin $headline -and $_.verdict -ne "within noise" } | Sort-Object { - [math]::Abs($_.delta) } | Select-Object -First 12)
      foreach ($r in @($pick + $extra)) {
        if ($null -eq $r) { continue }
        $title = if ($headlineTitle.ContainsKey($r.metric)) { $headlineTitle[$r.metric] } else { $r.metric }
        $L.Add("| $title | $(Format-Num $r.before $r.metric) | $(Format-Num $r.after $r.metric) | $(if ($r.delta -ge 0) { '+' })$(Format-Num $r.delta $r.metric) | $(Format-Num $r.noise $r.metric) | $($r.verdict) |")
      }
      $L.Add("")
    }
  }
  $abm = @($rep["ab_matrices"] | Where-Object { $null -ne $_ })
  if ($abm.Count -gt 0) {
    $inv = [System.Globalization.CultureInfo]::InvariantCulture
    $f2 = { param($x) [string]::Format($inv, "{0:0.00}", [double]$x) }
    $sg = { param($x) [string]::Format($inv, "{0:+0.00;-0.00;0.00}", [double]$x) }
    $L.Add("## A/B matrices (in-session, A-X-A)"); $L.Add("")
    $L.Add("One game boot, one placement: per round A0 X1 A1 X2 A2 ... (A = baseline, X = a variant, each switch settled and undone). The delta of a variant is X minus the mean of the two A windows around it, so drift cancels. A-A spread = noise floor of this session: a delta is only a change when it is beyond it (tools/perf_bench_ab_matrix.gd).")
    $L.Add("")
    foreach ($e in $abm) {
      $L.Add("### $($e.name) at $($e.pose_name)"); $L.Add("")
      if ($e.Contains("error")) { $L.Add("FAILED: $($e.error)"); $L.Add(""); continue }
      $L.Add("Window $([math]::Round([double]$e.window_s, 1)) s, $($e.rounds) round(s).")
      $ml = @($e.metrics_list); $bl = $e.baseline
      $has = { param($m) ($ml -contains $m) -and $bl -is [System.Collections.IDictionary] -and $bl.Contains($m) }
      $gpuSpread = $null; $wallSpread = $null
      if (& $has "gpu_total_p50") { $gpuSpread = [double]$bl["gpu_total_p50"].spread }
      if (& $has "wall_p50") { $wallSpread = [double]$bl["wall_p50"].spread }
      $bg = if ($null -ne $gpuSpread) { "GPU p50 $(& $f2 $bl['gpu_total_p50'].median) ms, spread $(& $f2 $gpuSpread) ms" } else { "GPU p50 -" }
      $bw = if ($null -ne $wallSpread) { "wall p50 $(& $f2 $bl['wall_p50'].median) ms, spread $(& $f2 $wallSpread) ms" } else { "wall p50 -" }
      $nA = if (& $has "gpu_total_p50") { @($bl["gpu_total_p50"].values).Count } else { 0 }
      $L.Add(""); $L.Add("Baseline (A, $nA windows): median $bg; $bw (A-A spread = noise floor of this session).")
      $L.Add("")
      $L.Add("| Variant | dGPU p50 ms (median, min..max) | dwall p50 ms | dwall p95 ms | drender CPU p50 ms | ddraws | beyond floor |"); $L.Add("|---|---|---|---|---|---|---|")
      foreach ($v in @($e.variants)) {
        if ($v.Contains("error")) { $L.Add("| $($v.name) | error: $($v.error) |||||| "); continue }
        $dm = { param($m) if (($ml -contains $m) -and $v.delta -is [System.Collections.IDictionary] -and $v.delta.Contains($m)) { $v.delta[$m] } else { $null } }
        $g = & $dm "gpu_total_p50"; $w50 = & $dm "wall_p50"; $w95 = & $dm "wall_p95"; $rc = & $dm "render_cpu_p50"; $dr = & $dm "draws_p50"
        $cG = if ($g) { "$(& $sg $g.median) ($(& $sg $g.min)..$(& $sg $g.max))" } else { "-" }
        $cm = { param($d) if ($d) { & $sg $d.median } else { "-" } }
        $beyond = "-"
        if (($g -and $null -ne $gpuSpread) -or ($w50 -and $null -ne $wallSpread)) {
          $by = ($g -and $null -ne $gpuSpread -and [math]::Abs([double]$g.median) -gt $gpuSpread) -or ($w50 -and $null -ne $wallSpread -and [math]::Abs([double]$w50.median) -gt $wallSpread)
          $beyond = if ($by) { "yes" } else { "no" }
        }
        $L.Add("| $($v.name) | $cG | $(& $cm $w50) | $(& $cm $w95) | $(& $cm $rc) | $(& $cm $dr) | $beyond |")
      }
      $L.Add("")
      if ($e.Contains("stills") -and $e.stills -is [System.Collections.IDictionary] -and $e.stills.Count -gt 0) {
        $L.Add("Stills:"); $L.Add("")
        foreach ($k in $e.stills.Keys) { $L.Add("- ${k}: $($e.stills[$k])") }
        $L.Add("")
      }
      if ($e.Contains("stills_error")) { $L.Add("Stills FAILED: $($e.stills_error)"); $L.Add("") }
      if ($e.Contains("stills_failed_variants")) { $L.Add("Stills not taken for: $(@($e.stills_failed_variants) -join '; ')"); $L.Add("") }
    }
  }
  if ($rep.stills -and @($rep.stills).Count -gt 0) {
    $L.Add("## Stills"); $L.Add("")
    $L.Add("One still per standing pose, HUD hidden, in ``$(Split-Path (Split-Path $rep.stills[0].path -Parent) -Parent)\stills``. Capture mode (default): time scale 0, cloud march held, TAA off, wind frozen, clouds, grass and gust debris visible. Legacy (-NoCapture): quiet scene (clouds, grass, debris hidden, wind off). Stills of the two modes are not comparable.")
    $L.Add("")
    foreach ($st in @($rep.stills)) {
      $how = "legacy (no capture)"
      if ($st.ContainsKey("capture") -and $st.capture -is [System.Collections.IDictionary]) {
        $cp = $st.capture
        $how = if ($cp.ContainsKey("deterministic") -and [bool]$cp.deterministic) { "capture: deterministic, $($cp.frames_waited) frames" } else { "capture fallback (not deterministic, $($cp.last_pair_differing_px) px)" }
      }
      $L.Add("- $($st.name): $how")
    }
    $L.Add("")
    if ($rep.Contains("stills_compare")) {
      $L.Add("| pose | SSIM vs baseline | PSNR | heatmap |"); $L.Add("|---|---|---|---|")
      foreach ($c in $rep.stills_compare) { $L.Add("| $($c.name) | $(if ($c.ContainsKey('ssim')) { $c.ssim } else { $c.note }) | $(if ($c.ContainsKey('psnr')) { $c.psnr } else { '' }) | $($c.heatmap) |") }
      $L.Add(""); $L.Add("Capture-mode stills keep the clouds, which move between two runs (the clocks run until the still), so identical code gives SSIM about 0.986 to 0.996 with differences in the sky; quiet (-NoCapture) stills of identical scenes are near 1.0. Judge the heatmap, not only the number (visual-golden-check skill).")
    } else { $L.Add("(no baseline: nothing compared)") }
    $L.Add("")
  }
  if (@($rep.skipped).Count -gt 0) { $L.Add("Skipped: " + ($rep.skipped -join "; ")); $L.Add("") }
  Set-Content -LiteralPath $path -Value ($L -join "`n") -Encoding UTF8
}

function Save-Report($rep, [string]$out) {
  $rep | ConvertTo-Json -Depth 14 | Set-Content -LiteralPath (Join-Path $out "report.json") -Encoding UTF8
  Write-ReportMd $rep (Join-Path $out "report.md")
}
function Find-BaselineRun([string]$lbl) {
  $dir = Join-Path $benchRoot $lbl
  if (-not (Test-Path $dir)) { return $null }
  $run = Get-ChildItem $dir -Directory | Where-Object { Test-Path (Join-Path $_.FullName "report.json") } | Sort-Object Name | Select-Object -Last 1
  return $run
}

# ------------------------------------------------------------------ -ReportFrom (no game)
if ($ReportFrom) {
  $out = (Resolve-Path -LiteralPath $ReportFrom).Path
  $oldPath = Join-Path $out "report.json"
  if (Test-Path $oldPath) { $old = Read-JsonHash $oldPath } else {
    $wi = Resolve-BenchWorld
    $old = @{ label = (Split-Path (Split-Path $out -Parent) -Leaf); run_id = (Split-Path $out -Leaf); started_local = ""; mode = "rebuilt"; args = @{}; git = (Get-GitInfo); world = $wi.meta
      settings_source = "unknown (report rebuilt)"; settings_files = (Get-SettingsValues $wi.settings); resolution = $Resolution; godot_exit = 0 }
  }
  $ctx = @{ label = $old.label; run_id = $old.run_id; started = $old.started_local; mode = $old.mode; args = $old.args; git = $old.git; world_meta = $old.world; settings_source = $old.settings_source
    settings = $old.settings_files; resolution = $old.resolution; godot_exit = $old.godot_exit }
  $rep = Build-Report $out $ctx
  if ($Baseline) { $b = Find-BaselineRun $Baseline; if ($b) { Add-Baseline $rep (Join-Path $b.FullName "report.json"); if ($Stills) { Add-StillsCompare $rep $out $b.FullName } } else { Write-Host "no baseline run under $benchRoot\$Baseline" } }
  Save-Report $rep $out
  Write-Host "report rebuilt: $out\report.md"
  exit 0
}

# ------------------------------------------------------------------ checks, snapshot
if (-not $Label -and -not $SnapshotOnly) { Write-Host "$marker=FAIL -Label is required"; exit 2 }
if (-not (Test-Path (Join-Path $real "worlds\slot_3")) -and -not $World) { Write-Host "$marker=FAIL the real profile has no worlds\slot_3 and no snapshot exists"; exit 2 }
if (Get-UserPlaying) { Write-Host "USER_PLAYING"; Write-Host "$marker=FAIL the user is playing (a Godot with --remote-debug, or bin\DebugPlaySaved holds an assembly); exit 9"; exit 9 }
$spec = Read-JsonHash $scenarioPath
$allNames = @($spec.scenarios | ForEach-Object { $_.name })
foreach ($n in $Scenarios) { foreach ($x in ($n -split ",")) { if ($x -and $x -notin $allNames) { Write-Host "$marker=FAIL unknown scenario '$x'. Known: $($allNames -join ', ')"; exit 2 } } }
$Scenarios = @($Scenarios | ForEach-Object { $_ -split "," } | Where-Object { $_ })
if ($Quick -and $Scenarios.Count -gt 0) { Write-Host "$marker=FAIL use -Quick or -Scenarios, not both"; exit 2 }

# ---- -Variants (outer part only): validate, keep the standing scenarios, derive one ab_matrix scenario per kept one (written once the run folder exists)
$variantsSource = ""; $derivedSpec = $null; $argQuick = [bool]$Quick; $argScenarios = $Scenarios; $argScenarioFile = $ScenarioFile
if ($Variants -and -not $InQueue) {
  function Fail-Variants([string]$why) { Write-Host "$marker=FAIL -Variants: $why"; exit 2 }
  $vText = $Variants.Trim()
  if ($vText.StartsWith("{") -or $vText.StartsWith("[")) { $variantsSource = "inline" }
  else {
    $vPath = if ([System.IO.Path]::IsPathRooted($vText)) { $vText } else { Join-Path $project $vText }
    if (-not (Test-Path -LiteralPath $vPath)) { Fail-Variants "file not found: $vPath" }
    $variantsSource = $vPath; $vText = Get-Content -LiteralPath $vPath -Raw -Encoding UTF8
  }
  try { $vRaw = ConvertTo-Hash (ConvertFrom-Json -InputObject $vText) } catch { Fail-Variants "invalid JSON: $($_.Exception.Message)" }
  # A one-element array can arrive unrolled (Windows PowerShell 5.1): a lone {name, ops} object is that variant.
  if ($vRaw -is [System.Collections.IDictionary] -and -not $vRaw.ContainsKey("variants") -and $vRaw.ContainsKey("name")) { $vRaw = @($vRaw) }
  $vOpts = @{}
  if ($vRaw -is [System.Collections.IDictionary]) { $vOpts = $vRaw; $vList = @($vRaw["variants"] | Where-Object { $null -ne $_ }) } else { $vList = @($vRaw | Where-Object { $null -ne $_ }) }
  if ($vList.Count -eq 0) { Fail-Variants "no variants" }
  $seen = @{}
  foreach ($v in $vList) {
    if ($v -isnot [System.Collections.IDictionary]) { Fail-Variants "a variant is not an object" }
    $vn = if ($v.ContainsKey("name")) { [string]$v["name"] } else { "" }
    if (-not $vn) { Fail-Variants "a variant has no name" }
    if ($vn -notmatch '^[A-Za-z0-9_.-]+$') { Fail-Variants "variant name '$vn' must match ^[A-Za-z0-9_.-]+$" }
    if ($seen.ContainsKey($vn)) { Fail-Variants "duplicate variant name '$vn'" }
    $seen[$vn] = 1
    if (-not $v.ContainsKey("ops") -or $v["ops"] -isnot [System.Array]) { Fail-Variants "variant '$vn': ops must be an array" }
    $oi = 0
    foreach ($op in $v["ops"]) {
      if ($op -isnot [System.Collections.IDictionary]) { Fail-Variants "variant '$vn': op $oi is not an object" }
      $known = @("env", "sky", "node", "ssr_half_size") | Where-Object { $op.ContainsKey($_) }
      if (@($known).Count -eq 0) { Fail-Variants "variant '$vn': op $oi has none of env, sky, node, ssr_half_size (keys: $(@($op.Keys) -join ', '))" }
      if ($op.ContainsKey("node") -and -not ($op.ContainsKey("set") -or $op.ContainsKey("call"))) { Fail-Variants "variant '$vn': op $oi has node but neither set nor call" }
      $oi++
    }
  }
  if ($vOpts.ContainsKey("baseline")) {
    if ($vOpts["baseline"] -isnot [System.Array]) { Fail-Variants "baseline must be an array of ops" }
    $oi = 0
    foreach ($op in $vOpts["baseline"]) {
      if ($op -isnot [System.Collections.IDictionary]) { Fail-Variants "baseline op $oi is not an object" }
      if (@("env", "sky", "node", "ssr_half_size" | Where-Object { $op.ContainsKey($_) }).Count -eq 0) { Fail-Variants "baseline op $oi has none of env, sky, node, ssr_half_size" }
      if ($op.ContainsKey("node") -and -not ($op.ContainsKey("set") -or $op.ContainsKey("call"))) { Fail-Variants "baseline op $oi has node but neither set nor call" }
      $oi++
    }
  }
  $vSel = if ($Quick) { "quick" } elseif ($Scenarios.Count -gt 0) { $Scenarios -join "," } else { "all" }
  $vSelected = @(Select-BenchScenarios $spec $vSel)
  $vStand = @(); $vDropped = @()
  foreach ($s in $vSelected) {
    if (-not $s.ContainsKey("kind")) { Fail-Variants "scenario '$($s.name)' has no kind" }
    if ([string]$s.kind -eq "stand") { $vStand += $s } else { $vDropped += "$($s.name) ($($s.kind))" }
  }
  if ($vDropped.Count -gt 0) { Write-Host "-Variants: dropped (not standing): $($vDropped -join ', ')" }
  if ($vStand.Count -eq 0) { Fail-Variants "none of the selected scenarios is a standing one (kind stand); selected: $(@($vSelected | ForEach-Object { $_.name }) -join ', ')" }
  $derivedScenarios = @()
  foreach ($s in $vStand) {
    $d = [ordered]@{ name = "$($s.name)__ab"; kind = "ab_matrix" }
    foreach ($k in $s.Keys) { if ($k -notin @("name", "kind", "quick", "still", "vsync_pass")) { $d[$k] = $s[$k] } }
    $d["window_s"] = if ($vOpts.ContainsKey("window_s")) { $vOpts["window_s"] } elseif ($s.ContainsKey("window_s")) { $s["window_s"] } else { $spec.defaults.window_s }
    $d["rounds"] = if ($vOpts.ContainsKey("rounds")) { $vOpts["rounds"] } elseif ($Rounds -gt 0) { $Rounds } else { 2 }
    $d["hold_clocks"] = if ($vOpts.ContainsKey("hold_clocks")) { [bool]$vOpts["hold_clocks"] } else { $true }
    $vs = if ($vOpts.ContainsKey("variant_settle_s")) { $vOpts["variant_settle_s"] } elseif ($vOpts.ContainsKey("settle_s")) { $vOpts["settle_s"] } else { $null }
    if ($null -ne $vs) { $d["variant_settle_s"] = $vs }
    $d["stills"] = if ($vOpts.ContainsKey("stills")) { [bool]$vOpts["stills"] } else { [bool]$Stills }
    $d["baseline"] = @(); if ($vOpts.ContainsKey("baseline")) { $d["baseline"] = @($vOpts["baseline"]) }   # not an if-expression: a one-op array would unroll
    $d["variants"] = @($vList)
    $derivedScenarios += $d
  }
  $derivedSpec = [ordered]@{}
  foreach ($k in $spec.Keys) { if ($k -ne "scenarios") { $derivedSpec[$k] = $spec[$k] } }
  $derivedSpec["scenarios"] = $derivedScenarios
  if ($Baseline) { Write-Host "WARNING: -Variants with -Baseline: ab_matrix results are not compared with a baseline." }
  # From here on the run is a plain run of the derived file with everything selected (the ab_matrix scenarios take their own stills).
  $Quick = $false; $Scenarios = @()
}

$monoTemp = Join-Path $project ".godot\mono\temp"
$optDir = Join-Path $monoTemp "bin\OptRelease"
$optDll = Join-Path $optDir "$name.dll"; $optPdb = Join-Path $optDir "$name.pdb"

if (-not $InQueue) {
  $worldInfo = Resolve-BenchWorld   # may create the snapshot (reads the live slot; before the queue, no GPU needed)
  if ($SnapshotOnly) { Write-Host "snapshot ready: $($worldInfo.dir)"; exit 0 }
  if (-not $RunId) { $RunId = Get-Date -Format "yyyyMMdd_HHmmss" }
  $baseRun = $null
  if ($Baseline) { $baseRun = Find-BaselineRun $Baseline; if (-not $baseRun) { Write-Host "$marker=FAIL no baseline run with a report.json under $benchRoot\$Baseline"; exit 2 } }
  if ($derivedSpec) {
    $baseRun = $null
    $vOut = Join-Path $benchRoot "$Label\$RunId"
    New-Item -ItemType Directory -Force $vOut | Out-Null
    $scenarioPath = Join-Path $vOut "variants_scenarios.json"
    $ScenarioFile = $scenarioPath
    [System.IO.File]::WriteAllText($scenarioPath, ($derivedSpec | ConvertTo-Json -Depth 30), (New-Object System.Text.UTF8Encoding($false)))
    $spec = Read-JsonHash $scenarioPath
    Write-Host "-Variants: derived scenario file $scenarioPath"
  }
  if ($SettingsOverlay) {
    $overlayDir = if ([System.IO.Path]::IsPathRooted($SettingsOverlay)) { $SettingsOverlay } else { Join-Path $project $SettingsOverlay }
    if (-not (Test-Path -LiteralPath $overlayDir)) { Write-Host "$marker=FAIL settings overlay directory not found: $overlayDir"; exit 2 }
  }
  # The plan the driver will run (perf_bench.gd _select order); the driver gets the names as --expect and fails on a mismatch.
  $planSelect = if ($Quick) { "quick" } elseif ($Scenarios.Count -gt 0) { $Scenarios -join "," } else { "all" }
  $planList = @(Select-BenchScenarios $spec $planSelect)
  if ($planList.Count -eq 0) { Write-Host "$marker=FAIL no scenario selected by --select=$planSelect in $scenarioPath"; exit 2 }
  Write-Host "PLAN scenario_file=$scenarioPath"
  foreach ($s in $planList) {
    if (-not $s.ContainsKey("kind")) { Write-Host "$marker=FAIL scenario '$($s.name)' has no kind (the driver requires one)"; exit 2 }
    $pw = if ($s.ContainsKey("window_s")) { $s.window_s } else { $spec.defaults.window_s }
    $pv = if ([string]$s.kind -eq "ab_matrix") { " variants=" + (@($s.variants | ForEach-Object { $_.name }) -join ",") } else { "" }
    Write-Host "PLAN $($s.name) kind=$($s.kind) window_s=$pw$pv"
  }
  # Pre-launch check (no GPU, no ticket): the driver and its preloads must parse. The engine exits 0 on a parse error and would idle with the ticket held.
  Import-Module (Join-Path $PSScriptRoot "testing\GpuJob.psm1") -Force -DisableNameChecking
  $pfFiles = @($scenarioPath); if ($SettingsOverlay) { $pfFiles += $overlayDir }
  $pfRes = @(Invoke-GodotPreflight -EntryScripts @('res://tools/perf_bench.gd') -RequiredFiles $pfFiles 6>&1)
  $pfLines = @($pfRes | Where-Object { $_ -is [System.Management.Automation.InformationRecord] } | ForEach-Object { "$($_.MessageData)" })
  $pfLines | ForEach-Object { Write-Host $_ }
  if ($pfRes[-1] -ne $true) {
    $first = ($pfLines | Where-Object { $_ -like 'PREFLIGHT problem:*' } | Select-Object -First 1) -replace '^PREFLIGHT problem:\s*', ''
    Write-Host "$marker=FAIL preflight: $first"; exit 2
  }
  # Everything else that does not need the GPU happens before the ticket: the build (staleness check, lock) and the prune.
  $buildRc = Invoke-OptimizedBuild -Build:$Build -SkipBuild:$SkipBuild -Marker $marker
  if ($buildRc -ne 0) { exit $buildRc }
  & (Join-Path $PSScriptRoot "prune_test_output.ps1") -Apply -Quiet -Exclude "appdata_perf_bench", "perf_bench"
  $mode = if ($derivedSpec) { "variants: " + (@($planList | ForEach-Object { $_.name }) -join ",") } elseif ($Quick) { "quick" } elseif ($Scenarios.Count -gt 0) { "scenarios: " + ($Scenarios -join ",") } else { "full" }
  $out = Join-Path $benchRoot "$Label\$RunId"
  New-Item -ItemType Directory -Force $out | Out-Null
  $ctxPath = Join-Path $out "run_context.json"
  Remove-Item -LiteralPath $ctxPath -Force -ErrorAction SilentlyContinue
  # The inner part (in the ticket) only launches Godot; the build, report and baseline comparison are done here.
  $inner = "& $(Quote $PSCommandPath) -InQueue -Label $(Quote $Label) -RunId $RunId -Resolution $Resolution -TimeoutMin $TimeoutMin -StallMinutes $StallMinutes -World $(Quote $worldInfo.dir) -WindowScale $WindowScale -Rounds $Rounds"
  if ($Quick) { $inner += " -Quick" }
  if ($Scenarios.Count -gt 0) { $inner += " -Scenarios " + (($Scenarios | ForEach-Object { Quote $_ }) -join ",") }
  foreach ($sw in @("VsyncOn", "Stills", "LiveSettings", "Tails", "NoCapture")) { if ($derivedSpec -and $sw -eq "Stills") { continue }; if ((Get-Variable $sw).Value) { $inner += " -$sw" } }
  if ($EngineArgs) { $inner += " -EngineArgs $(Quote $EngineArgs)" }
  if ($ScenarioFile) { $inner += " -ScenarioFile $(Quote $scenarioPath)" }
  if ($SettingsOverlay) { $inner += " -SettingsOverlay $(Quote $SettingsOverlay)" }
  # The outer part's plan goes to the inner part through the environment, not a parameter: an inner part from an older or newer copy of this
  # script ignores it or falls back to its own resolution, so a file edited between enqueue and launch cannot break the run on a version skew.
  $env:RINGWORLD_PERF_BENCH_EXPECT = (@($planList | ForEach-Object { [string]$_.name })) -join ','
  # One queue ticket per run: gpu_queue finds tickets by label, so a shared label let a second bench match the first one's ticket,
  # run at once on the shared appdata_perf_bench profile and release the other run's ticket (2026-11-17).
  & (Join-Path $PSScriptRoot "gpu_queue.ps1") -Label "perf_bench_$($Label)_$RunId" -Wrap $inner
  $wrapExit = $LASTEXITCODE
  Remove-Item Env:RINGWORLD_PERF_BENCH_EXPECT -ErrorAction SilentlyContinue   # never leaks into a later run in the same session
  # ---- the ticket is released: report, baseline deltas, stills comparison, prune
  if (-not (Test-Path $ctxPath)) {
    if ($wrapExit -eq 0) { Write-Host "$marker=FAIL the run left no run_context.json in $out"; exit 1 }
    exit $wrapExit   # 9 user playing, 2 setup problem, 1 queue timeout; the inner part printed the reason
  }
  $rc = Read-JsonHash $ctxPath
  $code = $rc.godot_exit
  $jobStatus = if ($rc.ContainsKey("job_status")) { [string]$rc.job_status } else { "exited" }
  $jobDetail = if ($rc.ContainsKey("job_detail")) { [string]$rc.job_detail } else { "" }
  Write-Host "godot exit $code, job status $jobStatus, output $out"
  if (-not (Test-Path (Join-Path $out "driver_report.json"))) {
    if ($jobStatus -ne "exited") { Write-Host "$marker=FAIL ${jobStatus}: $jobDetail; see $out\console.log"; exit 1 }
    Write-Host "$marker=FAIL no driver_report.json (godot exit $code); see $out\console.log"
    exit 1
  }
  $ctx = @{ label = $Label; run_id = $RunId; started = $rc.started; mode = $mode; args = @{ quick = $argQuick; scenarios = $argScenarios; scenario_file = $argScenarioFile; variants = $variantsSource; settings_overlay = $SettingsOverlay; vsync_on = [bool]$VsyncOn; stills = [bool]$Stills; no_capture = [bool]$NoCapture; tails = [bool]$Tails; rounds = $Rounds; window_scale = $WindowScale; baseline = $Baseline; live_settings = [bool]$LiveSettings }
    git = (Get-GitInfo); world_meta = $worldInfo.meta; settings_source = $rc.settings_source; settings = $rc.settings; resolution = $Resolution; godot_exit = $code }
  $rep = Build-Report $out $ctx
  if ($baseRun) {
    Add-Baseline $rep (Join-Path $baseRun.FullName "report.json")
    if ($Stills) { Add-StillsCompare $rep $out $baseRun.FullName }
  }
  Save-Report $rep $out
  Write-Host "report: $out\report.md"
  Get-Content -LiteralPath (Join-Path $out "report.md") | Select-Object -First 60 | ForEach-Object { Write-Host $_ }
  $regressed = if ($rep.Contains("baseline")) { [int]$rep.baseline.counts.regressed } else { 0 }
  if ($rep.Contains("baseline")) { Write-Host "BASELINE_DELTAS improved=$($rep.baseline.counts.improved) regressed=$regressed within_noise=$($rep.baseline.counts.within_noise)" }
  & (Join-Path $PSScriptRoot "prune_test_output.ps1") -Apply -Quiet -Exclude "appdata_perf_bench", "perf_bench"
  if ($jobStatus -ne "exited") { Write-Host "$marker=FAIL ${jobStatus}: $jobDetail (partial report above)"; exit 1 }
  if ($rep.verdict -ne "PASS") { Write-Host "$marker=FAIL $($rep.verdict_reasons -join '; ')"; exit 1 }
  if ($FailOnRegression -and $regressed -gt 0) { Write-Host "$marker=FAIL $regressed regressed metric(s) against baseline $Baseline (-FailOnRegression)"; exit 1 }
  Write-Host "$marker=PASS"
  exit 0
}

# ------------------------------------------------------------------ inside the queue (GPU needed only from here): restore, launch, restore. Report building is the outer part's job.
$worldInfo = Resolve-BenchWorld
$exe = Join-Path $project "artifacts\engine-upgrade\Godot_v4.8-dev7_mono_win64\Godot_v4.8-dev7_mono_win64_console.exe"
$out = Join-Path $benchRoot "$Label\$RunId"
New-Item -ItemType Directory -Force $out | Out-Null
if (Get-UserPlaying) { Write-Host "USER_PLAYING"; Write-Host "$marker=FAIL the user is playing; exit 9"; exit 9 }

# ---- isolated profile (one reused profile): caches copied once, world and settings restored from the snapshot before every run
if ((-not (Test-Path $iso))) {
  Write-Host "first use: creating the isolated profile .godot\appdata_perf_bench (copies caches, a few GB) ..."
  New-Item -ItemType Directory -Force (Join-Path $iso "worlds") | Out-Null
  foreach ($d in Get-ChildItem $real -Directory) {
    if ($d.Name -in @("worlds", "artifacts", "objectdb_snapshots", "certification_reports", "pda_debug", "terrain_generation_preview", "bench_worlds", ".test-appdata")) { continue }
    Copy-Item -Recurse $d.FullName (Join-Path $iso $d.Name)
  }
}
$otherBench = @(Get-CimInstance Win32_Process -Filter "Name like 'Godot%'" -ErrorAction SilentlyContinue | Where-Object { $_.CommandLine -like "*res://tools/perf_bench.gd*" })
if ($otherBench.Count -gt 0) { Write-Host "$marker=FAIL another perf_bench Godot is still running (pid $($otherBench[0].ProcessId)) on the shared profile; not touching it"; exit 2 }
$isoSlot = Join-Path $iso "worlds\slot_3"
if ([System.IO.Path]::GetFullPath($isoSlot) -notlike "*\.godot\appdata_perf_bench\*") { throw "refusing: $isoSlot" }
if (-not (Test-Path -LiteralPath (Join-Path $worldInfo.world "world.json"))) { Write-Host "$marker=FAIL the benchmark world $($worldInfo.world) has no world.json"; exit 2 }
New-Item -ItemType Directory -Force (Join-Path $iso "worlds") | Out-Null
# Mirror the frozen world into the profile: only what the game changed is copied (the snapshot is the source and is never written).
& robocopy $worldInfo.world $isoSlot /MIR /R:1 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { Write-Host "$marker=FAIL robocopy of the world failed (exit $LASTEXITCODE)"; exit 2 }
$srcStat = Get-ChildItem -LiteralPath $worldInfo.world -Recurse -File -Force | Measure-Object -Property Length -Sum
$dstStat = Get-ChildItem -LiteralPath $isoSlot -Recurse -File -Force | Measure-Object -Property Length -Sum
if ($srcStat.Count -ne $dstStat.Count -or $srcStat.Sum -ne $dstStat.Sum) {
  Write-Host "$marker=FAIL world restore mismatch: snapshot $($srcStat.Count) files / $($srcStat.Sum) bytes, profile $($dstStat.Count) files / $($dstStat.Sum) bytes"; exit 2
}
Get-ChildItem $iso -File -Filter *.tres | Remove-Item -Force
$settingsSource = ""
if ($LiveSettings -or -not $worldInfo.settings -or -not (Test-Path $worldInfo.settings)) {
  Get-ChildItem $real -File -Filter *.tres | Copy-Item -Destination $iso -Force
  $settingsSource = "LIVE files of the real profile at $(Get-Date -Format s)"
} else {
  Get-ChildItem $worldInfo.settings -File -Filter *.tres | Copy-Item -Destination $iso -Force
  $settingsSource = "frozen with the snapshot $($worldInfo.meta.name)"
}
if ($SettingsOverlay) {
  $overlayDir = if ([System.IO.Path]::IsPathRooted($SettingsOverlay)) { $SettingsOverlay } else { Join-Path $project $SettingsOverlay }
  if (-not (Test-Path -LiteralPath $overlayDir)) { Write-Host "$marker=FAIL settings overlay directory not found: $overlayDir"; exit 2 }
  $overlayFiles = @(Get-ChildItem -LiteralPath $overlayDir -File -Filter *.tres)
  $overlayFiles | Copy-Item -Destination $iso -Force
  $settingsSource += " + overlay $overlayDir ($(($overlayFiles | ForEach-Object { $_.Name }) -join ', '))"
}
$settings = Get-SettingsValues $iso

# ---- optimized assembly (built by the outer part)
$slotDll = Join-Path $monoTemp "bin\Debug\$name.dll"; $slotPdb = Join-Path $monoTemp "bin\Debug\$name.pdb"
if (-not (Test-Path $optDll)) { Write-Host "$marker=FAIL no OptRelease build (run with -Build)"; exit 1 }

$select = if ($Quick) { "quick" } elseif ($Scenarios.Count -gt 0) { $Scenarios -join "," } else { "all" }
$expectNames = if ($env:RINGWORLD_PERF_BENCH_EXPECT) { $env:RINGWORLD_PERF_BENCH_EXPECT } else { (@(Get-BenchPlanNames $spec $select)) -join "," }
$drvArgs = @("--out=$out", "--scenarios=$scenarioPath", "--select=$select")
if ($expectNames) { $drvArgs += "--expect=$expectNames" }   # no names: no check (the driver also skips an empty list)
if ($VsyncOn) { $drvArgs += "--vsync-pass" }
if ($Stills) { $drvArgs += "--stills" }
if ($NoCapture) { $drvArgs += "--no-capture" }
if ($Tails) { $drvArgs += "--tails" }
if ($Rounds -gt 0) { $drvArgs += "--rounds=$Rounds" }
if ($WindowScale -ne 1.0) { $drvArgs += "--window-scale=$WindowScale" }

$savedDir = Join-Path $monoTemp "bin\DebugSaved_perfbench"
New-Item -ItemType Directory -Force $savedDir | Out-Null
Copy-Item -Force $slotDll, $slotPdb $savedDir
Copy-Item -Force $optDll $slotDll
Copy-Item -Force $optPdb $slotPdb
$code = -1
$smi = $null
$job = $null
$started = (Get-Date).ToString("s")
try {
  $arguments = @("--path", $project, "--resolution", $Resolution)
  if ($EngineArgs) { $arguments += ($EngineArgs -split " " | Where-Object { $_ }) }
  $arguments += @("--script", "res://tools/perf_bench.gd", "--") + $drvArgs
  Import-Module (Join-Path $PSScriptRoot "testing\GpuJob.psm1") -Force -DisableNameChecking
  $smi = Start-Process -FilePath "nvidia-smi" -ArgumentList @("--query-gpu=timestamp,pstate,clocks.gr,clocks.mem,power.draw,temperature.gpu,utilization.gpu,memory.used,clocks_event_reasons.active", "--format=csv", "-lms", "1000") -RedirectStandardOutput (Join-Path $out "power_log.csv") -PassThru -NoNewWindow
  # Raw stdout / stderr files (console.log keeps its format: other tools grep ^PB in it); the child inherits the rest of this environment.
  $job = Invoke-GodotJob -Exe $exe -Arguments $arguments -WorkDir $project -StdOutLog (Join-Path $out "console.log") -StdErrLog (Join-Path $out "stderr.log") `
    -Env @{ APPDATA = $isoRoot; RINGWORLD_TEST_NO_GRAB = "1" } -TimeoutS ($TimeoutMin * 60) -StallS ($StallMinutes * 60) -EntryScript "res://tools/perf_bench.gd"
  if ($job.Status -ne "exited") { Write-Host "GODOT_JOB $($job.Status): $($job.Detail)" }
  $code = if ($null -ne $job.ExitCode) { $job.ExitCode } else { -1 }
} finally {
  if ($smi) { Stop-Process -Id $smi.Id -Force -ErrorAction SilentlyContinue }
  # Restore only when nobody rebuilt the Debug slot meanwhile (another agent's newer build must not be clobbered).
  if ((Get-FileHash $slotDll).Hash -eq (Get-FileHash $optDll).Hash) {
    Copy-Item -Force (Join-Path $savedDir "$name.dll") $slotDll
    Copy-Item -Force (Join-Path $savedDir "$name.pdb") $slotPdb
    Write-Host "Debug slot restored"
  } else { Write-Host "Debug slot was rebuilt by someone else during the run; left as is" }
  Remove-Item -Recurse -Force $savedDir -ErrorAction SilentlyContinue
}
# What only this part knows; the outer part builds the report from it once the ticket is released.
[ordered]@{ started = $started; godot_exit = $code; settings_source = $settingsSource; settings = $settings; job_status = $(if ($job) { $job.Status } else { "error" }); job_detail = $(if ($job) { $job.Detail } else { "" }) } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $out "run_context.json") -Encoding UTF8
exit 0
