# Isolated grass render matrix

Run with a Godot Mono executable and Forward+ graphics:

```powershell
& $godot --path C:\Projects\RingworldTechDemo --rendering-method forward_plus --script res://tools/grass_compute_benchmark.gd -- --grass-render-matrix --grass-compute-benchmark-quick --grass-compute-roots 32768
& $godot --path C:\Projects\RingworldTechDemo --rendering-method forward_plus --script res://tools/grass_compute_benchmark.gd -- --grass-render-matrix --grass-compute-roots 1118430 --grass-compute-warmup 60 --grass-compute-samples 180 --grass-compute-repetitions 3
```

The quick run uses 12 warmup frames, 24 timed frames and one repetition; it is a smoke check. Full runs alternate case order over at least three repetitions. Root counts must be 32,768 through 2,097,152. Clump cases round actual roots down to multiples of 256, reported in each case. At 1,118,430 requested roots the fixture contains 1,118,208 represented blade roots and 279,552 clump source transforms, repeated across two compute LOD outputs (559,104 registered transforms total).

Six cases share one frozen camera, fixed lighting, disabled local wind, identical roots, density, compute and 64-tile layout: hidden grass; four-segment near with crossed intermediate ribbons; two-segment near; single-ribbon intermediate; simplified intermediate lighting; all three reductions combined. Every visible case enables camera facing with strength 1, start 0 and end 1 to isolate geometry count. Hidden grass uses draw layers zero while retaining identical compute submissions; its difference isolates drawing cost. The frozen camera overlooks near and intermediate distance ranges, but screenshot inspection is still required to confirm actual populated coverage.

Outputs overwrite `artifacts/grass-compute-benchmark/report.json`, `raw-samples.json`, `matrix-settings.json` and one PNG per case/repetition. Archive that directory between runs if both results are needed. Viewport GPU median is a whole-fixture measurement; GrassCulling timestamps measure the compute stage only. This 1280x720 fixture does not reproduce full-game FPS, production tile count, terrain backend, terrain streaming or a specific world location. It does not modify user preferences or saved worlds.

## Measured result, October 2, 2026

Root reviewed the implementation, ran the quick check, and ran three alternating-order repetitions with 60 warmup and 180 sampled frames per case. Hardware: NVIDIA RTX A1000 6GB Laptop GPU, Forward+, 1280x720, 1,118,208 represented blade roots / 279,552 clump transforms. Frozen requests use the production unchanged-input deduplication; all cases retain 128 active compute batches with zero timed dispatches after warmup, including hidden grass. Missing GrassCulling timings therefore do not represent measured zero-cost compute.

| Case | Median of three viewport GPU medians | Range of repetition medians |
| --- | ---: | ---: |
| Hidden draw layers | 0.94 ms | 0.94–0.98 ms |
| Four-segment near, crossed intermediate | 2.97 ms | 2.91–2.99 ms |
| Two-segment near | 2.77 ms | 2.73–2.77 ms |
| Single-ribbon intermediate | 2.39 ms | 2.38–2.40 ms |
| Simplified intermediate lighting | 2.97 ms | 2.94–2.98 ms |
| Combined options | 2.18 ms | 2.17–2.21 ms |

The combined change reduces this fixture's viewport GPU time by approximately 27%; it does not establish a production-game FPS gain or identify grass as the sole slowdown cause. Single ribbons provide the strongest isolated reduction. Lighting simplification does not show a meaningful benefit here and should remain experimental. Captures confirm populated foreground and distant grass in the fixture; in-game camera motion, wind silhouette, transitions, terrain edits, and the slowdown location still need visual acceptance and a matched performance capture.

The initial measurement used optional switches that defaulted off. After the user accepted the geometry recommendation, gameplay defaults enable reduced near segments and single-ribbon intermediate geometry; simplified lighting stays off. Controls are in the grave-key menu's Grass / Grass detail group, with camera-facing controls under Distant blades. Standalone layer and mesh-factory compatibility defaults remain unchanged. Geometry switches rebind resident renderers through the existing bounded upload queue without regenerating placements.

Gameplay camera facing defaults to full strength. Untouched facing distances adapt to the selected quality profile and saved LOD overrides, completing .5 m before the near/intermediate transition starts (end capped at 48 m). Explicit player geometry, lighting, and facing overrides retain priority. Single ribbons require full camera facing completed before that transition; crossed geometry remains selected when those conditions are absent. The capture-only profiler render-state snapshot records the requested geometry switches, single-ribbon eligibility, pending renderer uploads, and facing/LOD settings so the next slowdown report can identify the actual configuration.

Validation after default adoption: build passed with zero warnings/errors; geometry contract passed across every desktop and laptop quality preset, explicit override/fallback and reset behavior; live-menu apply/save/reload/reset passed in Forward+; the camera-facing GPU regression passed. In-game motion and appearance at the reported slowdown location still need acceptance; the original matrix above remains isolated evidence.
