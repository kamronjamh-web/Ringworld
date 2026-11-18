# Performance benchmark (`tools/perf_bench.ps1`)

One reusable, all-in-one benchmark of the game as the player runs it, so that results from different days and different agents are comparable. It replaces the throwaway drivers of the 2026-11 perf pass (`.godot/perf_pass_real/`, see `docs/perf-pass-2026-11.md` section 0). Use it before and after any change that can move frame time.

## Run it

```powershell
.\tools\perf_bench.ps1 -Label baseline_<head> -Quick                       # about 5 min
.\tools\perf_bench.ps1 -Label after_<change> -Quick -Baseline baseline_<head>   # adds the delta table
.\tools\perf_bench.ps1 -Label full_<head>                                  # every scenario, about 20 min
.\tools\perf_bench.ps1 -Label x -Scenarios save_spot,fly_240               # picked scenarios
.\tools\perf_bench.ps1 -Label x -Quick -VsyncOn -Stills -Baseline y        # VSync-on pass, capture-mode stills + SSIM against y
.\tools\perf_bench.ps1 -Label x -Scenarios plains -Variants .\my_variants.json   # in-session A/B of run-time toggles (section "In-session A/B")
.\tools\perf_bench.ps1 -Snapshot -SnapshotOnly                             # freeze the current slot 3 as a new world, then stop
```

Other switches: `-Variants <path or inline JSON>` (in-session A/B, below), `-FailOnRegression` (exit 1 when a metric regressed against `-Baseline`), `-World <dir>`, `-LiveSettings`, `-Resolution 1600x900`, `-Rounds N`, `-WindowScale F` (shorter windows while developing), `-Tails` (full windows everywhere, no adaptive stop), `-NoCapture` (quiet stills as before, see Stills), `-Build` / `-SkipBuild`, `-ReportFrom <run dir>` (rebuild the report of a finished run). `-ScenarioFile <json>` (absolute or relative to the project root; replaces `tools/fixtures/perf_bench_scenarios.json`, exit 2 when missing; recorded as `args.scenario_file`). `-SettingsOverlay <dir>` (after the frozen or live settings are copied, every `*.tres` in the directory is copied over them into the isolated profile; the report's settings source names the directory and files; recorded as `args.settings_overlay`). Use the overlay for before/after studies of a setting: the frozen snapshot stays untouched. Environment hooks, applied once after boot: `PERF_BENCH_TREE_PROPS="Prop=1,Prop2=0"` (bools on the tree layer) and `PERF_BENCH_NODE_PROPS="WetWorld:SsrPolicyTargetOverride=0;WetWorld:ForceUniform(0.02,0.02,0,0)"` (`;`-separated; a node path relative to the game, then `Prop=value` with a number or true/false, or `Method(numbers)`). Do not wrap it in `tools/gpu_queue.ps1`; it takes the queue itself, under the label `perf_bench_<label>_<run id>` (one ticket per run). Only the Godot part holds the ticket: the staleness check (`git ls-files '*.cs'` against the optimized assembly), the optimized build (serialised by `.godot/mono/temp/opt_build.lock`) and the prune run before it, the world is mirrored from the snapshot into the profile with `robocopy /MIR` inside it, and the report, baseline deltas and stills comparison are built after release from `run_context.json` in the run folder. The queue appends every enqueue, acquire, release and timeout to `.godot/gpu_queue/history.jsonl`. It also refuses to start while another perf_bench Godot is alive, because all runs share one profile). Before the build and the ticket, `res://tools/perf_bench.gd` is parse-checked headless (`tools/testing/GpuJob.psm1`; a parse error prints `PERF_BENCH=FAIL preflight: ...`, exit 2, no ticket). Godot then runs under `Invoke-GodotJob`, which kills it (own process tree only) after `-StallMinutes` (default 10) without any output line (`job_status` `stalled`), after `-TimeoutMin` active minutes (`timeout`) or when the driver fails to load (`load-failed`); `run_context.json` records `job_status` and `job_detail`, a status other than `exited` is `PERF_BENCH=FAIL <status>: <detail>`, and an existing `driver_report.json` is still turned into a report first. Run the baseline and the comparison run on the same machine, with the same uncommitted changes apart from what you are measuring, and use `-SkipBuild` for the second run when nobody changed the sources in between (the report warns when the dirty-diff hash differs).

Output: `.godot/perf_bench/<label>/<run id>/` holding `report.md` (read this), `report.json`, `driver_report.json`, `console.log`, `power_log.csv` and `stills/`. `tools/prune_test_output.ps1` keeps `perf_bench` and the profile `appdata_perf_bench`; delete old run folders by hand when they are no longer a baseline.

Exit codes: 0 `PERF_BENCH=PASS` (every selected scenario completed; a regression does not fail it unless `-FailOnRegression`), 1 fail, 2 setup problem, 9 the user is playing (a Godot with `--remote-debug`, or a non-empty `.godot/mono/temp/bin/DebugPlaySaved`). The manifest entry is `perf_bench` (gpu, tier manual, own-runner).

## What it measures

**World.** The first run (or `-Snapshot`) copies the player's `worlds/slot_3` and the top-level settings `*.tres` once to `%APPDATA%/Godot/app_userdata/RingworldTechDemo/bench_worlds/slot3_<date>/` (`world/`, `settings/`, `snapshot.json` with generator, seed, date, git head and the save pose at that moment). Every run copies from that snapshot into the isolated profile `.godot/appdata_perf_bench` (one reused profile; the game autosaves into it). The live slot is only read at snapshot time, and the user's real profile is never written apart from `bench_worlds`. The snapshot name is part of the report, and `-Baseline` warns on a world, GPU, resolution or settings mismatch. A new snapshot (`-Snapshot`) starts a new comparison series: take a new baseline with it.

**Settings.** The profile copies the snapshot's frozen `*.tres` files (so later changes in the live profile do not move the numbers; `-LiveSettings` uses the live files instead). Their sha and every key/value are recorded in `report.json` (`settings_files`) and listed in `report.md`, with the tree LODs the game actually used. The monitor's detailed GPU intervals are switched on for the run.

**Boot.** Optimized C# (`bin/OptRelease`, as `tools/play.ps1`) swapped into the Debug slot and restored in a `finally`; the real `StartMenu` and Continue (`tools/tree_visibility_world.gd boot_game`); 1600 x 900, VSync off and `max_fps` 0 for the windows.

**Scenarios** (`tools/fixtures/perf_bench_scenarios.json`, data driven: pose or flight, time of day, window length, flags `quick` / `still` / `vsync_pass`):

| name | what | notes |
|---|---|---|
| `save_spot` | stand at the perf-pass save spot (around 20,295,044 m, across 123,863 m, 624 m, noclip hover) | quick, still, vsync pass |
| `plains`, `forest_interior`, `forest_edge`, `forest_clearing`, `coast`, `mountain` | ground poses of `tools/fixtures/tree_visibility_poses.json` | quick: plains, forest_interior |
| `noon_9km` | the old E1 pose, 9,011 m up | quick |
| `night_plains` | plains at a planet-lit night | |
| `yaw_sweep_forest` | 360 deg/s turn in the forest interior | |
| `fly_72`, `fly_240` | noclip at 72 and 240 m/s, 150 m / 300 m over the plains | quick: fly_240 |
| `fly_1200`, `fly_36840` | 1.2 km/s and 36.8 km/s from the save spot | |

Noon with the celestial and weather clocks running (unless a scenario sets `hold_clocks`), after streaming builds are idle (`wait_builds_idle`). Standing scenarios run two back-to-back windows (A-A); flights are re-placed for each round. Two rounds of 12 s (flights 15 s, sweep 10 s) by default; standing windows stop early when they settle (next paragraph).

**Adaptive windows.** A standing window (`kind: "stand"`) is a cap, not a fixed length. The driver checks every 2 s from 4 s on and stops when, for both frame time and GPU time, the 95 % confidence interval of the median (order statistics) is within +/- tol and the median of the first 2 s block differs from the last complete 2 s block by no more than tol, with tol = max(0.05 ms, 0.5 % of the median), after at least 4 s and 300 frames. `window_s` is the cap: a window is never longer than before. Flights and the yaw sweep keep fixed windows, and `-Tails` (`--tails`) gives full windows everywhere. The report has `window_actual_s`, `window_stop` (`settled`, `cap` or `fixed`) and `tails_info` per scenario; the summary table shows the actual window ("2 x 4.0..6.0 of 12 (adaptive)"). A window that stopped early is too short to judge tails, so p99, max, hitches and the GC counts are informational: marked "(info)" in the table and not compared against a baseline (a warning names the scenario). Use `-Tails` for a tail or GC study. The rule is `tools/perf_bench_adaptive.gd`, tested by `perf_bench_adaptive_rule_test`.

**Metrics per window** (`report.json`: `scenarios.<name>.metrics.<key> = {values, value (median), noise (round spread)}`):

- `fps`, `wall_p50/p95/p99/max` (frame-to-frame ms), `hitch33_count` and `hitch33_worst_ms` (frames over 33.3 ms), `setup_p50/p95` (frame setup CPU), `process_p50/p95` (main thread `_process`), `render_cpu_p50/p95` (root plus sub-viewport render CPU), `gpu_total_p50/p95` (all viewports), `draws_p50`, `prims_p50`, `vram_mb`, GC counts and allocation rate, flight speed and distance.
- `gpu.<name>`: ms per frame of every `RingPerformanceGpu` detailed interval (clouds and their stages, tree and grass culling, height shadow, cloud shadows, water FFT, rain, ...). Nested stages overlap their parents: never sum them. `gpu_sample.<name>` is the per-sample mean. `vp.<viewport path>.gpu_p50` / `.cpu_p50` is the per-viewport time (near terrain and main scene, far viewport, sun shadow viewports).
- `cpu.<scope>`: ms per frame of the key scopes (trees frame request, trees update, camera cull, grass, water, macro shadow install, LOD coordinator, proxy install, far ring) and any scope over 0.02 ms; `worker.<scope>`: worker-thread work (macro shadow build, ...), not frame-critical. The wet world has no scope of its own: its cost shows in `process_p50` and `setup_p50`.
- Per-pass values (`gpu.*`, `cpu.*`, `worker.*`) are per frame over the frames the monitor's retained events span. In a 12 s window the monitor's event rings overflow: the GPU ring keeps about the last 1,000 to 1,140 frames and the CPU ring only the last 550 to 610 (plains and 9 km, 2026-11-18), so the driver divides by that span (`monitor_span` in `report.json`: dropped counts and span frames) and `report.md` flags windows with overflow. Windows without drops divide by all frames as before. Per-pass values in reports made before 2026-11-18 with `monitor_dropped > 0` are too low by roughly span/frames: GPU passes by about a quarter to a half, CPU scopes by a factor of 2 to 3.5. The per-pass value of a long window describes its last few seconds only.

**Noise.** `noise` is the spread between the two rounds. A delta against a baseline is called a change only when it exceeds the largest of the two spreads and a per-metric floor (0.3 ms for medians and passes, max(1 ms, 25 %) for tails, 3 % for fps, 64 MB VRAM (set from the quick A/B repeat on 2026-11-16: identical code differed by up to 0.25 ms in GPU p50 at 9 km and 60 MB VRAM)); otherwise it is "within noise". Tail metrics (p99, max, hitches) are intrinsically noisy: repeat before believing one.

**Machine record.** GPU, driver, enforced power limit, CPU, RAM, OS, engine, display on, refresh rate, the P-state share, power, clocks, temperature and throttle reasons while the GPU was busy (`power_log.csv`, 1 s), resolution, preset, shadow atlas, SSAO/SSR/AA, git branch and HEAD, the uncommitted files and a hash of the uncommitted diff. Numbers are only comparable on the same GPU at P0 (`perf-capture` skill, section 0).

**Stills** (`-Stills`). After the measurements, one still per `still` scenario, taken in still-capture mode (`tools/tree_visibility_world.gd capture_begin` / `capture_end`: time scale 0, cloud march held, TAA off, clocks held, wind frozen rather than switched off, HUD hidden; clouds, grass and gust debris stay visible), with a byte-identical pair self-check recorded per still (`capture`; if it fails the still is still saved and marked `capture_fallback`). `-NoCapture` gives the old quiet stills (clouds, grass and gust debris hidden, wind off). Still baselines taken before this change are quiet stills and differ from capture-mode stills, so SSIM against an older baseline's stills is not a regression signal: compare against a baseline taken with the same mode, or use `-NoCapture` to compare with an old baseline. With `-Baseline`, `tools/media/ffmpeg_tools.ps1 compare` gives SSIM, PSNR and a heatmap per pose, listed in the report. Capture mode makes a still repeatable inside one run, not across runs: the clouds move between two runs, so two capture-mode runs of identical code gave SSIM 0.986 to 0.996 with the differences in the sky (2026-11-18). Judge the heatmap, not only the number.

**VSync** (`-VsyncOn`). The scenarios marked `vsync_pass` run again as `<name>@vsync` with VSync as shipped. If the display is off (VSync then runs at about 4 fps), the pass is skipped and noted.

## Custom scenario files and POI poses

`-ScenarioFile` takes any file of the same schema. `tools/fixtures/perf_bench_visible_opts.json` is the set for the visible-perf-options study (poses: plains, plains with the arch, mountain, coast, forest interior, a steep slope, 9 km, two 300 m altitudes, Weather Lung dam and overlook, emitter rim; 8 s windows).

A pose may be `{"poi": "weather_lung" | "emitter", "viewpoint": N}` instead of `around_m` / `across_m`. The driver reaches it through the live tuning drawer's teleport (`RequestWeatherLungTeleport`, or `FindEmitterSite("main")` plus `RequestEmitterTeleport`), waits for `Arrived` (180 s), for the Weather Lung detail scene (viewpoints 0 and 1) and then freezes the breath at the harness phase. A failed teleport records `error` for that scenario or still and the run continues.

**`ssr_fade` scenarios** (`kind: "ssr_fade"`) are skipped by the measurement loop and the stills, and run last. They need the `WetWorld` SSR policy (`SsrPolicyWeight`, `SsrPolicyTargetOverride`, `SsrPolicyWeightOverride`, `SsrToggleCount`); without it the scenario fails with "ssr policy not available". The scene is quiet and the clocks are held. It writes under `<run>/ssr_fade/`:
- `<name>_w100.png` ... `<name>_w0.png`: stills with the weight pinned to 1, 0.75, 0.5, 0.25, 0.1, 0.
- A timing pass without grabs: per-frame wall ms, weight and `ssr_enabled` over 1 s steady, the fade out (until weight 0, plus 2 s) and the fade in (until weight 1, plus 2 s). The summary (max and median frame ms, frame ms on the frames where `ssr_enabled` flipped, toggle count before and after, frame ranges) is in `report.json` and `report.md` ("SSR fade"); the arrays are in `driver_report.json`.
- `<name>_frames/f%05d.png`: a frame pass that saves every frame of one out and one in transition (0.5 s steady before each, 1 s after); the transition frame ranges are in the summary (`frame_ranges`).

## In-session A/B (`ab_matrix`, `-Variants`)

For a feature that can be switched at run time (a property or method of a node under the game root, a property of the main `Environment` or its sky, SSR half size) one launch answers a before/after question, and the same-session A-A spread is a much tighter noise floor than the difference between two launches (0.00 to 0.04 ms against up to 0.25 ms). Do not build another A/B mechanism: the scenario kind `ab_matrix` (`tools/perf_bench_ab_matrix.gd`) is the one.

**The kind.** An `ab_matrix` scenario stands at one `pose` and runs `baseline` plus the named `variants` in an interleaved A-X-A pattern: per round A0 X1 A1 X2 A2 ... Xn An, each window `window_s` long. Keys: `pose`, `window_s`, `rounds`, `hold_clocks`, `variant_settle_s` (settle after every switch and restore, default 1 s), `stills`, `force_wet` (`[film, soak, standing, rain]`, `WetWorld.ForceUniform`), `baseline` (ops applied for the whole scenario) and `variants` (`[{name, ops}]`). An op is one of:

- `{"env": {prop: value}}`: properties of the main `Environment`;
- `{"sky": {prop: value}}`: properties of the main `Environment`'s sky resource;
- `{"node": "Path", "set": {prop: value}}`: properties of a node below the game root (the value is coerced to the old value's type);
- `{"node": "Path", "call": "Method", "args": []}`: a method call, run again after the restore so an apply method pushes the old values back;
- `{"ssr_half_size": bool}`: `RenderingServer.environment_set_ssr_half_size`, restored afterwards.

A missing node, property or method fails that variant only (its row shows the error). Everything is restored after each window, whatever happened. Scenarios of this kind run right after the generic measured loop and write `ab_matrices` to `report.json`; `perf_bench.gd` and `perf_bench_ab_matrix.gd` headers have the details.

**What the numbers mean.** The delta of a variant per round is X minus the mean of the two A windows around it, so a slow drift cancels. `report.md` has one section "A/B matrices (in-session, A-X-A)" with, per scenario, the baseline median and its spread over all A windows (the A-A spread, the noise floor of this session) and a table: dGPU p50 (median over rounds, min..max), dwall p50 and p95, drender CPU p50, ddraws and `beyond floor`. `beyond floor` is `yes` when the median dGPU is larger than the baseline GPU p50 spread, or the median dwall p50 larger than the baseline wall p50 spread; `no` means the variant is not distinguishable from nothing in this session (repeat with more rounds or a longer window before believing a small `yes`). A variant that cannot be applied shows `error: ...`. The same data per metric (`delta.<metric>.values`, `abs`, per-viewport GPU) is in `report.json`.

**Stills.** With `stills: true` one quiet still per variant and one of the baseline are written to `<run>/ab_matrix/<scenario>__<variant>.png` (clouds, grass and gust debris hidden, clocks, wind, TAA held), after the measurements; the paths are listed in the report, and `stills_error` / `stills_failed_variants` say what could not be taken. Compare two of them with `tools/media/ffmpeg_tools.ps1 compare`.

**`-Variants <path or inline JSON>`.** Builds the scenarios for you. The value is a path to a JSON file (absolute or relative to the project root) or inline JSON starting with `{` or `[`: either an array of variants, or an object `{"variants": [...], "baseline": [ops], "window_s", "rounds", "hold_clocks", "variant_settle_s" (or "settle_s"), "stills"}` (all but `variants` optional). The runner validates it before the queue (at least one variant; names unique and matching `^[A-Za-z0-9_.-]+$`; `ops` an array; every op an object with `env`, `sky`, `node` or `ssr_half_size`; a `node` op needs `set` or `call`; otherwise `PERF_BENCH=FAIL -Variants: <problem>`, exit 2), takes the scenarios the run would select (`-Scenarios`, else `-Quick`, else all), keeps the standing ones (kind `stand`; a line lists the ones dropped, none left is exit 2) and writes `<run>/variants_scenarios.json`: the source file with `scenarios` replaced by one `ab_matrix` scenario `<name>__ab` per kept scenario (same pose and time, `window_s` from the file, else the scenario, else the defaults; `rounds` from the file, else `-Rounds`, else 2; `hold_clocks` true; `stills` from the file, else `-Stills`). The run is then a plain run of that file with everything selected. `-Variants` may be combined with `-ScenarioFile` (the standing scenarios come from that file); with `-Baseline` it warns, because ab_matrix results are not compared with a baseline. `args.variants` in the report holds the source path or `inline`.

```json
{
  "baseline": [{"node": "WetWorld", "set": {"SsrPolicyWeightOverride": 1.0}}],
  "window_s": 8, "rounds": 3, "stills": true,
  "variants": [
    {"name": "same_as_a", "ops": [{"node": "WetWorld", "set": {"SsrPolicyWeightOverride": 1.0}}]},
    {"name": "ssr_off",   "ops": [{"node": "WetWorld", "set": {"SsrPolicyWeightOverride": 0.0}}]},
    {"name": "no_fog",    "ops": [{"env": {"fog_enabled": false}}]}
  ]
}
```

```powershell
.\tools\perf_bench.ps1 -Label ssr_ab -Scenarios plains,forest_interior -Variants .\my_variants.json
```

A variant identical to the baseline (`same_as_a`) is a cheap sanity check: it must come out `beyond floor: no`.

**Plan lines.** Every run, not only `-Variants`, prints before the queue the plan it resolved: `PLAN scenario_file=<path>` and one `PLAN <name> kind=<kind> window_s=<w>[ variants=a,b]` per scenario in the order `perf_bench.gd` `_select` returns them (file order for all and quick, the `-Scenarios` order for a list; the run order by kind comes later). The runner passes the names as `--expect=` (the outer part hands them to the part inside the queue through the environment, so a copy of the script edited between enqueue and launch cannot break the run; an absent or empty list is no check); `_select` runs right after the scenario file is read, before any boot work, and a different list ends the run in about 4 s with `PERF_BENCH=FAIL plan mismatch: runner expected [..], driver selected [..]` (exit 2, the reason is in the report). The names are recorded as `expected` in `driver_report.json`.

**Limit.** Only what can be switched in a running session is covered. A code or shader source change cannot be toggled: that still takes two launches (`-Label before`, `-Label after -Baseline before`). Where a comparison will be repeated, expose the feature as a debug property so it becomes a variant.

## Adding or changing a scenario

Edit `tools/fixtures/perf_bench_scenarios.json` (poses, scenario list, `key_scopes`). A changed scenario or pose is a new series: take a new baseline. The driver is `tools/perf_bench.gd`, the C# helper (explorer placement, GC counters, monitor rows) is `tools/PerfBenchProbe.cs`, the runner `tools/perf_bench.ps1`.

## Reference

Run on 2026-11-16/17 at git `a785e4b` (uncommitted: the near-terrain lead's terrain changes plus this tooling), RTX 4070 desktop at P0 (median 2820 MHz, 184 W), 1600 x 900 High, the user's frozen settings (tree near detail 10 to 20 m, billboards 150 to 900 m), snapshot `slot3_2026-11-16`, optimized C#, VSync off, clocks running. Run folders (kept by the prune): `.godot/perf_bench/full_a785e4b/<run id>/` (the full reference, with stills and the VSync pass), `.godot/perf_bench/baseline_a785e4b/` (quick), `.godot/perf_bench/quick2_a785e4b/` (quick repeat with `-Baseline`: 0 improved, 0 regressed, 420 within noise).

| scenario | fps | frame p50 | p95 | p99 | max | hitches >33 ms | process p50 | GPU p50 | draws |
|---|---|---|---|---|---|---|---|---|---|
| save_spot | 93.8 | 10.64 | 11.23 | 11.81 | 20.1 | 0 | 11.9 | 10.29 | 1542 |
| plains | 103.3 | 9.63 | 10.24 | 10.87 | 20.4 | 0 | 11.1 | 9.30 | 1678 |
| forest_interior | 99.8 | 9.93 | 10.68 | 12.49 | 22.3 | 0 | 11.5 | 9.57 | 1650 |
| forest_edge | 94.3 | 10.57 | 11.17 | 11.80 | 21.3 | 0 | 11.6 | 10.23 | 1284 |
| forest_clearing | 112.9 | 8.79 | 9.46 | 10.04 | 18.9 | 0 | 10.4 | 8.53 | 770 |
| coast | 98.8 | 10.01 | 10.90 | 13.93 | 23.9 | 0 | 14.6 | 9.69 | 1503 |
| mountain | 129.7 | 7.64 | 8.21 | 8.70 | 19.1 | 0 | 9.4 | 7.43 | 1294 |
| noon_9km | 149.6 | 6.61 | 7.18 | 7.61 | 15.6 | 0 | 8.2 | 6.39 | 340 |
| night_plains | 101.5 | 9.82 | 10.43 | 11.03 | 19.8 | 0 | 11.1 | 9.44 | 1594 |
| yaw_sweep_forest | 80.0 | 12.22 | 14.80 | 16.84 | 32.1 | 0 | 18.3 | 11.51 | 1675 |
| fly_72 | 69.4 | 14.07 | 18.37 | 22.14 | 31.2 | 0 | 21.4 | 10.32 | 960 |
| fly_240 | 75.0 | 12.77 | 17.31 | 21.59 | 25.2 | 0 | 21.1 | 9.98 | 737 |
| fly_1200 | 58.6 | 17.05 | 22.30 | 24.90 | 44.4 | 3 | 22.2 | 7.67 | 558 |
| fly_36840 | 163.5 | 4.80 | 11.64 | 14.87 | 42.1 | 1 | 12.2 | 4.21 | 144 |
| save_spot@vsync | 91.6 | 10.89 | 11.48 | 12.12 | 19.5 | 0 | 12.2 | 10.53 | 1544 |
| forest_interior@vsync | 100.8 | 9.90 | 10.52 | 10.99 | 19.7 | 0 | 11.2 | 9.55 | 1637 |
| fly_240@vsync | 74.6 | 12.82 | 17.46 | 20.29 | 28.0 | 0 | 20.4 | 9.76 | 758 |

Caveats seen in the reference run:
- `fly_36840` (36.8 km/s from 624 m): the terrain cannot stream in at that speed (144 draw calls, builds busy 100 %), so the GPU is nearly idle and fps is high. It measures the streaming and GC cost (29 gen2 collections, 505 MB/s allocated), not rendering. Read its `process` and `cpu.*` / `worker.*` rows.
- The VSync-on pass (`@vsync`) showed no 60 fps cap on this machine (91 to 100 fps although the engine reports 60 Hz): the swapchain or monitor did not hold the frame rate. Treat that pass as unverified until the present mode is checked.
- Per-pass GPU is limited to what `RingPerformanceGpu` marks (clouds and their stages, tree and grass culling, height shadow, cloud shadows, water FFT, rain, ...). Engine timestamps give only per-viewport totals: there are no near-terrain, sun-shadow-map, SSR or SSAO intervals. For those use an ablation (`perf-capture` skill).
- The save spot in the scenario list is the 2026-11 perf-pass spot (around 20,295,044 m). The live slot's save has since moved (at snapshot time around 20,236,490 m, across 89,671 m, 139 m, recorded in `snapshot.json`); the game boots there, and every scenario then places itself explicitly.
