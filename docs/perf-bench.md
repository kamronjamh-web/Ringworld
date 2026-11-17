# Performance benchmark (`tools/perf_bench.ps1`)

One reusable, all-in-one benchmark of the game as the player runs it, so that results from different days and different agents are comparable. It replaces the throwaway drivers of the 2026-11 perf pass (`.godot/perf_pass_real/`, see `docs/perf-pass-2026-11.md` section 0). Use it before and after any change that can move frame time.

## Run it

```powershell
.\tools\perf_bench.ps1 -Label baseline_<head> -Quick                       # about 5 min
.\tools\perf_bench.ps1 -Label after_<change> -Quick -Baseline baseline_<head>   # adds the delta table
.\tools\perf_bench.ps1 -Label full_<head>                                  # every scenario, about 20 min
.\tools\perf_bench.ps1 -Label x -Scenarios save_spot,fly_240               # picked scenarios
.\tools\perf_bench.ps1 -Label x -Quick -VsyncOn -Stills -Baseline y        # VSync-on pass, quiet stills + SSIM against y
.\tools\perf_bench.ps1 -Snapshot -SnapshotOnly                             # freeze the current slot 3 as a new world, then stop
```

Other switches: `-FailOnRegression` (exit 1 when a metric regressed against `-Baseline`), `-World <dir>`, `-LiveSettings`, `-Resolution 1600x900`, `-Rounds N`, `-WindowScale F` (shorter windows while developing), `-Build` / `-SkipBuild`, `-ReportFrom <run dir>` (rebuild the report of a finished run). Do not wrap it in `tools/gpu_queue.ps1`; it takes the queue itself for the whole run. Run the baseline and the comparison run on the same machine, with the same uncommitted changes apart from what you are measuring, and use `-SkipBuild` for the second run when nobody changed the sources in between (the report warns when the dirty-diff hash differs).

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

Noon with the celestial and weather clocks running (unless a scenario sets `hold_clocks`), after streaming builds are idle (`wait_builds_idle`). Standing scenarios run two back-to-back windows (A-A); flights are re-placed for each round. Two rounds of 12 s (flights 15 s, sweep 10 s) by default.

**Metrics per window** (`report.json`: `scenarios.<name>.metrics.<key> = {values, value (median), noise (round spread)}`):

- `fps`, `wall_p50/p95/p99/max` (frame-to-frame ms), `hitch33_count` and `hitch33_worst_ms` (frames over 33.3 ms), `setup_p50/p95` (frame setup CPU), `process_p50/p95` (main thread `_process`), `render_cpu_p50/p95` (root plus sub-viewport render CPU), `gpu_total_p50/p95` (all viewports), `draws_p50`, `prims_p50`, `vram_mb`, GC counts and allocation rate, flight speed and distance.
- `gpu.<name>`: ms per frame of every `RingPerformanceGpu` detailed interval (clouds and their stages, tree and grass culling, height shadow, cloud shadows, water FFT, rain, ...). Nested stages overlap their parents: never sum them. `gpu_sample.<name>` is the per-sample mean. `vp.<viewport path>.gpu_p50` / `.cpu_p50` is the per-viewport time (near terrain and main scene, far viewport, sun shadow viewports).
- `cpu.<scope>`: ms per frame of the key scopes (trees frame request, trees update, camera cull, grass, water, macro shadow install, LOD coordinator, proxy install, far ring) and any scope over 0.02 ms; `worker.<scope>`: worker-thread work (macro shadow build, ...), not frame-critical. The wet world has no scope of its own: its cost shows in `process_p50` and `setup_p50`.

**Noise.** `noise` is the spread between the two rounds. A delta against a baseline is called a change only when it exceeds the largest of the two spreads and a per-metric floor (0.3 ms for medians and passes, max(1 ms, 25 %) for tails, 3 % for fps, 64 MB VRAM (set from the quick A/B repeat on 2026-11-16: identical code differed by up to 0.25 ms in GPU p50 at 9 km and 60 MB VRAM)); otherwise it is "within noise". Tail metrics (p99, max, hitches) are intrinsically noisy: repeat before believing one.

**Machine record.** GPU, driver, enforced power limit, CPU, RAM, OS, engine, display on, refresh rate, the P-state share, power, clocks, temperature and throttle reasons while the GPU was busy (`power_log.csv`, 1 s), resolution, preset, shadow atlas, SSAO/SSR/AA, git branch and HEAD, the uncommitted files and a hash of the uncommitted diff. Numbers are only comparable on the same GPU at P0 (`perf-capture` skill, section 0).

**Stills** (`-Stills`). After the measurements, one quiet still per `still` scenario (clouds, grass and gust debris hidden, clocks, wind, TAA and cloud interleave held, HUD hidden). With `-Baseline`, `tools/media/ffmpeg_tools.ps1 compare` gives SSIM, PSNR and a heatmap per pose, listed in the report. Judge the heatmap, not only the number.

**VSync** (`-VsyncOn`). The scenarios marked `vsync_pass` run again as `<name>@vsync` with VSync as shipped. If the display is off (VSync then runs at about 4 fps), the pass is skipped and noted.

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
