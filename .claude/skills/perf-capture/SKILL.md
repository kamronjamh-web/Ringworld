---
name: perf-capture
description: Measure Ringworld CPU/GPU cost reliably - choose the right harness (viewport totals, per-pass GPU timestamps, A-B-A ablation), avoid the known traps (night pose, paused clocks, A1000 throttling, unapplied cloud settings), compute median/p95 with a noise floor, and report a before/after table. Use for any performance claim, optimisation, or regression hunt.
---

# Performance capture

## 0. Rules that invalidate results if ignored

- **Machine:** numbers are only comparable on the same GPU. Record the `Using Device` line.
  - **A1000:** valid only at P0 / 35 W (±0.6) with the memory clock at 5300-5700 MHz.
  - After 45-60 min it can drop to P5 / 20 W / 810 MHz, which makes frames about 3.3x slower.
  - The WD19TB dock once capped it at 20 W. Run undocked, or verify 35 W.
  - Log alongside every run:
    `nvidia-smi --query-gpu=timestamp,pstate,clocks.gr,clocks.mem,power.draw,enforced.power.limit,temperature.gpu,clocks_event_reasons.active --format=csv -lms 500 > power_log.csv`
    Redirect the output; `-f` buffers until exit.
- **GPU exclusive:** no other Godot instance may be running. See the `godot-test-run` skill.
- **Settings:** VSync off and `Engine.max_fps = 0`. State the resolution and quality preset.
  The usual setups are 1600x900 Medium (A1000 ranking) and 1600x900 High (4070 phase-C).
- **Noise:**
  - Main-viewport run-to-run noise is 3-6 ms, and deltas under about 2 ms are noise unless repeated.
  - Use A-B-A(-B-A) rounds, windows of 8 s or more (or ≥300 frames) after streaming settles, and report the median.
  - After a teleport, wait for grass and tree fills to finish (1-3.5 min of worker activity).
- **Pose time of day:** `FarRingGameplayCapture` fixes the clock at 4506.117868, which is **night**.
  - At night the cloud sun-light march is skipped, so cloud A/B tests there show nothing.
  - Call `RingDayNightCycle.SetCycleFraction(0.6)` after `SetPose` for noon.
- **Clocks:** the harnesses pause weather and celestial clocks after warmup. That hides wind material writes and far weather atlas cost, so also measure with the clocks running.
- **Cloud settings:** `RaymarchSteps`, `LightingSteps` and `CloudLodBias` changed at runtime need `ApplyTemporalQualityTuning()`; `ApplyCloudAppearanceTuning()` alone does not push them.
- **Accounting:**
  - Viewport GPU timings lag the renderer by a few frames.
  - Never sum parent and child intervals. `GPU.Clouds` is inclusive; its child stages are not additive.
  - Never add viewport time to compute time.
  - "Unavailable" is not zero.

## 1. Pick the harness

| Question | Harness |
|---|---|
| Menu, load or fly-over cost, memory, variants | `tools/menu_altitude_perf_capture.ps1` (viewport totals, mean/p50/p95/p99) |
| Fixed gameplay pose, ablate a subsystem | `tools/far_ring_ablation_capture.gd` with `FarRingGameplayCapture.cs` (A-B-A, `--detailed-gpu`) |
| Shadow feature cost at named poses | `tools/shadow_attribution_test.gd` (A-X-A matrix, power-gated, `delta_ms` with a `baseline_repeat` noise floor) |
| Per-pass GPU for a feature | `RingPerformanceGpu` markers ("Detailed GPU intervals"), or `tools/ShadowTimingBridge.cs` in a custom harness |
| Load or menu hitches | `menu_warm_stall_probe.ps1` + `warm_stall_report.py` (see the `log-triage` skill) |
| Subsystem microbenchmarks | `grass_compute_benchmark.gd`, `TreeGpuBenchmark.cs`, `sunshine_gpu_benchmark.gd`, `terrain3d_gameplay_travel_benchmark.gd`. Read each header first. |

## 2. Commands that have been used before

```powershell
# menu/altitude: the first run copies caches, later runs reuse them
.\tools\menu_altitude_perf_capture.ps1 -Label perf_<task> -OutLabel before -Cache copy -ScriptArgs '--plan=a:s16,a:m,a:s23'
.\tools\menu_altitude_perf_capture.ps1 -Label perf_<task> -OutLabel after  -Cache reuse -ScriptArgs '--plan=a:s16,a:m,a:s23'
#   extra flags: --pause-clocks, --cycle=auto|keep|<fraction>, --sample=20, --settle=20, --load-only
#   results: .godot\phase_c_p0\<OutLabel>\results.json ; console lines 'P0 STEP <label> wall_mean= p95= gpu_sum='
#   NOTE: this runner does not exit with Godot's code; check P0_RESULT_FILE= in the log.

# fixed-pose ablation (A-B-A)
& $exe --path . --rendering-method forward_plus --resolution 2548x1320 --script res://tools/far_ring_ablation_capture.gd -- `
  --detailed-gpu --artifact-label <label> --cases sunshine-off,grass-render-off `
  --warmup-seconds 30 --collection-seconds 10 --settle-seconds 1
#   output: .godot\far-ring-gameplay\<label>-...\ablation-suite.json

# shadow attribution (~2 h full; --rounds=2 --seconds=8 ~1 h; --quick for smoke)
& $exe --path . --resolution 1920x1080 --script tools/shadow_attribution_test.gd -- --poses=forest_noon,alt_1km --labels=<labels>
#   read report.json -> poses.<pose>.summary.<label>.delta_ms
```

For a custom per-pass measurement:
1. Enable `RingPerformanceMonitor.DetailedGpuEnabled`, or call `ShadowTimingBridge.Begin(engineTimestamps)`.
2. Call `Sample()` once per frame.
3. Call `Window()` per window.

Godot 4.8-dev7 engine timestamps give only `vp_begin/vp_end` per viewport. There are no shadow, opaque or sky regions, so put your own `BeginScope` around the passes you care about.

## 3. Report

Give:
- the machine and power state, resolution, preset, pose, time of day, and whether clocks were paused
- the window length and number of rounds
- this table:

| metric | before (median) | after (median) | Δ | noise floor |
|---|---|---|---|---|

Add p95 when hitches matter. Call out any rejected windows. Put durable results in the feature's doc in `docs/` (perf sections) or in `docs/benchmarks/`. Raw run folders stay in `.godot/` and get pruned.
