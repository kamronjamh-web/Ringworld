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

## User answer (2026-11-12): visible-first gate (L2-3d)

"Yes, out of view only." At entry, the arrival view must look final. Terrain outside the view (behind and to the sides) may sharpen over the next few seconds. The benchmark first-frame capture must prove the arrival view matches the serial or gated first frame.
