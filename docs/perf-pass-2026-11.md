# Performance pass, phase P0: where the frame goes (2026-11-15)

Status: measurement only. Section 0 (real world, slot 3) was added on 2026-11-16. No gameplay, renderer, shader or settings change. Harness options were added in `tools/landscape_baseline_common.gd` and `tools/landscape_harness_common.gd` (section 9). Nothing is committed.

Target set by the user: at least 90 fps (11.1 ms per frame) at 1920 x 1080 on the RTX 4070 desktop.

**Read section 0 first.** Section 0 measures the world the user actually plays (slot 3, v9), the way `tools/play.ps1` runs it. Sections 1 to 10 are **fixture-world numbers**: the pinned v8 world (seed 1337), Debug C#, clocks held, standing still, VSync off. Their method notes still hold. Their absolute frame times do not describe the user's game, and their ranked plan is replaced by section 0.7.

## 0. Real world: slot 3 (v9) as the user plays it (2026-11-16)

Measurement only. Nothing in the game changed. Nothing is committed.

### 0.1 Setup

- **World.** A copy of `worlds/slot_3` ("World 3"): generator `ringworld_surface_world_v9`, seed 2028030306, `world_generation` 1, review feature `water.rivers`, climate machines on, radius 5,000 km. All of the user's top-level `.tres` files were copied along with it (shadow atlas 16,384 at 16-bit depth, tree LOD0 to 200 m, cards 550 to 900 m, `DetailedGpuEnabled` on), plus the shader, Vulkan, ring and generated caches. The copy lives in an isolated APPDATA, and the real profile was only read. The game autosaves into the copy, so the runner restores slot 3 and the `.tres` files before every run (an early run spawned at the previous run's last pose; those windows are discarded).
- **Boot path, as the player does it.** `res://scenes/StartMenu.tscn` runs with the real warm world. The driver waits for the loiter, then presses **Continue**: fly-in, then `RingWarmWorld.hand_off_to_play`. Warm load to loiter took 21 s with copied caches (193 s on a fresh copy with the display off). From pressing Continue to play took 12.5 s.
- **Build and window.** The C# assembly is optimized, built like `tools/play.ps1` (Debug configuration with `Optimize=true`), swapped into the Debug slot for the run and restored afterwards. The engine is the pinned 4.8-dev7 mono build. The window is **1600 x 900**, the `project.godot` override that `play.ps1` gets (the game never changes window mode). The game also ran at 1920 x 1080 for comparison. Very High preset with the user's files; FXAA; SSAO and SSR on; 3D scale 1.0.
- **Clocks.** Celestial and weather clocks were left running. The save is at dusk (sun at 0 to -15 degrees). Each pose was also set to noon and the clock left running from there.
- **VSync: not measured with VSync on.** The project never sets VSync, so the game runs FIFO VSync at 60 Hz. The user had been away for over 2 h and the display had powered off (`nvidia-smi display_active = Disabled`). With the display off, VSync on runs at a fixed 3.8 fps (265 ms per frame) and the GPU stays at P8, which is a display-off artifact. All windows below are therefore **VSync off** (GPU at P0, 2,835 MHz). The VSync-60 columns are **estimates**: a FIFO presentation model with a queue of 2 frames, run over the measured per-frame times (`analyze.py`, `vsync_sim`). They give the average shown fps and the share of frames held for 2 or more refreshes (visible judder). If the swapchain behaved like strict double buffering, every frame over 16.7 ms would show at 30 fps instead. This needs a run with the display on.
- **Poses near the save.** They were found with the v9 sampler for this exact recipe, using the T0 rules (LandscapePoseFinder), searched outward from the save. The game's ground height matched the finder to within 0.03 m at every pose.

  | pose | around m | across m | ground m | distance from the save | note |
  |---|---|---|---|---|---|
  | spawn (the save) | 6,354,521 | 189,654 | 379 (mountain) | 0 | noclip at 9,011 m, pitch +3.8 degrees |
  | plains_eye | 6,380,021 | 166,154 | 741 | 34.7 km | |
  | coast_eye | 6,338,521 | 190,404 | 3.8 | 16.0 km | ocean 250 m away, yaw 112.5 |
  | forest_interior_eye | 6,381,171 | 151,854 | 675 | 46.2 km | 6 trees within 30 m |
  | forest_edge_eye | 6,382,821 | 152,404 | 635 | 46.8 km | yaw 225, into the forest |
  | forest_clearing_eye | 6,382,721 | 152,604 | 642 | 46.6 km | |

  No forest focus exists within 30 km of the save; the nearest one is 46 km away.
- **Windows.** Each case is 10 s, two rounds (about 500 to 1,250 frames each). Motion windows are 15 to 30 s. Ablations are A-X-A with 8 s windows. Noise, from the gap between the two baselines around a case, is 0.00 to 0.16 ms on the median.

### 0.2 Standing still (VSync off, noon, clocks running)

| pose | window | wall p50 ms | fps | p95 | p99 | frames > 33.3 ms | GPU p50 (all viewports) | VSync-60 est. fps | est. frames held ≥ 2 refreshes |
|---|---|---|---|---|---|---|---|---|---|
| spawn, 9 km, dusk as loaded | 1600 x 900 | 7.7 | 124 | 8.1 | 18.4 | 0 % | 7.5 | 60 | 0 % |
| spawn, 9 km, noon | 1600 x 900 | 8.0 | 105 | 15.7 to 17.1 | **69** | 1.5 % | 7.7 | 57 | 1.5 % |
| plains | 1600 x 900 | 15.1 | 61 | 29.4 to 30.4 | 36 to 37 | 3.4 % | 14.8 | 57 | 5 % |
| coast | 1600 x 900 | 14.6 | 63 | 31.1 | 37.5 to 38.1 | 3.5 % | 14.3 | 56 | 6 % |
| forest clearing | 1600 x 900 | 14.9 | 63 | 27.3 | 34.5 to 34.8 | 2.5 % | 14.5 | 58 | 3 % |
| forest edge | 1600 x 900 | 17.3 | 54 | 29.2 to 29.6 | 36 | 3.5 to 3.9 % | 17.0 | 54 | 10 % |
| forest interior | 1600 x 900 | 18.7 | 50 | 29.2 to 29.8 | 36 | 3.8 to 4.2 % | 18.5 | 50 | 18 % |
| plains | 1920 x 1080 | 15.9 | 57 | 34.6 | 42 | 6.5 % | 15.6 | 56 | 7 % |
| coast | 1920 x 1080 | 17.0 | 53 | 36.9 | 44 | 7.3 % | 16.7 | 53 | 10 % |
| forest clearing | 1920 x 1080 | 17.4 | 54 | 31.3 | 39 | 3.7 % | 17.1 | 54 | 10 % |
| forest edge | 1920 x 1080 | 19.3 | 48 | 35.7 | 42.5 | 8.0 % | 19.0 | 48 | 22 % |
| forest interior | 1920 x 1080 | 20.1 | 47 | 33.8 | 40.6 | 5.8 % | 19.7 | 47 | 27 % |

- **GPU-bound at every ground pose.** The wall frame equals GPU time plus 0.3 ms. At the save (9 km up) the GPU needs only 7.5 ms.
- **Compared with P0's fixture world at the same settings and 1080p** (section 3.3): plains 15.9 against 12.4 ms (the real plains sit 741 m up with hills and cost 3.5 ms more), coast 17.0 against 16.4, forest edge 19.3 against 20.8, forest interior 20.1 against 21.9. The world is no lighter. Except on the plains it is within about 2 ms of P0.
- **The P0 half-second hitch is here too** (section 4.3): a 30 to 44 ms frame every 0.505 s at every ground pose, with 12 to 13 ms of frame setup CPU, plus 11 to 14 ms of far-viewport render CPU on the next frame. It makes 2.5 to 8 % of frames longer than 33.3 ms and sets the p95. At the save (fewer terrain materials on screen) the same frames are 18 to 20 ms with 8 ms of setup.
- **New: 70 ms main-thread spikes at the save in daylight.** About 1.5 per second at irregular intervals (0.03 to 0.5 s apart). Frame setup is 5.6 ms and GPU 7.7 ms, so the time is in `_process`, and none of the instrumented CPU scopes is longer than 3.9 ms. They are not garbage collection (no gen2, 3 gen1 per 10 s, as at dusk). They are absent at dusk at the same pose and at every ground pose at noon. None of the ten ablations removes them (`WetWorld`, trees, sun map, atlas, clouds, far viewport, near terrain, monitor). Unattributed: see 0.7 item 5.

### 0.3 Moving (VSync off)

| workload | window | wall p50 | fps | p95 | p99 | frames > 33.3 ms | GPU p50 | VSync-60 est. fps |
|---|---|---|---|---|---|---|---|---|
| walk into forest, normal controller (263 m in 30 s, grounded 100 %) | 1600 x 900 | 18.1 | 50 | 34.1 | 46.7 | 5.3 % | 17.1 | 50 |
| walk, same | 1920 x 1080 | 20.5 | 44 | 38.5 | 57.6 | 7.0 % | 19.5 | 44 |
| noclip 72 m/s at 150 m (tier 1) | 1600 x 900 | 23.5 | 38 | 53.8 | 62.3 | 13.6 % | 15.1 | 38 |
| noclip 72 m/s at 150 m | 1920 x 1080 | 24.8 | 35 | 60.7 | 70.9 | 19.9 % | 17.3 | 35 |
| noclip 240 m/s at 300 m (tier 2) | 1600 x 900 | 28.1 | **32** | 65.1 | 73.8 | 26.5 % | 10.5 | 32 |
| noclip 240 m/s at 300 m | 1920 x 1080 | 30.7 | **28** | 75.1 | 80.2 | 37.2 % | 12.2 | 28 |
| from the save, 9 km, 240 m/s | 1600 x 900 | 9.4 | 92 | 22.3 | 31.5 | 0.8 % | 8.0 | 59 |
| from the save, 1.2 km/s | 1600 x 900 | 29.6 to 29.8 | 34 to 37 | 36 to 37 | 39 to 42 | 18 to 21 % | 9.5 to 10.0 | 34 to 36 |
| from the save, 5 km/s | 1600 x 900 | 33.4 | **30** | 41.7 | 45.7 | 52 % | 13.8 | 30 |
| **from the save at the user's saved noclip speed, 36.8 km/s** | 1600 x 900 | 35.3 to 39.1 | **25 to 27.5** | 45.5 to 50.5 | 54 to 56 | **73 to 95 %** | 14.5 to 15.2 | 25 to 27 |

- **The user's save has noclip speed 36,840 m/s** (tier 0 with the speed wheeled up; `world_state_surface_runtime_v1.json`). Pressing W after Continue flies at that speed. That gives 25 to 27 fps, with 73 to 95 % of frames over 33 ms.
- **Every fast flight is CPU-bound.** The GPU needs 8 to 15 ms; the frame takes 24 to 39 ms. At 1.2 km/s and above, **every** frame carries 15 to 17 ms of frame setup CPU plus 12 to 16 ms of far-viewport render CPU: the hitch signature of 0.2 on every frame.
- **Allocation rate in flight**: 7 to 11 GB allocated in 15 to 25 s (300 to 550 MB/s), 60 to 210 gen1 and 2 to 13 gen2 collections per window, 0.4 to 0.85 s of total GC pause. On foot: 1.2 GB in 30 s. Worker threads are saturated: terrain proxy builds take 0.8 to 1.3 s each at 36.8 km/s (about 2 to 7 cores busy), plus tree and grass tile builds.
- **Main-thread CPU in flight, besides the wet world** (ms per frame): `Terrain LOD coordinator update (inclusive)` 2.4 to 9.2 (mostly about 15 `Terrain proxy update/install` calls per frame, with `install ocean publish` at 0.7 to 1.6 ms per call); `Trees update/install` 6 to 10 at 72 to 1,200 m/s (the tree streaming budget is 2 ms); `Trees output selection` 3 to 7 ms every 2 to 3 frames; `Grass update` 3.5 at 72 m/s.

**The wet world causes most of the motion cost** (A-X-A, VSync off; case = `WetWorld` processing off):

| flight | window | baseline fps (p50 / p95 ms) | WetWorld off | baseline again | frame setup p50: on → off | far-viewport CPU p50: on → off |
|---|---|---|---|---|---|---|
| from the save, 1.2 km/s | 1600 x 900 | 34.1 (29.6 / 37.2) | **74.5 (13.5 / 17.3)** | 37.2 (29.8 / 35.9) | 15 → 0.6 | 12 → 0.3 |
| from the save, 36.8 km/s | 1600 x 900 | 27.5 (35.3 / 45.5) | **58.9 (16.7 / 21.6)** | 25.0 (39.1 / 50.5) | 15 → 0.6 | 12 → 0.3 |
| from about 650 m, 1.2 km/s | 1920 x 1080 | 18.1 (54.1 / 65.6) | **40.7 (23.9 / 30.8)** | 17.2 (56.9 / 69.2) | 15.2 → 0.8 | 12.6 → 0.3 |
| from about 650 m, 36.8 km/s | 1920 x 1080 | 18.4 (53.1 / 65.7) | **54.6 (17.8 / 25.1)** | 19.4 (50.3 / 63.2) | 15.8 → 0.7 | 12.7 → 0.3 |
| 72 m/s at 150 m | 1920 x 1080 | 34.7 (24.8 / 60.0) | 40.3 (23.4 / **34.5**) | 34.8 (24.6 / 58.7) | 0 | hitch frames only |
| 240 m/s at 300 m | 1920 x 1080 | 27.6 (30.9 / 75.2) | 36.1 (27.6 / **37.6**) | 26.4 (31.8 / 78.1) | 0 | hitch frames only |

With the wet world off, the remaining flight cost is the streaming CPU and GC listed above. Allocations stay the same (7.0 GB in 15 s at 36.8 km/s), so they come from elsewhere.

### 0.4 Ablations at the real poses (A-X-A, VSync off, 1600 x 900, wall ms saved)

| case | save, 9 km, noon (p50 8.0) | plains, noon (p50 14.7) | forest interior, noon (p50 18.0 to 18.6) | P0 fixture, forest interior, user settings, 1080p |
|---|---|---|---|---|
| `WetWorld` processing off | 0.0 (p95 −2.8) | 0.1 (**p95 −9.6**, 25 → 15 ms) | 0.0 (**p95 −10.5**, 29 → 19 ms) | p95 31.6 → 18.5 ms |
| trees hidden | 0.0 | 2.9 | **7.1** | 10.0 |
| tree sun shadows off | 0.0 | 0.6 | 0.8 | 3.1 |
| sun shadow map off | 0.0 | 3.0 | 3.1 | 5.3 |
| atlas 16,384 → 8,192 (user's setting, **visual change**) | 0.0 | 1.8 | 1.8 | about 1.2 to 3 |
| clouds at half resolution (**visual change**) | 1.0 | 1.6 | 0.8 | 1.3 |
| far viewport stopped (diagnostic only) | 1.5 | 1.4 | 1.4 | 1.9 |
| near terrain hidden (diagnostic only) | 2.9 | 4.4 (and the hitch goes) | 3.6 | 4.0 |
| `RingPerformanceMonitor.DetailedGpuEnabled` off | 0.0 | 0.0 | 0.1 | not measured |
| `RingPerformanceMonitor` off | 0.0 | 0.1 | 0.1 | not measured (P0 disabled it) |

- The user's diagnostics settings (detailed GPU, 60 s history, 16 MiB, 0.1 s refresh) cost nothing measurable.
- Hiding the near terrain also removes the half-second hitch at the plains (p95 25 → 11 ms). This fits the mechanism in 0.6: the cost is the terrain (and far-ring) materials rebuilding their uniform sets.
- Trees cost nothing at 9 km.

### 0.5 Does anything from the start menu keep running after Continue?

No. Audited right after the hand-off, again after a minute, and at the end of each run:
- `current_scene` is the game. `root.disable_3d` is false. `StartMenu`, its boot cover and the flight director are freed (0 orphan nodes).
- Outside the game, root holds only 4 small processing nodes: `WorldSession` and `TestRunMouseRelease` (autoloads, always present), plus `RingWarmWorld` (state IDLE; its `_process` only joins finished threads) and `RingWorldLoading` (inactive; returns at once).
- Every `SubViewport` and every current `Camera3D` belongs to the game: the far ring, the far weather cloud bake (3D disabled), the minimap (update ONCE) and Terrain3D's 2 x 2 mouse viewport. None comes from the menu.
- While the menu loiters, its world renders at 10.4 ms (1600 x 900) with the clocks paused. After Continue, that world is the game itself.

### 0.6 Why the user "struggles to hold 30"

1. **It is motion, not standing still.** Standing still, the game runs at 50 to 63 fps on the ground and 105 to 124 fps at the save, with VSync off. With VSync on at 60 Hz, the estimate is 50 to 60 fps shown, with judder in forest (18 to 27 % of frames shown for 2 refreshes). Flying is the problem. At the user's **saved noclip speed of 36.8 km/s** the game runs at 25 to 27 fps. At 5 km/s it runs at 30 fps. At 240 m/s near the ground it runs at 28 to 32 fps with 27 to 37 % of frames over 33 ms. Walking stays near 44 to 50 fps, but with a p99 of 47 to 58 ms.
2. **The main cause in motion is the wet world's map publish.** `RingWetWorld` re-dispatches its 256 x 256 map whenever its 16 km window moves by a 64 m texel (`moved` at `scripts/Weather/RingWetWorld.cs:191`), otherwise every 0.5 s (`RefreshSeconds`). Each dispatch makes the global `Texture2DRD` point at the other ping-pong texture: `_mapTexture.TextureRdRid = link.Textures[ping]` (`scripts/Weather/RingWetWorld.Gpu.cs:218`). That replaces the texture behind a global shader uniform, so every material that reads it has to rebuild its uniform set. That costs 12 to 17 ms of frame setup, then 11 to 16 ms of far-viewport render CPU. Standing still, this happens twice a second (the half-second hitch). Above about 1 km/s it happens on every frame. With the wet world off, fast flight goes from 18 to 37 fps to 41 to 75 fps (0.3).
3. **Next is the main-thread streaming and the allocation rate.** With the wet world off, flight is still 24 to 28 ms per frame at 72 to 240 m/s near the ground: tree install runs 3 to 5 times over its 2 ms budget, plus grass, the LOD coordinator and proxy installs, plus 300 to 550 MB/s of allocations.
4. **VSync quantisation adds to it but does not cause it.** Ground poses sit at 14.6 to 20 ms of GPU, right around the 16.7 ms refresh. In forest, VSync on will show uneven 16.7/33.3 ms frames, an estimated 50 fps (not measured; the display was off). If the swapchain behaved as double-buffered it would lock to 30 fps in forest and at the coast at 1080p. That would match "can't get above 30" standing still, and it needs a display-on check.
5. **Ruled out:** the start menu (0.5); the user's diagnostics settings (0.4); the Debug assembly (everything here is optimized); the world being heavier than the fixture (only the plains are, by 3.5 ms).

### 0.7 Revised plan (replaces the order in section 7)

Fixes 1 to 4 are CPU work and change no pixels. Fix 6 holds the GPU items that change no pixels. Anything marked **visual** needs the user's decision.

| # | Item | Measured cost it targets | Expected gain | Visual risk | Size |
|---|---|---|---|---|---|
| 1 | **Wet world: publish without replacing the global texture.** Keep one stable RD texture behind the global `Texture2DRD`; after each dispatch copy the ping-pong result into it (`rd.TextureCopy`, 256 x 256 RGBA16F, 512 KB). Also reuse the per-dispatch byte arrays. | Hitch: p95 25 to 31 ms against 15 to 19; fast flight: 15 + 12 ms of CPU per frame | Standing p95 −10 ms; flight at 1.2 km/s 34 → about 74 fps, at 36.8 km/s 26 → about 59 fps, at 240 m/s p95 75 → 38 ms | None: the published texels are the same; verify with map readback and frame compare | S |
| 2 | **Tree streaming over its 2 ms budget** (`Trees install` 6 to 10 ms per frame in flight; `Trees output selection` 3 to 7 ms not time-sliced) | 6 to 13 ms per frame at 72 to 1,200 m/s | 4 to 8 ms per frame in flight | None (install order and timing only; trees may appear a few frames later at speed, as the budget already intends) | M |
| 3 | **LOD coordinator and proxy installs at speed** (about 15 `Terrain proxy update/install` per frame; `install ocean publish` 0.7 to 1.6 ms per call) | 2.4 to 9.2 ms per frame | 3 to 6 ms per frame at ≥ 1 km/s | None if only scheduling changes | M |
| 4 | **Allocation rate** (300 to 550 MB/s in flight, mostly on workers: proxy, tree and grass builds) | 60 to 210 gen1 and up to 13 gen2 per window, 0.4 to 0.85 s of GC pause | p99 and GC pause; first attribute bytes per scope | None | M |
| 5 | **70 ms daytime spikes at the save (9 km)** | 1.5 % of frames at 69 to 86 ms | p99 69 → about 18 ms at the save | Unknown until traced | S (trace) |
| 6 | **GPU work with zero visual change**: tree shadow-pass vertex work (`IN_SHADOW_PASS` exists in 4.8-dev7); empty tree batches; the fixed cloud interleave and reconstruct cost (0.68 ms at 1600 x 900) | Tree shadows 0.6 to 0.8 ms; clouds 0.7 ms fixed | 0.3 to 1.0 ms | None, by construction; verify with a shadow-depth compare | S to M |
| 7 | P0 items 2 to 13 (section 7), re-scaled by 0.4: near terrain 2.9 to 4.4 ms, sun map 3.0 ms, far viewport 1.4 to 1.5 ms, SSR when dry | as measured | as in section 7 | as in section 7 | as in section 7 |
| S1 | **Visual, user decision:** atlas 16,384 → 8,192 | 1.8 ms at the plains and in forest | 1.8 ms; the forest interior would then be at 16.9 ms at 1600 x 900 | Visual | settings |
| S2 | **Visual, user decision:** tree LOD distances (LOD0 to 200 m, cards to 900 m) | trees 7.1 ms in forest | up to about 5 ms (P0 S2) | Visual | settings |

The P0 open questions in section 9 still apply. One new question: the saved noclip speed of 36.8 km/s puts the user in the CPU-bound regime from the first key press. Is flight at that speed expected to hold 60 fps?

### 0.8 Fast-flight CPU work orders (PERF-3, lead design 2026-11-16)

Scope: the main-thread and allocation cost that is left in flight once the wet-world fix (0.7 item 1, done separately in `scripts/Weather/RingWetWorld.Gpu.cs`) lands, plus the 70 ms daytime spikes at the save. Every order is meant to change **no pixel at rest**. Orders that can only change timing in motion (something arriving one or more frames later at speed) say so and are marked **timing-only**. Line numbers are as of `2a4910e` plus the uncommitted haze work in the tree.

#### 0.8.1 What the run data already says (per frame, main thread)

Monitor rows drop events when the ring buffer is full (scope counts are about 60 % of the frame count), so the figures below are mean per call × calls per frame of the same window. They are not window totals.

| window (WetWorld as noted) | `Trees install` | of which `output selection` | `source prepare` | upload + submission + retire | unscoped rest of `Trees install` | `Terrain LOD coordinator update` | proxy `install` (all levels) | `install ocean publish` |
|---|---|---|---|---|---|---|---|---|
| 72 m/s at 150 m (on) | 6.3 (p95 11.8) | 7.4 × 39 % of frames = 2.9 | 0.4 | 2.0 (frame request 1.2 every frame) | about 1.0 | 2.4 | 0.4 | 0.6 × 0.27 per frame |
| 240 m/s at 300 m (on) | 9.2 (p95 14.4) | 7.4 × 72 % = 5.3 | 0.8 (max 12.4) | 0.9 | about 2.2 | 5.0 | 1.7 | 0.7 × 1.1 |
| 1.2 km/s from the save (off) | 6.1 (p95 9.2) | 2.9 × 84 % = 2.4 | 1.3 (max 8.5) | 0.6 | about 1.8 | 0.8 | 0.15 | 0.8 × 0.05 |
| 36.8 km/s from the save (off) | 2.0 | 0.06 | 1.5 (max 6.4) | 0.1 | about 0.3 (+ `Trees queue` 0.8) | **6.8** (p95 10.1) | **5.1** | **1.6 × 1.4 = 2.2** |

- **Why the 2 ms tree budget is not honoured.** `_gpuTreeFrameDeadline` is armed only inside `ServiceGpuTreeRenderUpdates` (`RingBiomeDecorationLayer.Gpu.cs:183`, cleared at `:209`). By then `CompleteTileStreaming` (`RingBiomeDecorationLayer.TreeStreaming.cs:536-606`) has already done its completion loop, `RebuildVegetationRequestQueue`, `StartVegetationTileBuilds`, `EvictVegetationCache` and `ScheduleVegetationDiskBudget`, all unbudgeted (the unscoped column, 1.0 to 2.2 ms). The deadline is checked only in the retire loop (`Gpu.cs:612-613`), the upload loop (`:628-630`), the fallback retire (`:674-675`), `:1082` and the source-prepare loop (`GpuPreparation.cs:98-99`), and each of those always allows one item. The two big items have no check at all:
  - **`Trees output selection`** (`Gpu.cs:540-603`) is one monolithic rebuild. It runs whenever the camera has moved more than guard/4 = 8 m from the last selection focus (`GpuTreeSelectionCanReuse`, `Gpu.cs:504-506`; guard = max(32, 2 × cell) at `:527`), which means every 1 to 2 frames at 240 m/s and every frame above about 500 m/s. Inside it, `UpdateGpuTreeEffectiveEdges` (`:795-879`) and `TryGpuTreeGroupPruneCounts` (`:883-916`) each walk every root of every group in range, once per LOD.
  - **`Trees source prepare`** is sliced at 0.6 × budget (`GpuPreparation.cs:96`), but single enumerator steps are unbounded. `plan.Lookup = new Dictionary<...>(_gpuTreeRootLookup)` (`:201`) copies the whole root lookup (up to `MaximumGpuTreeRoots` = 65,536 entries, a large-object-heap allocation) in one step. `Array.Sort(plan.VisibleTiles, …Math.Pow…)` (`:146-151`), the `retainedTiles` LINQ (`:222-224`) and the commit (`:230-248`) are also single steps. This gives the 8.5 to 12.4 ms maxima.
- **Time-slicing the selection over several frames cannot keep the output identical in motion.** The selection is only valid while the camera stays within 8 m of its focus; that is the invariant `GpuTreeSelectionCanReuse` relies on, and the prune and guaranteed counts are built for exactly that region. At 240 m/s and above, a selection that finishes N frames later was built for a focus 8 to 40 m behind the camera. It can be made **faster with identical output** (T2) or **one frame late** (T3, timing-only, needs the user's yes). It cannot be sliced without changing output.
- **Ocean publish.** Each proxy level calls `ApplyCompletedBatch` once per frame, and publishes (`RingTerrainProxySector.cs:6057-6062`; departures also notify, at `:6143` and `:6178`) only when it installed something. The 1.4 publishes per frame at 36.8 km/s are therefore **different levels**, not repeats. "Coalesce to once per frame" saves almost nothing by itself. The cost is that each publish makes its dependent level rebuild its ocean-replacement masks **for every active sector** (`NotifyOceanReplacementDependents` `:2747` → `RefreshOceanReplacementFromFiner` `:2756-2768` → `CaptureFinerOceanSnapshot` `:2962` + `BuildOceanReplacementMasks` + `ApplyOceanReplacementMasks` `:3009`). `UpdateSharedFftOceanCoverage` itself is cheap (`FFT ocean coverage atlas` 0.09 ms). P2 makes that rebuild incremental; P3 adds the install budget you asked for.
- **(d) Far weather atlas: no RID-swap bug, no order.** `RingFarWeatherAtlasGpu.cs:270-271` and `:289` bind each of the two `Texture2DRD` wrappers to its RD texture **once**, when the atlas is created (`EnsureAtlas` / `EnsureFine`). It never reassigns `TextureRdRid` per dispatch. What alternates is which of the two wrappers is set on the five or six far materials (`RingScaledFarViewport.GpuAtlas.cs:149-162` and `:202-214`), never a global uniform. The whole-ring atlas publishes every `FarWeatherAtlasRefreshSeconds` = 8 s. The fine atlas publishes every 30 s or after 60 km of drift: every 1.6 s at 36.8 km/s and every 50 s at 1.2 km/s. That is at most one small material-only rebind per 1.6 s, with no global texture swap. `Far ring weather atlas` measures 0.001 ms per frame. Leave it as it is. If the WetWorld fix shows the material-rebind cost is large per material, the fine atlas could later move to "one stable texture plus a copy" with the same code pattern.
- **(e) The 70 ms spikes at the save are strictly periodic.** In the per-frame series of `run4_spawn` (`spawn_noon_*`, every `spawn_abl_*` window at noon) a spike comes every 60 ± 2 frames. In time, that is every 0.53 to 0.59 s, and the interval equals 0.5 s plus the spike's own length: the mark of a 0.5 s accumulator that is **reset to zero** when it fires, not decremented. Excess per spike: 61 to 64 ms, and 45 to 48 ms with clouds at half resolution (`spawn_abl_cloud_half_res`, interval 0.54 to 0.57 s). The next frame is also about +7 ms. Frame setup (5.4 to 5.9 ms) and GPU (6.0 ms main) are normal on the spike frame, so the time is inside the process step. Section 0.2's "irregular, 0.03 to 0.5 s apart" is not borne out by the raw series. Daytime 0.5 s reset-to-zero timers that `proc_off:WetWorld` does not cover: `RingTerrainCloudSunVisibility` (`scripts/RingTerrainCloudSunVisibility.cs:237-239`, `RefreshSeconds` 0.5 from `SunshineCloudBridge.TerrainCloudShadowRefreshSeconds`; sun-gated; each generation marshals a request dictionary at `:246-260` and creates new RD textures plus new `Texture2DRD` wrappers, at `scripts/render/RingTerrainCloudSunVisibilityGpu.gd:83-98` and `RingTerrainCloudSunVisibility.cs:262-273`), `RingRainbow` (`scripts/Weather/RingRainbow.cs:168-178`, which returns early at a sun elevation of 75 degrees, so it is unlikely), and `RingLocalVolumetricCloudRenderer.WeatherRefreshSeconds`. P0 already ruled out `SunshineCloudBridge.ShadowRefreshSeconds` for the ground hitch (and its period is clamped to at least 1 s, `RingSunshineCloudBridge.cs:2146`). **Prime suspect: `TerrainCloudSunVisibility`.** It is unconfirmed. E1 confirms or clears it without changing any code.

#### 0.8.2 Ranked orders

Ranked by expected saving per hour of work. Savings are per frame, measured against the post-WetWorld-fix flight numbers (1.2 km/s at 13.5 ms, 36.8 km/s at 16.7 ms, 72 to 240 m/s near the ground at 23 to 28 ms).

| rank | order | saves | where | size | visual |
|---|---|---|---|---|---|
| 1 | **E1** spike attribution (ablations, no code) | finds the 61 ms ×1.7/s spike at the save (p99 69 → about 18) | save, daylight | 0.5 h run | none |
| 2 | **T1** tree bookkeeping: budget, slicing, allocations | 0.5 to 1.5 ms mean, removes the 8 to 12 ms prepare peaks | 240 m/s to 1.2 km/s | S (2 to 3 h) | none at rest; timing-only in motion (deadline) |
| 3 | **P1** proxy publish allocations + P0 scopes | 0.2 to 0.5 ms, less garbage | ≥ 1 km/s | S (1 h) | none |
| 4 | **T2** identical-output selection: parallel per-group counts | 1 to 3.5 of the 2.4 to 5.3 ms | 72 m/s to 1.2 km/s | M (3 to 4 h) | none (verified by an in-process comparison) |
| 5 | **P3** predictive install budget including the publish | caps the coordinator at its budget: about 2 to 4 ms at 36.8 km/s | ≥ 5 km/s | S to M (2 h) | timing-only: sectors land later while flying, identical once caught up |
| 6 | **C0 + G1** allocation bytes per scope; `SustainedLowLatency` | attribution; G1 only avoids blocking gen2 (2 to 15 per window) | all flight | S (1.5 h) | none |
| 7 | **P2** footprint-limited ocean replacement refresh | 1 to 2 ms at ≥ 1 km/s | ≥ 1 km/s | M (3 to 4 h) | none (verified by an in-process comparison) |
| 8 | **E2/E3** probe tool, then the fix for whatever E1 finds | as rank 1 | save | S + S/M | none |
| — | **T3** async selection, one frame late | the remaining selection cost (2 to 5 ms) | 72 m/s to 1.2 km/s | M | **timing-only, user decision** (a tree band edge can lag by one frame in motion) |
| — | **C2** proxy `SectorData` arrays from `ArrayPool` | LOH churn (gen2) | ≥ 1 km/s | M, risky lifetimes | none; only after C0 names it the top allocator |
| — | Far atlas (d) | nothing to gain | | | no order |

#### 0.8.3 File ownership (two implementers in parallel, a third optional)

| lane | implementer | owns (may edit) | orders, in order |
|---|---|---|---|
| Trees | A | `scripts/Voxel/LOD/RingBiomeDecorationLayer.TreeStreaming.cs`, `.Gpu.cs`, `.GpuPreparation.cs`, `.GpuPreparationRegression.cs` | T0 (scopes) → T1 → T2 |
| Terrain | B | `scripts/Voxel/LOD/RingTerrainProxySector.cs`, `scripts/Voxel/LOD/RingTerrainLodCoordinator.cs` | P0 → P1 → P3 → G1 → P2 |
| Diagnostics | C (or A/B when done) | `scripts/diagnostics/RingPerformanceTelemetry.cs`, `scripts/diagnostics/RingPerformanceMonitor*.cs`, new `tools/ProcessSpikeProbe.cs` | C0, E2; the E3 fix in `scripts/RingTerrainCloudSunVisibility.cs` + `scripts/render/RingTerrainCloudSunVisibilityGpu.gd` only once E1 has named it |

- Nobody touches `RingWetWorld*.cs`, the haze files in the dirty tree, or `.GpuFrameCache.cs`.
- `tools/test_manifest.json` is shared. Prefer extending existing in-code regressions (`RingBiomeDecorationLayer.GpuPreparationRegression.cs`, the proxy `*.Tests.cs` partials). If a new test entry is unavoidable, only lane C edits the manifest, append-only.
- New telemetry names count against `RingPerformanceTelemetry.MaximumNames` = 128 (about 87 are in use). T0, P0 and E2 together add about 15, so stay under 128.
- A/B switches go in as public, non-exported `bool` properties on the owning node, with no menu row, as `DisableGpuTreeEffectiveEdges` already does (`Gpu.cs:450`). The perf driver toggles them with `prop:<node>:<property>=true`. Node names in `VoxelRingIntegrationTest.tscn`: `BiomeDecorations`, `TerrainLodCoordinator`, `TerrainCloudSunVisibility`, `Rainbow`, `LocalVolumetricCloudRenderer`, `SunshineCloudBridge`, `StandingGrass`.

#### 0.8.4 Shared test plan

- Per step: `tools/run_tests.ps1 -Files <the files you changed> -Label perf3-<lane> -DryRun`, then run it. If the vegetation glob selects more than about 25 tests, run the named list with `-Tests` instead (named per order below). Report the `results.md` summary line and any non-pass rows. Smoke always runs.
- **Flight sweep (the lead's driver, display on and VSync off, no other Godot on the GPU; check `tools/gpu_queue.ps1 -Status` first):**
  `.godot/perf_pass_real/run_real.ps1 -OutLabel perf3_<order> -ScriptArgs "--ablate-poses=none --skip-walk --sweep=72,72@<X>,72,240,240@<X>,240,1200,1200@<X>,1200,36840,36840@<X>,36840"`
  where `<X>` is the order's A/B switch (`prop:<node>:<Property>=true`, which selects the **old** path). The sweep flies from the save (9 km). The near-ground tier-1 and tier-2 flights (72 m/s at 150 m, 240 m/s at 300 m) run in the same invocation without A-X-A. Compare them against `run1/` and between orders. Summarise with `analyze.py`. Report wall p50/p95, the order's own scopes, and `gc` (allocated MB, gen0/1/2, pause). `run_real.ps1` restores slot 3 and the optimized DLL itself. It refuses to run while the user plays (exit 9).
- Identical-output proof: in-process comparison counters (T2, P2) must read 0 over the whole sweep. At rest: after the sweep, stop, `wait_builds_idle`, capture a still with the old and new paths, and run `tools/media/ffmpeg_tools.ps1 compare` on the pair. Judge the heatmap, not the SSIM (cloud noise alone gives about 0.997). Delete the frames and keep only the compare PNG that you cite.
- Finish with `tools/prune_test_output.ps1 -Apply`. Keep `.godot/perf_pass_real/` (the driver and baselines).

#### 0.8.5 The orders

**E1: attribute the 70 ms spikes (no code; any lane, first, about 30 min of GPU time).**
- Run: `run_real.ps1 -OutLabel perf3_e1 -ScriptArgs "--ablate-poses=spawn --skip-walk --skip-flight --ablations=proc_off:TerrainCloudSunVisibility,proc_off:LocalVolumetricCloudRenderer,proc_off:Rainbow,prop:SunshineCloudBridge:TerrainCloudShadowRefreshSeconds=2.0"`. The spawn ablations run at noon.
- Read: for each case, the spike count and interval from `raw.wall`/`raw.process` (> 35 ms). Use the `analyze.py --hitch` logic: a spike is a frame with `process` > 40 ms and `wall` > 35 ms, and the interval is the difference in cumulative wall time between spikes. The culprit removes the spikes under `proc_off`, or moves their interval to about 2.0 s plus the spike length under the `prop:` case. (The `prop:` case only works if the bridge re-applies its tuning, `RingSunshineCloudBridge.cs:861`. If the period does not move, use `proc_off` only.)
- If none removes them: E2.
- Risk: none. Ablations are diagnostic only.

**E2: process-spike probe (lane C, S, only if E1 is inconclusive).** New `tools/ProcessSpikeProbe.cs` (a `Node`; it stays out of the shipped scene and the driver adds it with `load(...).new()`). On enable, for every node under the game root to depth 3 that is processing, it adds a probe child at index 0 with the same `ProcessPriority`, plus a first probe (`int.MinValue`) and a last probe (`int.MaxValue`) under root. Each probe records `Time.GetTicksUsec()`. When last minus first exceeds 30 ms, it logs the segments over 1 ms as "after <node A> … before <node B>", and the gap from last to the next frame's first (deferred calls, `process_frame` signals). It also emits `RingPerformanceTelemetry.MeasureCpu`-style rows `Process gap: <name>` for the top 8 offenders. No subsystem file changes. Debug-only, off by default. Test: `-Tests performance_diagnostics_contract_test,performance_attribution_test`, plus one probe run at the noon spawn.

**E3: the fix (lane C, after E1).** If it is `TerrainCloudSunVisibility`, add scopes first (`Cloud sun request build`, `Cloud sun begin`, `Cloud sun advance`, `Cloud sun publish`) and re-run E1's spawn window. Candidate fixes, all with identical texels:
- (a) `BuildRequest` (`RingTerrainCloudSunVisibility.cs:246-260`) marshals `_packedHeights` (the whole-ring min-max pyramid) into a Godot dictionary every 0.5 s. The GDScript reads `request.height` only when `terrain_key` changed (`RingTerrainCloudSunVisibilityGpu.gd:68-75`). Pass an empty `PackedFloat32Array` when the key is unchanged. `levels` is still read every dispatch (`:126`), so keep it. Compute `regional_maximum`/`regional_minimum` once per `RegionalHeights` (now `.Max()` twice and `.Min()` twice per request).
- (b) Each generation creates new RD textures (`.gd:83-98`), and `Publish` wraps them in new `Texture2DRD`s (`.cs:264`), so every receiver rebinds on every publish. That is the same texture-replacement pattern as the wet world, and a likely cause of the +7 ms frame after the spike. Give it stable published textures: generate into a back pair, then `texture_copy` into a fixed front pair on publish. Size: M. Verify with `terrain_cloud_sun_visibility_gpu_test` and `terrain_cloud_sun_visibility_state_test` plus a readback compare of the published texels.
- Risk: (a) low; (b) medium (generation lifetime and retire timing in the GDScript).

**T0: tree scopes (lane A, 15 min).** Add `Trees completion bookkeeping` (`TreeStreaming.cs:541-601`), `Trees build start` (`StartVegetationTileBuilds` calls at `:602`, `:233` and `:240`), `Trees cache eviction` (`:603`, `:241`), `Trees disk budget` (`:604`, `:242`), and inside selection `Trees selection effective edges` (`Gpu.cs:552-554`), `Trees selection prune counts` (`:555-578`) and `Trees selection order` (`:579-597`). Zero behaviour change. This tells T1/T2 where the unscoped 1.0 to 2.2 ms and the selection cost actually are.

**T1: tree bookkeeping within budget, no output change (lane A, S).**
1. Arm the deadline at the top of `CompleteTileStreaming` (`TreeStreaming.cs:538`): `_gpuTreeFrameDeadline = now + budget`, cleared in a `finally`. `ServiceGpuTreeRenderUpdates` (`Gpu.cs:183`) must then only arm it when it is 0 (it is also called from other paths), and must not clear a deadline it did not arm. Effect: when completion bookkeeping has used the 2 ms, retire, upload and fallback-retire do one item each (their existing progress guarantee). **Timing-only**: GPU outputs for newly wanted batches can install one or more frames later at speed. Fallbacks stay until each replacement is ready, so this shows an older LOD for longer and never a hole. Identical once caught up.
2. Bound the prepare steps (identical result): replace the lookup copy at `GpuPreparation.cs:201` with a reused spare dictionary filled in yielded chunks of 4,096 entries. After commit (`:243`), the previous `_gpuTreeRootLookup` becomes the spare and is cleared at the next plan start. (Nothing else holds the old reference; check `RefreshGpuTreeProtectedFlags` `Gpu.cs:337-387`, which reads `_gpuTreeRootLookup` only on the main thread outside preparation.) Hoist the sort keys out of the comparison at `:146-151`: precompute the squared distance per visible tile into a parallel array and sort indices, keeping the tie-break (around, across) so the order is the same. Yield inside the `retainedTiles` construction (`:222-228`) without LINQ.
3. Remove per-frame and per-call garbage (identical): `new List<KeyValuePair<…>>(_vegetationBuilds)` (`TreeStreaming.cs:541`) → collect completed keys into a reused `List<VegetationTileKey>`, then process them. The `gates` array (`Gpu.cs:526`), `GpuTreeLodEdges()`/`GpuTreeConfiguredEdges()` (`:452-485`) and `GpuTreeLodCaps()` (`:1173`) allocate every frame. **Intern them, do not reuse them in place.** `_gpuTreeSelectionEdges/Caps/Gates` and `_gpuLast*` keep the references and compare them with `SequenceEqual`, so a mutated shared array would freeze selection invalidation. Compute into a stack span, and return the previously returned array when the values are equal, else a new array. Also: `pruneCounts` (`:547`) → a reused field; `PruneGpuTreeBudgetGuaranteedPrefix` (`:920`, `:927`, `:940`) → reused scratch; `UpdateGpuTreeEffectiveEdges` histograms (`:834-835`), `ends` (`:860`) (keep `ends` a new array, because it is stored); `_gpuLastCaps = (uint[])caps.Clone()` (`:696`) is unneeded once caps are interned.
4. `EvictVegetationCache` (`TreeStreaming.cs:741-761`) rescans every tile per victim (O(N·k)). Collect the evictable tiles once, sort by `LastUsed` (unique, from `++_vegetationUseClock`), and evict in that order until the same stop conditions hold. Same victims.
5. Cache what never changes between calls: `ScheduleVegetationDiskBudget` (`:654-667`) re-resolves `ProjectSettings` and `GlobalizePath` every completion frame; `ResolveVegetationDiskCachePath` (`:608-639`) builds a definition string and a SHA-256 per build start. Cache the identity per (layout, sampler hash, settings) tuple and invalidate on any change. Same paths.
- Not in T1: moving `sampler.CreateIndependentCopy()` (`:506`) off the main thread. The proxy's equivalent scope measures 0.08 ms per copy.
- A/B switch: `BiomeDecorations.LegacyTreeStreamingBudget` (old deadline placement, old lookup copy).
- Tests: `-Tests tree_gpu_preparation_smoke,tree_gpu_frame_cache_smoke,tree_gpu_rebase_smoke,tree_streaming_contract_test,gpu_tree_integration_test,tree_lod_decimation_test`; extend `GpuPreparationRegression` so the chunked lookup copy equals a full copy (entries and count) after two consecutive plans. Run the sweep. Expect `Trees install` mean −0.5 to −1.5 ms at 240 to 1,200 m/s, `source prepare` max ≤ about 2 ms, `allocated_mb` down.
- Risk: low to medium. The interning in step 3 is the subtle part; a mistake shows as trees not updating LOD after a menu change, so include a live-tuning edge change in the test.

**T2: same selection, computed faster (lane A, M).**
- Refactor `Gpu.cs:540-603` into a pure `ComputeGpuTreeSelection(in SelectionInputs, float[]? storedEffectiveEnds) → SelectionResult` (order list, wanted set, output-group map, prune counts, effective ends, truncated flag, shadow signature) and a separate commit of the result into the existing fields. Keep the serial body as the reference implementation.
- Fast path: (1) precompute `GpuTreeGroupDistanceBounds` serially for the groups of each LOD; that cache is a non-thread-safe dictionary, `_gpuTreeGroupDistanceScratch`. (2) `Parallel.For` over chunks of about 32 groups for `TryGpuTreeGroupPruneCounts`, writing into a per-group result array. (3) Build `_gpuTreeOrder` serially in the **original loop order** from that array, so the later unstable `List.Sort` sees the same input sequence. (4) For `UpdateGpuTreeEffectiveEdges`, use per-chunk `int[64]` histograms and protected counts, summed in chunk order, and `protectedFarthest` by max. Integer sums and max are order-independent, so the result is bitwise identical. Per-root `dx*dx + dz*dz` stays the same expression (RyuJIT does not contract it to an FMA). Use `MaxDegreeOfParallelism` 4. The thread pool may be saturated by proxy and tile builds; then the caller does the work alone, no slower than today minus a small overhead. If the sweep shows no helper participation, switch to a dedicated 3-thread worker.
- Debug switch `BiomeDecorations.VerifyTreeSelection`: run both paths on the same inputs, and count mismatches (order sequence, wanted set, group references, ends, truncated, signature) in a public `TreeSelectionMismatches`. Commit the serial result while verifying, so the effective-ends hysteresis evolves once. A/B switch: `BiomeDecorations.SerialTreeSelection`.
- Identity: same function of the same inputs; proven by `TreeSelectionMismatches == 0` over the full sweep, both near-ground flights and the forest walk.
- Tests: T1's list plus `tree_lod_compute_regression` and `tree_lod_dither_gpu_test` (extended, GPU) once at the end. Sweep with `VerifyTreeSelection` on, then off with A-X-A.
- Expect `output selection` 2.4 to 5.3 → 1 to 2 ms per frame on the 32-thread desktop if helpers participate. Risk: medium (refactor size). Escalate if the unstable sort turns out to be fed in a different order anywhere else.

**T3 (decision, not tonight): selection one frame late.** Run `ComputeGpuTreeSelection` on a worker from a snapshot taken at the end of frame N, and commit it at frame N+1. This removes the selection from the main thread. In motion, groups entering a LOD band can lag by one frame (4 to 40 m at 240 to 1,200 m/s, at band edges 200 to 900 m away, mostly inside dither fades). Not zero visual in motion; ask the user.

**P0: proxy scopes (lane B, 15 min).** Inside `install ocean publish` add `ocean publish shared coverage` (`:6060`) and `ocean publish dependents` (`:6061`). In `RefreshOceanReplacementFromFiner` (`:2756`), add `ocean replacement snapshot` (`CaptureFinerOceanSnapshot`), `ocean replacement build` (`BuildOceanReplacementMasks`) and `ocean replacement apply`. In the install loop, add `Terrain proxy sector mesh` (`BuildSectorMesh`/`TryInstallSectorSlot`, `:5974-5979`). Zero behaviour change.

**P1: publish without garbage (lane B, S, identical).** `_oceanReplacementDependents.ToArray()` (`:2749`, `:1306`) → reused list; `_active.Keys.Where(…).ToArray()` (`:2763`) → reused list filled in the same order; `CaptureFinerOceanSnapshot` (`:2964-2966`) → per-finer reused dictionary and sets (the snapshot never outlives the call; check that `BuildOceanReplacementMasks` stores only new mask objects); `RefreshRegistered…`-style `ToArray()` in the coordinator path if P0 shows them. Order of iteration over the reused collections must match the old ones (`List` filled from the same enumerations). Tests: `-Tests ocean_replacement_ownership_contract_test,fft_ocean_coverage_contract_test,water_fft_distant_ocean_contract_test,proxy_partial_coverage_test,proxy_build_allocation_test,proxy_inland_water_morph_test`. Risk: low.

**P3: install budget that includes the publish (lane B, S to M, timing-only).**
- In `ApplyCompletedBatchCore` (`:5872`), a level today always installs its first sector whenever `remainingMs > 0` (`:5940-5951`), and its publish (`:6057-6062`) runs after the budget loop, uncounted. One heavy sector (up to 17.8 ms) plus a publish can therefore start with 0.1 ms left.
- Change: keep a per-level rolling average of the publish cost (`AverageOceanPublishMilliseconds`, the same EMA as `AverageSectorUploadMilliseconds`). Allow a level's **first** install only when `remainingMs ≥ AverageSectorUploadMilliseconds + (willPublish ? AverageOceanPublishMilliseconds : 0)`.
- Starvation guard: install one sector anyway if this level has had pending uploads for 4 frames in a row with none installed, or if the coordinator installed nothing this frame and this is the finest level with pending work.
- The coordinator (`RingTerrainLodCoordinator.cs:371-391`) is unchanged apart from passing the frame budget through. It already measures elapsed time including earlier levels' publishes.
- Effect: the coordinator stays near `MaxUploadMillisecondsPerFrame` at 36.8 km/s instead of 6.8 ms (p95 10). **Timing-only**: in fast flight, sectors install over more frames, so the older LOD is visible a little longer. Coverage is unaffected: the previous window is retained until the replacements are in (`:6048-6056`). Identical once streaming is idle.
- A/B switch `TerrainLodCoordinator.LegacyInstallBudget`.
- Tests: `-Tests terrain_streaming_settings_smoke_test,terrain_generation_streaming_playable_test,proxy_coverage_culling_settings_smoke` plus P1's list. In the sweep, check that `wait_builds_idle` still settles within its 60 s at every speed, and do the at-rest compare after the 36.8 km/s leg.
- Risk: medium. Watch for a level that never gets a turn (round-robin `_roundRobinStart`, `:378` and `:391`). The starvation guard is mandatory.

**P2: footprint-limited ocean replacement refresh (lane B, M, identical; only after P0 shows `ocean replacement build` dominates the publish).** In `BuildOceanReplacementMasks` (called from `RefreshOceanReplacementFromFiner`, `RingTerrainProxySector.cs:2756-2768`), a dependent coordinate whose footprint, expanded by one finer sector, touches no finer snapshot sector and no inland `present` sector, and whose previous mask is all-false, would be computed all-false again and keep the previous object. Skip it: keep the previous mask and do not stage its cells. This includes the fallback-parent (seam and ring-edge) path, which also yields false outside the finer footprint. At 9 km the coarse levels hold many such sectors, because each coarser window is about 4× the area of its finer one. Debug switch `VerifyOceanReplacement`: compute both versions and count coordinates where `!mask.Matches(full)` into a public counter. That counter must be 0 over the sweep and at the coast pose. A/B switch `LegacyOceanReplacementRefresh` (coordinator-forwarded). Tests: P1's list plus `proxy_vertex_table_contract_test` (extended, GPU). Expect `ocean publish dependents` down by roughly the out-of-footprint share. Risk: medium. If the shader treats "no mask" and an all-false mask differently anywhere, do not drop masks; only skip recomputing them.

**G1: GC latency mode in play (lane B, 15 min, zero visual).** In `RingTerrainLodCoordinator._Ready` (`:300`), save `GCSettings.LatencyMode` and set `GCLatencyMode.SustainedLowLatency`. Restore it in a new `_ExitTree`. This is sensible and zero-visual, but small: it only avoids **blocking** gen2 collections (2 to 15 per window, triggered mostly by large-object-heap churn) when concurrent GC is on (the .NET default; there is no GC configuration in `RingworldTechDemo.csproj`). It does nothing for the 60 to 210 gen1 collections, which make up most of the 0.4 to 0.85 s pause. Server GC is not advised: it adds a GC thread per core competing with the saturated workers. An optional experiment, not an order: launch the sweep with `DOTNET_GCgen0size=0x4000000` (64 MB) in the environment and compare the gen0/gen1 counts and pause. Adopt it, through `play.ps1` or a `runtimeconfig.template.json`, only if the counts fall and p95 improves. Verify the knob is honoured by the hosted runtime (the counts must move).

**C0: allocated bytes per scope (lane C, S).** `CpuScope` (`RingPerformanceTelemetry.cs:53-58`) takes `GC.GetAllocatedBytesForCurrentThread()` at start and dispose. `Completed` (`:9`) gains `long AllocatedBytes`, clamped at ≥ 0, because a scope that crosses an await can change threads. The monitor's per-name rows gain `alloc_mb_per_s` and `alloc_kb_mean` (find the aggregation in `RingPerformanceMonitor*.cs` that produces `monitor_rows`; the perf driver reads that list). Overhead: one extra thread-local read per scope, and nothing when telemetry is off. Tests: `-Tests performance_diagnostics_contract_test,performance_attribution_test,performance_far_viewport_contract_test`. Then one sweep, to rank allocators by MB/s per scope (worker scopes included: `Terrain proxy build`, `Trees build`, `Grass build`).
- Candidates found by reading the code, to confirm or drop with C0:
  - proxy worker `ToArray()` copies into `SectorData` (`RingTerrainProxySector.cs:4268-4273`, `:4548-4550`, `:5428-5436`): vertex arrays over 85 KB go to the LOH, a likely source of the gen2 counts;
  - tree preparation's lookup copy (fixed in T1);
  - `CaptureFinerOceanSnapshot` (fixed in P1);
  - `PlanVegetationSets` building four `HashSet`s per tile crossing (`TreeStreaming.cs:296-305`, `Trees queue` 0.8 ms at 36.8 km/s);
  - grass tile builds (`Grass build` 733 ms/s of worker time at 72 m/s; not read in this pass).
- **C2 (only if C0 names the proxy arrays):** rent the `SectorData` arrays from `ArrayPool<T>.Shared` with explicit lengths, and return them after `BuildSectorMesh`/`TryInstallSectorSlot` and on every drop path (`ReleaseDroppedGpuSector`, not desired, stale, the cache write). Marshal through a span overload of `Variant`/`Godot.Collections.Array` with the exact length (check GodotSharp 4.8-dev7 for the overload first). Risk: high. A lifetime bug corrupts meshes, which is a visual change. Do not attempt it tonight without C0 data and a lead review.

#### 0.8.6 Escalation and stop rules

- Any order whose identity proof fails (a mismatch counter above 0, or an at-rest compare that shows structure in the heatmap): stop and report. Do not tune it into passing.
- T2 or P2 needing to change the unstable-sort input order, the ocean shader, or the coverage atlas layout is an architecture decision for the primary agent.
- Lane B stops after P3 and G1 if P0 shows `ocean replacement build` is not the bulk of `ocean publish dependents`; P2 is then re-scoped by the lead.

### 0.9 Evidence and how to repeat it

Everything is under `.godot/perf_pass_real/` (ordinary test output, pruned after 7 days):
- `run1/`: 1600 x 900, all ground poses, walk, tier-1 and tier-2 flights, ablations at the plains and in the forest interior. Its "spawn" windows were at the plains pose because of the autosave, and are reported as plains. **`run1/report.json`, `console.log`, `power_log.csv` and `stderr.log` were overwritten on 2026-11-16 by a lane B run with default arguments; `summary.md` and `ablations_plains_and_forest.md` are the originals.**
- `run2_1080/`: 1920 x 1080, the same poses (no ablations).
- `run3_wet/`: 1920 x 1080, flights with `WetWorld` on, off and on again.
- `run4_spawn/`: 1600 x 900, the real saved spawn with ablations and the speed sweep, including the `WetWorld` on/off/on test at 1.2 and 36.8 km/s.
- Each run folder holds `report.json` (per-frame series and monitor rows per window), `console.log`, `power_log.csv` and `summary.md`; ablation tables are in `run1/ablations_plains_and_forest.md` and `run4_spawn/ablations_spawn.md`.
- Driver `real_world_perf.gd`, runner `run_real.ps1` (isolated APPDATA, optimized DLL swap with restore, slot restore every run), `analyze.py` (tables, VSync estimate, `--hitch`), `ablate.py`, `poses_slot3_all.json`. The throwaway C# helper is saved as `TmpSlot3PerfProbe.cs.txt` and was removed from `tools/`.

**Reusable benchmark.** The throwaway drivers above are replaced by `tools/perf_bench.ps1` (frozen slot 3 snapshot, fixed scenarios, A-A rounds, per-pass GPU, baseline deltas with a noise floor): see `docs/perf-bench.md`. Use it for any new before/after claim.

Not measured: VSync on with the display on; the A1000; the 70 ms spikes' source; the megastorm; a storm in the real world.

### 0.10 Overnight results (2026-11-16)

Kept (all measured on slot 3, optimized C#, 1600 x 900, VSync off, RTX 4070):

| Commit | Change | Result |
|---|---|---|
| a682cb1 | Wet map published through one stable texture (no per-dispatch RID swap) | plains p95 26.4 -> 15.0 ms, frame setup p95 8.4 -> 0.36 ms; 1.2 km/s flight 34 -> 75 fps; byte-identical pixel A/B |
| d1b40dd | Trees T0-T2: parallel selection with identical output (0 mismatches), bookkeeping budget, scopes | selection 4.6-7.0 -> 0.7-1.7 ms; flight at 150-300 m p50 22.5-25.2 -> ~20 ms, p95 33-36 -> 28-30 ms; 1.2 km/s p50 13.5 -> 10.5 ms |
| 992bf48 | Static ground mist removed (user-approved); haze skips the mist layer at zero density | no visible change at the user's values; -0.03 ms |
| 4b4d7f6 | rain_streaks_gpu_test camera interpolation reset | test flake fixed |

Tried and dropped (no gain beyond noise): tree and terrain shadow-pass IN_SHADOW_PASS skips (0.02 ms trees, ~0.16 ms sun map), P3 install budget, G1 SustainedLowLatency (unmeasurable), P2 footprint-limited ocean refresh (0 mismatches over 188k checks but no gain), P0/P1 terrain scopes and publish garbage (no measurable gain; patch kept out of the tree).

Still open, in order: the 70 ms periodic process spikes at the 9 km save in daylight (suspect `RingTerrainCloudSunVisibility`, 0.5 s timer; order E1 in 0.8); VSync behaviour with the display on (a 60 Hz FIFO swapchain may lock 17-20 ms forest frames to 30 fps); the GPU-side items that change the image (user decision, section 8): the 16k shadow atlas (1.8 ms), tree distances (trees 7.1 ms in forest), cloud resolution, far viewport scale.

### 0.11 Occlusion culling study (2026-11-16)

Full study and plan: [`docs/occlusion-culling-plan.md`](occlusion-culling-plan.md). Measured on slot 3 at the forest interior, forest edge, plains and save-spot ground poses, four yaws each, by reading back the camera depth and every tree and grass instance.
- Hidden by terrain or trees: 5 to 60 % of tree instances, but only 0 to 15 % of tree triangles (20 % with no wind margin). The near trees carry the triangles and are the occluders; LOD0 trees were never hidden. Terrain does almost all of the hiding; trees hiding trees adds nothing measurable.
- Trees have no per-instance frustum culling (Godot only culls whole 240 m tile renderers): 10 to 22 % of submitted tree instances are off screen.
- Oracle savings at the pose yaw (tree camera draws replaced by copies holding only the kept instances): frustum culling 0.86 to 1.06 ms in forest and 0.41 on the plains; occlusion on top 0.5 to 1.3 ms at forest views facing a slope (about a third of that averaged over yaws), 0.07 on the plains, 0 at altitude. The tree camera draws cost 6.4 to 6.6 ms in forest, 70 % of it in the colour pass.
- Collapsing hidden instances in the vertex shader keeps only 3 to 40 % of those savings: they must be compacted out of the indirect count.
- Grass (at most 0.1 to 0.2 ms), proxy terrain sectors (5 to 18 % hidden) and POIs (none near the poses) are not worth culling. Godot `OccluderInstance3D` would cull no tree renderer.
- Recommended (Phase 1, orders OC-0 to OC-3): exact per-instance frustum culling of the tree camera lists, a second compaction after the budget-correct one, shadows untouched, byte-identical; about 0.7 to 1.0 ms in forest and 0.3 to 0.4 ms on the plains. Occlusion proper is a user decision and not recommended now: the exact colour-pass variant nets about 0.1 ms, the terrain-horizon variant (0.2 to 0.5 ms averaged) is exact only with correct LOD-error margins, and previous-frame Hi-Z is not invisible.
- Side finding: Godot's primitive counter reports indirect multimeshes at full capacity; the "15 to 17 million tree primitives" in section 5.1 is inflated (about 5 million per camera pass are drawn).

### 0.12 Waste audit: work that does not change the picture (2026-11-16)

Question: is any work done far from the player, done twice, or done in many small pieces that could be one? Constraint: no pixel may change and no draw distance or density may drop. One small fix is in the working tree, not committed (W1). Everything else is measured and written up as orders.

**Method.**
- Slot 3 copy through the real-world driver (`.godot/perf_pass_real/real_world_perf_waste.gd` and `run_waste.ps1`, a copy of the E1 driver with an `occl_off` label, a still-capture A/B and a macro-shadow A/B). Optimized C# built from a sparse worktree, so the shared tree and the other lanes' work in progress stay untouched. 1600 x 900, VSync off, noon with the clocks running, RTX 4070 at P0.
- Evidence: `RingPerformanceMonitor` scopes (mean per call, because the ring buffer drops events), per-frame wall, GPU, frame setup and viewport render CPU, draw and primitive counts, per-viewport GPU and CPU times.
- Code was surveyed for global and material parameter writes, `_Process` nodes, timers, compute dispatches, readbacks, texture replacement and viewports.
- **The save has moved.** The user's slot 3 save is now at around 20,295 km, across 124 km, 624 m up, on the ground near trees. That spot costs **22.5 ms of GPU** (44 fps), more than any pose in section 0.2.
- **Caveat on run 1 and run 2.** Lane T's in-progress `ring_gpu_vegetation_indirect.glsl` was loaded at runtime with HEAD C#, so the GPU tree compute failed to initialize (`Binding (8), set (1) was not provided`). The absolute frame times of those runs are therefore not comparable with sections 0.2 to 0.10 (plains 17.2 ms against 14.0 ms). The A/B deltas below are for terrain and far-viewport material work, which does not depend on the tree path. Run 3 repeats the plains and forest A/Bs with consistent tree files: lane T's 10:35 snapshot (OC-1 work in progress) plus HEAD. Its plains baseline is 14.1 ms, as at HEAD. Its 240 m/s flights (41 to 52 ms, getting worse window by window) are not used: they may be the work-in-progress snapshot.

**What the steady state looks like.**
- At rest every ground pose is GPU-bound. The instrumented main-thread scopes add up to less than 0.6 ms per frame:
  - far ring update 0.14 to 0.16 ms;
  - water 0.06 to 0.09 ms, of which `Ocean material refresh` runs twice per frame at 0.014 to 0.027 ms per call;
  - LOD coordinator 0.06 ms; diagnostics 0.06 ms;
  - the 15 proxy levels together 0.045 ms (camera uniforms 0.000 to 0.006 ms, lighting uniforms 0.03 ms);
  - POI haze tick 0.005 ms; tree lighting 0.005 ms.
- Frame setup is 0.26 to 0.28 ms per frame. So no material has a texture re-bound every frame (that would show in setup).
- CPU waste therefore cannot move the p50 at rest. It matters in three places:
  - **hitches** (p95 and p99);
  - **CPU-bound flight**;
  - **once the GPU frame is under about 7 ms.**

**Ranked candidates.** ms are measured unless marked "est.". "CPU" is main-thread or render-thread time; "GPU" is GPU time.

| # | Candidate | Evidence | Saves | Image risk | Size | How to prove identity |
|---|---|---|---|---|---|---|
| W1 | **Macro shadow install re-binds both mask textures on all 37 receivers** (`RingTerrainMacroShadowProjector.ApplyAll`, at every 0.51 degree sun step, so every 5 to 7 s at noon). Setting a texture parameter, even to the same texture, makes Godot rebuild the material's uniform set and the cached surface data of every instance that uses it: all terrain proxy sectors, Terrain3D, the global LOD and the far-viewport materials. The mask texture is already updated in place (`ImageTexture.Update`). | Plains, 4 windows of 20 to 30 s, A-B-A-B-A. Legacy: 1.7 to 2.0 hitches per 10 s, each frame setup 13 to 14.7 ms, then 2 frames of 12.6 to 13.7 ms far-viewport render CPU. That is +23 to 26 ms of wall per hitch, max frame 35.7 ms. Fixed: **0 hitches** in 150 s with installs happening (46 installs in the run); max frame 20.7 to 22.9 ms; p99 18.33-18.43 → 18.09-18.12 ms. The E1 run (HEAD) shows the same signature every 7 s at the old save and the plains: setup 11.5 ms, far CPU 12 to 13 ms ×2, frames of 27 to 31 ms. | about 25 ms of wall every 5 to 7 s (CPU); p99 at rest; more while CPU-bound. Run 3, trees working: legacy 1.5 hitches per 10 s at +40 to 48 ms each (max frame 41.8 to 45.2 ms); fixed 0 (max 28 to 31 ms) | none | S, **done** (uncommitted) | By construction: a parameter is skipped only when the material already holds a bit-identical value (same texture object, same float, same vector), so every receiver ends in the same state as before. Measured: zero hitches with the mask still updating. A/B switch `GlobalTerrainLod3.LegacyMacroShadowApply`. |
| W2 | **Godot occlusion culling raster with 512 bedrock occluders** (`project.godot` `use_occlusion_culling=true`, `RingTerrainGlobalLod.BuildTerrainOccluder`). The occluders sit under the bedrock; anything they hide is behind solid ring. | Main viewport render CPU p50 with `occl_off`: save spot 3.98-4.13 → 3.76; plains 3.40-3.49 → 3.00; forest 4.03-4.16 → 3.62; flight at 240 m/s and 300 m 3.59 → 3.20 ms. Wall unchanged at rest (GPU-bound). Flight p50 17.03-17.13 → 16.61 ms, p95 24.5-24.8 → 23.9 ms. Off adds 9 to 10 draws (55 k primitives) at the save spot and 1 on the plains: these are what it hid. Still compare at the save spot, clocks held: on against off SSIM 0.99940, on against on 0.99937, same heatmap (wind noise in the canopy only). Run 3, trees working: plains 3.34 → 2.93, forest 3.55-3.59 → 3.19 ms, wall unchanged. | 0.3 to 0.5 ms render CPU per frame, everywhere; about 0.4 ms of wall in CPU-bound flight | none (it can only reveal geometry hidden wrongly through a surface aperture) | S | Byte or heatmap A/B of stills at 4 poses with clocks held (done once at the save spot); draw-count delta must be only far geometry behind the ring. |
| W3 | **Periodic +1.1 ms frame setup every 0.59 s** (17.5 per 10 s at every pose, at 44 to 107 fps, so on a timer, not on frames). Half of it follows `SunshineCloudBridge`: 8.7 per 10 s with its processing off. The other half is not `TerrainCloudSunVisibility`, `WetWorld`, `LocalVolumetricCloudRenderer`, `Rainbow`, the monitor or the far viewport node: none of them changes the count. | setup 1.3 to 1.9 ms against 0.27 median, one frame each | about 2 ms CPU per second; absorbed while GPU-bound | none if fixed by a stable texture | S (trace) + S/M | Attribute first (W3 order). If it is the cloud-shadow cascade publish swapping its ping-pong texture on the receivers, use the wet-world pattern: a stable front texture plus a copy, then a readback compare of the published texels. |
| W4 | **Macro install itself** (after W1): `Image.SetData` and `ImageTexture.Update` of the 7,670 x 78 R8 mask, plus 444 parameter reads, on the main thread every 5 to 7 s | `Terrain macro shadow install` 1.9 to 6.6 ms per call (2.3 to 5.1 with W1) | 2 to 6 ms every 5 to 7 s, CPU | none | S | Update the RD texture in place (`RenderingServer.Texture2DUpdate` from a reused byte array) and keep per-receiver last-applied values instead of `GetShaderParameter`; readback of the mask texels. |
| W5 | **`TerrainCloudSunVisibility` publishes new `Texture2DRD`s every generation** (0.5 s timer plus about 12 dispatch frames) and re-binds them on the far-viewport, cloud and Sunshine receivers | `proc_off` changes nothing measurable at rest (bump count, setup and wall all within noise); GPU 0.05 ms per dispatch, 0.01 to 0.02 ms per frame | ~0 at rest | none | M | Order E3(b) in 0.8.5 already designs it. Keep it parked: the texture swap reaches only a handful of materials, so its rebind is not measurable. |
| W6 | **Ocean material refresh twice per frame** (`RingWaterFftController._Process`, `:151` and `:217`), also while the water is dormant | 0.014 to 0.027 ms per call | 0.01 to 0.03 ms CPU | none | S | Call once at the end, or skip when the version did not change; `water_*` contract tests. |
| W7 | Proxy levels: all 15 run camera and lighting uniforms every frame, including the inactive ones | 0.000 to 0.006 + 0.03 ms per frame in total | < 0.04 ms | none | S | Not worth an order. |
| W8 | Three far-ring SubViewports render every frame | 1.37 to 1.48 ms GPU, 0.2 ms CPU | — | **visual** (clouds, sun and the camera change every frame, so skipping a frame is a visible lag) | — | Not waste. Already in section 7 item 8 as a user decision. |
| — | Minimap viewport | Hidden in the user's settings: its render-time counters are frozen at 1.153 / 0.046 ms in every window (min = max), so it does not render. Terrain3D mouse viewports 0.000 ms. | 0 | | | No order. |
| — | Near cloud-shadow cascade refresh | `GPU.CloudShadowNear` 0.79 ms per dispatch, 0.3 to 0.6 ms per frame at rest | — | visual: clouds move, so its timed refresh is real work | | In `docs/cloud-perf-methods-plan.md`, not here. |
| — | Same quantity computed by many systems | Sun direction is read about 30 times per frame, the camera ring frame about 10 times, weather at the camera 5 times, terrain height under the camera 6 times. Each costs microseconds, and all of them sit inside the 0.6 ms above. | < 0.05 ms est. | none | M | Not worth an order. A shared per-frame camera/sun snapshot is a cleanliness refactor, not a performance one. |
| — | Unguarded per-frame global writes (`ringworld_solar_visibility`, `ringworld_night_light_scale`, `ringworld_height_shadow_frame`, `ring_surface_aperture_*`) | Non-texture globals only update the global uniform buffer; microseconds | < 0.01 ms est. | none | S | Not worth an order. |
| — | Far collision and lights: emitter bore liners and plug lights live to 16 km, Weather Lung collision to 3 km | Static bodies with no queries; lights are clustered and frustum-culled | not measurable | none | | No order. |
| — | Tree work (empty shadow batches, `CopyOutputMirrors` copying every mirror on every `Work` call, the 0.1 s forced re-cull) | Lane T owns these files. `GPU.TreeCulling` 0.03 to 0.09 ms per dispatch | ≤ 0.1 ms est. | none | | Pass to lane T after OC-2. |

**Work orders.**

**W1 (done, review and commit).** Diff: `scripts/RingTerrainMacroShadowProjector.cs` (+27 lines: `SkipUnchangedParameters`, an exact-equality `SameValue`, and the guard in `SetParameter`) and `scripts/Voxel/LOD/RingTerrainGlobalLod.cs` (+6 lines: the A/B property `LegacyMacroShadowApply`, not exported, no menu row).
- Tests at the commit point: `tools/run_tests.ps1 -Tests terrain_macro_shadow_uv_gpu_test,certified_terrain_macro_shadow_visual_smoke,eclipse_hub_light_test,grass_card_style_test,shadow_compose_identity_gpu_test -Label waste`. The `-Files` selection is 109 tests, mostly terrain generation, which this change does not touch.
- They were not run in this audit: the shared tree holds lane T's uncommitted tree files, and building it would have tested their work in progress.

**W2: occlusion culling off (S, any lane, 30 min plus captures).**
- Change: set `rendering/occlusion_culling/use_occlusion_culling=false` in `project.godot`, and default `RingTerrainGlobalLod.BuildTerrainOccluder` to false. That also removes the staged startup phase "occluders", 512 `OccluderInstance3D` builds.
- Do not delete the builder: a debug switch may want it back.
- Checks:
  - Stills with clocks held at the save spot, the plains, the forest interior and 9 km noon, on against off: the heatmap must match an on/on pair.
  - Draw counts: off may add only far geometry.
  - Driver A/B for render CPU.
- Tests: `-Tests world_loading_startup_contract,lod_altitude_activation_contract_test,terrain_lod_shadow_coverage_smoke_test`, plus whatever `-Files project.godot` selects (dry-run first).
- Escalate if any still differs beyond the on/on pair.

**W3: attribute and fix the 0.59 s setup bump (S trace, then S/M fix).**
- Trace: add scopes around `RingCloudShadowProjector.ApplyAll` and the GPU cascade promote (`RingSunshineCloudBridge.cs:2262-2263`, `PromoteCompletedGpuShadowCascade`). Count texture-valued `SetShaderParameter` calls per frame, and log the frame numbers of setup > 1 ms against those counts.
- Fix only if a texture swap is confirmed, by keeping one stable front texture per cascade and copying into it (as in a682cb1).
- Identity: readback of the published cascade texels, old against new, at rest and after a cloud move.
- Tests: `cloud_shadow_crossfade_test`, `sunshine_shadow_slice_equivalence_test`, `ring_cloud_shadow_receiver_cache_smoke_test`.

**W4: macro install without image churn (S).**
- In `RingTerrainGlobalLod.InstallMacroShadowMask` and `.PlanetShadows.cs`, keep the `Image`, write `mask.Pixels` with `RenderingServer.Texture2DUpdate`, or use a `Texture2DRD` with `rd.TextureUpdate`.
- In the projector, cache the last-applied values per receiver instead of reading them back.
- Identity: readback of the texture data before and after for one install.
- Expected: `Terrain macro shadow install` from 2 to 6 ms to under 1 ms.

**Not ordered:** W5 to W8 and the rows marked "—". Their measured cost is below 0.05 ms per frame, or the work changes the image.

**Evidence** (all in `.godot/perf_pass_real/`):
- Runs, each with `report.json` and `console.log`:
  - `waste_run1/`: save spot, plains and forest ablations, the macro A/B, the 240 m/s flight A/B.
  - `waste_run2/`: the occlusion still A/B, kept as the two heatmaps `cap_spawn_1_occl_off_diff.png` and `cap_spawn_2_none_diff.png`, plus the bump attribution.
  - `waste_run3/`: the occlusion A/B with trees working.
- Scripts: driver `real_world_perf_waste.gd`, runner `run_waste.ps1`. The runner refuses to start while any Godot runs, and restores the Debug slot only if nobody rebuilt it during the run.

### 0.13 Close-up trees: which LODs are drawn and where the triangles go (2026-11-16)

Question from the user: "what LOD levels are being used for the close up trees? where can we reduce tris up close". Study only: nothing in the game changed.

**Method.**
- Slot 3 copy, real start menu, Continue. Optimized C# built from a separate worktree at HEAD `d8cbb55`, so lane T's work in progress (frustum culling) was not loaded. 1600 x 900, VSync off (display asleep), noon with the clocks running, RTX 4070 at P0, the user's own `.tres` files.
- Poses:
  - **the save**: around 20,295.0 km, across 123.9 km, 624 m, yaw 144.5, a maple trunk 4.5 m away;
  - **forest interior** and **forest edge** from section 0.1.
- **Census.** Every tree renderer and shadow proxy was read back: the indirect instance count, every instance row (position, scale, dither coverage), the triangles of its mesh per surface, and the sun cascades its box touches.
- **Timing.** A-X-A cases, 6 s windows, two rounds (the second in reverse order). Each case is compared with the baselines on either side of it. The main-viewport GPU time is split by compositor timestamps:
  - "prepass": from the start of the viewport to `PRE_OPAQUE`;
  - "colour": from `PRE_OPAQUE` to `POST_OPAQUE`. This span also holds the sun shadow maps.

  Noise: the two rounds agree within 0.05 to 0.2 ms. Copying the tree draws unchanged costs -0.12 to +0.07 ms.
- **Copy oracles.** A tree renderer is hidden and redrawn from its own rows by a CPU copy with another mesh or a subset of rows. This is the same method as the occlusion study (section 0.11).
- **Main-viewport GPU at baseline:** 17.1 ms at the save (18.8 ms over all viewports, wall 19.1 ms), 16.8 ms in the interior and 16.3 ms at the edge.
  - Section 0.12 gave 22.5 ms at the save. That run had lane T's half-edited compute shader loaded (its caveat), so its figure does not describe HEAD.

**1. LOD bands at the user's settings.** The user's saved fades are: near detail 25 to 200 m, billboard 550 to 900 m, mid-tier batch span 3. The tree sun-shadow distance is 700 m, but the sun map ends at 400 m.

The slot caps (256 / 640 / 2,696 / 5,200) bind at every pose. So the cap fall-through of TR-9a moves each hand-off inward and keeps its configured width. Effective bands, as read from the layer:

| slot | mesh | save | interior | edge | shadow caster |
|---|---|---|---|---|---|
| 0 | authored LOD0, full material | 0-178 m, **dithers out over 3-178 m** | 0-175 m, out over 0-175 m | 0-175 m, out over 0-175 m | authored LOD2 (shadow material) |
| 1 | authored LOD2, simplified material | 3-240 m (in 3-178, out 200-240) | 0-215 m | 0-240 m | LOD3 crossed cards |
| 2 | LOD2b (TR-5 decimated) | 200-605 m, **dithers out over 255-605 m** | 175-565 m | 200-590 m | LOD3 crossed cards |
| 3 | shared impostor, crossed cards (16-36 tris) | 255-1,261 m | 215-1,066 m | 240-942 m | none |
| 4 | far quad (4 tris) | from 1,111-1,261 m to 5,500 m | from 916-1,066 m | from 792-942 m | none |

- **Near trees are drawn twice.** The configured LOD0 fade is 175 m wide (25 to 200 m). The 256 cap pulls its end to 175-178 m, and the fade keeps its width, so it starts at 0-3 m. Every tree within about 175 m is drawn as LOD0 and as authored LOD2, each partly dissolved.
  - Godot has no automatic mesh LODs on the tree meshes: the surfaces carry no `lods`.
- **Sun shadows.** The sun uses PSSM with 2 splits: cascades 0-160 m and 160-400 m. The proxies are 240 m tiles, so every one touches both cascades.

**2. Triangles per tree per LOD** (bark / foliage; from the catalog):

| species, size | LOD0 | authored LOD1 (unused) | authored LOD2 | LOD2b | cards |
|---|---|---|---|---|---|
| maple large (3 variants) | 12,987-22,490 (31-34 % bark) | 5,541-9,487 | 2,825-5,321 | 1,438-1,440 | 36 |
| maple medium | 4,750-14,577 (40-51 % bark) | 2,201-5,469 | 1,052-2,584 | 758-797 | 36 |
| maple small, sapling | 465-3,442 | 300-1,233 | 157-531 | 157-407 | 36 |
| fir (large, medium, small) | 12,969 / 6,813 / 599 (53 / 51 / 10 % bark) | 6,633 / 3,598 / 437 | 3,268 / 2,278 / 318 | 800 / 2,278 / 318 | 20 |
| pine large | 10,142-11,844 (37-39 % bark) | 5,787-6,596 | 2,818-3,176 | 1,438-1,440 | 16 |
| pine big, medium | 2,923-5,485 (42-50 % bark) | 1,703-3,148 | 992-1,693 | 489-1,391 | 16 |
| pine small, sapling | 587-2,333 | 436-1,544 | 270-884 | 124-400 | 16 |

Leaf surfaces have 1.0 to 1.56 vertices per triangle; bark has 0.7 to 0.8.

**3. Instances and triangles per LOD and per pass.** Camera pass:

| pose | LOD0 | authored LOD2 | LOD2b | cards | quads | total | within about 240 m (LOD0 + LOD2) | coverage-weighted | in the view frustum |
|---|---|---|---|---|---|---|---|---|---|
| save | 256 trees, **2.10 M** (40 %; bark 0.83, leaves 1.27) | 571, 0.98 M | 2,576, 1.96 M | 4,923, 0.18 M | 7,557, 0.03 M | 5.25 M | 3.08 M (59 %) | 2.49 M (47 %) | 1.61 M (31 %) |
| interior | 256, 1.72 M (32 %; 0.75 / 0.96) | 640, 1.06 M | 2,696, 2.35 M | 4,918, 0.18 M | 8,102, 0.03 M | 5.33 M | 2.78 M (52 %) | 2.71 M (51 %) | 2.17 M (41 %) |
| edge | 256, 1.63 M (34 %; 0.72 / 0.91) | 440, 0.75 M | 2,696, 2.27 M | 4,737, 0.17 M | 8,438, 0.03 M | 4.86 M | 2.38 M (49 %) | 2.15 M (44 %) | 2.60 M (53 %) |

- **Per pass.**
  - The depth prepass and the colour pass draw the same instances, so the camera list above counts twice per frame.
  - The shadow pass draws the proxies in both cascades:
    - save: 2 x (0.42 M LOD0 casters + 0.07 M card casters) = 0.99 M;
    - interior: 2 x 0.44 M = 0.88 M;
    - edge: 2 x 0.33 M = 0.66 M.
  - Trees therefore submit about 11 M triangles per frame at the save. Shadow draws are 694 of 757 at the save, 635 of 767 in the interior and 329 of 434 at the edge.
- **Coverage-weighted** means triangles times the fraction of each tree's pixels its dither keeps. About half of the near-tree triangles are spent on trees that are partly dissolved. Their vertices cost the same whatever the dither throws away.
  - Coverage-weighted LOD0 is 35 % of LOD0's triangles at the save, 43 % in the interior and 31 % at the edge.
  - The cause is the two wide fades: LOD0 over 175 m, and LOD2b over 350 m.
- **In the view frustum** means the bounding sphere of the instance (mesh AABB times instance scale, no wind margin) intersects the camera frustum. The camera has an 80 degree vertical field of view, 112 degrees horizontal at 16:9.
- **Correction to section 0.11 and the occlusion plan:**
  - LOD0 holds 32 to 40 % of the camera triangles, not about 90 %. The 90 % is LOD0 to LOD2b together.
  - The frustum keeps 31 to 53 % of tree triangles, not the 78 to 90 % the occlusion probe reported (its LOD0 row: 22 of 256 outside; here 148 to 168 of 256 are outside).

**4. What each part costs** (main-viewport GPU ms saved, two rounds; prepass / colour split in brackets):

| case | save | interior | edge |
|---|---|---|---|
| all trees hidden | 6.89 / 7.20 | 6.90 / 7.34 | 7.19 / 7.36 |
| LOD0 camera draws hidden | **2.69 / 2.57** (0.79 / 2.17) | **2.09 / 1.96** (0.52 / 1.49) | **2.14 / 2.33** (0.57 / 1.66) |
| authored LOD2 (slot 1) hidden | 0.88 / 0.78 | 0.83 / 0.79 | 0.86 / 0.96 |
| LOD2b hidden | 1.87 / 1.87 | 2.72 / 3.00 | 2.73 / 2.80 |
| cards and quads hidden | 0.27 / 0.33 | 0.07 / 0.38 | 0.31 / 0.38 |
| tree sun shadows off | 0.56 / 0.78 | 0.33 / 0.40 | 0.35 / 0.38 |
| LOD0 shadow proxies hidden | 0.19 / 0.22 | 0.23 / 0.12 | 0.09 / 0.16 |
| card shadow proxies hidden (slots 1 and 2) | 0.37 / 0.57 | 0.12 / 0.21 | 0.23 / 0.23 |
| LOD0 bark alone kept (= leaves' cost) | 2.09 / 1.90 | 1.37 / 1.41 | 1.54 / 1.54 |
| LOD0 leaves alone kept (= bark's cost) | 0.67 / 0.64 | 0.65 / 0.80 | |

- **LOD0 costs 1.2 to 1.4 ms per million triangles.** Its leaves cost about one and a half to two times its bark per triangle (about 1.5 against 0.8 to 1.0 ms per million), because a leaf triangle has more vertices.
- **The depth prepass is 25 to 30 % of every tree LOD's camera cost** (all trees: 1.75 to 1.81 ms). The rest is the colour pass.
- **Not fill-bound.** At 50 % 3D scale (a quarter of the pixels), LOD0 still costs 2.42 against 2.63 ms at the save, 1.78 against 2.03 in the interior and 2.03 against 2.24 at the edge. Only 8 to 12 % of LOD0's cost (0.2 to 0.25 ms) scales with pixels. Alpha-tested overdraw is therefore small, and so is anything that tighter leaf cards could save.
- **Tree shadows are cheap here** (0.4 to 0.7 ms): there are only 2 cascades out to 400 m. LOD0 already casts with the authored LOD2, and the LOD2 tiers cast with the 22 to 33 triangle cards. The card casters cost as much as the LOD0 casters, because their cost follows draws (one per proxy per cascade).

**5. Options**

No pixel change (measured with oracles):

| # | option | saves (save / interior / edge) | image | where |
|---|---|---|---|---|
| T1 | **Per-instance frustum culling of the tree camera lists.** This is lane T's OC-1 and OC-2, already in progress. | copy oracle with a 0.3 x tree height margin: **3.05 to 3.23 ms** (save and interior) | none: stills match the unchanged copy, heatmap included | lane T |
| T2 | Drop instances whose dither keeps under 2 % of their pixels | 0.47 to 0.61 / 0.44 to 0.56 / 0.55 to 0.59 | near-invisible: at most 2 % of a fading tree's pixels, the same as moving each fade edge by 1 to 3 m | `accepted()` in the compute shader; lane T's file, after OC-2 |
| T3 | LOD0 shadow casters: authored LOD2 to LOD2b | 0.06 to 0.14 / 0.09 / 0.07 to 0.08 | near-invisible (LOD2b keeps crown coverage within 5 %) | `GpuShadows.cs`; too small on its own |
| T4 | Decimate LOD0 bark only: inner rings, the back faces of thin branches, twigs under a pixel at 25 m | estimate about 0.3 ms (bark costs 0.6 to 0.7 ms, 31 to 56 % of LOD0 triangles) | near-invisible if the silhouette is kept; judge on contact sheets at 5, 15 and 40 m | offline TR-4 tool (decision D5 reopened, bark only) |
| T5 | Tighter leaf-card geometry | at most 0.2 to 0.25 ms (the pixel share of LOD0) | none | not worth it |
| T6 | Keep the leaves out of the depth prepass | not offered: Forward+ in 4.8 has no per-material prepass opt-out, and SSAO, SSR and contact shadows need tree depth and normals | | no |

Image changes, user decision (all measured unless marked):

| # | option | saves (save / interior) | what changes |
|---|---|---|---|
| U1 | **Narrow the LOD0 fade:** `TreeNearDetailFadeStartMeters` 25 to 175, end stays 200. A setting only. | **0.71 to 0.90 / 0.61 to 0.86** | Trees from 3 m to about 150 m become pure LOD0. Today a growing share of their pixels is authored LOD2. **More detail, not less.** The LOD0 to LOD2 hand-off becomes a 25 m dither at about 150 to 175 m, so it is shorter and may show more when walking. Stills: `run2/still_1_base.png` against `run2/still_1_prop_TreeNearDetailFadeStartMeters_175.0.png`. |
| U2 | Billboard fade 550/900 to 300/350 (the shipped default): LOD2b gives way to cards from 300 m | 1.24 to 1.44 / 1.70 | LOD2b trees fall from 2,576 to 749. The card hand-off moves from today's effective 255-605 m dither to 300-350 m. Judge on the TR-8 switch strip. Stills at the interior: `run3/still_2_base.png` against `run3/still_2_prop_TreeBillboardFadeStartMeters_300.0_TreeBillboardFadeEndMeters_350.0.png` (no difference visible in that view). 450/500 saves 0.52 to 0.65 / 0.20 to 0.32. |
| U1+U2 | both | **1.96 to 2.07 / 2.28 to 2.33** | as above |
| U3 | LOD0 only to 100 m (fade 75 to 100) / to 60 m / shipped 25 to 40 m | 1.57 to 1.63 / 1.18 to 1.20; 2.35 to 2.38 / 1.79 to 1.80; 2.55 to 2.61 / 1.96 to 2.07 | LOD2 detail from 100 m, 60 m or 40 m. Still: `run2/still_1_prop_TreeNearDetailFadeStartMeters_75.0_TreeNearDetailFadeEndMeters_100.0.png` |
| U4 | Use the pack's own authored LOD1 (34 to 75 % of LOD0's triangles) beyond 50 m, LOD0 inside 50 m | 1.10 to 1.13 / 0.64 to 0.74 (beyond 100 m: 0.64 to 0.66 / 0.26 to 0.33) | Sparser leaf clusters on trees beyond 50 m. Needs a sixth slot or a remap in `GpuTreeLodInterval`. LOD1 everywhere: 1.25 to 1.37 / 0.86 to 0.87 / 0.96 to 0.97 at the edge. Still: `run3/still_1_copy_l0_lod1_beyond50.png` |
| U5 | Slot 1 draws LOD2b in place of authored LOD2 | 0.46 to 0.54 / 0.31 to 0.37 | LOD2b from 3 m |
| U6 | LOD0 shadows from cards | 0.14 to 0.15 / 0.14 to 0.19 | near tree shadows lose their branch shapes |
| — | Upper bound of any LOD0 mesh work (LOD0 drawn as authored LOD2) | 2.36 to 2.54 / 1.45 to 1.49 / 1.55 to 1.57 | |
| — | Impostor and quad distances | cards and quads cost 0.07 to 0.38 ms in all | not a close-up lever |

**6. Recommendation.**
1. **T1 first.** Lane T's frustum culling is the largest close-up win and changes no pixels. The oracle says about 3 ms here, against the 0.7 to 1.0 ms in section 0.11, whose frustum count looks wrong; OC-2's A/B will settle it. About half of it is LOD0: 1.1 to 1.4 M of LOD0's 1.6 to 2.1 M triangles are off screen.
2. **U1, for the user.** One setting, no code, and the near forest gets more detail for 0.6 to 0.9 ms. With U2, about 2 to 2.3 ms.
3. T2 after OC-2, in the same compute pass (lane T). Then T4 if the user wants LOD0 itself cheaper without a look change.
4. Side finding for TR-9a, not urgent: when a cap moves a hand-off inward, the fade keeps its width rather than its start. With the user's 175 m LOD0 fade, the dissolve then starts at 0 to 3 m.

**Evidence** (all in `.godot/perf_pass_real/tc/`):
- Run folders, each with `report.json` (census and windows) and `console.log`:
  - `run1/`: three poses, every case, the fade settings at the save and the interior;
  - `run2/`: the frustum oracle, bark-only and leaves-only copies, the coverage cut, stills;
  - `run3/`: the LOD1 split, billboard fades, stills.
- Scripts: driver `tc_driver.gd.txt`, runner `run_tc.ps1.txt`, the C# probe partial `TmpTreesCloseupProbe.cs.txt` (it ran from a throwaway worktree), analyser `tc_an.py`.
- run1's first leaves-only copy failed (custom vertex format); its rows are replaced by run2.

### 0.14 GPU budget: where the GPU time goes, what could move to the CPU, the wet world and shell shadows (2026-11-16)

Questions from the user: what can take load off the GPU, and what work is better suited to the CPU? The wet world should not show at long distances and should update less often. Are shadows cast on or from the inner ring shell?

**Answer in short.**
- **The GPU frame is raster work, not compute.**
  - At the three poses, five items make up about 85 % of the GPU frame: near terrain 4.5 to 5.1 ms, trees 2.8 to 4.7 ms, Sunshine clouds 2.2 to 2.4 ms, the sun shadow map 1.3 to 1.8 ms and the far-ring viewport 1.4 to 1.7 ms.
  - Every slowly changing compute dispatch (sky LUT, terrain-cloud sun visibility, height shadow, macro and storm maps, far weather bake, wet map) costs **under 0.02 ms per frame each, about 0.07 ms in total**.
  - Moving any of them to the CPU would save almost nothing, and the CPU versions would be slower. Lowering their cadence saves at most 0.02 ms each.
- **Real GPU savings that need a decision.** Ranked list below:
  - the near cloud-shadow cascade at 5 Hz instead of 10 Hz (about 0.2 ms);
  - engine SSR only while wet (0.2 to 0.9 ms);
  - tree wetness per instance instead of per vertex (about 0.3 ms in forest while wet; lane T's files);
  - a tree cull that re-runs on 2 of 3 frames while the camera stands still (0.16 ms; lane T).
- **Wet world (done, not committed).**
  - Wetness now fades out between 5.2 and 6.5 km from the camera, and nothing is evaluated beyond that.
  - The map is dispatched every 2 s (was 0.5 s), and on movement only after the camera drifts 4 texels (256 m).
  - The scaled far viewport no longer evaluates wetness at all: its positions are not main-world metres.
  - Measured saving is small: 0.04 to 0.07 ms while wet, 0 in forest. Wet shading costs 0.5 to 0.6 ms in total, and it is almost all within a few hundred metres of the camera. A dispatch costs 0.007 ms.
  - The near field is unchanged: the heatmap of new against old matches the old-against-old noise pair.
- **Shell shadows: nothing measurable is wasted.**
  - The far viewports render no shadows at all.
  - In the main viewport the global hull walls and roof do cast into the sun map: 20 visible casters, 5 to 24 shadow draws and 3,000 to 88,000 primitives. Turning them off saves 0.00 to 0.02 ms, which is noise.
  - Turning off every caster farther than 20 km also saves 0 ms.
  - The planet light casts no shadows by day.

**Method.**
- Slot 3 copy through the real-world driver.
  - Files: `.godot/perf_pass_real/real_world_perf_gpub.gd`, a copy of the E1 driver with a `--gpub` flow, a GPU timestamp reader and new ablations; runner `run_gpub.ps1`.
  - Optimized C# was built from the shared tree. It therefore includes lane T's work-in-progress tree culling as of 12:30 (GLSL and C# consistent; tree compute ran) and the uncommitted `project.godot` with occlusion culling off.
  - 1600 x 900, VSync off (display asleep), RTX 4070 at P0, Ultra preset with the user's files: atlas 8,192 at 16-bit depth, 2 cascades to 400 m in near-map mode.
  - Noon with the clocks running (sun 75 to 47 degrees over the run).
- Poses:
  - **save**: 20,295.04 km around, 123.86 km across, 624 m, on the ground under a tree (pinned with `--force-pose`);
  - **plains**: `plains_eye`;
  - **forest**: `forest_interior_eye`.
- Two kinds of number:
  - **T**: GPU timestamps of the newest readback frame, read every frame (engine `vp_begin/vp_end` per viewport plus every game marker), in ms per frame averaged over 3 to 4 baseline windows.
  - **A**: A-X-A ablation, 8 s windows, the GPU p50 of all viewports against the mean of the two baselines around it.
  - Noise between the two baselines is 0.00 to 0.12 ms (0.35 once on the plains).
  - Godot 4.8-dev7 exposes no engine sub-pass timestamps (prepass, opaque, shadow, sky), so these come from ablations.
- **The save spot costs 15.5 ms here, not 22.5 ms.** The 22.5 ms in 0.12 came from waste runs 1 and 2, where the tree GPU compute failed to initialize (see the caveat there).

**Per-pass GPU at the three poses** (ms; ablation rows overlap and must not be added; "per dispatch" in brackets):

| pass | method | save | plains | forest |
|---|---|---|---|---|
| **frame GPU, all viewports (p50)** | T | **15.46** | **13.06** | **15.46** |
| main viewport | T | 13.62 | 11.47 | 13.70 |
| near terrain, including its sun-map casting | A | 4.54 | 5.13 | 4.89 |
| trees, all passes | A | 3.99 | 2.78 | 4.68 |
| of which tree sun shadows | A | 0.58 | 0.21 | 0.33 |
| sun shadow map, all casters (2 cascades, 400 m) | A | 1.68 | 1.75 | 1.26 |
| sun-map draws / primitives (of which trees) | count | 756 / 3.1 M (1.7 M) | 831 / 2.1 M (0.8 M) | 739 / 2.5 M (1.2 M) |
| planet light shadows (off by day) | A | 0.00 | 0.00 | 0.00 |
| global hull shadow casters (walls and roof) | A | 0.01 | 0.00 | 0.02 |
| every caster farther than 20 km | A | 0.01 | 0.00 | 0.01 |
| grass draw | A | 0.56 | 0.25 to 0.51 | 0.67 |
| grass culling compute | T | 0.021 (0.031) | 0.021 (0.031) | 0.021 (0.031) |
| tree culling compute (lane T work in progress) | T | 0.16 (0.24) | 0.16 (0.23) | 0.17 (0.26) |
| **Sunshine clouds, inclusive** | T | **2.21** | **2.41** | **2.25** |
| clouds: prepass | T | 0.025 | 0.021 | 0.022 |
| clouds: raymarch | T | 1.39 | 1.60 | 1.43 |
| clouds: interleave | T | 0.35 | 0.35 | 0.35 |
| clouds: reconstruct | T | 0.41 | 0.41 | 0.41 |
| clouds: composite | T | 0.04 | 0.03 | 0.03 |
| clouds at half resolution (**visual**) | A | 0.84 | 1.36 | 0.71 |
| cloud shadow, near cascade (1,024², 10 Hz in slices) | T | 0.52 (0.92) | 0.62 (1.29) | 0.65 (1.17) |
| cloud shadow, far cascade | T | 0.011 (0.28) | 0.015 (0.44) | 0.017 (0.43) |
| cloud macro map / storm map | T | 0.007 / 0.005 | 0.006 / 0.010 | 0.007 / 0.011 |
| terrain-cloud sun visibility (0.5 s generations) | T | 0.018 (0.055) | 0.014 (0.056) | 0.017 (0.056) |
| height shadow slices | T | 0.008 (0.025) | 0.006 (0.025) | 0.007 (0.022) |
| haze sky-view LUT (rebuilt every frame while the sun moves) | T | 0.007 | 0.007 | 0.007 |
| far-ring viewport | T (GPU) / A (wall) | 1.54 / 1.67 | 1.36 / 1.54 | 1.37 / 1.42 |
| far weather cloud bake viewport (every 2 s) | T | 0.007 (0.12) | 0.006 (0.12) | 0.007 (0.12) |
| far backdrop composite | A | 0.01 | ≤ 0.10 (noise 0.20) | 0.01 |
| global LOD terrain in the main viewport | A | 0.09 | 0.00 | 0.05 |
| engine SSR (dry) | A | 0.86 | 0.19 to 0.35 | 0.26 to 0.43 |
| SSAO | A | 0.03 | 0.01 | 0.09 |
| FXAA | A | 0.11 | 0.00 | 0.05 |
| minimap viewport | T | 0.006 | 0.006 | 0.006 |
| water FFT | T | not dispatched (no ocean in range) | same | same |
| rain haze and streak composites | T | not dispatched (dry) | same | same |
| wet map dispatch and copy | T | 0.007 per dispatch, 0.5 per s: < 0.001 per frame | same | same |
| wet shading, all receivers, forced wet | A | 0.48 to 0.54 | 0.58 to 0.62 | 0.56 to 0.60 |

The clouds run as a post-transparent compositor effect. Everything before them in the main viewport takes 10.7 ms at the save, 8.3 on the plains and 10.7 in forest: depth prepass, sun map, opaque, sky and transparent. Near terrain, trees, the sun map, grass and SSR account for most of it.

**Ranked offload and cadence candidates** (savings measured unless marked est.):

| # | candidate | saves | image risk | notes |
|---|---|---|---|---|
| G1 | **Near cloud-shadow cascade at 5 Hz instead of 10 Hz** (`RingSunshineCloudBridge.GpuNearShadowUpdatesPerSecond`, Ultra preset) | marker 0.41 → 0.21 ms per frame; frame mean −0.20 to −0.21 ms, wall −0.22 to −0.28 ms. At 2.5 Hz −0.26 to −0.33 ms | low but real: cloud-shadow edges advance in 0.2 s steps instead of 0.1 s | User decision. Check with a 10 s capture and `ffmpeg_tools flicker` before and after. A CPU version is not sensible: 1,024² texels of cloud-density raymarching per update. |
| G2 | **Engine SSR only while the wet world is valid** (the CPU already knows: `RingWetWorld.Valid`; the water has its own SSR) | 0.86 ms at the save, 0.19 to 0.35 on the plains, 0.26 to 0.43 in forest, dry | low to medium: any dry surface with roughness below 0.7 loses its screen-space reflection | User decision. Needs a still A/B at the 4 poses and at a POI with metal. Also needs a fade on the switch so wet onset does not pop. |
| G3 | **Tree wetness per instance, not per vertex** (`ring_wet_sample_vegetation` runs in the tree vertex shaders; write one wet term per instance in the vegetation compute instead) | about 0.3 ms in forest while wet (wet adds 0.26 ms with trees hidden against 0.56 to 0.60 with trees) | none expected: the map texel is 64 m and a tree is smaller, so one sample per tree matches per-vertex sampling except within a few metres of a texel edge | Lane T owns the files. Prove it with a forced-wet still compare. |
| G4 | **Tree cull re-runs on 2 of 3 frames while the camera stands still** (`GPU.TreeCulling` 0.23 to 0.26 ms per dispatch) | 0.16 ms per frame est. | none | Pass to lane T. Measured with their work in progress; at HEAD (0.12) it was 0.03 to 0.09 ms per dispatch. |
| G5 | Far-ring viewport at a lower cadence or resolution | 1.4 to 1.7 ms | **visual** (section 0.12 W8) | Already a user decision in section 7. |
| G6 | Clouds at half resolution | 0.7 to 1.4 ms | **visual** | As in section 7. |
| — | Gate on input change, or move to a worker thread: sky-view LUT (0.007), terrain-cloud sun visibility (0.014 to 0.018; switching its processing off measured 0.00 to 0.05), height shadow (0.006 to 0.008), macro and storm maps (≤ 0.011), far weather bake (0.007), wet map (< 0.001), grass cull (0.021) | ≤ 0.02 ms each, about 0.07 ms in total | none | **Not worth doing.** On the CPU each would be slower and would then need an upload: the sky LUT is 192 x 108 x 3 sky integrals, the visibility field 1,026 x 34 x 8 terrain traces. |

The large GPU costs, near terrain and trees, are triangles (section 5). The CPU's part in reducing them is choosing less geometry: tree frustum culling (OC-1 to OC-3, lane T) and terrain LOD. Those belong to the tree and triangle study, not here.

**Wet world change (WW-1, uncommitted).** User-approved direction.
- Files:
  - `materials/ring_wetness.gdshaderinc`: new helpers `ring_wet_camera_world` and `ring_wet_reach`. `ring_wet_sample` and `ring_wet_sample_vegetation` return zero beyond the reach and multiply by the reach factor inside it.
  - `scripts/Weather/RingWetWorld.cs`:
    - `DefaultReachMeters = 6500`, published in the previously unused `ring_wet_clock.z`; `ReachMeters` is a test hook;
    - the camera height goes in `ring_wet_frame` column 3 `.w`;
    - `RefreshSeconds` 0.5 → 2.0; `MoveDispatchTexels = 4`.
  - `RingWetWorld.Gpu.cs` and `scripts/diagnostics/RingPerformanceGpu.cs`: a `GPU.WetMap` marker.
  - `ring_terrain_global.gdshader` and `ring_ocean_global.gdshader`: `wet_world_receiver`, set false by `RingScaledFarViewport.CreateMaterials`.
  - A dated note in `docs/storm-revamp-s2-wet-world-design.md`.
- Inside 5.2 km the reach factor is exactly 1.0, so near wetness is bit-identical. No new global uniform, no `project.godot` change.
- **No grave-key row.** The reach is tied to the 16 km map window: it must end inside the full-weight region even with the 256 m dispatch lag, and it saves 0.05 ms. It is a constant, not a look tunable.
- Before and after (GPU p50 of all viewports, ms; the map forced fully wet with `ForceUniform(0.9, 0.9, 0.7, 0.6)`; old reach = `ReachMeters` 10,000 km plus far-viewport wetness on; sequence dry, old, new, dry, new, old, dry):

  | pose | dry | wet, old reach | wet, new reach | old − new | far viewport GPU, old → new |
  |---|---|---|---|---|---|
  | save | 15.27 | 15.81 | 15.74 | 0.07 | 1.558 → 1.548 |
  | plains | 12.85 | 13.47 | 13.43 | 0.04 | 1.368 → 1.363 |
  | forest | 15.32 | 15.88 | 15.92 | −0.04 (noise; nothing beyond 5 km in view) | 1.382 → 1.371 |

- Cadence: 2 → 0.5 dispatches per second while wet (counted: 171 in 340 s), 0.007 ms of GPU each, dispatch plus copy. The GPU saving is under 0.001 ms per frame. The gain is fewer render-thread calls, not GPU time.
- Map display filter: it is time-based (alpha = 1 − exp(−dt / 0.5 s)), so a 2 s step lands about 98 % of the way. Film and soak change over 90 to 300 s (`WetnessModel` taus), so a step is a few percent at most.
- Image check: stills with the clocks held, old against new, and old against old as the noise pair (`ffmpeg_tools compare`):
  - save: SSIM 0.99828 against the noise pair's 0.99819. The heatmap matches the noise pair everywhere except two small patches of distant land on the horizon, both beyond 6.5 km.
  - plains, with the arch in view: SSIM 0.99963 against 0.99936. Same heatmap as the noise pair; the arch shows no change.
  - Evidence: `.godot/perf_pass_real/gpub_run1/cmp_{spawn,plains}_{new_vs_legacy,legacy_vs_legacy}.png`, plus the stills `wetcap_*_1_legacy.png` and `wetcap_*_2_new.png`.
- Not measured: a real storm (it was dry at the poses, so the map was forced); flight at speed with the new 4-texel move rule.

**Inner ring shell and far arch: shadow findings.**
- **Far viewports (`FarRingViewport`, `FarRingRegionalViewport`)**:
  - Both have their own World3D and positional atlas 0. Their lights (`FarRingSun`, `FarRingPlanetLight` and the regional pair) have `ShadowEnabled = false`.
  - Every mesh created there is `CastShadow = Off`.
  - They do no shadow-map work, and the main lights do not reach them.
- **Main viewport sun map** (StarLight, 2 cascades, 400 m in near-map mode). Far casters that are still on:
  - the global LOD hull: `GlobalStructuralEdgeWalls_*` (always On, `RingTerrainGlobalLod.cs` about line 1785) and `GlobalStructuralRoof_*` (hard On, about line 1834). That is 256 nodes, 20 of them visible casters.
  - The segment under the camera has the camera inside its AABB, so it enters every cascade.
  - Cost: 5 to 24 shadow draws and 3,000 to 88,000 primitives; 0.00 to 0.02 ms of GPU (noise).
  - All 18 visible casters whose bounds lie more than 20 km away together cost 0 ms.
  - Global LOD terrain, proxy oceans, analytic ring, cloud layers and stand-ins already cast nothing.
- **Planet light**: shadows off by day (energy 0 at noon), 0 ms. The scene's planet map is 6 km with 4 splits when it is on (night). Not measured here.
- **Receivers**: far terrain beyond the 400 m map does no cascade lookups.
- **Cloud-shadow systems covering the arch**, all cheap:
  - the far weather shadow bake: 0.12 ms every 2 s;
  - the macro terrain mask: built on the CPU, one texture fetch per pixel;
  - the whole-ring terrain-cloud sun visibility: 0.014 to 0.018 ms per frame.
  - The near and far Sunshine cascades (24 km and 96 km) cover only the main world.
- **Unmeasured side finding**: the far viewport reuses `ring_terrain_global` and `ring_ocean_global`. Their `light()` evaluates the main world's height-shadow globals on scaled positions (`ring_height_shadow.gdshaderinc`). It costs ALU on every arch pixel and could in principle pick up stray height-shadow texels. Its cost is inside the 1.4 to 1.5 ms far viewport; a per-material switch like `wet_world_receiver` would remove it.
- **Fix: none applied.** Gating the hull casters the way the proxy hull is gated (`ResolveStructuralHullShadowRelevance`: on only within 25 km of an edge or 2 km of the cavity) would be identical by construction, because the hull lies outside every cascade footprint elsewhere. It saves only draw calls (0.00 to 0.02 ms). It also touches `RingTerrainGlobalLod.cs`, which holds another lane's uncommitted W1/W2 edits. It is written up as an optional order, **SH-1**: S size; identity by a sun-map depth still compare at the save and plains; tests `terrain_lod_shadow_coverage_smoke_test` and `certified_terrain_macro_shadow_visual_smoke`.

**Evidence** (`.godot/perf_pass_real/`):
- `gpub_run1/`: the three poses with 17 ablations each, the wet A/B and the stills; `report.json` holds the per-window `ts` segments and the shadow draw and primitive counts.
- `gpub_run2/`: the cloud-shadow cadence at 5 and 2.5 Hz and an SSR repeat, at the plains and forest poses.
- Driver `real_world_perf_gpub.gd`, runner `run_gpub.ps1`.

### 0.15 Near terrain: where the terrain GPU time goes and what can go (2026-11-16)

Question: near terrain was the largest item in 0.14 (4.5 to 5.1 ms of a 13 to 15.5 ms frame). Where does that time go, and how much can be cut without a visible change?

**Answer in short.**
- **The 0.14 bucket was not "near terrain".** Its ablation (`near_terrain_hidden`) hid the Terrain3D surface **and all 15 proxy LOD levels** (every `RingTerrainProxySector` level, out to 500 km, with their ocean and hull children). It also counted what appears behind hidden terrain (sky, clouds). And 0.14 ran the **Ultra** preset; the user now plays **High**.
- **At High, the same bucket costs 1.8 to 2.4 ms** of main-viewport GPU at the save, plains and forest, and 3.0 ms of pre-sky passes at a valley pose looking up a slope (prepass plus opaque). **Switched to Ultra, it is 4.6 to 5.2 ms again**, so 0.14's figure is reproduced and nearly all of it is the proxy rings (4.5 to 5.3 ms at Ultra).
- **Terrain3D (the near clipmap, 0 to 112 m) is the small part: 0.35 to 0.59 ms. The proxy rings are the large part: 1.3 to 2.0 ms.**
- **Both are geometry-bound, not pixel-bound.** At half resolution the proxy rings cost the same, and Terrain3D 76 to 87 % as much. A flat fragment shader saves 0.15 to 0.41 ms on the proxies and 0.10 to 0.74 ms on Terrain3D.
- **Terrain3D wasted most of its work.** It drew all six clipmap levels (380,000 triangles) in the prepass, the colour pass and both sun cascades, at every view direction, but only level 0 within the 112 m handoff can produce a pixel. Two identical changes are done (uncommitted): build only the levels that can draw, and drop vertices beyond the handoff. **Saving 0.16 to 0.28 ms at every pose; stills match the noise pair.**
- **The proxy rings draw about 2.7 to 3.0 million triangles per camera pass**, about 230,000 to 330,000 per level whatever its distance. The 2 m level (Lod1) is the costliest single level (0.41 to 0.59 ms, including all proxy shadow casting). Levels 5 to 9 (4 to 500 km) together cost 0.58 to 0.68 ms for a thin band at the horizon.
- **Further identical work:** proxy sectors wholly beyond their level's far fade are still drawn (about 10 % of proxy triangles in view at High): 0.08 to 0.16 ms at High, 0.4 to 1.4 ms at Ultra (order NT-2). Terrain3D's own shadow casting duplicates the proxy casters within 112 m: 0.05 to 0.13 ms, unchanged at noon (NT-4, needs a low-sun check). Everything bigger changes the image and needs the user's decision (section 4).

**Method.**
- Slot 3 copy through the real-world driver: `.godot/perf_pass_real/real_world_perf_nt.gd` (the 0.14 driver plus a `--nt` flow), runner `nt/run_nt.ps1` (GPU queue label `near-terrain`, isolated APPDATA, optimized C# swapped into the Debug slot and restored). Optimized C# built from HEAD `a785e4b` (runs r1 to r3) and HEAD plus NT-1 (r4 to r6).
- 1600 x 900, VSync off, RTX 4070 at P0, **High** preset with the user's files (atlas 8,192 at 16-bit, 2 sun cascades 0 to 160 m and 160 to 400 m, tree near detail 10/20 m as the user set it, Terrain3D mesh LODs 6 from the user's `gameplay_surface_terrain_defaults.tres`, handoff 112 m). Noon with the clocks running.
- Poses:
  - **save**: 20,295.04 km around, 123.86 km across, 624 m, under a tree (`--force-pose`);
  - **plains** (`plains_eye`) and **forest** (`forest_interior_eye`) from section 0.1;
  - **slope**: the bottom of the steepest bowl near the old save (6,356,921 / 186,654, ground 1,210 m), looking up a 490 m rise (yaw 270).
- Timing: A-X-A, 7 to 8 s windows, GPU p50. The main viewport is split by compositor timestamps: **prepass** (viewport start to `PRE_OPAQUE`) and **opaque** (`PRE_OPAQUE` to `POST_OPAQUE`, which also holds the sun shadow map). Noise between the two baselines around a case: 0.00 to 0.08 ms.
- Counts: Godot's render info per pass (camera list = depth prepass and colour pass, which draw the same list; shadow = both cascades together), with Terrain3D or the proxies hidden, at four yaws per pose. A CPU census of every proxy mesh (custom AABB against the camera frustum, distance to the level's fades).
- Fragment cost: the Terrain3D and proxy shaders were swapped at runtime for variants (flat: the discards only; no `light()`; no haze; no biome textures). The pixel share: the same hides at 50 % 3D scale.
- Image checks: stills with clouds, grass and gust debris hidden and the clocks held, `ffmpeg_tools compare` against an A-A noise pair taken in the same sequence.

**1. Where the time goes** (main-viewport GPU ms saved; prepass / opaque-plus-sun-map in brackets; r1 and r2, before the change):

| part | save | plains | forest | slope |
|---|---|---|---|---|
| frame GPU, all viewports | 10.3 to 10.4 | 9.3 to 9.4 | 9.5 to 9.6 | 7.0 to 7.2 |
| main viewport: prepass / opaque+sun map | 1.71 / 4.39 | 1.45 / 3.66 | 1.53 / 3.76 | 1.20 / 2.98 |
| 0.14 bucket: Terrain3D + every proxy level hidden | 1.94 (0.64 / 1.39) | 2.39 (0.67 / 1.73) | 1.76 (0.57 / 1.23) | 0.97 (0.76 / 2.21; sky and clouds take over the freed pixels) |
| **Terrain3D** hidden | 0.35 to 0.38 (0.05 / 0.31) | 0.46 to 0.47 (0.11 / 0.34) | 0.38 to 0.40 (0.06 / 0.33) | 0.58 to 0.59 (0.13 / 0.46) |
| of which its sun-map casting | 0.25 | 0.26 | 0.30 | 0.32 |
| **proxy rings** hidden | 1.59 to 1.61 (0.60 / 1.07) | 1.48 to 1.50 (0.56 / 0.95) | 1.33 to 1.35 (0.52 / 0.81) | 1.79 to 2.00 (0.59 / 1.16) |
| of which proxy sun-map casting (Lod1 casters only) | 0.23 | 0.21 | 0.24 | 0.08 |
| Lod1 (2 m, 112 to 493 m) hidden, incl. its casters | 0.45 | 0.46 | 0.41 | 0.59 |
| Lod2 / Lod3 / Lod4 hidden | 0.12 / 0.17 / 0.18 | 0.14 / 0.11 / 0.09 | 0.13 / 0.08 / 0.10 | 0.00 / 0.13 / 0.08 |
| Lod5 to Lod9 hidden (4 to 500 km; r5) | 0.58 (0.33 / 0.29) | 0.65 (0.28 / 0.29) | 0.67 (0.27 / 0.29) | 0.68 (0.27 / 0.28) |
| at 50 % 3D scale: Terrain3D / proxies | 0.31 / 1.19 | 0.40 / 1.33 | 0.32 / 1.37 | 0.47 / 1.64 |
| flat fragment shader: Terrain3D / proxies | 0.10 / 0.40 | 0.49 / 0.22 | 0.18 / 0.15 | 0.74 / 0.31 |

- **Prepass against colour.** The proxies spend 0.52 to 0.60 ms in the depth prepass and 0.8 to 1.2 ms in the opaque span. Terrain3D spends only 0.04 to 0.13 ms in the prepass; almost all of its cost was in the opaque span, and most of that (0.25 to 0.32 ms) was its sun-map casting.
- **Per cascade.** Terrain3D sent the same 380,000 triangles into both cascades (nothing of it is culled, see 2), so its casting splits evenly: about 0.13 to 0.16 ms per cascade. Only Lod1 of the proxies casts (the other levels have `CastTerrainShadows` off); by a census estimate, about 30 % of its caster triangles fall in cascade 0 (0 to 160 m) and 70 % in cascade 1; Godot culls the rest per cascade (0.37 to 0.42 M of the casters' 0.64 to 1.23 M triangles reach the map).
- **Vertex against fragment.** Halving the resolution leaves the proxy cost unchanged (1.19 to 1.64 against 1.33 to 2.00 ms; the differences are within the pose-to-pose spread) and leaves 76 to 87 % of the Terrain3D cost. The fragment shader still matters where the terrain fills the screen (Terrain3D flat saves 0.49 to 0.74 ms on the plains and the slope): the triangles are so small near the horizon that the GPU shades mostly 2 x 2 quads that a triangle barely touches, so cheaper fragments help even though fewer pixels do not.
- **Why 0.14 saw 4.5 to 5.1 ms.** At Ultra, Lod1 (2 m) reaches 650 to 800 m and Lod2 1,500 to 1,900 m instead of 320 to 450 and 800 to 1,100 m (`resources/voxel/ring_lod_ultra_video.tres` against `ring_lod_high.tres`), with a 0.85 px error target instead of 2.0. Measured at Ultra (r6, NT-1 already in; save / plains / slope):
  - frame GPU, all viewports: 14.2 / 12.4 to 12.5 / 10.1 to 10.2 ms;
  - the 0.14 bucket: **4.81 / 5.15 / 4.62 ms** (prepass 1.71 / 1.73 / 2.13);
  - proxies: 4.54 / 4.47 / 5.28 ms, with **10.8 / 11.4 / 13.2 M proxy triangles per camera pass** (High: 2.7 to 3.0 M);
  - Lod1 0.95 / 0.70 / 1.91 ms; Lod2 0.48 / 0.39 / 0.25 ms;
  - Terrain3D 0.06 / 0.19 / 0.33 ms; NT-1 still saves 0.20 / 0.23 / 0.20 ms;
  - the NT-2 oracle (sectors wholly beyond their far fade) saves **0.43 / 0.40 / 1.37 ms**.

**2. Triangles per pass** (millions; camera list = depth prepass and colour pass each; shadow = both cascades):

| | save | plains | forest | slope |
|---|---|---|---|---|
| terrain, camera list | 3.11 | 3.41 | 3.35 | 3.12 |
| of which Terrain3D | 0.38 | 0.38 | 0.38 | 0.38 |
| of which proxies | 2.73 | 3.03 | 2.97 | 2.74 |
| terrain, shadow list | 1.14 | 1.18 | 1.16 | 1.16 |
| of which Terrain3D | 0.76 | 0.76 | 0.76 | 0.76 |
| of which proxy Lod1 casters | 0.37 | 0.42 | 0.40 | 0.40 |
| terrain triangles submitted per frame (2 x camera + shadow) | 7.4 | 8.0 | 7.9 | 7.4 |

- **Terrain3D is never frustum-culled.** Its triangle count is 380,000 in the camera list and 760,000 in the shadow list at every pose and at all four yaws. The Terrain3D node sits untransformed at the world origin and its shader moves the planar clipmap into the ring frame (`ring_frame_basis`, rotated about 37 degrees and with planar up mapped to a horizontal axis). To keep that safe, `RingTerrain3DSurface.ApplyWorldFrameMaterial` sets `cull_margin` to the window half-diagonal plus the height extent plus the frame offset (about 623 m), so every piece passes Godot's frustum and cascade tests.
- **What of it can draw.** fragment() discards everything farther than the 112 m handoff (84 to 128 m in the menu) in every pass. Level 0 (2 m cells) reaches 192 m each side of the camera, so levels 1 to 5 (300,000 triangles, the 4 m to 64 m cells reaching out to 6 km, and mostly outside the 768 m height window where the vertex shader already writes NaN) never produce a pixel or a shadow texel. Of level 0's 80,000 triangles, about a quarter lie within the handoff and roughly a third of those in view: **about 2 % of the Terrain3D triangles submitted per pass can show.**
- **The user's "Terrain3D mesh LODs" = 6** (default 4) is what made it 380,000; with the default it would be 260,000. The setting has no visible effect while the handoff is on.
- **Mesh density.** Terrain3D level 0 and proxy Lod1 are both 2 m lattices, the same density as the height data. Each proxy level is about 70 sectors of 64 x 64 cells (8,704 triangles each, skirts included) whatever its cell size, so each level puts 210,000 to 340,000 triangles in view. Near the horizon those triangles are far below a pixel tall.
- **Proxy triangles that cannot show** (census, triangles in the camera frustum):

  | | save | plains | forest | slope |
  |---|---|---|---|---|
  | proxy triangles in view | 2.65 M | 2.96 M | 2.90 M | 2.66 M |
  | in sectors wholly beyond their level's far fade (every fragment discarded) | 0.27 M (10 %) | 0.32 M (11 %) | 0.31 M (11 %) | 0.28 M (10 %) |
  | in sectors wholly inside the next finer level's reach (vertex-collapsed by the coverage atlas, still vertex-processed) | 0.22 M (8 %) | 0.25 M (9 %) | 0.26 M (9 %) | 0.19 M (7 %) |
  | in sectors wholly inside the near hole | 0 | 0 | 0 | 0 |

- **Double coverage.** The proxies do not double-draw each other beyond the fade bands: each level discards inside its near fade or is collapsed where the finer level covers it. Terrain3D and Lod1 do overlap: Lod1 starts at the 112 m handoff while Terrain3D level 0 runs to 192 m (discarded there), and **within 112 m both cast sun shadows**, because the proxy shadow caster (`ring_terrain_proxy_shadow.gdshader`) has no near hole. Turning Terrain3D's casting off leaves noon stills unchanged (SSIM against the noise pair: 0.99966 / 0.99963, 0.99996 / 0.99994, 0.99998 / 0.99998, 0.99959 / 0.99962; same heatmaps); a low sun is not checked (NT-4).

**3. The fragment shaders.**
- **Terrain3D** (`materials/terrain3d_ring_surface.gdshader`) per pixel: 12 region-map lookups (each a loop over the 1,024-entry `_region_map` uniform) and 12 height `texelFetch`es for the smoothed normal and height, 4 colour fetches for biome weights, 6 biome albedo maps triplanar (up to 18 anisotropic taps; dirt skipped on fully flat ground), grass coverage, the analytic haze (`ring_haze_sample`), the local cloud shadow, wetness only while wet, and in `light()` the macro and planet shadow masks, the height-shadow clipmap, the shadow compose terms, contact shadows and PCF.
  - Measured: `light()` 0.01 to 0.23 ms, haze 0.04 to 0.10, biome textures 0.00 to 0.20, everything (flat) 0.10 to 0.74. The rest of the flat saving is the 12 lookups and the normal.
  - **Waste:** the three region lookups and the discard test come first, and in the shadow pass and for every fragment beyond the handoff they ran for nothing; the vertex drop (5) removes those fragments.
- **Proxy** (`materials/ring_terrain_proxy.gdshader`): aperture and coverage discards, near and far fade discards, then a branchy biome triplanar (only the weights present), grass coverage, haze, the cloud and far-weather shadow, and in `light()` the height shadow, macro and planet masks, shadow terms and contact shadows. Measured: flat 0.15 to 0.40, `light()` 0.06 to 0.14, haze 0.05 to 0.08, biome textures 0.00 to 0.05. It is already lean; nothing large is computed and thrown away.
- **Not worth it:** a cheaper triplanar or skipping layers at distance. Terrain3D only draws to 112 m, and the proxy texture work is at most 0.05 ms.

**4. Options** (ms saved, measured unless marked est.; identical = no pixel can change by construction and stills match the noise pair; near-invisible = a change below what the stills show; image = needs the user's decision):

| # | option | save / plains / forest / slope | image | status |
|---|---|---|---|---|
| NT-1 | **Terrain3D: build only the clipmap levels that can draw, drop vertices beyond the handoff** | **0.16 to 0.19 / 0.19 to 0.22 / 0.14 to 0.19 / 0.20 to 0.28** | identical | **done, uncommitted** (5) |
| NT-2 | Proxy: take sectors wholly beyond their far fade (or inside the near hole) out of the camera layers; their casters are separate nodes and keep casting | High: 0.09 to 0.12 / 0.09 to 0.16 / 0.09 to 0.12 / 0.08 to 0.13; **Ultra: 0.43 / 0.40 / - / 1.37** | identical | order below |
| NT-3 | Proxy: skip sectors whose every cell the coverage atlas marks covered (now vertex-collapsed, 7 to 9 % of proxy triangles in view) | est. 0.05 to 0.10 | identical | order below |
| NT-4 | Terrain3D stops casting sun shadows; the proxy Lod1 casters (same 2 m lattice) already cover 0 to 112 m | after NT-1: 0.08 to 0.11 / 0.10 to 0.11 / 0.10 to 0.13 / 0.05 to 0.12 | near-invisible at noon (stills match the noise pair); low sun unchecked | order below; user sign-off |
| NT-5 | Proper frustum culling of Terrain3D pieces (node transform = ring frame, real AABBs) | est. < 0.05 after NT-1 (80,000 triangles left, beyond-handoff ones already dropped) | identical | not worth it; needs Terrain3D's mesher to follow a transformed node (native rebuild) |
| NT-6 | Proxy near-hole vertex collapse (oracle `proxy_holecollapse`) | 0.00 to 0.10 (noise) | identical | dropped |
| NT-7 | Culling proxy pieces from cascades they cannot reach | 0 | | already done by Godot: only Lod1 casts, and its casters' custom AABBs are culled per cascade |
| U-1 | Coarser far proxy levels: 32 x 32 sectors for Lod5 and beyond (4 km out), or a looser error target | est. half of the Lod5-9 cost (0.58 to 0.68 ms), so about 0.3 ms | image: distant silhouettes | user decision |
| U-2 | Narrower Lod1 band: at High the 2 m proxy runs from 112 m to a 493 m handoff (`Lod1OutMeters` 320/450 in `ring_lod_high.tres`, widened by the screen-space selection); 240/320 as in the scene default | est. 0.15 to 0.25 (Lod1 is 0.41 to 0.59 ms) | image: the 4 m proxy from about 280 m | user decision; a preset value |
| U-3 | Terrain3D fragment: drop the custom `light()` terms that matter little at 0 to 112 m | 0.01 to 0.23 | image | not recommended |
| U-4 | Lower the user's terrain preset (Ultra to High already did this) | 0.14's 4.5 to 5.1 ms became 1.8 to 2.4 ms | image | already the user's choice |

**5. Done: NT-1 (uncommitted).**
- `scripts/Terrain/Surface/RingTerrain3DSurface.cs`: new `EffectiveMeshLods(requested, meshSize, spacing, handoff)` and `ApplyNativeMeshLods()`. Terrain3D is given only the levels whose reach (2 x mesh_size cells each side, less two cells of snapping) is needed to pass the handoff plus 8 m; with the shipped 48-cell mesh and any handoff from 84 to 128 m that is **one level**. The requested value is kept everywhere else (menu, preference, staging residents); with the handoff off (0) nothing changes. Re-applied when the handoff or mesh size changes. `RingTerrain3DSurface.PreparedWindow.cs` uses it at native creation.
- `materials/terrain3d_ring_surface.gdshader`, `vertex()`: a vertex farther than handoff + 3 cells + 32 m from the viewer gets the same NaN the shader already writes for holes. Every triangle touching it lies wholly beyond the handoff (a triangle spans at most 2.5 cells across and the margin allows a 32 m height step over one cell), so fragment() would discard all of it; the same uniform drives both tests in every pass, shadows included.
- `tools/terrain_generation_staging_test.gd`: its two native-`mesh_lods` expectations under an 84 m handoff now expect 1 (the clamp is the intended contract).
- **Before/after** (r4, built with the change; "legacy" = the old shader code restored at runtime and `mesh_lods` 6; three A-X-A rounds per pose; GPU p50 of all viewports):

  | pose | after | legacy | saved | prepass / opaque saved | noise |
  |---|---|---|---|---|---|
  | save | 10.21 to 10.26 | 10.40 to 10.41 | 0.16 to 0.19 | 0.00 / 0.18 to 0.19 | 0.01 to 0.03 |
  | plains | 9.23 to 9.30 | 9.45 to 9.52 | 0.19 to 0.22 | 0.00 / 0.20 to 0.22 | 0.00 to 0.02 |
  | forest | 9.41 to 9.53 | 9.63 to 9.64 | 0.14 to 0.19 | 0.00 / 0.14 to 0.19 | 0.01 to 0.06 |
  | slope | 6.77 to 6.81 | 6.99 to 7.06 | 0.20 to 0.28 | 0.00 / 0.20 to 0.23 | 0.02 to 0.03 |

  Terrain3D now submits 80,000 triangles per camera pass and 160,000 to the sun map (was 380,000 and 760,000). What Terrain3D still costs (r5, hidden against shown): 0.19 / 0.22 / 0.22 / 0.43 ms (was 0.35 to 0.59), of which sun-map casting 0.08 to 0.14 (was 0.25 to 0.32).
- **Image.** Stills, after against legacy, with an after-after noise pair from the same sequence (`ffmpeg_tools compare`): SSIM of legacy against the two after stills, then of the after-after noise pair: save 0.99956 / 0.99967 against 0.99955; plains 0.99995 / 0.99994 against 0.99998; forest 0.99998 / 0.99998 against 0.99999; slope 0.99950 / 0.99972 against 0.99952. The heatmaps match the noise pairs: differences only in the sky (cloud shimmer) and in wind-moved distant trees on the horizon, which Terrain3D cannot reach. The oracles before the code change (runtime variants on HEAD) gave the same picture at all four poses.
- **Tests:** `tools/run_tests.ps1 -Tests terrain_generation_staging_test,terrain3d_gameplay_smoke,terrain3d_surface_pilot_smoke,terrain_generation_playable_smoke,surface_terrain_settings_smoke,terrain3d_addon_smoke,terrain_lod_shadow_coverage_smoke_test,smooth_terrain_overlap_ownership_test,terrain3d_gameplay_stream_test,terrain_generation_world_smoke,terrain3d_godot48_compatibility_test,terrain3d_gameplay_native_test,terrain_surface_include_regression,terrain_cloud_receiver_gpu_test,terrain_storm_shadow_parity_smoke_test -Label near-terrain`: 15 of 15 pass (run 20261117-001437). The `-Files` selection is 108 tests, mostly terrain generation, which this change does not touch; leave it to the batch gate.
- Not measured: the A1000; mesh sizes other than 48 (for 16 to 40 the formula keeps 2 or 3 levels; check with a still before shipping a smaller mesh size); the eclipse-hub and night lighting (the change is in geometry only, so lighting cannot interact).

**6. Work orders.**

**NT-2: proxy sectors beyond their far fade leave the camera (S/M, identical).**
- Where: the proxy level's per-frame update (`RingTerrainProxySector`, the code that sets `camera_world_position` and the fade uniforms on the level material) or the coordinator's per-level camera update.
- Change: for each sector node of a level with `far_fade_enabled`, when its custom AABB's nearest point is farther than the far handoff ((fade_out_start + fade_out_end) / 2) plus 1 m, set the sector's `Layers` to 0; restore when it comes back inside. With `near_fade_enabled`, the same when the AABB's farthest point is inside the near handoff minus 1 m. Touch only the sector `MeshInstance3D`; its `ShadowsOnly` caster child keeps its own layers (shadows unchanged). Only write `Layers` on a change; evaluate at most once per frame per level, or every few frames with a margin of the distance the camera can travel in between.
- Proof: same fragments are discarded by the shader (`ring_terrain_proxy.gdshader` fragment), so identical by construction. Check with `.godot/perf_pass_real/real_world_perf_nt.gd` (`proxy_farcull` is the oracle) and stills against a noise pair at the four poses; flight at 240 m/s to watch for popping at the far-fade boundary (none expected; flicker check with `ffmpeg_tools flicker`).
- Expected: 0.08 to 0.16 ms GPU at High, 0.4 to 1.4 ms at Ultra; also fewer draws (25 to 400 per frame).
- Tests: `proxy_partial_coverage_test`, `terrain_lod_shadow_coverage_smoke_test`, `lod_altitude_activation_contract_test`, `proxy_coverage_culling_settings_smoke`.

**NT-3: fully covered proxy sectors (S, identical; after NT-2).**
- Sectors whose coverage-atlas rect is all covered are drawn as zero-area triangles (vertex collapse) but still pay vertex work, four atlas fetches per vertex included. When the level's CPU coverage mask (the source of the atlas) marks every cell of a sector covered and the finer level is resident (the same readiness gate the atlas uses), take the sector out of the camera layers as in NT-2.
- Expected 0.05 to 0.10 ms est. Measure with an oracle first (hide the sectors the census marks covered); drop it if under 0.05 ms.

**NT-4: Terrain3D stops casting sun shadows (S, near-invisible; user sign-off).**
- Within the 112 m handoff both Terrain3D and the proxy Lod1 shadow casters cast into the sun map; Lod1 casters are the same 2 m lattice (`AlignSurfaceToTerrainSdf`, the canonical diagonal), so the map depth is nearly the same.
- Change: `cast_shadows` = 0 on the gameplay Terrain3D nodes (`RingTerrain3DSurface`, where the native node is configured; prepared residents too).
- Before shipping: stills at sun 5, 10 and 20 degrees at the slope pose and the save, against a noise pair (`real_world_perf_nt.gd` label `t3d_shadow_off`), and the shadow debug view (`ring_shadow_debug`) of the sun term. If the Lod1 caster misses any near self-shadow detail at low sun, drop the order.
- Expected 0.05 to 0.13 ms after NT-1. Tests: `terrain_lod_shadow_coverage_smoke_test`, `terrain_storm_shadow_parity_smoke_test`, `shadow_compose_identity_gpu_test`.

**U-1 / U-2 (user decisions).** Show the user the cost of the far proxy levels and of the 2 m Lod1 band with stills (Lod1 out 320/450 against 240/320; far levels at half the grid resolution), then decide. U-2 is a profile value (`resources/voxel/ring_lod_high.tres`); U-1 needs the far levels' sector grid made configurable (measure with an oracle first).

**Evidence** (`.godot/perf_pass_real/nt/`; each run folder holds `report.json` with every window, the census and the per-yaw counts, plus `console.log`):
- `r1/`: four poses, 18 ablations (hides, shadows, shader variants, half resolution, `mesh_lods`).
- `r2/`: census, per-yaw counts, per-level proxy hides, the near-hole collapse oracle.
- `r3/`: the vertex-drop and far-cull oracles and their combination.
- `r4/`: built with NT-1: after against legacy, the per-sector census, and the stills `still_<pose>_{0_base,1_t3d_uncollapse_t3d_lods6,2_base}.png` with heatmaps `*_diff.png` (legacy against after; after against after).
- `r5/`: built with NT-1: what Terrain3D and the proxies still cost, Lod5 to Lod9 hidden, Terrain3D shadows off with stills `still_<pose>_1_t3d_shadow_off.png` and heatmaps.
- `r6/`: Ultra preset (`--graphics-preset=4`) at the save, plains and slope.
- The oracle stills of r1 to r3 were compared and deleted; their SSIMs matched the noise pairs.
- Scripts: driver `../real_world_perf_nt.gd` (its new part is `nt_part.gd.txt`), runner `run_nt.ps1` (takes the GPU queue as `near-terrain`), build `build_nt.ps1`, analysers `nt_an.py` (A-X-A deltas with the prepass / opaque split) and `census_an.py` (proxy triangles beyond the far fade or covered).

## 1. Summary (fixture world)

1. **Which settings decide the answer.** The game starts on **Very High** on this machine (hardware tier `desktop_mid_high`, `scripts/world_size_launcher.gd:1981`; Medium is the laptop tier default). The user's last-played world (slot 3) is saved at Very High, with their own overrides on top: tree LOD0 out to 200 m and cards from 550 to 900 m, a 16,384 shadow atlas, and grass and water overrides (section 2.2).
2. **Standing still, clear noon, 1080p:**

   | configuration | open ground | 2 km up | coast | forest clearing | forest edge | forest interior |
   |---|---|---|---|---|---|---|
   | Medium, shipped defaults | 6.3 ms (158 fps) | 6.7 | 7.3 | 7.4 | 8.7 | 9.6 (104 fps) |
   | Very High, shipped defaults (the game's default here) | 11.4 ms (88 fps) | not run | 14.3 | not run | 14.7 | 16.1 (62 fps) |
   | The user's saved settings (Very High + their files) | 12.4 ms (81 fps) | 12.6 | 16.4 | 14.9 | 20.8 | 21.9 (46 fps) |

   Medium already meets 11.1 ms at every pose. Very High misses it everywhere, by 0.3 ms on open ground and by 5 ms in forest. The user's settings miss it by 1.3 ms on open ground and by 10.8 ms in forest.
3. **The frame is GPU-bound in all three configurations.** The wall frame equals the GPU time of the two viewports plus 0.1 to 0.4 ms at every pose. The main thread needs about 4.5 to 6.5 ms per frame at Very High (2.1 to 2.5 ms of `_process`, 1.9 to 2.8 ms of render CPU, 0.2 ms of frame setup), so the CPU would allow 150 fps or more if the GPU cost fell.
4. **A hitch every half second** sits on top of the medians at every pose after the first teleport, in every configuration and in two independent harnesses: every 0.50 s two frames in a row run 13 ms and then 9 ms long in the render step (frame setup, then the far-ring viewport's render CPU). That is four slow frames per second. It is CPU time, not GPU time. At Very High it puts the 95th-percentile frame at 26 to 37 ms while the median is 14 to 22 ms. The source is the wet world's half-second map refresh (`RingWetWorld`, scene node `WetWorld`), which runs in dry weather too: with that node's processing off, the slow frames fall from 20 per 5 s to 1 and the p95 from 31.6 to 18.5 ms (section 4.3). The game cannot feel like 90 fps until this is fixed, whatever the median is.
5. **Where the GPU time goes at Very High** (section 5): near terrain 2.9 to 6.0 ms (8 to 14 million primitives per frame, not pixel-bound), clouds 2.0 to 4.1 ms, trees 2.0 to 5.1 ms in forest (7.2 to 10.0 ms with the user's tree distances), the sun shadow map 1.2 to 2.2 ms (2.4 to 5.3 ms with the user's 16,384 atlas and tree distances), the far-ring viewport 1.8 to 2.1 ms, haze 1.6 to 1.8 ms, engine screen-space reflections 0.2 to 1.2 ms in dry weather (up to about 2.8 ms with the user's tree distances), grass 0.2 to 0.8 ms. The HUD, far terrain, cloud shadow maps, SSAO and water FFT are each under 0.4 ms. A storm adds 0.4 ms.
6. **Can 90 fps be reached?** Section 7 has the ranked list.
   - Very High defaults, open ground: yes, with any one small item (it is 0.3 ms over).
   - Very High defaults, forest: plausible. It needs about 5 ms, which is most of the list together (terrain triangles, dry-weather SSR, TR-10, far viewport, clouds).
   - **FSR 2 does not help at 1080p:** the production "Quality" option (85 % scale) made the forest frame 0.3 ms slower and the storm frame 0.9 ms slower, because the frame is geometry, not pixels.
   - The user's settings, forest: only if the two expensive overrides change (the shadow atlas, about 3 ms in forest, and the tree distances, about 5 ms) or nearly every item on the list lands. This is the user's decision (section 8).
7. **Moving** (section 3.4, user's settings): walking is still GPU-bound at the median (17.9 ms) with a p95 of 38.7 ms; flight at 72 m/s is CPU-bound (19.9 ms wall against 13.7 ms of GPU, p95 45.3 ms, 19 % of frames over 25 ms).

## 2. Setup

### 2.1 Machine and method

- RTX 4070 desktop (i9-14900KF, 32 threads), Godot 4.8-dev7 mono editor binary, Forward+, **Debug C# assembly** (the test configuration; `tools/play.ps1` plays with an optimised assembly, which `docs/quality-presets-plan.md` measured 0.4 to 0.8 ms faster standing still and 4.6 ms faster in 72 m/s flight). GPU numbers are valid on this machine only.
- 1920 x 1080 window, VSync off, `Engine.max_fps = 0`, native resolution (3D scale 1.0, no FSR), FXAA (the default; TAA and MSAA off), SSAO on, engine SSR on, glow off.
- Harness: `tools/landscape_cost_baseline.gd` (the T0 cost harness) on the pinned v8 world, seed 1337, at the T0 fixture poses. Clear weather (cloud time 2434.5), noon (sun 75 degrees) and sun 8 degrees setting. Weather and celestial clocks held, with one case that lets them run. The camera stands still. `RingPerformanceMonitor` is disabled by the harness.
- Each case is two windows (the second round runs the cases in reverse order) after 60 warm-up frames and a 1 s settle. Windows are 4 s in runs A and B and 3 s in run C: 170 to 670 frames each. Numbers are the median over the two windows of the per-window median. **Noise floor** = baseline against `baseline_repeat`: 0.00 to 0.05 ms at Medium and Very High defaults, 0.00 to 0.12 ms at the user's settings. Deltas above 0.15 ms are real.
- A delta is baseline minus case, in ms of **wall frame time**. Because the frame is GPU-bound, a wall delta is a GPU delta. Deltas overlap (hidden terrain also leaves the shadow map; hidden trees uncover terrain) and **must not be added up**. `GPU.Clouds` is an inclusive marker, not a delta.
- Frame split (new in the harness): process step = `process_frame` signal to `frame_pre_draw` (all `_process`, timers, deferred calls); draw step = `frame_pre_draw` to `frame_post_draw` (render CPU plus any wait for the GPU). Viewport render CPU and frame setup CPU are Godot's own counters.

### 2.2 The three configurations

| run | preset | preference files | what differs from the row above |
|---|---|---|---|
| A | Medium | none (shipped defaults) | tree LOD0 to 30 m, cards from 250 to 300 m, tree sun shadows 240 m; near sun map 300 m, 4,096 atlas; clouds at half resolution, 128 steps, 4 x 4 interleave; far viewport at 0.75; terrain pixel error 3.0, 5 proxy levels |
| C | Very High | none (shipped defaults) | tree LOD0 to 40 m, cards from 300 to 350 m, tree sun shadows 480 m; near sun map 400 m, 8,192 atlas; clouds at full resolution, 328 steps, 30 light steps, 2 x 2 interleave; far viewport at 1.0; terrain pixel error 1.25, 8 proxy levels; water SSR on |
| B | Very High | the user's files (`%APPDATA%\Godot\app_userdata\RingworldTechDemo\*.tres`, copied read-only) | **trees:** LOD0 to 200 m, cards from 550 to 900 m, mid batch span 3 tiles. **Shadows:** atlas 16,384 with 16-bit depth, filter 2, height shadow L0 texel 8 m, per-instance vegetation shadows on. **Grass:** 16 blades per square metre, 350 m radius, 5,000,000 instance cap. **Water:** FFT 256 with 4 cascades, SSR 32 steps to 1,000 m. Terrain3D mesh LODs 6. Atmosphere: the user's haze values. |

The user's files for render resolution and anti-aliasing hold no overrides (native resolution, default AA). Slots 2 and 3 are saved at Very High with cloud quality 2; slot 1 at High.

## 3. Baseline frame times (fixture world)

Wall frame in ms. "GPU" is the sum of the measured GPU time of every viewport (main plus far ring). "Draw calls" and "primitives" count every pass of the frame.

### 3.1 Run A: Medium, shipped defaults

| pose | time | frame median | frame p95 | fps | GPU (main + far) | main viewport GPU | far viewport GPU | render CPU | draw calls | primitives (M) |
|---|---|---|---|---|---|---|---|---|---|---|
| plains_eye (open ground) | noon | 6.32 | 6.68 | 158 | 6.09 | 4.74 | 1.21 | 1.39 | 1,033 | 4.7 |
| hills_2km (altitude) | noon | 6.66 | 7.06 | 150 | 6.41 | 5.29 | 0.98 | 1.12 | 464 | 2.5 |
| coast_eye | noon | 7.27 | 7.64 | 138 | 7.04 | 5.61 | 1.23 | 1.54 | 1,291 | 5.7 |
| forest_interior_eye | noon | 9.61 | 10.01 | 104 | 9.49 | 8.01 | 1.24 | 1.79 | 1,478 | 15.1 |
| forest_edge_eye | noon | 8.65 | 9.01 | 116 | 8.49 | 6.88 | 1.38 | 1.59 | 813 | 11.0 |
| forest_clearing_eye | noon | 7.36 | 7.75 | 136 | 7.16 | 5.68 | 1.23 | 1.43 | 645 | 8.9 |
| forest_edge_eye | sun 8 deg | 8.54 | 9.00 | 117 | 8.36 | 6.83 | 1.40 | 1.68 | 858 | 11.1 |

The largest frame in each window is 18 to 22 ms (the hitch of section 4.3; at these frame lengths it is under 5 % of frames, so the p95 does not show it).

### 3.2 Run C: Very High, shipped defaults (what a new player on this machine gets)

| pose | time | frame median | frame p95 | fps | GPU (main + far) | main viewport GPU | far viewport GPU | process step | render CPU | draw calls | primitives (M) |
|---|---|---|---|---|---|---|---|---|---|---|---|
| plains_eye | noon | 11.42 | 11.74 | 88 | 11.26 | 9.30 | 1.77 | 2.11 | 1.91 | 1,395 | 9.8 |
| coast_eye | noon | 14.27 | 32.01 | 70 | 14.02 | 11.98 | 1.84 | 2.77 | 2.64 | 2,148 | 15.5 |
| forest_interior_eye | noon | 16.13 | 31.70 | 62 | 15.93 | 13.92 | 1.83 | 2.32 | 2.53 | 1,953 | 21.4 |
| forest_edge_eye | noon | 14.66 | 27.86 | 68 | 14.47 | 12.26 | 2.04 | 2.22 | 2.20 | 1,237 | 17.4 |
| forest_edge_eye | sun 8 deg | 14.51 | 28.39 | 69 | 14.31 | 12.10 | 2.04 | 2.25 | 2.21 | 1,315 | 17.7 |

### 3.3 Run B: the user's saved settings (Very High plus their files)

| pose | time | frame median | frame p95 | fps | GPU (main + far) | main viewport GPU | far viewport GPU | process step | render CPU | draw calls | primitives (M) |
|---|---|---|---|---|---|---|---|---|---|---|---|
| plains_eye | noon | 12.38 | 12.72 | 81 | 12.23 | 10.26 | 1.78 | 2.24 | 2.03 | 1,386 | 10.0 |
| hills_2km | noon | 12.61 | 19.73 | 79 | 12.45 | 10.80 | 1.48 | 2.13 | 1.88 | 942 | 6.4 |
| coast_eye | noon | 16.41 | 34.59 | 61 | 16.04 | 14.05 | 1.84 | 3.82 | 2.80 | 2,222 | 17.1 |
| forest_interior_eye | noon | 21.92 | 33.44 | 46 | 21.67 | 19.67 | 1.83 | 2.53 | 2.78 | 2,013 | 26.1 |
| forest_edge_eye | noon | 20.77 | 27.24 | 48 | 20.54 | 18.33 | 2.05 | 2.44 | 2.55 | 1,448 | 27.7 |
| forest_clearing_eye | noon | 14.85 | 25.90 | 67 | 14.64 | 12.64 | 1.83 | 2.39 | 2.35 | 1,166 | 17.5 |
| forest_edge_eye | sun 8 deg | 20.66 | 27.66 | 48 | 20.39 | 18.12 | 2.07 | 2.43 | 2.55 | 1,511 | 28.1 |

Low sun changes little at these poses (within 0.2 ms of noon at the forest edge in all three runs): the height shadow owns the distance, so the near sun map does not grow with the sun angle.

### 3.4 Storm, walking and flight (user's settings)

`tools/quality_preset_capture.ps1` (a second, independent harness: its own fixture world from the production recipe, seed 1337; Very High; the user's files installed into an isolated profile; 1080p; Debug C#). Its `forest_noon` pose is not one of the T0 poses (it looks 12 degrees down into forest), so its baseline is its own.

| workload | clocks | frame median | p95 | p99 | frames over 25 ms | GPU (main + far) median | note |
|---|---|---|---|---|---|---|---|
| forest_noon, clear (9 windows of 6 s) | held | 18.02 | 33.1 | 40.3 | 24 per 6 s window | 17.8 | window medians span 18.01 to 18.07 |
| **storm** at the same place (spawned front; rain 0.92, wetness 1.0, lightning, rain haze on) | live | 18.42 | 37.3 | 44.5 | 26 per 6 s window | 17.9 | +0.4 ms over clear |
| standing still, clocks live, 30 s | live | 18.67 | 35.5 | 41.6 | 128 in 30 s | 18.3 | no streaming-busy frame |
| **walking** 30 s (7.1 m/s, 213 m, blocked 44 % of the time by trees) | live | 17.93 | 38.7 | 50.1 | 126 in 30 s | 17.3 | 417 of 1,481 frames streaming-busy |
| **flight** 72 m/s at 150 m, 30 s (2,140 m) | live | 19.86 | 45.3 | 61.5 | 266 in 30 s (19 %) | 13.7 | CPU-bound: every frame streaming-busy |

A-X-A labels at the two poses (wall frame ms saved; negative = slower; `baseline_repeat` is the noise floor):

| label | forest_noon | storm | what it is |
|---|---|---|---|
| haze off | 1.62 | 1.80 | physical haze, quality 4 (main viewport 1.3, far viewport 0.4 to 0.5) |
| SSAO off | 0.10 | -0.04 | |
| FSR 2 "Quality" (production option, 85 % scale) | **-0.27** | **-0.93** | slower: main viewport GPU +0.65 and +1.35 ms |
| cloud raymarch steps 328 to 128 | 0.04 | -0.19 | no gain at this pose (canopy hides most of the sky; the raymarch is 0.7 ms here) |
| clouds at half resolution | 1.27 | 1.52 | |
| cloud interleave 2 x 2 to 4 x 4 | 0.74 | 0.96 | |
| far viewport at half resolution | 0.98 | 1.21 | its GPU time falls from 1.7 to 0.73 ms |
| baseline_repeat (noise) | 0.02 | 0.05 | |

The storm does not change the picture: rain, rain haze, wet surfaces and lightning together cost 0.4 ms at this pose. The megastorm was not measured.

## 4. Bound analysis (fixture world)

### 4.1 GPU-bound standing still

| evidence | Medium (A) | Very High (C) | user's settings (B) |
|---|---|---|---|
| wall frame minus GPU of both viewports | 0.10 to 0.25 ms | 0.16 to 0.25 ms | 0.15 to 0.37 ms |
| far viewport stopped (`far_viewport_off`): wall frame falls by | 1.05 to 1.47 ms (its GPU time is 0.98 to 1.40) | not run | 1.53 to 2.19 ms (its GPU time is 1.48 to 2.07) |
| 3D render scale 50 %: wall frame falls by | 1.8 to 2.6 ms | not run | 2.9 to 5.2 ms |
| fastest window of the run | 3.5 ms (2 km up, terrain hidden) | | 4.9 ms (open ground, terrain hidden, 50 % scale) |

Every GPU saving shows up one-for-one in the wall frame, down to at least 4.9 ms at Very High. That is the proof that the CPU is not the limit at these frame times.

### 4.2 What the CPU costs

Main thread, per frame, medians (run B; run C is within 0.3 ms of it):

| part | open ground | coast | forest |
|---|---|---|---|
| process step (all `_process`) | 2.1 to 2.2 | 3.8 | 2.4 to 2.5 |
| viewport render CPU (main + far) | 1.9 to 2.0 | 2.8 | 2.4 to 2.8 |
| frame setup CPU | 0.16 | 0.24 | 0.25 |
| rest of the frame (physics, input, OS) | 0.3 | 0.3 | 0.3 |
| **CPU floor (sum)** | **about 4.5** | **about 7.1** | **about 5.5 to 5.9** |

- At Medium the render CPU is 1.1 to 1.8 ms. The process step was not split in run A.
- The instrumented script scopes add up to under 1 ms per frame except at the coast, where "Grass output visibility" (0.79 ms) and "Water demand check" (0.58 ms) are the two largest; both are 0.02 ms or less inland. About 1.2 to 1.5 ms of the process step is outside the instrumented scopes.
- Draw calls are 1,200 to 2,200 at Very High. Hiding all trees removes 570 to 1,120 draw calls and 0.2 to 0.5 ms of render CPU; hiding them where no tree is in range (open ground) still removes 572 empty draws. With the frame GPU-bound this costs nothing today. It matters once the GPU frame is under about 7 ms.
- Clocks running (weather and celestial) costs 0.1 to 0.3 ms.

So the CPU floor is 4.5 to 7 ms: between 140 and 220 fps. The 90 fps target is a GPU problem while standing still.

### 4.3 The half-second hitch

| | Medium (A) | Very High (C) | user's settings (B) |
|---|---|---|---|
| median frame | 6.7 to 9.6 | 14.3 to 16.1 | 12.6 to 21.9 |
| p95 frame | 7.1 to 10.0 | 27.9 to 32.0 | 19.7 to 34.6 |
| largest frame setup CPU in a window | 6.9 to 8.8 | | 11.0 to 17.1 |
| viewport render CPU, p95 (median 1.1 to 2.8) | 1.3 to 2.2 | | 9.4 to 15.5 |
| mean minus median frame | 0.25 to 0.45 | | 0.6 to 1.5 |

- **Shape** (per-frame list of `quality_preset_capture`, forest_noon, clocks held): at t = 0.29, 0.79, 1.29, 1.79, 2.30 s and so on, a frame with 13.0 to 13.6 ms of frame setup CPU, and the next frame with 9.0 to 10.5 ms. Exactly every 0.50 to 0.51 s. Over the windows, the far-ring viewport's render CPU has a median of 0.2 ms and a p95 of 11.6 to 14.4 ms, while the main viewport's render CPU p95 stays at 2.6 ms and the GPU p95 stays within 0.5 ms of its median. So: something every 0.5 s makes frame setup take 13 ms, and then makes the far viewport take about 14 ms of CPU to render one frame.
- It appears at every pose **except the first one visited after boot** (`plains_eye`: setup maximum 0.3 ms, largest frame 13 to 15 ms in run B). From the second pose on it is there in all runs and in every ablation case (trees hidden, sun map off, clouds off, cloud shadow generation off).
- It is in both harnesses, standing still with clocks held, with clocks live, in the storm and while walking. At Medium the spikes are 7 to 9 ms and, at 6 to 10 ms frames, they are under 5 % of frames, so the p95 hides them; the largest frame of every window is 18 to 22 ms.
- It is not the `nvidia-smi` power log of the harnesses (that also polls every 0.5 s): the log was running during the hitch-free first pose.
- It is not a GC pause: the process step p95 stays at 2.6 to 4.9 ms.
- Code with a 0.5 s refresh: `RingSunshineCloudBridge.ShadowRefreshSeconds` and `TerrainCloudShadowRefreshSeconds`, `RingLocalVolumetricCloudRenderer.WeatherRefreshSeconds`, `RingWetWorld.RefreshSeconds`. `docs/quality-presets-plan.md` (2026-10-24) saw "one 25 to 30 ms frame every 2 s" landing in the SunshineCloudBridge weather refresh; this is four times as frequent.
- **Bisect** (`quality_preset_capture`, forest_noon, user's settings, 5 s windows, A-X-A; frames over 25 ms per window, wall p95):

  | case | slow frames | wall median | wall p95 |
  |---|---|---|---|
  | baseline (7 windows) | 20 to 21 | 18.02 to 18.11 | 31.4 to 33.0 |
  | `SunshineCloudBridge.ShadowRefreshSeconds` 0.5 to 10 | 21 | 18.07 | 31.4 |
  | `SunshineCloudBridge.TerrainCloudShadowRefreshSeconds` 0.5 to 10 | 20 | 18.10 | 30.8 |
  | **`WetWorld` processing off** | **1** | 18.08 | **18.5** |
  | `LocalWeatherEffects` processing off | 21 | 18.05 | 31.6 |
  | `SunshineCloudBridge` processing off | 20 | 17.99 | 32.0 |
  | `RingVolumetricClouds` processing off | 20 | 18.09 | 31.5 |

- **Source: `scripts/Weather/RingWetWorld.cs`.** While its map is "valid" it dispatches the wet map every `RefreshSeconds` = 0.5 s (`:191`). The map is valid whenever `IsWetPossible` is true, and that is false "only when no system with rain potential can reach the 16 km window at any lag" (`:298`), so it stays on in dry weather near any weather system. That fits the hitch-free first pose. What in the dispatch or its publish costs 13 ms of frame setup and then 14 ms of far-viewport render CPU was not traced; a global texture or parameter that makes the renderer revisit every material that reads it is the likely mechanism. The median does not move with the node off, so the cost is the two slow frames only.
- One more lead, separate: at `hills_2km` the scope "Terrain proxy build start" ran 47 times in a 4 s window with the camera still, at a pose the harness reports as streaming-idle.

This is candidate 1 in section 7.

## 5. Where the GPU time goes (fixture world)

All numbers are wall-frame deltas in ms (baseline minus case), noon unless stated. Noise floor per row is in the last column. Do not add columns: they overlap.

### 5.1 Run B: the user's saved settings

| pose | frame | near terrain | trees (all) | of which tree shadows | sun shadow map (all casters) | clouds (`GPU.Clouds` marker) | far-ring viewport | SSAO + engine SSR | grass | water | cloud shadow maps | far terrain | HUD | clocks running | 50 % render scale | noise |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| plains_eye | 12.38 | 4.65 | 0.03 | 0.03 | 2.41 | 3.11 | 1.88 | 0.80 | 0.30 | 0.01 | 0.11 | 0.15 | 0.05 | +0.08 | 3.36 | 0.00 |
| hills_2km | 12.61 | 5.80 | 0.03 | 0.03 | 0.02 | 3.84 | 1.53 | 0.94 | 0.02 | 0.09 | 0.10 | 0.13 | 0.04 | +0.12 | 5.21 | 0.03 |
| coast_eye | 16.41 | 7.21 | 1.37 | 0.41 | 2.53 | 4.06 | 1.93 | 0.94 | 0.10 | 1.01 | 0.18 | 0.21 | 0.03 | +0.03 | 4.00 | 0.12 |
| forest_interior_eye | 21.92 | 4.00 | 10.02 | 3.09 | 5.25 | 1.88 | 1.87 | 2.85 | 0.53 | 0.00 | 0.30 | 0.20 | 0.01 | +0.27 | 3.74 | 0.06 |
| forest_edge_eye | 20.77 | 4.63 | 7.22 | 2.31 | 4.60 | 3.33 | 2.14 | 1.77 | 0.46 | 0.09 | 0.31 | 0.31 | 0.06 | +0.20 | 3.54 | 0.06 |
| forest_clearing_eye | 14.85 | 3.67 | 2.98 | 1.11 | 3.20 | 2.33 | 1.87 | 1.12 | 0.53 | 0.00 | 0.12 | 0.20 | 0.00 | +0.18 | 2.88 | 0.06 |
| forest_edge_eye, sun 8 deg | 20.66 | 3.49 | 8.24 | 3.31 | 4.41 | 3.27 | 2.19 | 1.85 | 0.55 | 0.09 | 0.43 | 0.17 | 0.06 | +0.29 | 3.63 | 0.01 |

Notes:
- "Trees (all)" includes their shadow casters. "Sun shadow map" includes the tree shadow casters and the terrain casters. Near terrain includes its own shadow casting and, at the coast, the ocean surface on the proxy sectors.
- Clouds: the `clouds_off` switch is not a valid ablation at Very High (a fallback cloud path takes over and the frame gets slower, by 15 ms at the coast), so the cloud column is the inclusive `GPU.Clouds` marker. Its children at Very High: raymarch 0.7 to 2.9 ms (lowest under the forest canopy), interleave 0.52 to 0.61, reconstruct 0.52 to 0.59, composite 0.05, prepass 0.03. At `hills_2km`, where the switch is clean, `clouds_off` saves 3.23 ms against a marker of 3.84.
- "Clocks running" is a cost (frame gets longer), shown with a plus sign.
- Not in the table: haze (1.6 to 1.8 ms, measured in section 3.4), sky, tonemap, FXAA and the composite of the far viewport.

Fill test (the same hide at 50 % render scale; a pixel-bound cost would fall towards a quarter):

| pose | near terrain at 100 % | near terrain at 50 % | trees at 100 % | trees at 50 % |
|---|---|---|---|---|
| plains_eye | 4.65 | 4.10 | | |
| hills_2km | 5.80 | 3.18 | | |
| coast_eye | 7.21 | 5.95 | 1.37 | 1.31 |
| forest_interior_eye | 4.00 | 3.91 | 10.02 | 9.19 |
| forest_edge_eye | 4.63 | 4.35 | 7.22 | 6.99 |
| forest_clearing_eye | 3.67 | 3.58 | 2.98 | 2.90 |

Near terrain and trees are **triangle-bound**, not pixel-bound, on the ground. Near terrain draws 8.3 to 8.7 million primitives per frame at the ground poses (14.4 million at the coast, 5.6 million from 2 km) across 610 to 1,130 draw calls; at Medium it draws 2.9 to 4.1 million across 230 to 370. Trees draw 15 to 17 million primitives in forest at the user's distances.

### 5.2 Run C: Very High, shipped defaults

| pose | frame | near terrain | trees (all) | of which tree shadows | sun shadow map (all casters) | atlas 8,192 to 4,096 saves | atlas 8,192 to 16,384 costs | clouds (marker) | far-ring viewport (its GPU time) | SSAO | engine SSR | grass | far terrain | noise |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| plains_eye | 11.42 | 3.41 | 0.00 | 0.00 | 1.24 | 0.65 | 2.44 | 3.14 | 1.77 | 0.09 | 0.22 | 0.39 | 0.17 | 0.00 |
| coast_eye | 14.27 | 6.03 | 0.27 | 0.12 | 1.21 | 0.63 | 2.13 | 4.08 | 1.84 | 0.09 | 0.21 | 0.21 | 0.25 | 0.01 |
| forest_interior_eye | 16.13 | 2.87 | 5.06 | 1.03 | 2.22 | 0.97 | 3.69 | 2.00 | 1.83 | 0.07 | 1.17 | 0.71 | 0.18 | 0.05 |
| forest_edge_eye | 14.66 | 3.69 | 2.03 | 0.48 | 1.71 | 0.84 | 2.96 | 3.40 | 2.04 | 0.07 | 0.28 | 0.74 | 0.24 | 0.03 |
| forest_edge_eye, sun 8 deg | 14.51 | 3.35 | 1.88 | 0.45 | 1.49 | 0.57 | 2.01 | 3.47 | 2.04 | 0.11 | 0.30 | 0.76 | 0.25 | 0.00 |

What the user's overrides cost, from B against C at the same pose:

| pose | Very High defaults | user's settings | difference | where it comes from |
|---|---|---|---|---|
| plains_eye | 11.42 | 12.38 | +0.96 | sun shadow map +1.17 (atlas 16,384 at 16-bit depth) |
| coast_eye | 14.27 | 16.41 | +2.14 | sun shadow map +1.32, trees +1.10, water |
| forest_interior_eye | 16.13 | 21.92 | +5.79 | trees +4.96 (of which tree shadows +2.06), SSR + SSAO +1.61, shadow atlas |
| forest_edge_eye | 14.66 | 20.77 | +6.11 | trees +5.19 (of which tree shadows +1.83), SSR + SSAO +1.42, shadow atlas |

The atlas column of run C is at the default depth format; the user's file sets 16-bit depth, which is why their 16,384 atlas costs about 1.2 ms on open ground and not 2.4.

### 5.3 Run A: Medium, shipped defaults

| pose | frame | near terrain | trees (all) | of which tree shadows | sun shadow map | clouds (marker) | clouds (`clouds_off`) | far-ring viewport | SSAO + engine SSR | grass | water | far terrain | HUD | 50 % render scale | noise |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| plains_eye | 6.32 | 1.84 | 0.00 | 0.00 | 0.47 | 1.09 | 0.58 | 1.25 | 0.57 | 0.34 | 0.00 | 0.16 | 0.00 | 1.84 | 0.03 |
| hills_2km | 6.66 | 3.08 | 0.03 | 0.03 | 0.01 | 1.37 | 1.05 | 1.05 | 0.69 | 0.00 | 0.07 | 0.12 | 0.05 | 2.55 | 0.01 |
| coast_eye | 7.27 | 2.61 | 0.24 | 0.02 | 0.47 | 1.30 | 0.25 | 1.30 | 0.58 | 0.17 | 0.33 | 0.19 | 0.02 | 1.98 | 0.05 |
| forest_interior_eye | 9.61 | 1.29 | 3.08 | 0.24 | 0.75 | 0.95 | 0.80 | 1.25 | 1.33 | 0.54 | 0.00 | 0.13 | 0.01 | 2.30 | 0.02 |
| forest_edge_eye | 8.65 | 1.66 | 1.55 | 0.12 | 0.65 | 1.36 | 1.22 | 1.47 | 0.87 | 0.56 | 0.03 | 0.18 | 0.03 | 1.97 | 0.01 |
| forest_clearing_eye | 7.36 | 1.38 | 0.90 | 0.03 | 0.48 | 1.00 | 0.87 | 1.33 | 0.79 | 0.60 | 0.00 | 0.17 | 0.03 | 1.81 | 0.00 |
| forest_edge_eye, sun 8 deg | 8.54 | 1.52 | 1.57 | 0.18 | 0.50 | 1.36 | 1.28 | 1.46 | 0.88 | 0.55 | 0.02 | 0.17 | 0.05 | 1.98 | 0.02 |

At Medium the cloud switch is clean (no fallback), and both numbers are given. Tree cost here is lower than TR-8's reference configuration R (3.1 to 6.0 ms) because R forced the 25/40 and 300/350 m fades and loaded the user's shadow file; the Medium preset's own fades are 20/30 and 250/300 m.

### 5.4 Compute markers

Per dispatch, median ms, run B: `GPU.CloudShadowNear` 0.20 to 0.47, `GPU.CloudShadowFar` 0.09 to 0.28, `GPU.TerrainCloudSunVisibility` 0.05, `GPU.TreeCulling` 0.04 to 0.05, `GPU.GrassCulling` 0.03, `GPU.WaterFft` 0.21 (coast only), `GPU.CloudMacroMap` 0.01, `GPU.CloudStormMap` 0.02. `GPU.HeightShadow`, `GPU.Vegetation` and `GPU.ProxyHeights` did not run with a still camera (not measured, not zero). None of these is a target.

## 6. What the measurements say about each system

- **Near terrain** is the largest fixed cost at Very High (2.9 to 6.0 ms, more at the coast) and the largest cost of all on open ground. It is triangles, not pixels: 8 to 14 million primitives per frame against 2 million pixels on screen. Medium draws a third of that for 1.3 to 3.1 ms. The split between Terrain3D and the proxy rings, and between the main pass and the shadow pass, was not measured.
- **Clouds** are the second fixed cost at Very High: 3.1 to 4.1 ms in open views against 1.0 to 1.4 ms at Medium. About 1.1 ms of it (interleave plus reconstruct) does not depend on the step count.
- **Trees** follow the fade distances. Very High defaults: 2.0 ms at the forest edge, 5.1 ms in the interior. The user's distances: 7.2 and 10.0 ms. TR-8's finding holds at Very High: the cost is triangles (15 to 17 million primitives), not fill.
- **Sun shadow map.** With no tree in range it still costs 1.2 ms at Very High (terrain casters, 0.7 to 1.3 million primitives, about 460 draws of which 360 are empty tree batches) and 0.5 ms at Medium. Atlas size is a large part: 8,192 to 4,096 saves 0.6 to 1.0 ms; 16,384 costs 2.0 to 3.7 ms more at the default depth format.
- **Tree shadows** are 0.5 to 1.0 ms at Very High defaults and 2.3 to 3.3 ms with the user's distances and atlas. The shadow proxies use the full tree shader (`ImportedTreeRenderMaterials.GetShadowMesh` duplicates the material and sets `simplified_tree_shading`), so the shadow pass runs the wind, haze, wetness, cloud and height-shadow vertex work for every caster vertex. `IN_SHADOW_PASS` is not used anywhere in `materials/`.
- **Engine SSR** is on in every preset and in dry weather (it was kept for wet ground and puddles, `docs/storm-revamp-s2-wet-world-design.md`). Dry, it costs 0.2 to 0.3 ms in open views and 1.2 ms in the forest interior at Very High defaults; SSAO plus SSR is 1.8 to 2.9 ms in forest with the user's tree distances. SSAO alone is 0.1 ms. The water has its own SSR in its shader and does not need the engine pass.
- **Far-ring viewport**: 1.8 to 2.1 ms at Very High (resolution scale 1.0), 1.0 to 1.4 ms at Medium (0.75). Half resolution saves 1.0 to 1.2 ms. Its content is hundreds of kilometres away.
- **Haze** (physical, quality 4): 1.6 to 1.8 ms, of which 0.4 to 0.5 ms is in the far viewport.
- **Grass** 0.2 to 0.8 ms, **water** 1.0 ms at the coast with the user's SSR settings (0.3 at Medium), **cloud shadow maps** 0.1 to 0.4 ms, **far terrain** 0.1 to 0.3 ms, **HUD** under 0.1 ms. Not targets.
- **Pixel-bound share**: half the pixels in each direction saves 1.8 to 2.6 ms at Medium and 2.9 to 5.2 ms at Very High. Resolution scaling therefore recovers at most a quarter of a Very High forest frame; the rest is geometry and fixed-size passes. FSR 2 at 85 % scale costs more than it saves (section 3.4).

## 7. Ranked candidates (fixture world; the order is superseded by section 0.7)

Savings are estimates from the measured cost each item targets; "at" names where. Size: S under a day, M a few days, L a week or more. "WO" says whether a work order exists.

| # | Candidate | Measured cost it targets | Estimated saving | Visual risk | Size | WO |
|---|---|---|---|---|---|---|
| 1 | **Find and remove the half-second render-step hitch** (section 4.3). The source is located: the wet world's 0.5 s map refresh (`RingWetWorld.DispatchMap` and what it publishes). Trace which call costs the frame setup, then publish without invalidating materials (keep the texture and globals, update contents in place), and do not refresh while nothing in the window is wet. | p95 frame 26 to 37 ms against a median of 14 to 22 ms at Very High; 13 + 9 ms of CPU twice per second | No median change. p95 falls to within about 1 ms of the median; about 45 ms per second of main-thread time returns | None | S to M | none |
| 2 | **Near terrain triangle budget at High and above.** First measure Terrain3D against proxy rings and main pass against shadow pass (`tools/terrain_render_attribution_test.gd`), then cut the level that over-tessellates. | 2.9 to 6.0 ms at Very High, 8 to 14 M primitives; 1.3 to 3.1 ms and 3 to 4 M at Medium | 1.0 to 2.5 ms at Very High on every pose | Low to medium: silhouettes and LOD morphing must hold; needs strips at the proxy hand-offs | M to L | none (`docs/quality-presets-plan.md` N8 left it at "measure first") |
| 3 | **Engine SSR only while surfaces are wet** (or keep dry bark above the SSR roughness cut). | 0.2 to 0.3 ms open, 1.2 ms forest interior at Very High defaults; up to about 2.8 ms with the user's tree distances | 0.2 to 2.7 ms | Low: a `compare` of dry frames must show no change beyond noise; wet weather unchanged | S | none |
| 4 | **TR-10: canopy in the height shadow**, then pull the tree sun-shadow distance in. | Tree shadows 0.5 to 1.0 ms (Very High defaults), 2.3 to 3.3 ms (user's settings) | 50 to 80 % of that: 0.3 to 0.8 ms, or 1.2 to 2.6 ms at the user's settings | Medium: the hand-off strip and light through crown gaps need the user's sign-off | M | TR-10 |
| 5 | **Cheaper vertex work in the tree shadow pass**: skip haze, wetness, cloud and height-shadow sampling when `IN_SHADOW_PASS` (check that 4.8-dev7 has it in `vertex()`), keep the wind. | The same tree shadow cost as item 4 (0.5 to 3.3 ms), 2 to 5 M primitives in the shadow pass | 20 to 40 % of it: 0.1 to 1.2 ms. Unmeasured; prototype first | None (shadow pass only) | S | none; complements TR-10 |
| 6 | **Sun shadow map: terrain casters and atlas.** Terrain casters only where the heightfield cannot carry the shadow; do not submit empty tree shadow batches. | 1.2 ms with no tree in range at Very High (0.5 at Medium); 360 empty draws | 0.4 to 0.8 ms | Low to medium: small terrain self-shadows inside 400 m | M | none (shadow plan review list) |
| 7 | **Clouds at Very High: the fixed 1.1 ms** (interleave + reconstruct) and the raymarch in open views. Settings options are in section 8. | 3.1 to 4.1 ms open, 2.0 ms under canopy | Engineering: 0.3 to 0.8 ms. Settings: see section 8 | Engineering: none to low | M | `docs/cloud-perf-methods-plan.md` |
| 8 | **Far-ring viewport**: update every second frame with reprojection, or a lower resolution scale at Very High. | 1.8 to 2.1 ms at Very High | 0.5 ms (scale 0.75), 1.0 to 1.2 ms (scale 0.5, measured), about 0.9 ms (half rate) | Half rate: lag on fast turns unless reprojected. Scale: softer arch | S (scale) or M (half rate) | `docs/quality-presets-plan.md` N7 |
| 9 | **TR decision D5: a lighter LOD0 for long near-detail fades.** | LOD0 to 200 m costs 1.8 to 2.6 ms for 256 trees (TR-8, user's fades) | 0.8 to 1.3 ms, only with the user's fades | Low to medium | M | TR D5 (deferred) |
| 10 | **TR-9a and TR-9** (cap fall-through, tree count to the budget). | Not a saving: TR-9 spends what TR-5 to TR-7 saved | 0, by design. Listed so the tree budget is set with this table in hand | | M | TR-9a, TR-9 |
| 11 | **Draw-call batching for trees and terrain; skip empty tree batches.** | 1,200 to 2,200 draws, 1.9 to 2.8 ms render CPU; 570 empty tree draws on open ground | 0.2 to 0.5 ms of render CPU. No frame change while GPU-bound | None | M | none (TR-8 noted the 300 extra draws) |
| 12 | **Coast script cost**: "Grass output visibility" 0.79 ms and "Water demand check" 0.58 ms per frame at the coast only. | 1.4 ms of the 3.8 ms process step at the coast | Up to 1.2 ms of CPU. No frame change while GPU-bound | None | S | none |
| 13 | **Haze cost** (physical haze, quality 4). Not investigated: where the 1.6 to 1.8 ms goes (per-pixel march in the receivers, the sky, the far viewport's 0.4 to 0.5 ms). | 1.6 to 1.8 ms | Unknown; measure first | Depends on the method | M | `docs/atmosphere-haze-revamp-plan.md` (light shafts deferred there) |
| 14 | **Flight and fast travel** (CPU-bound). | 19.9 ms wall against 13.7 ms GPU at 72 m/s; 19 % of frames over 25 ms | Its own list: the L2/L3 load-time orders and `docs/gpu-offload-plan.md` | None | L | load-time L2-3, GPU offload |
| not a candidate | FSR 2 at 1080p | measured: 0.3 to 0.9 ms slower at 85 % scale | | | | `docs/quality-presets-plan.md` N2 found the same at 1080p |

Recommended order: 1 (it decides how the game feels), then 3 and 5 (small, no visible change), then 2 (the largest fixed cost, on every pose), then 4 (already designed), then 6 to 8 and 13. Items 9 to 12 wait for the decisions in section 8.

What this adds up to, forest interior, noon:

| configuration | today | needed | what gets there |
|---|---|---|---|
| Very High defaults | 16.1 | -5.0 | items 2 (1.0 to 1.5), 3 (1.2), 4 + 5 (0.6), 6 (0.5), 7 (0.3 to 0.8), 8 (0.5 to 1.2): 4.1 to 5.8 ms. Reachable only if most of them land; one settings item from section 8 (clouds at half resolution: 1.3 ms) closes the rest |
| User's settings | 21.9 | -10.8 | the same items scale up (3: about 2.5; 4 + 5: about 2.3; 9: about 1.0): about 9 to 10.5 ms. Reachable only with every item, or with the atlas or tree distances changed (section 8) |
| Medium defaults | 9.6 | met | |

## 8. Settings that cost much for what they show (ask before changing anything)

Project rule: no visual reduction is applied without the user's decision. These are measured costs, not changes.

| # | Setting | Where | Measured cost | What would change on screen |
|---|---|---|---|---|
| S1 | Shadow atlas 16,384 (user's file; the Very High preset is 8,192) | user's `gameplay_shadow_defaults.tres` | about 1.2 ms on open ground and at the coast; about 3 ms in forest together with the tree distances (run C: 8,192 to 16,384 costs 2.0 to 3.7 ms at the default depth format) | Near sun-map texels double in size (2.4 cm to 4.9 cm across the first cascade); edges slightly softer |
| S2 | Tree LOD0 to 200 m, cards from 550 to 900 m (user's file; Very High is 40 m and 300 to 350 m) | user's `gameplay_vegetation_defaults.tres` | +5.0 to +5.2 ms in forest (trees 10.0 against 5.1 ms interior, 7.2 against 2.0 ms edge), plus about 1.5 ms of SSR on the extra full-detail bark | Full-detail trees end at 40 m and cards start at 300 m, as shipped. TR decision D3 already made the shipped fades the reference |
| S3 | Shadow atlas 8,192 at Very High (4,096 at Medium and High) | preset | 0.6 to 1.0 ms | Near shadow texels double |
| S4 | Clouds at full resolution with 2 x 2 interleave at Very High (Medium: half resolution, 4 x 4) | preset | half resolution saves 1.3 to 1.5 ms; 4 x 4 interleave saves 0.7 to 1.0 ms (forest and storm; open views not measured, where the cloud cost is 3 to 4 ms). Steps 328 to 128 saved nothing under canopy; not measured in open views | Softer cloud detail and more ghosting on cloud edges in motion (`docs/quality-presets-plan.md` N3, N4) |
| S5 | Far viewport resolution scale 1.0 at Very High (0.75 at Medium) | preset | about 0.5 ms | Softer far arch and far terrain |
| S6 | Engine SSR in dry weather | scene, every preset | 0.2 to 2.8 ms | Expected none; listed here until a dry-weather `compare` confirms it (then it is item 3, not a reduction) |
| S7 | Terrain pixel error 1.25 and 8 proxy levels at Very High (High: 2.0 and 7) | `resources/voxel/ring_lod_very_high.tres` | part of the 2.9 to 6.0 ms near terrain; not split yet | Terrain LOD steps closer to the camera |

## 9. Open questions for the user

1. **Which configuration is the 90 fps target judged on:** Very High as shipped, or your saved settings? They differ by 1 ms on open ground and 6 ms in forest.
2. **S1 and S2:** keep your 16,384 atlas and your tree distances while we work on the engineering items, or take the shipped Very High values for the pass? (About 3 ms and 5 ms in forest.)
3. FSR 2 is not an option at 1080p (it is slower). If the engineering items leave forest a millisecond or two short at Very High, which of S3 to S5 would you give up first?
4. Or must 90 fps be reached with Very High's cloud, shadow-atlas and far-viewport values exactly as they are?
5. The measurements are standing still. Section 3.4 has one walk and one flight. Should the target also hold while walking and in flight? That is a CPU and streaming problem, with its own list.

## 10. Harness changes and evidence

Harness (test-only, not committed):
- `tools/landscape_baseline_common.gd`: `--extra-cases=` for `run_cost` (`clouds_off`, `cloud_shadow_gen_off`, `sun_map_off`, `hs_receivers_off`, `water_hidden`, `ui_hidden`, `clocks_running`, `far_viewport_off`, `env_post_off`, `env_ssao_off`, `env_ssr_off`, `atlas_4096`, `atlas_8192`, `atlas_16384`; `tree_shadows_off` and the `@rs50` suffix already existed and can now be listed too). Every sampled window also records the process, draw and other steps, `Performance.TIME_PROCESS`, frame setup CPU, the GPU and CPU sums over all viewports, the CPU scopes per frame and a state probe; the console prints one line per window; `cost_partial.json` holds the finished poses.
- `tools/landscape_harness_common.gd`: `--weather=storm [--weather-pose=<name>]` (written, not used in this pass).
- Two cases are not valid ablations and are excluded from the tables: `hs_receivers_off` (the sun map falls back to 6 km and 4 cascades, so the frame gets slower) and `clouds_off` at Very High (a fallback cloud path takes over).

Commands (all through `tools/gpu_queue.ps1 -Label perf_pass -Wrap`):

```
Godot_console.exe --path . --resolution 1920x1080 --script res://tools/landscape_cost_baseline.gd -- --prefs=defaults --preset=medium --poses=forest_edge_eye,forest_interior_eye,forest_clearing_eye,plains_eye,hills_2km,coast_eye --rounds=2 --seconds=4 --extra-cases=tree_shadows_off,sun_map_off,hs_receivers_off,clouds_off,cloud_shadow_gen_off,water_hidden,far_viewport_off,env_post_off,ui_hidden,baseline@rs50,clocks_running
... --prefs=user --preset=keep (same poses) --extra-cases=tree_shadows_off,sun_map_off,clouds_off,cloud_shadow_gen_off,water_hidden,far_viewport_off,env_post_off,ui_hidden,baseline@rs50,near_terrain_hidden@rs50,trees_hidden@rs50,clocks_running
... --prefs=defaults --preset=keep --poses=forest_edge_eye,forest_interior_eye,plains_eye,coast_eye --rounds=2 --seconds=3 --extra-cases=tree_shadows_off,sun_map_off,atlas_4096,atlas_16384,env_ssao_off,env_ssr_off
```

Evidence:
- Run A: `.godot/audit_baseline/rtx4070/t0/cost/2026-11-15_21-19-48/` (`cost.json`, `cost.md`, `power_log.csv`).
- Run B: `.godot/audit_baseline/rtx4070/t0/cost/2026-11-15_21-43-38/` (with `prefs_snapshot/`, the user's files as measured).
- Run C: `.godot/audit_baseline/rtx4070/t0/cost/2026-11-15_22-11-21/`.
- Q1 (storm, motion, labels): `.godot/quality_presets/perf_pass_vh_user/` (`report.json`, `console.log`, `stall_frames.csv`), copied to `.godot/perf_pass/runQ1_report.json` and `runQ1_console.log`. Command: `tools/quality_preset_capture.ps1 -Label perf_pass -OutLabel perf_pass_vh_user -Preset 3 -Res 1920x1080 -Poses forest_noon,storm -Labels haze_off,ssao_off,fsr2_quality,cloud_steps_128,cloud_half_res,cloud_interleave_4x4,far_vp_half,baseline_repeat -Seconds 6 -Warmup 2 -BootSeconds 10 -LiveStationarySeconds 30 -WalkSeconds 30 -Flights 72@150 -Cache reuse -ScriptArgs "--user-prefs"`, with the user's `.tres` files copied into the isolated profile `.godot/quality_presets/appdata_perf_pass` first.
- Q2 (hitch bisect): `.godot/quality_presets/perf_pass_hitch/`, copied to `.godot/perf_pass/runQ2_hitch_report.json` and `runQ2_hitch_console.log`. Same command with `-OutLabel perf_pass_hitch -Poses forest_noon -Seconds 5 -BootSeconds 5` and `-Labels 'prop:SunshineCloudBridge:ShadowRefreshSeconds=10.0','prop:SunshineCloudBridge:TerrainCloudShadowRefreshSeconds=10.0','proc_off:WetWorld','proc_off:LocalWeatherEffects','proc_off:SunshineCloudBridge','proc_off:RingVolumetricClouds'`.
- `.godot/perf_pass/`: the console logs, `analyze.py` (full tables) and `summ.py` (compact tables), their outputs `runA_medium_defaults.md`, `runB_user_veryhigh.md`, `runC_veryhigh_defaults.md`, and the Q1 and Q2 reports.
- `audit_baseline` is protected by `tools/prune_test_output.ps1`; `.godot/perf_pass/` is ordinary test output and is pruned after 7 days.

Not measured in this pass: the A1000; 1440p; night and planet light; the megastorm; a split of near terrain; the optimised C# assembly; per-slot tree cost at Very High (TR-8 has it at Medium).
