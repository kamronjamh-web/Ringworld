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

## Indexed mesh comparison

The separate `--grass-indexing-matrix` mode compares the adopted geometry: two-segment near clumps, one intermediate ribbon per planted source, full intermediate lighting, and camera facing at strength 1 from 0 to 1 m. It changes only vertex indexing between its visible cases. The hidden control retains the indexed mesh and identical compute requests with draw layers zero. Roots, clump sources, 64 tiles, 128 compute outputs, camera, light, LOD densities, and frozen wind clock stay equal. Alternate repetition order reverses the indexed/unindexed pair.

```powershell
& $godot --path C:\Projects\Ringworld --rendering-method forward_plus --script res://tools/grass_compute_benchmark.gd -- --grass-indexing-matrix --grass-compute-benchmark-quick --grass-compute-roots 32768
& $godot --path C:\Projects\Ringworld --rendering-method forward_plus --script res://tools/grass_compute_benchmark.gd -- --grass-indexing-matrix --grass-compute-roots 1118430 --grass-compute-warmup 60 --grass-compute-samples 180 --grass-compute-repetitions 3
```

Outputs go to `artifacts/grass-indexing-benchmark/`, independently of the historical geometry matrix. Archive this directory between builds. `report.json` and `raw-samples.json` contain viewport timings; `indexing-settings.json` records actual near/intermediate vertex, index, and triangle counts plus rendered equivalence results. Each repetition captures combined, near-only, and intermediate-only images for hidden, unindexed, and indexed cases outside timed windows. Each visible unindexed pass must differ from its hidden control in at least 100 pixels; an indexed pair passes when at most 0.1% of RGB8 pixels differ by more than two channel levels. The report retains the maximum and mean channel difference to expose tolerance effects. Inspect paired captures as well as these numerical checks.

This remains an isolated, stationary raster fixture with disabled wind; it does not establish gameplay FPS, motion appearance, streaming costs, or laptop certification. Unchanged-input caching can yield zero timed compute dispatches, and unavailable compute timestamps are not zero-cost measurements. Run GPU profiles one at a time with the game closed. Quick runs are smoke checks; full runs require at least three repetitions. Indexing results must be recorded only after running the fixture; the historical measurements above predate this comparison.

### Latest verified clean indexing result, October 2, 2026

After the user closed the gameplay process, the primary agent verified that only the Godot editor remained (PID 40904) and reran the same RTX 4070 / Forward+ / 1280x720 fixture. Before the clean run, GPU utilization was approximately 6%, with 1,961 MiB used memory and a 210 MHz idle clock. The code remained unchanged after its passing build and correctness tests.

Both clean runs retained 1,118,208 represented blade roots, 279,552 clump sources, 64 tiles, and 128 compute outputs. The standard run used 60 warmup frames and 180 samples per case; the extended confirmation used 600 warmup frames and 1,800 samples. Each used three alternating-order repetitions.

| Repetition | Standard unindexed GPU median | Standard indexed GPU median | Extended unindexed GPU median | Extended indexed GPU median |
| --- | ---: | ---: | ---: | ---: |
| 1 | 0.700 ms | 0.538 ms | 0.674 ms | 0.537 ms |
| 2 | 0.675 ms | 0.573 ms | 0.674 ms | 0.561 ms |
| 3 | 0.643 ms | 0.590 ms | 0.653 ms | 0.548 ms |
| Median of repetition medians | 0.675 ms | 0.573 ms | 0.674 ms | 0.548 ms |

Every clean indexed repetition was faster than its matched unindexed repetition. The standard run reduced whole-fixture viewport GPU time by approximately 15%; the extended confirmation reduced it by **18.69%, or 0.126 ms**. Extended hidden-control GPU medians were 0.278, 0.279, and 0.279 ms. The extended wall-time median of repetition medians fell from 0.929 to 0.798 ms. These are verified improvements for this isolated, stationary rendering fixture; they do not imply a 19% gameplay FPS gain.

All nine extended rendered comparisons again had exactly zero maximum and mean channel difference, with the same populated-pixel counts: 314,298 combined, 276,027 near, and 60,730 intermediate. Indexing changed near vertices from 48 to 24 and intermediate vertices from 24 to 16 while preserving 16 and 8 triangles respectively. Both visible cases retained 58 draw calls and 2,556,988 submitted primitives, including the floor. Density and source counts did not change; timed compute dispatches remained zero after warmup. Active gameplay performance, moving/windy appearance, active-compute cost, and CPU-cache/sampler performance attribution remain unmeasured.

The current `artifacts/grass-indexing-benchmark/` reports, raw samples, settings, and captures contain the extended clean run. The standard clean run is archived at `artifacts/grass-optimization-validation/indexing-clean-standard/`; logs are `artifacts/grass-optimization-validation/indexing-clean.log` and `indexing-clean-extended.log`. The earlier contended run below is archived separately at `artifacts/grass-optimization-validation/indexing-contended/`.

### Archived contended indexing result, October 2, 2026

The primary agent reviewed the changes and ran the quick and full indexing fixtures on NVIDIA RTX 4070, Forward+, 1280x720. The full run used 60 warmup frames, 180 samples, and three alternating-order repetitions, with 1,118,208 represented blade roots, 279,552 clump sources, 64 tiles, and 128 compute outputs.

| Repetition | Unindexed viewport GPU median | Indexed viewport GPU median |
| --- | ---: | ---: |
| 1 | 0.860 ms | 0.804 ms |
| 2 | 0.749 ms | 0.761 ms |
| 3 | 0.685 ms | 0.711 ms |
| Median of repetition medians | 0.749 ms | 0.761 ms |

This earlier run was an **exploratory, contended measurement** and established no reliable GPU speedup. Another Godot game remained active throughout that run; before the fixture the GPU reported approximately 35% utilization and 7,747 MiB used memory. The second hidden repetition had a 20.102 ms wall-time median, compared with 0.651–0.682 ms in the other hidden repetitions. These conditions prevent treating those paired values as an uncontended performance baseline or a gameplay FPS improvement. The clean comparison above supersedes this run for stationary rendering performance.

All nine rendered comparisons—combined, near-only, and intermediate-only across three repetitions—were exactly equal, with maximum and mean channel difference zero. Populated-pixel checks found 314,298 combined pixels, 276,027 near pixels, and 60,730 intermediate pixels. Indexing reduced the near clump from 48 to 24 vertices and the intermediate clump from 24 to 16 vertices, retaining 16 and 8 triangles respectively. Both visible cases submitted 58 draw calls and 2,556,988 primitives, including the fixture floor. Timed compute dispatches were zero after warmup, so this run supplies no active-compute cost comparison.

The accompanying interaction and modifier material caches and avoidance of a redundant sampler copy reduce repeated CPU work independently of vertex indexing. The primary agent reviewed those changes and their correctness checks; this frozen raster fixture does not attribute numerical frame-time or FPS gains to them. A matched moving-gameplay capture with other Godot processes closed remains necessary for performance attribution.

Primary validation passed: build with zero warnings/errors; headless mesh, geometry, material-cache, surface, streaming, and modifier-streaming checks; Forward+ integration, camera-facing, interaction, modifier-tint, and wind checks; and quick/full indexing fixtures. The wind fixture used its existing `--vegetation-wind-output` override to write `res://artifacts/grass-optimization-validation/wind-captures` after a sandbox denial of its default `user://` capture destination; the redirected run passed. A leaked test node in the new cache check was fixed and the check reran cleanly. Known missing native mesher and certificate diagnostics remain separate from the passing assertions. Validation logs are under `artifacts/grass-optimization-validation/`; this earlier run's indexing reports, samples, and captures are archived under `artifacts/grass-optimization-validation/indexing-contended/`. The latest clean extended artifacts occupy `artifacts/grass-indexing-benchmark/`.
