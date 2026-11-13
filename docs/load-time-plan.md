# World load time plan (2026-11-12)

Survey and plan only. No code was changed. Author: Fable (acting Lead).

## Summary (plain language)

**Where we are (RTX 4070 desktop, measured today):**

| Load | Time now | What the wait is |
|---|---|---|
| Re-loading an existing world (no rivers) | 28 to 30 s | Half of it is one job: building the low-detail terrain of the whole ring. |
| New world, no rivers | 43.5 s | Two thirds of it is that same job, at a higher detail setting. |
| New world with rivers | about 58 s (43.5 + 14.5; not yet measured end to end) | The above plus the one-time river mapping. |

**What we found:**

- **One job dominates.** The whole-ring terrain ("global terrain") is built in 128 pieces, one piece at a time, on a single processor core, while most of the other 31 sit idle. It takes 26.5 s on a new world and about 12 to 15 s on a reload. Almost all of the ring it builds is too far away to see from where you arrive.
- **Work is done twice.** On a reload the game starts that job at the machine's default quality, then reads your saved quality setting, throws the work away and starts again. A new world also rebuilds a 10 s terrain summary that an earlier world with the same seed already built.
- **The river mapping waits its turn for no reason.** It starts only after 6 s of other loading has finished, and nothing else runs while it works.
- **The loading screen works in small sips.** Many steps are limited to a few milliseconds per frame, a rule made for smooth gameplay. Behind an opaque loading screen that rule only makes loading slower.
- Load time has not grown since October (27 to 28 s then, 28 to 29 s now), apart from the river mapping.

**What we propose:**

1. Build a measuring tool first (L0), so every later change is judged by numbers.
2. Quick fixes (L1): stop the double work, start the river mapping at once, run two early steps side by side.
3. Use all processor cores for the whole-ring terrain and its summary (L2).
4. Let far-away parts finish after you are in the world (L3), and keep per-machine caches so a repeated seed is instant (L4).

**Expected result on the desktop (estimates):** reload 28 s to about 10 to 13 s; new world 43.5 s to about 13 to 15 s; new world with rivers 58 s to about 22 to 24 s, or about 13 s visible if the river mapping may start while you are still on the New World screen.

**Your decisions:** five questions in section 7.

---

## 1. What is measured and what is not

**Measured today** (DESKTOP-RAVA9C1, RTX 4070, 32 logical CPUs, 2026-11-12 around 12:00, user away, working tree of `terrain-vegetation-revamp` as built):

- Three reloads of a copy of real slot 1 with `tools/menu_altitude_perf_capture.ps1 --load-only` (VSync off, uncapped).
- One new-world load on a fresh profile with `world_loading_smoke_test` through `tools/run_tests.ps1`.
- The T2R-9 river bake benchmark (`.godot/audit_baseline/DESKTOP-RAVA9C1/t2r/bench/20261112_115109/`).
- Evidence kept in `.godot/audit_baseline/DESKTOP-RAVA9C1/load/20261112_prelim/` (`summary.txt` and the three reload console logs). The new-world log was pruned after I read it; its numbers are in `summary.txt`.

**Earlier measurements used:** `docs/phase-c-p0-measurements.md` (2026-10-21, same desktop) and the 2026-11-04 smoke-test log (39.9 s new world).

**Not measured:**

- Any load on the A1000 laptop.
- A load with VSync on, which is how the game normally runs. Steps that are limited per frame will be slower there than in the numbers below.
- A new world with rivers, end to end. Rivers are still a review toggle; the 58 s figure is a sum.
- What the 3.2 to 3.6 s scene load spends its time on, and how the 6.6 s terrain stage splits between saved-state restore, region generation, upload and collision.
- The live-menu path (the world loading behind the start menu) since October.
- True cold-disk loads (the OS file cache was warm in every run).
- Every saving in section 3. They are estimates derived from the measured stage times.

One run per case, except three reloads (27.8, 29.3 and, with empty shader caches, 30.3 s). Treat differences under about 1.5 s as noise. Another agent's CPU tests may have been running during the new-world load (39.9 s on 2026-11-04 against 43.5 s today).

## 2. The load timeline

### 2.1 How loading is driven (established)

- `scripts/world_size_launcher.gd` `_start_game()` runs the sequence. `scripts/Loading/ring_world_loading.gd` owns the screen, the stages and the report. `scripts/Loading/RingWorldLoadingBridge.cs` holds player input, enables world children on a budget and reports readiness.
- Four stages: `preparing`, `terrain`, `surroundings`, `position`/`ready`.
- The root viewport has 3D disabled until the reveal, and all SubViewports are held.
- Every load already prints `WORLD_LOADING_REPORT` (stage times, operations, per-child `_Ready` times, slowest frames with pipeline-compile counts, per-owner status).
- The last-played world normally loads behind the start menu (Phase C live menu) and Continue flies in. The loading screen is used for New World, Advanced and other slots. Both paths run the same code; the menu path has no overlay and must keep the menu smooth.

### 2.2 Measured stage times (ms)

| Run | Caches | Total | preparing | terrain | surroundings | ready |
|---|---|---|---|---|---|---|
| Reload, slot 1, saved preset High | empty shader/pipeline | 30 260 | 6 662 | 8 128 | 14 577 | 876 |
| Reload, same | warm | 27 757 | 5 722 | 6 580 | 14 823 | 622 |
| Reload, same | warm | 29 323 | 6 184 | 6 980 | 15 581 | 566 |
| New world, fresh profile, default preset Very High | empty | 43 514 | 5 873 | 6 712 | 30 058 | 865 |
| New world, headless test, Medium (no GPU) | empty | 24 675 | 6 781 | 4 187 | 13 382 | 321 |

Operations inside `preparing` (reload warm / new world): game scene load 3 331 to 3 610 / 3 154; resource warm-up 2 111 to 2 222 / 2 578; instantiate about 50; configure about 18; attach 517 to 525 / 473 (1 092 with cold caches).

### 2.3 Step by step

"Main" means the main thread. Times are from the runs above.

| # | Step | What it does | Where it runs | Time | Notes |
|---|---|---|---|---|---|
| 1 | Recipe and slot setup | Validates the launcher settings, stores a new world's recipe | Main, before the screen appears | Unmeasured | |
| 2 | Game scene load | `VoxelRingIntegrationTest.tscn`: 81 scripts, 15 audio streams (12 MB), sub-scene, cloud and LOD resources | Engine loader thread; main only polls | 3.2 to 3.6 s | Re-read from disk on every load (`CACHE_MODE_IGNORE_DEEP`, a workaround for godotengine/godot#83762). Nothing else runs meanwhile. |
| 3 | Resource warm-up | 69 shaders, textures and compositor scripts requested; 55 shaders compiled; C# pre-JIT of 59 306 methods; terrain colour palette averaged | Shader compile on 1 worker (1.3 to 1.9 s); pre-JIT on 2 to 6 workers (0.7 to 0.9 s, once per process); palette on main in row slices (once per process) | 2.1 to 2.7 s | Starts only after step 2 ends. |
| 4 | Instantiate and configure | Builds the scene tree, applies ring size and hardware tier | Main | 0.07 s | |
| 5 | River mapping (rivers worlds only) | Bakes or loads the river graph | Workers and GPU; main only polls | 14.25 to 14.75 s cold, 0.49 s warm | Grid 1.58, conditioning 6.54, routing 1.10, graph 3.71, pack 0.97. Starts after steps 2 to 4. Nothing else runs meanwhile. Shown as 2 % of the progress bar. |
| 6 | Attach | `_Ready` of about 100 world children | Main | 0.5 s warm, 1.1 s cold | Largest: `WeatherAmbience` 384 ms cold (37 warm), `TerrainLodCoordinator` 73 to 105, `ScaledFarViewport` 64 to 104, audio owners about 45 to 56 each when cold. |
| 7 | Activation | Children are disabled at attach and re-enabled a few per frame | Main, 8 ms per frame budget | About 75 to 85 frames | Overlaps step 8. |
| 8 | Terrain stage | Saved state restore; Terrain3D generates 9 regions around the arrival point, uploads them and builds collision | Region generation on workers; upload and collision on main under gameplay budgets | 6.6 to 7.0 s warm, 8.1 s cold | Cold caches add pipeline compiles here (hitches of 160 to 1 200 ms). The world's saved graphics preset is applied here (see 3.2). |
| 8a | Structural hull, then bedrock occluders | Ring structure meshes | Main, 3 ms or 4 units per frame | Done about 2.5 s and 7.7 s after attach | Overlaps step 8. |
| 8b | Terrain summary (min/max pyramid) | Height range of the whole ring in 4 km cells; also macro shadows | One worker | 9.8 s when generated; 0.05 s from cache | Cached in the world's own `lod_cache`, so every new world rebuilds it. The global terrain cannot start before it. |
| 8c | Far view | Far structure, cloud shell, cloud canopy, first weather atlas, coarse far terrain | Main, 1.5 to 4 ms per frame | Ready within about 14 s of attach | Never the last one ready in these runs. |
| 8d | Trees and emitter spires | Tree templates and the first tree streaming pass; emitter pool | Sliced on main plus workers | Spires ready within 4 s; trees within about 14 s | Never the last one ready. |
| 9 | Global terrain | Whole-ring low-detail terrain: 128 segments of 4096 x 256 vertices (Very High) or 4096 x 128 (High) | Sampling on one worker, one segment at a time; publish on main, one per frame | New world 26.5 s (207 ms per segment, measured). Reload about 12 to 15 s (estimated from the surroundings stage) | From 14 s after attach this is the only thing the load waits for. |
| 10 | Position and reveal | Settle the player on collision, release viewports, render the first world frame, 0.3 s fade | Physics and main; GPU | 0.6 to 0.9 s | The first frame compiles 63 to 71 pipelines even with warm caches (240 to 510 ms). |

### 2.4 New world compared with reload

- **New world:** no saved state; the terrain summary is generated (9.8 s on one core); the default preset for this desktop is Very High, so the global terrain is twice as heavy; rivers worlds add the 14.5 s bake.
- **Reload:** the terrain summary loads from the world's cache in 50 ms and the river graph in 0.49 s. The global terrain is still rebuilt from scratch every time, and is started twice (3.2).
- **Empty shader and pipeline caches** (first run on a machine or after a driver update) add 2.5 to 5 s and several long hitches. Godot's shader cache and the Vulkan pipeline cache already persist per machine.

## 3. Ranked opportunities

Savings are for the 4070 desktop and are estimates unless marked measured. Effort: S is under a day, M is a few days, L is a week or more.

| # | Opportunity | Kind | Saving: reload | Saving: new world | Risk | Effort |
|---|---|---|---|---|---|---|
| 1 | Build global terrain segments on several workers | Parallel | 9 to 11 s | 20 to 22 s | Low to medium | M |
| 2 | Apply the saved graphics preset before attach (stop the double build) | Done twice | 4 to 6 s now; about 1 s after #1 | 0 | Low | S |
| 3 | Start the river bake when loading begins, overlapping the scene load | Parallel | 0 | 5.5 to 6.5 s (rivers worlds) | Low | S to M |
| 4 | Build the terrain summary on several workers | Parallel | 0 | About 7 s once #1 is in | Low | S to M |
| 5 | One loading-screen work budget; palette off the frame loop | Budget | 1 to 3 s (more with VSync) | 1 to 3 s | Low | M |
| 6 | Load the scene and warm resources at the same time | Parallel | About 2 s | About 2 s | Low | S |
| 7 | Enter when the visible global terrain is ready; stream the rest | Lazy | 2 to 3 s after #1 | 2 to 3 s after #1 | Medium | M |
| 8 | Per-machine caches keyed by recipe (terrain summary, river graph) | Cache | 0 | Up to 24 s for a repeated seed | Low | M |
| 9 | Speculative work on the New World screen (scene preload, river bake) | Parallel | 0 | Up to 5.5 s, plus up to 14.5 s with rivers | Medium | M |
| 10 | Pipeline pre-warm for first run | Shader | 0 on later runs | 2.5 to 5 s on a first run; fewer hitches | Medium | M |
| 11 | Lazy audio banks and small load-path cleanups | Lazy | Up to 0.5 s | Up to 0.5 s | Low | S |
| 12 | Cache the built global terrain per world | Cache | Only if #1 and #7 leave more than 3 s | 0 | Medium | M |

### 3.1 Global terrain on several workers (#1)

- **Established:** `RingTerrainGlobalLod.ServicePreparedSegment` keeps one pending segment and one sampling task. 128 segments run in sequence at 207 ms each (measured, Very High). `MaxSegmentRebuildsPerFrame` is 1.
- **Proposal:** sample up to N segments at once (N from the logical CPU count, capped, lower in menu mode), each with its own sampler copy, and publish in the existing order. Raise the publish cap while the loading screen is opaque.
- **Estimate:** 26.5 s to about 4 s with 8 workers; the reload build to 2 to 3 s.
- **Risk:** contention with Terrain3D generation workers and the tree streamer during the terrain stage; memory for N sampler copies. Edits and the morph snapshot keep their current order, so results stay identical.

### 3.2 Stop the double build (#2)

- **Measured in both reload logs:** "Graphics preset applied: Very High", then "High"; "Segmented global terrain HLOD queued" twice (256 across, then 128); macro shadows built at both settings.
- **Cause:** the launcher sets the hardware default (`world_size_launcher.gd`, `selected_graphics_preset`), and `RingWorldSaveManager` applies the world's saved preset during the staged load (`RingWorldSaveManager.cs:1061`), which restarts the global terrain.
- **Proposal:** read the saved preset from the world state before attach and set it as `DefaultPreset` on `GraphicsQuality` and `TerrainLodCoordinator`.
- **Estimate:** the first build runs for roughly the terrain stage (6.6 s) before it is discarded. Needs L0 timestamps to confirm.
- Affects any world whose saved preset differs from the hardware default, which includes the user's own slot 1.

### 3.3 River bake earlier (#3)

- **Established (T2R-9):** the graph must exist before the world is attached, because terrain sampling depends on it. `BeginHydrology` captures its inputs from the configured game node, so it starts after the scene load, the warm-up, instantiate and configure.
- **Proposal:** derive the bake inputs from the stored recipe and start the bake at `begin`. The existing identity check guards against a mismatch. The loading stage then waits only for what is left.
- **Estimate:** hides 5.5 to 6.5 s of the 14.5 s.
- **Not proposed now:** attaching part of the world (clouds, far cloud shell, audio) during the bake. It needs the attach to be split and is a large change for about 2 s.
- **For the T2 owner, not this plan:** conditioning (6.54 s) and graph (3.71 s) are 70 % of the bake.
- **Progress bar:** the bake has 2 % of the bar for 14.5 s. Rebalance all stage fractions from L0 numbers.

### 3.4 Terrain summary on several workers (#4)

- **Established:** `RingTerrainMinMaxPyramid.BuildBaseline` fills 598 260 cells on one worker (8.5 to 9.8 s measured, three runs). The global terrain waits for it.
- **Proposal:** split the base rows across workers with independent sampler copies.
- **Estimate:** 9.8 s to 1 to 1.5 s. After #1, a new world's wait after attach drops from about 14 s to the terrain stage's 6.7 s.
- Unmeasured: the cost with rivers on.

### 3.5 Loading-screen budget (#5)

- **Established:** staged owners use gameplay-sized limits: resource warm-up 3 ms or 4 units per frame; activation 8 ms; hull and occluders 3 ms or 4 units; far weather atlas 4 ms; Terrain3D upload and collision at their gameplay budgets. The palette averages 5 textures in 640 row steps, 4 per frame: about 160 frames for a few milliseconds of real work.
- **Derived, not measured:** the palette's 160 frames and the 35 frames of resource requests cost about 0.5 s in today's uncapped runs and would cost about 3 s at 60 fps with VSync.
- **Proposal:** one shared budget that owners read. About 12 ms per frame while the loading screen is opaque; the current small values in menu (background) mode. Compute the palette on a worker, or cache the 5 colours per build.
- These steps are off the critical path today because the global terrain is slower. They become the limit after #1 and #4, so measure them in L0.

### 3.6 Scene load and warm-up together (#6)

- The scene load runs on the engine's loader thread and the warm-up on workers, but they run one after the other. Start the warm-up at `begin`.
- **Estimate:** the 2.1 to 2.7 s warm-up hides under the 3.2 to 3.6 s scene load.
- L0 should break down the scene load. If the audio streams or the sub-scene dominate, more can be deferred.

### 3.7 Enter when the visible global terrain is ready (#7)

- **Established:** the launcher sets the global terrain's fade end to 850 km and its regional visibility to 1 000 km. One segment covers 245 km of the ring, so about 10 of 128 segments can be seen from the arrival point. The far arch is drawn by the far viewport, which has its own coarse surface. A hull roof already stands in for a segment that is not ready.
- **Proposal:** build nearest-first, enter when the segments within the visibility distance are published, and finish the rest in the background.
- **Risk:** the morph snapshot is published only when all segments are staged; its consumers must be checked (Lead design). A fast flight in the first seconds could reach plain hull.
- With #1 in place this saves only 2 to 3 s. Do it after #1, and only if L0 shows the global terrain still holds the load.

### 3.8 Recipe-keyed machine caches (#8)

- **Established:** the terrain summary cache name already contains generator version, seed, terrain hash and ring size, but the file lives in the world's own `lod_cache`. The river graph is also stored per world.
- **Proposal:** look in a per-machine cache (`user://ring_cache`, with the existing budget and pruning) before building, and copy or link into the world.
- **Benefit:** a second world with the same seed and recipe (the showcase seed 1337, and every fresh-profile test) skips 9.8 s and 14.5 s. The test suite gets shorter too.

### 3.9 Speculative work on the New World screen (#9)

- The scene load and warm-up do not depend on the seed. They could start when the New World screen opens.
- The river bake could start for the seed shown and be cancelled when a setting changes.
- **Risk:** CPU and memory use in the menu; interplay with tearing down the warm world. Needs a user decision (question 3).

### 3.10 Shader and pipeline pre-warm (#10)

- **Established:** shaders are compiled on a worker before attach (`RingStartupWarmup`), and Godot's shader and Vulkan pipeline caches persist. Pipelines are still compiled at first draw: 63 to 71 on the first world frame with warm caches, 60 to 210 per slow frame with empty caches.
- **Strategy:**
  1. Keep the current shader warm-up.
  2. In L0, record pipeline compiles per stage and for the first 10 s of play.
  3. If the first-frame or first-seconds hitch exceeds about 100 ms, add a warm-up pass behind the loading screen: draw one small mesh per material family (terrain, proxies, water, grass, trees, POI, clouds, emitter beams) in a tiny SubViewport for two frames. `docs/emitter-poi-streaming-spec.md` already proposes this for POI slot 0.
- This is mostly a smoothness gain. It saves time only on a first run.

### 3.11 Lazy audio and small cleanups (#11)

- Load the megastorm, hazard, field-strike, containment and emitter-shot audio banks on first need with a threaded request. A storm gives minutes of warning. Saves main-thread time at attach (about 0.3 to 0.5 s cold).
- Diagnostics that run on every load: the certified runtime self-test, the LOD self-test, the min/max self-test and the hydrology and elevation audits. Cost unmeasured; move to tests or a debug flag if L0 shows more than 100 ms.
- Macro shadows are rebuilt 4 to 6 times during a load (175 to 360 ms each on a worker). Hold rebuilds until the global terrain is complete.
- Megastorm, containment field, eclipse and emitter systems cost under 5 ms each at start-up. Lazy creation would save nothing; leave them.

### 3.12 What must be ready before the first frame

**Must stay before entry:**

- The river graph (terrain correctness).
- Saved state, and Terrain3D visuals and collision around the arrival point.
- The player settled on solid ground.
- The global terrain within visibility distance, the far viewport's coarse surface, far cloud shell and first weather atlas (the sky and the arch).
- Trees around the arrival point (the loading screen path only; the live menu already skips them).
- Compiled shaders for everything visible.

**Can follow after entry:**

- Global terrain segments beyond the visibility distance.
- Macro shadow refinements and adaptive far-terrain refinement (the latter already streams).
- Rare-weather audio banks.
- Cache writes and audits.
- The live tuning drawer (already built on first open).

## 4. Measurement harness (L0)

**Goal:** one command per scenario that writes a comparable timeline, so each later work order reports before and after.

**Extend, do not replace:**

- `ring_world_loading.gd` `_finish_report`: add start and end offsets for each stage and operation, and the run's settings (preset, VSync, frame cap, new or existing world, rivers on or off, whether the shader, pipeline and world caches existed, build configuration, commit).
- `RingWorldLoadingBridge.CaptureReadiness`: record the time each structure owner first reports ready, and report it in `GetStartupDiagnostics`. This answers "who held the stage open" without reading logs.
- A small static `RingLoadTimeline.Mark(name, detail)` (C#), called at existing log points: river bake stages, terrain summary ready (cache or generated), global terrain queued and ready (with a restart count), hull and occluders done, saved state ready, regions generated, collision ready, tree templates ready, far weather atlas ready, graphics preset applied.
- Pipeline compiles per stage (the loader already samples the counters each frame), and for 10 s after entry.
- `tools/menu_altitude_perf_capture.ps1 --load-only` is the model for the runner: isolated profile, cache modes, GPU queue. `tools/menu_warm_stall_probe.ps1` covers the live-menu path. The T2R-9 benchmark covers the inside of the river bake.

**New runner `tools/load_time_benchmark.ps1`:**

- Scenarios: `new` (fresh profile, certified preset), `new-rivers`, `reload-cold` (slot copy, empty caches), `reload-warm` (three runs, median), `menu-warm` (boot to warm world ready behind the menu).
- Options: `-VSync on|off`, `-Preset`, `-Slot`. It takes the GPU queue itself and prunes its profiles.
- Output: `.godot/audit_baseline/<machine>/load/<yyyyMMdd_HHmmss>_<scenario>/timeline.json` and `timeline.md` (stage table, owner ready times, marks, slowest frames).
- `-Compare <a> <b>` prints stage and mark deltas with the noise floor from the three warm runs.
- Manifest entry: kind `tool`, tier `manual`, tags `loading`, `perf-telemetry`.

**Baselines to record:** desktop and laptop; VSync on and off; all five scenarios.

## 5. Phased work orders

Each phase reports before and after from the L0 runner. `claude-lead` designs where noted; `implementer` does the bounded changes.

| Phase | Goal | Work orders | Systems | Verification | Depends on |
|---|---|---|---|---|---|
| L0 | Measure | L0-1 report offsets, owner ready times, `RingLoadTimeline` marks. L0-2 runner, compare mode, manifest entry. L0-3 baselines on desktop and laptop. | Loading scripts, bridge, log points, `tools/` | Runner output for all five scenarios; marks sum to within 5 % of the total; existing loading tests pass | None |
| L1 | Quick wins, no behaviour change | L1-1 saved preset before attach. L1-2 warm-up in parallel with the scene load. L1-3 river bake from the recipe at `begin`. L1-4 palette on a worker or cached. L1-5 stage fractions from measured times. | Launcher, save manager, `RingWorldLoadingResources`, bridge, hydrology inputs | Global terrain queued once per load; river graph identity unchanged; `world_loading_*` and river cache tests pass | L0. L1-3 needs a short Lead design and the T2R-10 work to be landed |
| L2 | Use the cores | L2-1 parallel global terrain segments (Lead design first). L2-2 parallel terrain summary. L2-3 shared loading budget read by staged owners. | `RingTerrainGlobalLod`, `RingTerrainMinMaxPyramid`, staged owners, bridge | Byte-identical summary cache and segment meshes against the serial build; menu path frame times not worse (`menu_warm_stall_probe`); gate suite | L0, L1-1 |
| L3 | Lazy and streamed | L3-1 nearest-first global terrain and entry on visible segments (Lead design: morph snapshot consumers). L3-2 lazy audio banks. L3-3 hold macro shadow rebuilds during load; self-tests off the load path. | Global terrain, bridge readiness, audio owners | Capture at arrival matches before and after (`compare`); fast-flight check for hull fallback; no audio pop on first storm | L2-1, user answer 2 |
| L4 | Caches and first run | L4-1 recipe-keyed machine cache for the terrain summary and river graph. L4-2 pipeline pre-warm pass if L0 shows the need. L4-3 global terrain cache only if still needed. | Cache budget code, hydrology store, warm-up | Second same-seed world skips both builds; cache budget respected; first-run hitch numbers | L0, user answer 4 |
| L5 | Menu-time work | L5-1 scene preload on the New World screen. L5-2 speculative river bake. L5-3 budgets for the live-menu path. | Start menu, launcher, loading owner | Menu frame times; cancel and settings-change paths; no leaked loads | L1 to L2, user answer 3 |

**Amended 2026-11-12:** the L2-3 cell above is superseded. "L2-3 work orders" at the end of this file holds the current orders. It also takes in L3-1 (as L2-3d-3) and the terrain summary and river graph part of L4-1 (as L2-3f).

**Success criteria (desktop, warm caches, proposed):** reload at most 13 s after L2 and 10 s after L3; new world without rivers at most 15 s; new world with rivers at most 25 s. Laptop targets are set after L0-3.

## 6. Risks

- **The laptop is unmeasured.** It has fewer cores, so the parallel gains are smaller and the river bake is longer.
- **VSync.** Today's numbers come from uncapped runs. Frame-limited steps may cost more in normal play, which makes #5 more valuable than estimated.
- **Worker contention.** Parallel builders compete with Terrain3D generation, the tree streamer and the river bake. L2 needs one worker budget for the load, not a pool per system.
- **The menu path.** Larger work slices would make the live menu stutter. Every budget change must keep menu mode on its current values unless L5 decides otherwise.
- **Determinism.** Parallel builds must give the same bytes as the serial ones; the caches and goldens depend on it.
- **Overlap with T2.** L1-3 and L4-1 touch the hydrology store while the rivers work is active. Schedule them after T2R-10.

## 7. Questions for the user

1. **Which load matters most, and what is the target?**
   Recommended: Continue and reload first (at most 10 s on the desktop), then a new world with rivers (at most 25 s on the desktop). A first launch with empty shader caches may take 5 s longer.
2. **May distant parts of the ring finish after you are in the world?**
   The land within about 850 km would be ready on entry. The rest of the ring, which cannot be seen from the arrival point, would complete within about 10 to 20 s. A very fast flight in that window could show plain hull.
   Recommended: yes.
3. **May the game start work while you are still on the New World screen?**
   Recommended: yes for the scene preload (it does not depend on the seed). Decide on the speculative river bake after L2, when its real saving is known.
4. **May the game keep a per-machine cache shared by worlds with the same seed and settings?**
   About 20 MB per recipe (river graph 15 MB, terrain summary 5 MB), inside the existing 512 MB cache budget with automatic pruning.
   Recommended: yes.
5. **May the loading screen animate less smoothly (about 30 fps) so that loading finishes sooner?**
   The live menu would keep its smooth, slower loading.
   Recommended: yes.

## User answers (2026-11-12)

1. **Target:** "I don't want to peg you to a number, just do what is possible without drastically changing the look of the game." There is no fixed time target. Every optimisation must leave the look of the game unchanged, and look changes need a before/after comparison.
2. **Lazy distant segments:** yes. Unseen ring segments may finish 10-20 s after entry. Plain hull on a very fast flight is acceptable.
3. **Work on the New World screen:** not asked. Default: yes for the scene preload; decide on a speculative river bake after L2.
4. **Per-machine cache shared by same-seed worlds:** yes (about 20 MB per recipe, inside the 512 MB budget).
5. **Loading-screen frame rate:** "30 fps until the world is finished loading." The cap applies only while loading, and normal frame pacing returns at entry.

## L0 results (2026-11-12, RTX 4070, warm caches, VSync off)

- Reload (3 runs, median): 26.1 s. The saved preset is applied at 11.6 s and restarts the global terrain (plan 3.2 confirmed).
- New world (3 runs, median): 41.2 s. **Caveat:** this used backend 1 (Terrain3D surface), not what the start menu creates. The benchmark now uses backend 2 for `new` as well, so the next `new` baseline must be re-measured.
- New rivers world (1 run, backend 2): 62.6 s. Terrain stage 25.3 s, of which the river bake is 14.6 s. Surroundings 31.7 s.
- **New finding: a single frozen frame of 5.5 s at attach in rivers worlds** (0.49 s without rivers). This is probably the T2R-10 lazy sampler being built on the main thread at first read; it is unconfirmed. This goes first in L1: the user sees it as a hang.
- Output: `.godot/audit_baseline/DESKTOP-RAVA9C1/load/20261112_l0/` and `20261112_l0_rivers_fix/`.

## L1 results (2026-11-12, part 1)

- **Attach freeze fixed.** In rivers worlds, attach went from 5.5 s to 0.46 s. The cause was a serial stem lateral-reach measurement on the first sampler read (`RiverCarveOperator` constructor). It is now parallel and warmed on the hydrology worker, behind the loading screen.
- **Reload double build removed.** The saved preset is applied before the global terrain is queued. The reload total did not change (23.0 s → 22.9 s) because the single build now runs under contention. Global terrain is still the bottleneck, so L2 is the real win.
- Medians: reload 22.98 → 22.92 s; new 39.15 → 39.31 s; new-rivers 62.46 → 57.88 s.
- Not done yet: the 30 fps loading cap and the other L1 items.

## L2-1 results and the entry gate (2026-11-12, uncommitted)

**L2-1 (parallel global terrain segments)** cut the global terrain from 29 s to 10 s on a new world. Segment output is bit-identical to the serial build.

**What it exposed.** The loading screen never waited for the terrain proxy levels (`TerrainLodCoordinator`, 15 levels). They need about 23 s after attach on a new world (8 workers, 1 947 sectors) and about 21 s on a reload (3 workers at High), with or without L2-1. The old 23 to 39 s loads hid this. With L2-1 alone the world was revealed 8 to 13 s too early:

- Look: coarse mid-distance terrain, a coarse near slope and a coarse arch at entry, refining over the next 3 to 13 s (first-frame captures in `.godot/captures/l2/entry_look/`: L2-1 alone, serial, and with the gate).
- Frames: proxy installs cost up to 30 ms each on the main thread (median 21 ms, p95 46 ms for 8 to 9 s on a new world). "Install ocean publish" is 74 % of install time.

**Fix (L2-1b).** `RingWorldLoadingBridge` holds the reveal until the proxy levels and the far arch geometry have been idle for 0.75 s (30 s timeout, marks `streaming_settled` / `streaming_settle_timeout`). While the loading screen is opaque, `RingTerrainLodCoordinator.LoadingScreenBoost` raises the proxy workers to cores/4 (at most 8, never below the preset) and the install budget to 12 ms and 16 sectors per frame. The menu (background) path has neither.

| Scenario (median of 3) | after L1 | L2-1 alone | L2-1 + gate |
|---|---|---|---|
| Reload: total / post-entry p50 / p95 / max | 22.9 s / 8.2 / 13.9 / 30.5 ms | 14.0 s / 7.8 / 17.3 / 32.2 ms | 21.1 s / 8.2 / 9.7 / 26.9 ms |
| New: same | 39.3 s / 10.7 / 11.6 / 28.2 ms | 20.5 s / 17.8 / 45.4 / 70.1 ms | 30.3 s / 10.8 / 14.1 / 45.3 ms |
| New with rivers: same | 57.9 s / 10.7 / 11.7 / 28.8 ms | 37.5 s / 21.3 / 48.1 / 77.3 ms | 47.4 s / 11.2 / 16.4 / 36.7 ms |

With the gate the global terrain is ready at 16.6 / 21.1 / 38.8 s and the proxies settle at 20.6 / 29.7 / 46.8 s (reload / new / rivers). No run hit the timeout.

Output: `.godot/audit_baseline/DESKTOP-RAVA9C1/load/after_l2b/`.

**Proxy streaming is now the critical path.** The global terrain no longer holds any load. Next work, in this order (replaces the old L2-3 text):

- **L2-3a Proxy build throughput.** Find the long pole with `RingProxyBuildCounters` per level: one build per level at a time, single builds of up to 5 s, one build start per frame. A floor of 16 workers was no faster than 8 in any scenario and slowed the global terrain by 4 to 7 s, so workers are not the limit; treat the proxy and segment workers as one budget. On a reload, check why 21 s is needed although levels from `FirstPersistentLod` (3) up have a disk cache.
- **L2-3b Proxy install cost.** Coalesce `UpdateSharedFftOceanCoverage` and `NotifyOceanReplacementDependents` to once per coordinator frame; look at the 27 ms spikes in the coverage mask atlas and in build starts. This also helps travel stutter.
- **L2-3c Trees.** Tree streaming still runs after entry (about 250 ms/s of installs, up to 14 ms per frame). It is the likely cause of the remaining gap in post-entry p95 on new worlds (14.1 and 16.4 against 11.6 and 11.7 ms); not yet confirmed with a capture. Either gate on it or lower its per-frame slice.
- **L2-3d Visible-first gate.** If the user accepts refinement outside the arrival view, release on the levels the arrival camera can see. Needs a before/after capture at the entry frame, not 10 s later.
- The benchmark's `-Shot` captures 10 s after entry. Add a capture on the first revealed frame; that is the frame a look check must compare.
- Not checked: the menu path (`menu_warm_stall_probe`), the laptop, VSync on.

These four items, the 30 fps loading cap and the shared cache are written out as work orders in "L2-3 work orders" at the end of this file.

## User answer (2026-11-12): visible-first gate (L2-3d)

"Yes, out of view only." At entry, the arrival view must look final. Terrain outside the view (behind and to the sides) may sharpen over the next few seconds. The benchmark first-frame capture must prove the arrival view matches the serial or gated first frame.

## L2-3 work orders (2026-11-12)

Written by Fable (acting Lead) from the `after_l2b` logs and a reading of the code. Nothing was run for this section, because the user was at the computer. Items under "Read from code" are unmeasured until L2-3m reports them. The layout follows `docs/work-orders/terrain-vegetation/TEMPLATE.md`. "Lead" is the Lead role of AGENTS.md.

### Phase facts

**Measured** (`after_l2b`, median of 3, RTX 4070, VSync off, uncapped; ms from the start of the load):

| Mark | Reload | New | New-rivers |
|---|---|---|---|
| Attach ends | 5 764 | 5 631 | 20 765 |
| `surface_ready` (terrain stage ends) | 11 663 | 11 333 | 25 989 |
| `owner_ready:BiomeDecorations` (tree templates) | 13 964 | 13 462 | 28 748 |
| `global_terrain_ready` | 16 610 | 21 143 | 38 823 |
| `streaming_settled` (proxy gate) | 20 625 | 29 700 | 46 808 |
| Total | 21 132 | 30 263 | 47 352 |
| Proxy gate after `global_terrain_ready` | 4.0 s | 8.6 s | 8.0 s |
| Proxy gate after `surface_ready` | 9.0 s | 18.4 s | 20.8 s |

- **The limit on proxy work.** The reveal also waits for the whole global terrain: `RingTerrainGlobalLod.StartupComplete` needs the morph snapshot of all 128 segments. So 3a, 3b and the proxy part of 3d together cannot save more than the row "Proxy gate after `global_terrain_ready`" (4.0 / 8.6 / 8.0 s). More than that needs a load that does not wait for the whole global terrain (L2-3d-3).
- **Noise.** The three runs spread 1.2 s (reload), 0.6 s (new) and 0.2 s (new-rivers). Treat under 1.5 s on reload and under 1 s on the other two as noise.
- The loading screen drew 2 939 to 2 968 frames per load (about 100 fps). No run logged mesh memory pressure. The reload log applies the graphics preset twice (5.7 s and 11.7 s) and builds macro shadows five times.

**Read from code (unmeasured):**

- **F1. A level's build is cancelled when its finer level installs a sector.**
  - Each level polls every `StreamingUpdateIntervalSeconds`: 0.25 s at 2 m, up to 3.0 s at 32 km (`ServiceStreaming`, RingTerrainProxySector.cs:938).
  - With a build in flight, the poll calls `StartStreamingUpdate` directly (:1505 to :1512). A changed `finerCoverageRevision` fails the "nothing changed" test (:1558 to :1566), and the code then cancels the build (:1608 to :1613).
  - Every installed sector raises the finer level's revision (:5890). The finest level watches the Terrain3D coverage revision in the same way.
  - On the restart, staged uploads are discarded and requeued (:1700 to :1703), and every lane makes a new sampler copy (:3441).
  - The worker output does not depend on the finer level: `BuildRawBatch` gets no coverage masks (:1842) and the install reads the latest mask (:5875). The comment at :1647 says a provider revision must never cause sampling or uploads.
  - If this happens during a load, the 15 levels finish one after another. That would explain why 16 workers were no faster than 8.
- **F2. The terminal level is built twice.** When the global terrain becomes ready, `UpdateTerminalGlobalMorphTarget` (RingTerrainLodCoordinator.cs:733) gives the morph snapshot to the coarsest active level. `SetGlobalMorphTarget` (:1132) then cancels its build, clears its staged uploads and forces a rebuild of every sector (`InvalidateMorphTargetGeometry`, :1168). The sector cache key contains the snapshot hash (:3554), so the first build's cache files are not read again. The proxy gate waits for this rebuild.
- **F3. `ApplyRuntimeLodConfiguration` resets every level's window sentinels** (:7493 to :7497), so the next poll takes the cancel path of F1. It runs on every preset application, altitude plan change and viewport or FOV change. On a reload the unchanged preset is applied a second time at 11.7 s (RingWorldSaveManager.cs:1062).
- **F4. The loading boost does not cover build starts.** `MaxBuildStartsPerFrame` is 1 and `MaxBuildStartMillisecondsPerFrame` is 2 ms with or without `LoadingScreenBoost` (coordinator :94, :1293).
- **F5. The ocean publish is outside the install budget.** `ApplyCompletedBatchCore` budgets the sector loop and then calls `UpdateSharedFftOceanCoverage` and `NotifyOceanReplacementDependents`, once per level per frame that installed anything (:5914 to :5919; also :5999 and :6034). The notify makes the next coarser level recompute the ocean replacement masks of all its active sectors (`RefreshOceanReplacementFromFiner`, :2705), whether or not the installed sectors hold water.
- **F6. The sector disk cache stores samples, not meshes** (:3408 to :3431). A cache hit still assembles the sector. The copy of slot 1 holds 503 MB of sector cache in 15 key folders against a 512 MiB budget, so writes may be refused and a trim may run during the load.
- **F7. Trees are not gated.** `RingBiomeDecorationLayer.StartupComplete` (Startup.cs:18) is true once templates and impostors exist. The first tile pass runs on after that, 2 builds at a time (`MaximumVegetationTileBuildsInFlight`), with 1.5 ms of uploads per frame. A reload reads tiles from the world's `lod_cache/vegetation` (23 MB in slot 1); a new world generates them. `PrepareVegetationRenderPlan` reruns after every 8 changed tiles and has no budget (TreeStreaming.cs:766 to :772).
- **F8. Workers.** Global segments use cores/2 (at most 8; RingTerrainGlobalLod.cs:207), proxies under boost cores/4 (at most 8), trees 2, plus Terrain3D generation. Each pool is sized alone.
- **F9. Machine cache.** `RingGeneratedLodCacheBudget.ResolveBudgetRoot` already accepts a folder named `ring_cache`. The real profile's `user://ring_cache` holds 520 MB (sector caches from launches without a world, and old summaries). That is above the 512 MiB budget, and nothing trims it while a world is active.
- **F10. In flight: T2R-12b** (uncommitted in this working tree). It edits `BuildRawSector`, `SectorBuildScratch` and the uniform block of RingTerrainProxySector.cs, and adds the river line splat counter to RingProxyBuildCounters.cs (`AddRiverLineSplat`; `riverLineSplat` in `Snapshot`). The splat runs once per built sector of the 256 m and coarser levels on river worlds, including sectors that are dropped later. Its own limits are 200 ms per load and 10 ms per call.

### Rules for every order

- **Measurement.** `tools/load_time_benchmark.ps1 -Scenario reload,new,new-rivers -Label loadbench` (warm caches, VSync off).
  - During an order: `-Runs 1` on the scenario the change aims at.
  - At the end: `-Runs 3` on all three scenarios, once, against the last accepted baseline.
  - Report the totals, the marks of the Phase facts table, loading frame p95, and post-entry p50 / p95 / max. State the noise.
  - The user may be at the computer. Ask the primary before any GPU run.
- **No visible look change.** The entry frame (L2-3m) is compared with the baseline entry frame with `tools/media/ffmpeg_tools.ps1 compare`. The heatmap shows cloud noise only. Where an order names it, the sector content hash is equal too.
- **Generation 1 identical.** `world_generation_fingerprint_test` passes unchanged. No cache version, record layout or cache file name changes.
- **Menu path.** The warm world behind the start menu keeps today's budgets and worker counts. `menu_warm_stall_probe` is not worse.
- **No menu entries.** Loading budgets and debug flags are not tunables.
- **Tests.** `tools/run_tests.ps1 -Tests <list> -Label load` (`-DryRun` first). A new test gets a manifest entry and must fail on the old code. The primary runs `-Suite gate` at the commit point. No order commits.
- **Stop rule.** Budgets are in watchdog periods (55 min) and build-plus-test attempts. When a budget runs out, or the order's own stop condition is met, stop and report in at most 15 lines: what worked, what failed, and the one next step.

### Order, gain and parallelism

Ordered by expected gain per effort, within the dependencies. Savings are estimates for the RTX 4070 at VSync off, given as reload / new / new-rivers.

| # | Order | Role | Size | Depends on | Estimated saving |
|---|---|---|---|---|---|
| 1 | L2-3m Measuring additions | implementer | S | T2R-12b committed (for the baseline) | 0. Every later order is judged with it. |
| 2 | L2-3a Proxy build throughput | Lead (design, S), then implementer (M) | M | L2-3m, T2R-12b committed | 1.5 to 3.5 / 3 to 8 / 3 to 7.5 s. Upper end if F1 holds, lower end if only F2 holds. |
| 3 | L2-3f Shared recipe cache | implementer | M | L2-3m | Repeated recipe only. Reload 0. New: 0 now, 3 to 4 s once 3a is in. New-rivers: 15 s now, up to 20 s once 3a is in. First load of a recipe: 0. |
| 4 | L2-3b Install cost and ocean publish | implementer | M | L2-3m; after 3a (same file) | 0 to 2 s on each. Loading frame p95 from 48 ms to under 35 ms. Install frames after entry and in travel at about a third of today's cost. |
| 5 | L2-3c Trees | implementer | S to M | L2-3m | 0 s; may add up to 1 s on new worlds. Post-entry p95 on new worlds from 14.1 and 16.4 ms to about 11 ms. |
| 6 | L2-3d Visible-first gate | Lead (design, M), then two implementer orders (M each) | L | 3a, 3b, 3c | 2 to 4 / 5 to 9 / 7 to 12 s if the design finds that the arrival frame does not need the whole global terrain. Otherwise 0 to 2 s. |
| 7 | L2-3e 30 fps cap and one loading budget | implementer | M | 3b; best after 3d | VSync off: 0 within 1 s before 3d, 1.5 to 2.5 s after it. VSync on: unmeasured. |

- 3f and 3c touch other files than 3a and 3b and may run alongside them. 3a and 3b share RingTerrainProxySector.cs and run one after the other.
- 3e moves up to directly after 3a if the VSync-on baseline of L2-3m is more than 2 s slower than VSync off.
- Estimated end state for the first load of a recipe: reload 13.5 to 15.5 s, new 14.5 to 16.5 s, new-rivers 29 to 31 s (15 s of it the river bake). A repeated recipe with rivers: 14 to 16 s.

### L2-3m. Measuring additions

ROLE: implementer
SIZE: S
DEPENDS ON: none for the code. The baseline of item 6 is taken after T2R-12b is committed.

OBJECTIVE
The benchmark shows where proxy and tree time goes per level, captures the entry frame, and can prove that the installed terrain is unchanged.

CONTEXT / ROOT CAUSE
- The 23 s proxy time, the 74 % ocean share and the entry pictures were measured by hand. F1 to F3 cannot be told apart without per-level numbers.
- The benchmark's `-Shot` captures 10 s after entry. The user's rule is about the entry frame.

FILES/SYMBOLS
- tools/load_time_benchmark.gd, tools/load_time_benchmark.ps1.
- RingTerrainLodCoordinator.cs (new `GetStreamingDiagnostics`), a new partial file RingTerrainProxySector.LoadDiagnostics.cs, `RingLoadTimeline.Mark`.
- `RingBiomeDecorationLayer.VegetationPendingTiles`, `VegetationPendingRenderUploads`, `VegetationTilesGenerated`, `VegetationDiskCacheHits` (read only).

REQUIRED CHANGES
1. `run.json` gains `streaming`, per proxy level: label, spacing, sectors built, installed, dropped stale, dropped not desired, batches completed, batches cancelled by cause (finer revision, window move, morph target, configuration), cache hits, cache writes, worker ms (total and largest batch), upload ms, time of the first build start and of the last install. It also gains the whole `RingProxyBuildCounters.Snapshot()`.
2. Marks: `proxy_level_settled:<label>` (last install before the gate) and `trees_settled` (first frame with no pending tree tiles and no pending tree uploads), also when it falls after entry. `run.json` gains tree tiles pending at entry, generated, and read from disk.
3. `run.json` gains, for the load and for the seconds after entry, the cost of the scopes "Terrain proxy install", "Terrain proxy install ocean publish", "Terrain proxy coverage mask atlas", "Terrain proxy build start (inclusive)" and "Trees install": total, p95 per frame, largest frame.
4. `--entry-shot=<dir>`: `entry.png` on the first frame after the loading layer is gone, and `entry_plus20.png` 20 s later from the same pose with no input. `--freeze-clocks` holds the sun and the cloud simulation at fixed values from attach, so that frames of different runs can be compared.
5. `--sector-hash` (debug flag): per level, an order-independent hash of the installed set (coordinate plus vertex, normal, colour and UV2 bytes; the slot payload bytes for slot sectors), at the gate and 20 s after entry.
6. Baseline `l23_base` under `.godot/audit_baseline/<machine>/load/`: three runs per scenario with items 1 to 5, and one `-VSync on` run per scenario.

DO-NOT-CHANGE CONSTRAINTS
- The rules above. With the new flags off, the install path does no added work and the totals stay within noise of `after_l2b`.
- File rule for T2R-12b: in RingTerrainProxySector.cs only the one-line counter increments at the cancel sites and the hash hook at install. Nothing in RingProxyBuildCounters.cs. Make these edits after T2R-12b is committed, unless the primary decides otherwise.

VALIDATION STEPS
1. Two runs of one scenario give equal sector hashes on every level.
2. `entry.png` against `entry_plus20.png` of the same run: report the heatmap. With today's gate it should show cloud noise only. Anything else is a finding for 3c and 3d.
3. `.\tools\run_tests.ps1 -Tests world_loading_startup_contract,world_loading_smoke_test -Label load`.
4. Send the user the entry frame of each scenario.

STOP RULE
3 periods or 4 attempts.

ESCALATION CONDITIONS
- The hashes of two runs of the same build differ. Say which levels, and drop item 5.
- The clocks cannot be frozen so that two runs match. Report what still moves.
- The entry frame is too dark to judge terrain.

### L2-3a. Proxy build throughput

ROLE: Lead (design, S), then implementer (M)
SIZE: M
DEPENDS ON: L2-3m baseline; T2R-12b committed

OBJECTIVE
During a load the proxy levels build side by side and each sector is built once. The proxy gate closes within 1 s of `global_terrain_ready`.

CONTEXT / ROOT CAUSE
- F1 to F4, F6, F8. Measured: the gate closes 4.0 / 8.6 / 8.0 s after the global terrain is ready, and 16 workers were no faster than 8.
- F1 and F2 both fit the measurements. The L2-3m numbers decide which one holds, or both.

FILES/SYMBOLS
- RingTerrainProxySector.cs: `QueueStreamingUpdate` (:1501), `StartStreamingUpdate` (:1515), `InvalidateMorphTargetGeometry` (:1168), `ApplyCompletedBatchCore` (:5727), `ApplyRuntimeLodConfiguration` (:7449).
- RingTerrainLodCoordinator.cs: `EffectiveConcurrentWorkerBuilds` (:77), `UpdateTerminalGlobalMorphTarget` (:733), `StartPendingWorkers` (:1258).
- RingTerrainGlobalLod.cs:207 (segment workers); RingWorldSaveManager.cs:1062 and RingGraphicsQualityController.cs:449 (preset application).

REQUIRED CHANGES
1. **Lead.** Read the `streaming` block of the three scenarios. Write a design note under this order: which of F1 to F4 the numbers confirm, and which of items 2 to 8 are in. An item is in only when its trigger is met.
2. **Keep the build when only the finer level changed (F1).** Trigger: batches cancelled for "finer revision" on three or more levels, or sectors built exceed sectors installed by more than 10 % on any level. In `StartStreamingUpdate`, when a build is in flight and the desired set, the edit version and the force flag are unchanged, apply the coverage and ocean replacement masks (the block at :1636) and return without `Cancel()`.
3. **Keep finished work across a restart (F1, F3).** Trigger: dropped-stale sectors still above 5 % of installed after item 2. Staged sectors that are still desired and were sampled with the current edit version, parent spacing and morph target stay staged. Only the rest is requeued. The Lead specifies the version check.
4. **Build the terminal level once (F2).** Trigger: the terminal level's `proxy_level_settled` is the last one and falls after `global_terrain_ready`. While `LoadingScreenBoost` is set and the global terrain is still building, the level that will receive the morph snapshot does not start. When the snapshot arrives, it builds with the proxy share and the idle global share of workers. If that single build still takes more than 2 s, report the number: the remaining fix is per-segment readiness in L2-3d-3.
5. **A preset application that changes nothing does nothing (F3).** On a reload there is one `graphics_preset_applied` mark, no sentinel reset and no extra macro shadow build.
6. **Build starts under boost (F4).** Trigger: a level waits more than 0.5 s between request and start, or `DeferredBuildStartCount` grows during the load. Up to 4 starts and 6 ms per frame while `LoadingScreenBoost` is set.
7. **One worker budget for the load (F8).** Trigger: after items 2 to 4 the proxies use all their workers for most of the load. Cores/2 in total (at most 16). Global segments take at most half while they build, proxies the rest, and each side takes the other's share when it goes idle.
8. **Reload cache report (F6).** Cache hits per level, refused writes, and whether a trim ran during the load. Fix only what is free: stale key folders are trimmed first. No cache format change.

T2R-12b (F10)
- Start after T2R-12b is committed. Do not edit `BuildRawSector`, `SectorBuildScratch`, the `UV2.y` payload or the river line uniform. The splat stays one call per built sector.
- Report `riverLineSplat` (calls, total ms, largest call) before and after on new-rivers. The call count falls with the number of sectors built and never rises. T2R-12b's limits stay: 200 ms per load and 10 ms per call. Under F2 the terminal level's splats run twice today; item 4 removes the second run.
- The new-rivers baseline is `l23_base`, taken after T2R-12b, not `after_l2b`.

DO-NOT-CHANGE CONSTRAINTS
- The rules above. The sector hash of every level equals the baseline at the gate and 20 s after entry, in all three scenarios.
- The gate (`StreamingSettledForReveal`), its dwell and its timeout.
- Items 4, 6 and 7 act only while `LoadingScreenBoost` is set. Items 2, 3 and 5 also act in gameplay: the final terrain is the same and sectors arrive sooner.
- `global_terrain_ready` is not later than the baseline by more than 1 s.
- `LodSectorCacheVersion`, `LodSectorCacheRiverVersion`, the record layout and the cache definition.

VALIDATION STEPS
1. New headless test `proxy_streaming_restart_contract_test`: a finer revision change during a build does not cancel it, and the masks are applied; a window move does cancel; staged sectors survive a restart only with equal inputs.
2. `.\tools\run_tests.ps1 -Tests proxy_streaming_restart_contract_test,lod_build_start_scheduling_test,proxy_sample_cache_dedupe_contract_test,proxy_seam_geometry_contract_test,proxy_partial_coverage_test,lod_radial_morph_contract_test,ocean_replacement_ownership_contract_test,world_generation_fingerprint_test -Label load`.
3. `.\tools\run_tests.ps1 -Tests proxy_gpu_sector_parity_test,shadow_compose_identity_gpu_test,river_lod_consistency_gpu_test,terrain_altitude_lod_flight_test,world_loading_smoke_test -Label load`, then `menu_warm_stall_probe`.
4. Benchmark as in the rules, with `--entry-shot`, `--freeze-clocks` and `--sector-hash`. Report the per-level table before and after.

STOP RULE
Design: 1 period. Implementer: 5 periods or 7 attempts. Stop early when the gate closes within 1 s of `global_terrain_ready` in all three scenarios: the proxies are then off the critical path.

ESCALATION CONDITIONS
- The numbers confirm none of F1 to F4 (no cancelled batches, workers busy throughout). Report the per-level timeline and do not guess.
- A sector hash differs, or a seam or parity test fails after one fix.
- A coverage hole or double water during a load or in the flight test.
- The splat exceeds its limits, or `global_terrain_ready` slips by more than 1 s.
- The menu path gets worse.
- Item 3 needs a sector version to mean more than the three inputs named.

### L2-3f. Shared recipe cache

ROLE: implementer
SIZE: M
DEPENDS ON: L2-3m (benchmark switch). May run alongside 3a.

OBJECTIVE
A second world with the same recipe on this machine reads the terrain summary and the river graph from a shared cache instead of building them.

CONTEXT / ROOT CAUSE
- Plan 3.8 and user answer 4 (about 20 MB per recipe, inside the 512 MB budget).
- Measured build times: terrain summary 3.9 s (new) and 4.9 s (new-rivers), from `minmax_queued` to `minmax_ready`; river graph 15.2 s.
- Both files live in the world's own folder today. F9: the shared root exists and is already over budget.

FILES/SYMBOLS
- RingTerrainGlobalLod.cs: `ResolveHeightPyramidCachePath` (:734) and the load and save around :500 to :560.
- scripts/Terrain/Hydrology/RiverGraphStore.cs: `TryLoad` (:169), `Bake` (:185), `Write` (:206).
- RingGeneratedLodCacheBudget.cs; tools/load_time_benchmark.ps1.

REQUIRED CHANGES
1. Shared folder `user://ring_cache/recipes/`. File names and formats stay as they are. The summary name already holds the generator version, seed, terrain hash, ring size and cell size. The river graph keeps its identity check.
2. Read order: the world's own file, then the shared file. A shared hit is verified like a world file and then copied into the world on the worker, so a later reload never depends on the shared cache.
3. After a build: write the world file as today, then the shared file (temp file, then move).
4. Budget: the shared root is registered with the existing budget code (512 MiB, trim to 75 %) when a load starts, and hits are touched. A root that is over budget is trimmed before the first write, oldest first. A write is not refused because old sector files fill the root.
5. The details of `minmax_ready` and `river_graph_ready` say `source=world`, `shared` or `generated`.
6. Benchmark: `-RecipeCache cold|warm`, default cold (the `recipes` folder of the isolated profile is emptied before each run). Every summary states the mode.

DO-NOT-CHANGE CONSTRAINTS
- The rules above. The bytes of the summary and of the graph are equal with and without the shared cache.
- Sector samples and tree tiles stay per world (user question 1).
- RiverGraph.cs, RiverFootprint.cs and RiverCarveOperator.cs are not touched (T2R-12b). `HydrologyVersions` and the bake identity are not touched.

VALIDATION STEPS
1. Extend `river_graph_cache_test`: a second world with the same inputs loads once from the shared folder, bakes nothing and holds equal bytes; a change of any identity field is a miss; a damaged shared file is rebuilt without an error.
2. The same three cases for the summary, in `global_terrain_preparation_contract_test` or a new `recipe_cache_contract_test`.
3. `.\tools\run_tests.ps1 -Tests river_graph_cache_test,global_terrain_preparation_contract_test,world_generation_contract_test,world_generation_fingerprint_test,world_loading_save_contract -Label load`.
4. Benchmark new and new-rivers: `-RecipeCache cold` equals the baseline within noise; `-RecipeCache warm` shows the saving.

STOP RULE
4 periods or 6 attempts.

ESCALATION CONDITIONS
- The name or identity of a file does not cover an input that changes its content. Do not share that file; report the input.
- The shared root cannot be trimmed below its budget.
- A reload reads the shared file instead of the world's own.
- The copy into the world costs more than 100 ms.

### L2-3b. Install cost and ocean publish

ROLE: implementer
SIZE: M
DEPENDS ON: L2-3m; after 3a (same file)

OBJECTIVE
An install frame costs the sector uploads plus at most one ocean publish per level, and the publish does nothing when no water changed.

CONTEXT / ROOT CAUSE
- F5. Measured: "install ocean publish" is 74 % of install time; install frames have a median of 21 ms and a p95 of 46 ms.
- Measured: 27 ms spikes in the coverage mask atlas and in build starts.
- This order is what lets terrain stream after entry without hitches (3d), and it helps travel.

FILES/SYMBOLS
- RingTerrainProxySector.cs: the publish sites (:5914, :5999, :6034), `NotifyOceanReplacementDependents` (:2696), `RefreshOceanReplacementFromFiner` (:2705), `UpdateSharedFftOceanCoverage` (:6767), the coverage block (:1636).
- RingTerrainLodCoordinator.cs: `_Process` (:351).

REQUIRED CHANGES
1. The three publish sites mark the level dirty. The coordinator publishes after its level loop and before `StartPendingWorkers`, finest level first, once per level per frame. A level without a coordinator publishes at the end of its own `ServiceStreaming`.
2. A level notifies its dependent only when its visible ocean set changed: a water sector was installed, removed, hidden or shown, or an ocean mask changed. Dry installs notify nobody.
3. Measure after items 1 and 2. Only if the publish is still above 20 % of install time during a load: while `LoadingScreenBoost` is set, publish when a level goes idle and once in `PrepareReveal`.
4. The 27 ms spikes: report the scopes "coverage mask atlas" and "build start (inclusive)" per level. Fix one cause if it is above 10 % of main-thread install time. Otherwise report it and leave it.

DO-NOT-CHANGE CONSTRAINTS
- The rules above. Mask contents are equal.
- Ocean ownership: coarser water is restored before a fine sector is hidden; the atlas is uploaded before the frame renders; no frame shows a water hole or double water.

VALIDATION STEPS
1. `.\tools\run_tests.ps1 -Tests ocean_replacement_ownership_contract_test,fft_ocean_coverage_contract_test,water_fft_distant_ocean_contract_test,proxy_partial_coverage_test -Label load`.
2. `.\tools\run_tests.ps1 -Tests water_fft_lod_ownership_visual_test,water_fft_lod_seam_gpu_test,shadow_compose_identity_gpu_test,inland_water_render_gpu_test -Label load`.
3. Benchmark as in the rules: publish share of install time, loading frame p95, post-entry frames.
4. One coastal flight before and after (perf-capture skill) for the travel frames, with `flicker` on the capture.

STOP RULE
4 periods or 6 attempts. Stop when the publish is under 20 % of install time and the loading frame p95 is under 35 ms.

ESCALATION CONDITIONS
- A frame with a water hole or double water.
- A contract test fails after one fix.
- The publish share stays above 40 %. Report what remains.

### L2-3c. Trees

ROLE: implementer
SIZE: S to M
DEPENDS ON: L2-3m. May run alongside 3a and 3b.

OBJECTIVE
The trees of the arrival area are placed before the reveal, and tree work after entry stays inside its budget.

CONTEXT / ROOT CAUSE
- F7. Post-entry p95 is 14.1 ms (new) and 16.4 ms (new-rivers) against 9.7 ms on a reload. Trees are the suspect; it is not confirmed.
- Plan 3.12 lists the trees around the arrival point as required before entry. The gate does not check them today, and shorter loads will leave more of them unplaced at entry.

FILES/SYMBOLS
- RingBiomeDecorationLayer.TreeStreaming.cs: `StartVegetationTileBuilds` (:470), `CompleteTileStreaming` (:536), `ServiceVegetationRenderUpdates` (:763).
- RingBiomeDecorationLayer.Startup.cs; RingWorldLoadingBridge.cs: `StreamingSettledForReveal` (:685).

REQUIRED CHANGES
1. Confirm with L2-3m: tiles pending at entry, `trees_settled`, and "Trees install" per frame after entry. If the trees are settled before entry and the p95 gap has another cause, stop and report the scope that owns the gap.
2. Gate: the loading-screen path waits for no pending tree tiles and no pending tree uploads inside the settle check, with a 10 s timeout and the marks `trees_settled` and `trees_settle_timeout`. The menu path keeps skipping trees.
3. While the loading screen is opaque, tile builds in flight rise to cores/8 (2 to 6).
4. After entry: find the part of "Trees install" that exceeds 1.5 ms and slice it or do it once per batch. Candidates: `PrepareVegetationRenderPlan` after every 8 tiles, `RefreshGpuTreeResidentSources` per finished tile, the sampler copy per tile start.

DO-NOT-CHANGE CONSTRAINTS
- The rules above. Tree positions, variants and tile bytes; `VegetationDiskCacheVersion`.
- The trees of the entry frame equal those 20 s later.

VALIDATION STEPS
1. `.\tools\run_tests.ps1 -Tests tree_streaming_contract_test,world_loading_startup_contract,menu_warm_world_lifecycle_test -Label load`.
2. `.\tools\run_tests.ps1 -Tests world_loading_smoke_test,tree_lod_dither_gpu_test -Label load`.
3. Benchmark as in the rules.

STOP RULE
3 periods or 5 attempts. Stop when the post-entry p95 of new and new-rivers is within 1.5 ms of the reload's.

ESCALATION CONDITIONS
- The gate adds more than 1.5 s to any scenario after item 3.
- Trees appear between the entry frame and 20 s later.
- Tile bytes differ.

### L2-3d. Visible-first gate

ROLE: Lead (design, M), then implementer (two orders, M each)
SIZE: L
DEPENDS ON: 3a, 3b, 3c; user answers "Lazy distant segments: yes" and "Yes, out of view only"

OBJECTIVE
The reveal waits only for what the arrival view shows. Terrain and trees outside the view, and global terrain the first frame does not need, finish after entry at gameplay budgets. The arrival view looks final.

CONTEXT / ROOT CAUSE
- After 3a the load waits for the whole global terrain: 10.6 / 11.6 / 13.4 s after it is queued. Plan 3.7 and L3-1 already propose to enter on the near segments.
- T2R-12b's review found that `ring_terrain_global` draws no pixel in the fly-out and the proxy levels cover the screen out to 6 500 to 8 000 km. If that also holds at the arrival pose, the global terrain matters to the first frame only through what is built from it.
- Without 3b, terrain that streams after entry is costly: with L2-1 alone the post-entry p95 was 45 to 48 ms on new worlds and the largest frame 70 to 77 ms.

PARTS
- **L2-3d-1 (Lead design).** A design section under this order, with evidence:
  1. What draws the arrival frame. A debug-colour capture per proxy level and for the global terrain at the arrival pose of the three scenarios (the proxy shader has `debug_color_enabled`).
  2. What else in the frame depends on the global terrain: the terminal level's morph target (F2), macro shadows, the cloud and planet shadow snapshots, the hull roof, the height source of the coverage tests. For each: needed complete, needed near only, or not needed.
  3. The view rule. Proposal: sectors and tree tiles that intersect the horizontal view wedge (arrival yaw, half the horizontal field of view plus 20 degrees on each side), plus everything within 4 km all round (`TerrainShadowCoverageMeters`: the near ground and the shadow casters).
  4. Build order: in-view first in `CompareSectorPriority` and in the tree request queue.
  5. Gate: `ArrivalViewSettled` per level (every desired in-view sector installed, none staged) in place of `StreamingSettled`, and the tree gate of 3c narrowed in the same way.
  6. After entry: the boost ends, and the rest installs at gameplay budgets.
  7. Global terrain: segments nearest first; which segments the gate needs; whether the terminal level can take its morph target per segment.
- **L2-3d-2 (implementer).** Proxy levels and trees, as designed.
- **L2-3d-3 (implementer).** Global terrain, as designed. This replaces L3-1. It is dropped if step 2 finds that the first frame needs the whole global terrain.

DO-NOT-CHANGE CONSTRAINTS
- The rules above. Inside the arrival view: the entry frame equals the frame 20 s later (same run, clocks frozen) and the gated baseline entry frame, cloud noise only.
- The sector hash of every level 20 s after entry equals the baseline.
- Post-entry p95 is at most 1.5 ms above the gated baseline, and the largest frame is at most 45 ms.
- The gate timeout stays. The menu path is unchanged.

VALIDATION STEPS
1. The two entry-frame comparisons and the hashes, in all three scenarios.
2. A turn-around clip: 360 degrees in 4 s from the entry frame, as mp4 and sheet, sent to the user. It shows the approved sharpening outside the view; the user confirms the margin of the view rule on it.
3. The test lists of 3a, 3b and 3c, plus `global_terrain_preparation_contract_test` and `terrain_altitude_lod_flight_test`.
4. Benchmark as in the rules.

STOP RULE
Design: 2 periods. Each implementer order: 5 periods or 7 attempts.

ESCALATION CONDITIONS
- A difference inside the arrival view other than cloud noise after two fixes.
- Post-entry p95 worse by more than 1.5 ms.
- The design finds that the first frame needs the whole global terrain. Report before building anything; only the saving of L2-3d-2 remains.
- Plain hull, a hole or missing water anywhere in the turn-around clip.

### L2-3e. 30 fps cap and one loading budget

ROLE: implementer
SIZE: M
DEPENDS ON: 3b. Best after 3d (see the order table).

OBJECTIVE
While the loading screen is opaque the game runs at 30 fps and spends most of each frame on loading work. Normal frame pacing returns with the first world frame.

CONTEXT / ROOT CAUSE
- User answer 5: "30 fps until the world is finished loading." Plan 3.5 (old L2-3 and L1-5).
- Today the benchmark load is uncapped (about 100 fps), and the per-frame limits are sized for gameplay: one build start, one global segment publish, 1 to 4 activated children, 8 ms of activation, 3 ms or 4 units for the hull and for resources, 1.5 ms of far weather, Terrain3D upload and collision at gameplay budgets.
- A cap alone would slow every step that is limited per frame. The cap and the budget go in together.

FILES/SYMBOLS
- scripts/Loading/ring_world_loading.gd: `begin` (:109), `wait_for_world` (:382), `fail`, `_cancel_and_return`.
- RingWorldLoadingBridge.cs (:44, `EndLoadingScreenBoost`); a new scripts/Loading/RingLoadingBudget.cs.
- Owners: RingTerrainLodCoordinator (:75 to :79, :94), RingTerrainGlobalLod (`MaxSegmentRebuildsPerFrame` :133, hull and occluder slices), RingWorldLoadingResources, RingScaledFarViewport (weather slice), RingTerrain3DGameplay (upload and collision), RingBiomeDecorationLayer (`ServiceStartupResources`, uploads).

REQUIRED CHANGES
1. Cap: `Engine.max_fps = 30` from `begin` on the loading-screen path. The previous value is restored in `PrepareReveal` and on every fail and cancel path. Never in menu (background) mode. The report records both values. PdaDebugChatPanel.cs:172 saves and restores `Engine.MaxFps` in the same way; the two must not overwrite each other's saved value.
2. `RingLoadingBudget`: active between `PrepareWorld` and `PrepareReveal` on the loading-screen path only. A frame has 24 ms of loading work. Each owner keeps its gameplay slice as a floor and may work on while the frame's pool is not used up. Count limits (starts, publishes, units, children per frame) are lifted while it is active; time is the only limit.
3. The owners listed above read it. It replaces `LoadingScreenUploadMilliseconds` and `LoadingScreenUploadsPerFrame`.
4. Progress bar stage fractions from the measured stage times (old L1-5).

DO-NOT-CHANGE CONSTRAINTS
- The rules above. Menu path values. Gameplay values from the first frame after `PrepareReveal`.
- No cap is left behind after a failed or cancelled load.

VALIDATION STEPS
1. `.\tools\run_tests.ps1 -Tests world_loading_lifecycle_test,world_loading_startup_contract,loading_presentation_settings_smoke_test,menu_warm_world_lifecycle_test -Label load`, then `world_loading_smoke_test` and `menu_warm_stall_probe`.
2. Benchmark at VSync off and at VSync on, three scenarios. Pass: no scenario is slower than its baseline beyond noise at VSync off; VSync on is equal or faster; the loading frame p50 is near 33 ms and the p95 at most 45 ms; `max_fps` after entry equals the value before the load.
3. A 10 s clip of the loading screen for the user.

STOP RULE
4 periods or 6 attempts.

ESCALATION CONDITIONS
- A scenario is slower at VSync off after two tuning passes of the pool. Report which owner is short.
- The loading frame p95 is above 50 ms.
- The cap survives a cancel.
- The loading animation stutters visibly.

### Phase acceptance

- A three-scenario `-Runs 3` table against `l23_base` after each order, and one at VSync on at the end.
- The entry-frame comparisons and sector hashes each order names.
- `-Suite gate` at each commit point, and `menu_warm_stall_probe`.
- Laptop: one baseline and one final run per scenario. It is unmeasured so far, and the worker shares use core counts.
- One results paragraph per finished order in this file, with the medians.

### Not in this phase

These are still open from L1 and L2 and sit on the critical path of every load or every new world:

- **L1-2 Scene load and resource warm-up at the same time.** The warm-up (1.7 s) still starts when the scene load ends (3.2 s). About 1.7 s on every load. Size S.
- **L2-2 Terrain summary on several workers.** 3.9 s (new) and 4.9 s (new-rivers) pass before the global terrain can be queued. About 3 s on the first load of a recipe once 3a is in. Size S to M.
- The river bake itself (15.2 s) belongs to the T2 owner.

### Questions for the user

1. **May the shared cache also hold the terrain proxy samples and tree tiles of the arrival area?** A repeated seed would then load almost like a reload. The size is unmeasured: a played world's proxy cache is about 500 MB, a fresh load writes far less, and it would need its own cap. Default: no. The cache holds only the 20 MB per recipe you approved.
2. **Should L1-2 and L2-2 join this batch?** They are small and save about 1.7 s on every load and about 3 s on the first load of a new world. Recommended: yes, straight after L2-3a.

### User answers on L2-3 (2026-11-12)

- **Shared cache scope:** the user chose to include the arrival-area data. L2-3f also caches the arrival-area proxy samples and tree tiles per recipe, so a repeated seed loads almost like a reload. Measure their size first, then propose a per-recipe cap and a total cap with eviction. Generation 1 must stay identical, and stale entries are keyed out by the same identity as the 20 MB part.
- **L1-2 and L2-2:** they join this batch, straight after L2-3a.
