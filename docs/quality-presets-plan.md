# Graphics quality presets: plan and baseline (2026-10-24)

Status: plan and measurements only. No production code, settings or shaders were changed. Measured on the RTX 4070 desktop (i9-14900KF); the RTX A1000 numbers below are estimates until the user runs the A1000 checks.

## 0. Summary

- **4070 at Very High, standing still (1080p):** 64-96 fps (median frame 10.2-15.5 ms). This is CPU-bound on the render thread: frame setup is about 5 ms and far-viewport render CPU 1.7-3.4 ms. The GPU sum is 8.6-13.8 ms.
- **Biggest single finding:** turning off `_process` of **LocalWeatherEffects** in clear weather removes the 5.5 ms frame setup and 3.2 ms of far-viewport render CPU (CPU render -8.7 ms, wall 14.5 → 10.5 ms), and the stationary hitches go from 2-3 per window to 0. The screenshots are identical. Reproduced twice; the cause is inside its per-frame update (section 2.3, WO-6).
- **Same, maximized 2560x1369:** 53-86 fps. Coast (18.5 ms) and forest (15.7 ms, p95 16.7) miss 60. This is GPU-bound.
- **"Not stable" is not the stationary cost.** Three things cause it:
  - *Flight at gameplay speed:* 36 fps at 72 m/s and 28 fps at 240 m/s, with 69-89% of frames over 25 ms. Streaming runs on the main thread (BiomeDecorations, TerrainLodCoordinator, deferred installs) with 225-250 MB/s of managed allocation. The GPU stays at 10-11 ms.
  - *First minute after load:* 27-40 fps for 30 s, then about 64 fps by 50 s. Allocation is 460-540 MB/s with about 10 gen1+ GCs per second.
  - *Stationary hitches:* about one 25-30 ms frame every 2 s. 88-90% of them are a gen1 GC of ~14 ms that lands in the SunshineCloudBridge weather refresh.
- **Shader and pipeline compiles are not a cause with warm caches** (0-4 per minute). Cold caches were not measured.
- **Top sustained costs** (1080p Very High, A-X-A deltas):
  1. Near terrain (Terrain3D + proxy sectors): GPU 3.2-5.4 ms, CPU 0.8-1.2 ms.
  2. RenderingServer frame setup: CPU 4.5-6.0 ms. It disappears when the scene tree is paused, or when only LocalWeatherEffects stops processing.
  3. Sunshine clouds: GPU 1.4-4.8 ms (GPU.Clouds marker 1.7-4.8).
  4. Sun shadow map: GPU 1.8-3.3 ms, CPU 0.4-0.6 ms. The planet map at night costs 2.3 ms.
  5. Far viewport: render CPU 1.7-3.4 ms (5.4 ms maximized at the coast) plus GPU 0.8-1.5 ms.
- **The A1000 Medium 60 fps target is far away.** The 2026-10-15 ranking measured the A1000 Medium at about 40-44 ms GPU with a 20-23 ms CPU floor. Section 3 has the estimate and what it implies.

## 1. Contract: non-negotiable on every preset

Presets scale resolution, samples, distances, density and update rates. They never remove one of these:

| # | System | Active means (runtime proof, used by the smoke test) |
|---|---|---|
| C1 | Sun shadows (at least the near map) | `StarLight.shadow_enabled` while the sun lights the ring; GraphicsQuality `RequestedSunShadowsEnabled`; `EffectiveShadowDistanceMeters >= 250`; atlas >= 2048. With the heightfield, `NearSunMapActive` once it is published. |
| C2 | Terrain shadows to the horizon | GraphicsQuality `EffectiveTerrainMacroShadowsEnabled` and `EffectiveTerrainMacroShadowMaxTraceMeters >= 64000`; terrain casters `TerrainLodCoordinator.TerrainShadowCoverageMeters > 0`; with the heightfield, `RingTerrainHeightShadow.IsSunHeightfieldReady`. |
| C3 | Haze / aerial perspective | `GetAtmosphereTuningDiagnostics()["enabled"] == true` after boot, after a preset change and after a world load; `ringworld/runtime/haze_enabled == true`. |
| C4 | POIs: haze, shadows, lighting like any surface | `RingPoiHaze.RegisteredCount > 0` near a POI, with `ring_atmosphere_ray_frame_valid` / `poi_ring_lighting_valid` set; `RingLargePoiShadowExtents` registrations unchanged by preset; no POI-specific preset value exists (AGENTS.md). |
| C5 | Clouds (volumetric or the far layer) | SunshineCloudBridge `Enabled && CloudRenderingEnabled`, or the far cloud layer visible while Sunshine is handed off. |
| C6 | Cloud shadows on terrain | Bridge `CastCloudShadows`, `CloudShadowReceiversEnabled`, `IsCloudShadowProjectorEnabled()`, `GetCloudShadowReceiverCount() > 0`. |
| C7 | Rain | LocalWeatherEffects `Enabled`, `MaximumParticleCount >= 512`; under a storm `PrecipitationIntensity > 0`; distant curtains eligible (`ProviderEligible`). |
| C8 | Lightning | LocalWeatherEffects `EnableLightning`; under a storm `LightningFlashCount` increases within 60 s (or `TriggerLightningPreview()` renders a flash). |

Allowed OFFs (user decision):
- water SSR on Low and Medium;
- SSAO on Low (and Medium if the budget needs it);
- contact shadows on every preset below Ultra;
- per-instance vegetation shadows below Very High.

One exception, stated explicitly: the Mega Ring world disables clouds, rain and lightning by design (`world_size_launcher.gd:1266-1281`). The contract test excludes Mega worlds, or asserts that exception.

**Automated check: `tools/quality_preset_contract_test.gd` (new, WO-1).**
- Boots the isolated certified world (the `tools/shadow_harness_common.gd` fixture).
- For each of Low / Medium / High / Very High / Ultra it calls `GraphicsQuality.ApplyUserPreset(p)` and asserts C1-C3 and C5-C7 at noon, C1 for the planet at `night_planet_lit`, C4 at `emitter_rim`, and C7-C8 under `SpawnStormFrontAhead()`.
- It repeats C3 after a simulated world load through `RingWorldSaveManager` (catches inconsistencies 12 and 15).
- GPU / windowed: about 4 min. A headless half (`tools/quality_preset_contract_headless_test.gd`) asserts the configuration-only fields (flags, distances, counts) for every preset in seconds.
- Joins the batch GPU gate list.

## 2. Baseline, RTX 4070, Very High (current presets)

Setup:
- Godot 4.8-dev7 mono, **C# Debug build** (the editor/run configuration; an exported Release build would lower CPU), Forward+, VSync off, `max_fps` 0.
- Factory settings: isolated APPDATA, no user tuning files; shader and Vulkan caches copied from the user profile, so caches are warm.
- Certified runtime-v2 world (seed 1337 fixture).
- Haze OFF, as shipped (inconsistency 15).
- Weather and celestial clocks paused for the pose windows and running for the first minute, storm and flights.
- Harness: a scratch harness built on `tools/shadow_harness_common.gd`, `tools/ShadowTimingBridge.cs` and `tools/WarmStallProbe.cs` (to be committed in WO-0).
- Windows: 8 s (1080p) or 6 s, after a 2 s warmup. A-X-A order; deltas are against the mean of the two neighbouring baselines. `baseline_repeat` is the noise floor, mostly ≤ 0.1 ms.
- "CPU render" = root + sub-viewport render CPU + `get_frame_setup_time_cpu()`; "wall" is the frame interval.
- The user's editor was open (about 30% idle GPU use) for the first-minute trace and the forest and coast poses of the first 1080p run. Those were re-measured with the editor closed; the deltas agreed within about 0.5 ms.
- Two later runs were discarded after the world was paused through the pause menu mid-run. The harness now blocks that.

### 2.1 Frame times

1080p, Very High (frame times in ms):

| Pose | Wall median | Wall p95 | Wall p99 | GPU median | GPU p95 | GPU p99 | CPU render (setup) | fps | Hitches > 25 ms |
|---|---|---|---|---|---|---|---|---|---|
| forest_noon (re-measured, see note) | 13.5 | 14.8 | 19.3 | 9.8 | 9.9 | 10.1 | 10.4 (5.1) | 72 | 4.0 per 8 s |
| coast (re-measured, see note) | 14.4 | 15.6 | 20.1 | 11.9 | 12.1 | 12.3 | 10.9 (5.4) | 68 | 3.3 per 8 s |
| emitter_rim (POI) | 13.3 | 14.1 | 18.8 | 8.6 | 9.5 | 9.7 | 9.8 (5.1) | 75 | 3.6 per 8 s |
| alt_1km | 13.0 | 14.0 | 18.8 | 12.1 | 12.6 | 13.3 | 9.8 (5.3) | 76 | 3.9 per 8 s |
| alt_30km | 10.2 | 10.9 | 15.5 | 9.2 | 10.1 | 10.3 | 7.4 (4.5) | 96 | 3.8 per 8 s |
| night_planet_lit | 13.4 | 14.3 | 18.8 | 9.3 | 10.1 | 10.3 | 10.0 (5.3) | 74 | 3.5 per 8 s |
| storm (rain, lightning, clocks running) | 14.3 | 16.9 | 22.6 | 11.6 | 12.4 | 12.8 | 10.2 (5.4) | 68 | 4.3 per 8 s |
| first minute after load | 17.3 | 41.0 | 54.4 | 9.0 | 10.6 | 11.3 | 11.0 | 44 average | 30% of frames |
| first minute, first 30 s | 28.7 | 51.1 | - | - | - | - | - | 33 | - |
| flight 72 m/s at 150 m | 28.0 | 37.1 | 42.3 | 9.6 | 13.0 | 13.3 | 5.6 | 36 | 69% of frames |
| flight 240 m/s at 500 m | 35.7 | 46.3 | 50.4 | 11.1 | 13.0 | 13.8 | 7.2 | 28 | 89% of frames |

Note (2026-10-24, phase 1): the forest_noon and coast rows were replaced with a clean editor-closed run (`s2_debug`, committed harness `tools/quality_preset_capture.ps1`, Debug C#, 3 baseline windows each). The original rows came from `vh_1080`, which ran with the user's editor open: GPU was 1.9 ms higher at both poses (forest 11.7, coast 13.8; wall 13.2 / 15.5; CPU render 9.8 / 11.7). The other rows above are unchanged and still from `vh_1080`. The same poses with the optimized (Release) C# assembly: forest wall 12.7 / p95 13.7, GPU 10.0, CPU render 10.1 (4.9); coast wall 13.6 / p95 14.7, GPU 12.1, CPU render 10.8 (5.4) (`s2_release`). Debug costs +0.4-0.8 ms wall standing still and +4.6 ms median wall in 72 m/s flight; see the phase 1 report.

Maximized 2560x1369, Very High:

| Pose | Wall median | Wall p95 | Wall p99 | GPU median | GPU p95 | GPU p99 | CPU render | fps |
|---|---|---|---|---|---|---|---|---|
| forest_noon | 15.7 | 16.7 | 21.6 | 14.2 | 14.6 | 14.7 | 12.2 | 63 |
| coast | **18.5** | 19.4 | 24.7 | **16.6** | 16.9 | 17.2 | 14.6 | **53** |
| emitter_rim | 13.9 | 14.7 | 19.5 | 10.5 | 11.0 | 11.1 | 10.5 | 71 |
| alt_1km | 13.8 | 14.7 | 19.6 | 13.3 | 13.7 | 14.0 | 10.7 | 71 |
| alt_30km | 11.5 | 12.0 | 15.2 | 11.3 | 11.6 | 11.7 | 7.1 | 86 |
| night_planet_lit | 12.9 | 13.8 | 18.7 | 11.4 | 11.7 | 11.8 | 9.6 | 77 |
| storm | 14.7 | 16.4 | 21.0 | 14.2 | 14.8 | 15.2 | 9.8 | 66 |
| flight 72 m/s at 150 m | 26.1 | 35.6 | 40.3 | 11.0 | 14.8 | 15.0 | 8.8 | 38 |
| first minute | 30.6 | 55.7 | 66.8 | 10.8 | 13.2 | 16.6 | 8.8 | 31 |

Medium on the 4070 (A1000 proxy): see section 3.

### 2.2 Attribution (1080p Very High; GPU / wall delta in ms when the system is turned off; negative = saving)

| System (switch) | forest | coast | emitter | 1 km | 30 km | night | storm |
|---|---|---|---|---|---|---|---|
| Near terrain: Terrain3D + proxy sectors (hidden) | -4.06 / -1.34 | -5.44 / -1.83 | -3.18 / -1.11 | -4.80 / -1.37 | -3.52 / -0.50 | -3.97 / -1.17 | -4.50 / -1.34 |
| Sunshine clouds (CloudRenderingEnabled off) | -1.39 / -0.14 | -3.70 / -0.14 | +1.83* | -4.83 / -0.19 | -3.29 / -0.29 | +0.39* | -1.52 / +0.30 |
| Sun shadow map (off) | -2.60 / -0.70 | -3.30 / -0.84 | -1.81 / -0.69 | -1.76 / -0.50 | -0.16 | n/a | -2.33 / -0.72 |
| Planet shadow map (night, off) | | | | | | -2.34 / -0.58 | |
| Trees (hidden) | -0.32 / -0.64 | -1.00 / -0.65 | -0.26 | -0.23 | -0.06 | -1.14 / -0.47 | -1.53 / -0.56 |
| Tree sun shadows (off) | -0.28 / -0.60 | -0.13 / -0.50 | +0.30 | +0.08 / -0.47 | +0.20 | -0.23 / -0.67 | -0.17 / -0.59 |
| Height shadow + 400 m near map (on; proposal N1) | -0.59 / -0.14 | -1.66 / -0.68 | -0.71 / -1.64 | -1.02 / -1.33 | **+0.52** / 0.00 | +0.05 / -0.47 | -0.48 / -0.43 |
| Haze, Legacy (on) | +0.42 | +0.41 | +0.61 | +0.34 | +0.64 | +0.30 | +0.46 |
| Haze, Physical q4 / q8 (on, separate run) | +0.84 / +0.82 | +0.48 / +0.81 | | | | | |
| Far viewport process (ScaledFarViewport `set_process(false)`) | 0.00 / -0.18 | -0.01 / -0.32 | -0.01 / -0.32 | +0.05 / -0.33 | +0.13 / -0.25 | +0.02 / -0.29 | +0.13 / -0.69 |
| Far terrain (scaled ring meshes hidden) | -0.02 | +0.02 | +0.27 | +0.04 | -0.05 | -0.01 | -0.07 |
| SSAO (off) | -0.12 | -0.03 | -0.29 | -0.05 | +0.17 | -0.11 | -0.07 |
| Water FFT (off) | -0.02 | +0.05 | -0.06 | +0.04 / -0.64 | +0.14 | -0.10 | -0.03 |
| Cloud shadow generation (off) | -0.04 | +0.05 | -0.09 | 0.00 | +0.03 | +0.01 | -0.12 |
| Terrain macro shadows (off) | -0.04 | -0.04 | -0.05 | -0.10 | -0.23 | -0.28 | -0.10 |
| Standing-grass nodes (hidden) / grass process | ≈0 / -0.2 wall | ≈0 | ≈0 | ≈0 | ≈0 | ≈0 | ≈0 |

\* Emitter and night: turning Sunshine off raises GPU time, so a fallback path takes over. Do not read these as cloud cost.

Notes on the table:
- **Grass cards are not separable** with these switches. They are drawn inside the near-terrain item. The standing-grass node layer is about 0.
- **Wall barely follows GPU savings at 1080p**, because the frame is CPU-bound. Sunshine off saves 3.7 ms of GPU at the coast but only 0.14 ms of wall. At 1440p the frame is GPU-bound and GPU savings do appear.
- **Fixed per-pose costs:**
  - Far viewport: GPU 0.80-1.45 ms; render CPU 1.7-3.4 ms at 1080p and 1.8-5.4 ms maximized.
  - Frame setup: 4.5-6.0 ms in every pose.
  - Root viewport render CPU: 1.1-2.4 ms.
  - Draw calls: 0.4-2.9k.

### 2.3 Frame-setup finding (CPU)

- `get_frame_setup_time_cpu()` (the RenderingServer scene update: dirty instances, transforms, AABBs, instance and material parameters) is 4.5-6 ms in every pose, with clocks paused and the camera still.
- Pausing the scene tree (`SceneTree.paused`, which is what the pause menu does) drops it to 0.02 ms. The FarRingViewport render CPU falls from 3.4 to 0.2 ms, and wall from 13.9 to about 10.5 ms (GPU-bound).
- So about 8 ms per frame of render-thread CPU comes from per-frame script updates of RenderingServer state, even when nothing visible changes.
- **Owner found: `LocalWeatherEffects` (`scripts/RingLocalWeatherEffects*.cs`).** Turning off its process (process_mode DISABLED, so its children too) gives the same result as pausing the tree, at forest noon with no rain:

  | Window | Wall (ms) | Frame setup (ms) | Far-viewport CPU (ms) | Hitches |
  |---|---|---|---|---|
  | Baseline | 14.3-14.7 | 5.45-5.58 | 3.46-3.51 | 2-3 |
  | LocalWeatherEffects process off | 10.54 / 10.57 | 0.11 | 0.23-0.25 | 0 |

  - GPU +0.4 ms (now GPU-bound).
  - The screenshots before and after are identical.
  - Reproduced in two separate runs and two windows each; it recovers when restored.
  - Its sibling `DistantRainCurtains` (added to the game root) has no effect.
- **No effect when turned off on its own** (as a single node): FloatingOrigin, RingDayNightCycle, DaySideAtmosphere, RingWorld, Explorer, ChunkGridManager, TerrainLodCoordinator, GlobalTerrainLod3, AnalyticGlobalRing, RingCloudLayer, RingVolumetricClouds, SunshineCloudsDriver, SunshineCloudBridge, TerrainCloudSunVisibility, CloudProviderController, ScaledFarViewport, GraphicsQuality, ScaleAnchors, EmitterStandIns, EmitterGrid, DistantRainCurtains.
- Not isolated yet: the method inside `_Process` (`RingLocalWeatherEffects.cs:360-410`). Suspects:
  - `PublishVegetationWind` / `SharedWindField.Publish`: cached global shader parameters.
  - `UpdateRainHaze` → `PublishRainHazeFrame` (the rain-haze compositor publish).
  - `UpdateParticleMotion` / `UpdateSnowParticleProfile`.
  - `UpdateWetness`.
  - `UpdateLightning` (OmniLight / bolt nodes).

  A write that dirties shared materials or global uniforms every frame would explain the frame setup (`update_dirty_resources` runs inside it) and the far viewport's extra CPU (its materials share the globals). WO-6 bisects the methods.

- **Resolved in phase 1 (WO-6, 2026-10-25).** Root cause: `RingWindField.SetCached` deduplicated global shader parameters with `Variant.Equals`. `Godot.Variant` does not override `Equals`, so `ValueType.Equals` compares a per-conversion `_disposer` reference and an Object Variant never matches. The sampler global `ring_wind_gust_texture` was therefore re-set every frame (`LocalWeatherEffects._Process` -> `PublishVegetationWind` -> `SharedWindField.Publish`), which queues every material that uses it for a uniform-set update. A Release A-X-A bisect at forest: skipping only that texture re-set gives CPU render -8.18 ms / wall -2.70 ms; every other `_Process` call is within ±0.1 ms. Fix: `scripts/Rendering/RingVariantEquality.cs` (`Same`, objects by reference), used by every Variant dedup cache. After the fix (Release, VH 1080p): RS frame setup 4.9-5.4 -> 0.1-0.2 ms, far-viewport CPU 3.2 -> 0.14 ms, forest wall 12.7 -> 10.1 ms, coast 13.6 -> 11.9 ms, 72 m/s flight wall 21.3 -> 11.9 ms (p95 31.9 -> 17.4), stationary hitches 28 -> 5 per minute. The 4070 at Very High 1080p is now GPU-bound standing still.

### 2.4 Hitches vs sustained cost

From the frame-decomposition trace (`WarmStallProbe`: per-frame physics / node-process / deferred / RS-draw buckets, GC, pipeline compiles, JIT, and the owner gap):

| Situation | Frames > 25 ms | Main cause | Evidence |
|---|---|---|---|
| Standing still, any pose | ~1% (0.5 per s) | **.NET gen1 GC** | 88-90% of hitch frames contain a gen1 collection. Median pause 13.7 ms (max 24), one every 1.8 s, at only 11 MB/s allocation. They land in the SunshineCloudBridge `_Process` frame (weather refresh every 0.25 s). No pipeline compiles. With LocalWeatherEffects' process off, the windows had 0 hitches (vs 2-3), so its per-frame work is likely also the main stationary allocator (confirm in WO-6 / WO-4). |
| First minute after load | 30% (first 30 s: 27-40 fps) | **Streaming on the main thread plus allocation-driven GC** (sustained) | Node-process median 9-14 ms (TerrainLodCoordinator, GlobalTerrainLod3, BiomeDecorations). Allocation 460-540 MB/s, about 10 gen1+ GCs per s (pause ~4 ms). Pipeline compiles 4 in the minute. Settles by 40-50 s. Also a one-off 200 ms macro-shadow build at load (`Terrain macro shadows ready ... buildMs=203`). |
| Flight 72 m/s | 69% (36 fps) | **Sustained streaming CPU, not spikes** | Node-process median 11 ms (p95 19), BiomeDecorations dominant, then TerrainLodCoordinator. Deferred / RS-sync bucket 7-8 ms at p95. 226 MB/s allocation, a GC every 0.2 s. GPU 9.6 ms. |
| Flight 240 m/s | 89% (28 fps) | Same, heavier | Node-process median 16 ms. BiomeDecorations 773 of 876 hitch frames. 249 MB/s allocation. |
| Shader / pipeline compiles | ≈ 0 with warm caches | - | 0 in stationary windows, 0-4 in the first minute. **Cold caches (a fresh install) were not measured.** |
| Cloud map / macro rebuilds | not observed as hitches at runtime | - | The only macro build is the 200 ms one at load. |

### 2.5 Medium on the 4070 (A1000 proxy), 1080p

Same harness; 6 s windows, 6 baselines per pose. Note: clouds run at the Very High settings because of inconsistency 1 (GPU.Clouds 1.7-4.4 ms, the same as Very High).

| Pose | Wall median | Wall p95 | Wall p99 | GPU median | GPU p95 | CPU render (setup) | fps |
|---|---|---|---|---|---|---|---|
| forest_noon | 11.2 | 12.0 | 16.5 | 8.5 | 8.8 | 8.0 (3.9) | 88 |
| coast | 13.0 | 13.9 | 18.8 | 9.8 | 10.3 | 9.6 (4.7) | 76 |
| emitter_rim | 13.6 | 14.4 | 19.0 | 6.0 | 6.1 | 10.2 (5.1) | 73 |
| alt_1km | 12.8 | 13.6 | 18.4 | 8.8 | 9.3 | 9.8 (5.0) | 77 |
| alt_30km | 8.6 | 9.4 | 14.1 | 7.5 | 7.9 | 6.0 (3.5) | 115 |
| night_planet_lit | 11.8 | 12.7 | 17.5 | 6.9 | 7.3 | 8.6 (4.4) | 84 |
| storm | 12.8 | 15.1 | 20.7 | 8.8 | 9.4 | 8.9 (4.6) | 76 |
| flight 72 m/s at 150 m | 22.7 | 33.7 | 37.5 | 7.4 | 10.1 | 8.5 | 42 |
| first 30 s after load | 13.8 | 21.3 | 45.8 | 6.8 | 7.2 | 7.8 | 65 |

Medium GPU deltas (ms):

| Change | Delta |
|---|---|
| Sunshine off | -1.2 to -4.8 |
| Cloud half resolution | -0.8 to -1.8 |
| FSR2 Quality | -1.1 to -2.6 |
| Sun map off (6 km / 4096 atlas) | -0.1 to -0.95 |

Medium is CPU-bound on the 4070 in every pose; the same LocalWeatherEffects frame-setup cost applies.

## 3. Frame budgets per target

60 fps = 16.6 ms. Keep 1.5-2 ms of headroom for p95: sustained wall ≤ 14.5 ms, p99 ≤ 20 ms, and ≤ 1 frame over 25 ms per minute.

| Budget | 4070, Very High, 1080p and 1440p | A1000, Medium (at its native resolution with FSR2 where approved) |
|---|---|---|
| **CPU, main + render thread** | **≤ 12 ms** | **≤ 13 ms** (≈ 8-9 ms on the 4070 desktop CPU) |
| `_process` + physics + deferred | ≤ 4 ms stationary, ≤ 6 ms flying | ≤ 4.5 ms |
| RS frame setup | ≤ 1.5 ms (today 5) | ≤ 1.5 ms |
| Render submission (root + far) | ≤ 4 ms (today 4-5) | ≤ 4 ms |
| GC pause p99 | ≤ 2 ms (today 14) | ≤ 2 ms |
| Streaming per frame (trees, terrain, installs) | ≤ 3 ms at 72 m/s (today 10+) | ≤ 3 ms at 72 m/s |
| **GPU** | **≤ 13.5 ms** at 1440p | **≤ 14 ms on the A1000** (≈ 3.5-4 ms of the 4070 at the same internal resolution) |
| Terrain + grass cards | ≤ 4 | ≤ 4 |
| Clouds + cloud shadows | ≤ 3.5 | ≤ 3 |
| Sun / planet shadow maps | ≤ 2 | ≤ 2 |
| Far viewport | ≤ 1 | ≤ 1 |
| Haze | ≤ 0.8 | ≤ 1 |
| Trees | ≤ 1 | ≤ 1 |
| SSAO + SSR + post + water | ≤ 1 | ≤ 1 (SSR and SSAO allowed off) |

**A1000 estimate (needs the user's runs).**
- There is no matched A1000/4070 pair in docs. The closest is the Sunshine raymarch: about 10.6 ms on the A1000 (2026-10-15) against about 3 ms on the 4070 (2026-10-17). From that and the hardware (about 5.6 vs 29 TFLOPS; 132 vs 504 GB/s), I use **GPU ×3.5-4.5** and **CPU ×1.4-1.8**.
- The 2026-10-15 ranking measured the A1000 Medium at 1600x900 at about 40-44 ms GPU with a 20-23 ms CPU floor, using the user's heavy preferences.
- **4070 Medium proxy (2.5):** ground poses use 8.5-9.8 ms GPU, which scales to **about 30-44 ms on the A1000** (storm 31-40, emitter 21-27). That is consistent with the 10-15 ranking. CPU render 8-10 ms plus process scales to about 15-22 ms.
- To reach 14 ms GPU on the A1000, the 4070-equivalent must be about 3.1-4.0 ms at the same internal resolution. That is roughly a 2.5x cut from Medium today. Fixing the LocalWeatherEffects cost (WO-6) alone takes about 4 ms off the 4070 Medium CPU frame, about 6-7 ms on the laptop CPU.
- **Conclusion:** Medium on the A1000 at 60 fps needs a GPU cut of about 2.5-3x from today plus the CPU work in WO-4 to WO-6. Retuning alone will not get there. Lower internal resolution (FSR2, proposal N2) and cheaper clouds (N3/N4) are required.

## 4. Preset matrix

`cur` = today's effective value (after the factory cloud file override). Proposed values are the targets for WO-3 and are tuned by the gates in section 7. **Bold** = changed. An allowed OFF is marked OFF.

### 4.1 Contract systems (always on, scaled)

| Setting | Low | Medium | High | Very High | Ultra | Notes |
|---|---|---|---|---|---|---|
| Sun shadow atlas | **2048** (cur 4096) | 4096 | **4096** (cur 8192) | 8192 | 8192 | 8192→4096 saved 0.8-1.35 ms GPU on the 4070. |
| Sun filter | Hard | SoftVeryLow | SoftLow | SoftMedium | SoftHigh | unchanged |
| Sun map distance (no heightfield) | 4 km | 6 km | **10 km** (cur 12) | 14 km | 16 km | |
| Sun near map with heightfield (N1) | **250 m PSSM2** | **300 m PSSM2** | 400 m PSSM2 | 400 m PSSM2 | **600 m PSSM3** | Today, with HeightShadowEnabled the preset distance is ignored (inconsistency 10). |
| Heightfield L0 texel / levels (N1) | **16 m / 2** | **12 m / 2** | 8 m / 2 | 8 m / 2 | **4 m / 3** | |
| Terrain macro trace / steps | 64 km / 8 | 96 km / 10 | 128 km / 14 | 144 km / 16 | 160 km / 16 | unchanged; macro costs ≤ 0.3 ms |
| Planet light map | **2 km / 2 casc.** | **3 km / 2** | **4 km / 3** | 6 km / 4 | 6 km / 4 | cur 6 km / 4 everywhere (inconsistency 8); costs 2.3 ms at night on the 4070 |
| Haze | on, Physical q4 or Legacy | on, q4 | on, q8 | on, q8 | on, q16 | Always on (inconsistency 15). The model is a user decision (Q2). Physical costs +0.5-0.85 ms on the 4070. |
| Sunshine resolution | **Half + jitter** | **Half + jitter** | Native | Native | Native | cur Native everywhere; half saved 1.0-1.6 ms on the 4070 (≈ 5 ms on the A1000 per the 10-15 ranking) |
| Sunshine interleave | **4x4** | **4x4** | 2x2 | 2x2 | **off** | 4x4 saved 0.2-1.1 ms |
| Raymarch steps | **96** | **128** | **192** | **256** | 328 | cur 328 on every preset (inconsistency 1) |
| Lighting steps | **8** | **12** | **16** | **24** | 30 | cur 30 everywhere (factory file) |
| Near cloud-shadow map | 256 px / 2 Hz | **512 px / 5 Hz** | 512 / 5 Hz | **1024 / 10 Hz** | 1024 / 10 Hz | cur 512 / 5 Hz everywhere; generation measured ≈ 0 ms on the 4070 |
| Far cloud-shadow map | 128 / 0.25 Hz | 128 / 0.5 Hz | **256 / 1 Hz** | **384 / 1 Hz** | **512 / 1.25 Hz** | cur 128 / 2.5 Hz everywhere |
| Cloud-shadow vertical samples | **8** | **12** | **16** | 24 | 24 | cur 24 everywhere |
| Weather system uploads | 40 | 48 | 48 | 64 | 64 | today overridden to 64 |
| Cloud shadows on terrain | on | on | on | on | on | contract C6 |
| Rain particles (`MaximumParticleCount`) | **1024** | **2048** | **3072** | 4096 | **6144** | cur 4096 everywhere |
| Distant rain curtains | on | on | on | on | on | needs mesh CloudQuality ≠ Off (inconsistency 4) |
| Lightning branches / secondary depth | **1 / 0** | **2 / 1** | 3 / 1 | 3 / 1 | **4 / 2** | cur 3 / 1 everywhere |
| POIs | follow the world | | | | | no POI values (AGENTS.md) |

### 4.2 Scaled, not contract

| Setting | Low | Medium | High | Very High | Ultra | Notes |
|---|---|---|---|---|---|---|
| Terrain LOD TargetPixelError | 4 | 3 | 2 | 1.25 | 0.85 | unchanged; the real distance knob (inconsistency 3) |
| Terrain active levels | 3 | 5 | 7 | 8 | 8 | unchanged |
| Global segments (around×across) | 256×16 | 512×32 | 1024×64 | 2048×128 | 3072×192 | unchanged |
| Render resolution (N2) | **FSR2 Balanced (0.59)** | **FSR2 Quality (0.67)** on the laptop tier, Native on desktop | Native | Native | Native | FSR2 Quality saved 1.5-1.8 ms GPU at 1080p and 2.9-3.9 ms at 1440p on the 4070 |
| Adaptive resolution (exists, default off) | **on (laptop)** | **on (laptop)** | off | off | off | proposal N2 |
| Far viewport resolution scale | **0.5** | **0.75** | 1.0 | 1.0 | 1.0 | dead preset fields today (inconsistency 2); far GPU is only 0.8-1.5 ms |
| Grass (standing + cards) | table Low | table Medium | High | Very High | Ultra | laptop uses the table plus caps (inconsistency 5) |
| Tree billboard fade start / end | **200 / 240** | **250 / 300** | 300 / 350 | **400 / 460** | **550 / 650** | cur 300 / 350 everywhere; trees cost 0.3-1.5 ms GPU |
| Tree near-detail fade end | **25** | **30** | 40 | 40 | **60** | |
| Tree sun-shadow distance | **160 m** | **240 m** | **360 m** | 480 m | **700 m** | cur 480 everywhere; costs about 0.5 ms CPU (wall) |
| Per-instance vegetation shadows | OFF | OFF | OFF | **on** | on | luxury; default off today |
| Contact shadows | OFF | OFF | OFF | OFF | **on** | luxury; +0.2 ms |
| SSAO | **OFF** | on (detail 0.75) | on 0.85 | on 0.92 | on 0.96 | allowed OFF; costs ≤ 0.3 ms on the 4070 |
| Water SSR | OFF | **OFF** (cur 12 steps) | **12 steps / 120 m** (cur 24) | 24 / 250 m | **32 / 400 m** | allowed OFF; distinct per preset (inconsistency 6) |
| Water FFT resolution / cascades / Hz | **64 / 1 / 12** | **128 / 1 / 20** | 128 / 2 / 24 | 128 / 2 / 24 | **256 / 3 / 30** | not preset-driven today; FFT ≈ 0.1 ms GPU on the 4070 |
| Streaming workers (one source, inconsistency 7) | 1 | 2 | 3 | 8 | 16 | LOD profile is the only owner |
| Upload budget ms / uploads per frame | 1 / 1 | 1.75 / 3 | 2 / 3 | **3 / 4** (cur 4 / 6) | 4 / 6 | flight is CPU-bound; lower VH caps cut per-frame spikes (WO-5 gate) |

## 5. Fix list for inconsistencies 1-16

All verified against the working tree (file:line). Each fix lists the test that proves it.

| # | Problem (verified) | Fix | Files | Test |
|---|---|---|---|---|
| 1 | `GameplayCloudDefaults.OnGraphicsPresetApplied` captures the preset values as a baseline and then re-applies every stored override (`scripts/ui/GameplayCloudDefaults.cs:125-130`). The factory `resources/clouds/gameplay_cloud_defaults.tres` stores 10 of the 12 preset keys (RaymarchSteps 328 :57, near 512 px :37 / 5 Hz :40, far 128 px :34 / 2.5 Hz :36, vertical samples 24 :43, scale 0, jitter false, interleave 1, uploads 64 :70), plus LightingSteps 30. So Low = Very High for clouds, and Cinematic's maps are cut down. Only GpuShadowStepMeters / GpuShadowMaxSamples follow the preset. | Split the keys into appearance (stays in the factory file) and preset-owned quality (removed from the factory file, owned by `ApplySunshineCloudQuality`, extended with LightingSteps). A user's explicit override of a preset-owned key still wins, using the `SetWaterSsrOverrides` pattern. One-time cleanup of the factory file; user:// files untouched. | `RingGraphicsQualityController.cs:675-722`, `scripts/ui/GameplayCloudDefaults.cs`, `resources/clouds/gameplay_cloud_defaults.tres` | Extend `tools/gameplay_cloud_tuning_smoke_test.gd`: per preset, the bridge values equal the preset tuple with the factory file; a sparse override still wins. |
| 2 | Tuning `FarViewportResolutionScale` / `FarAroundSegments` / `FarAcrossSegments` (GQC:36-38) are never read. The far viewport always gets 1.0 / 4096 / 64 from `GameplayFarRenderingDefaults` (:13-15, :67-68), which `FarRenderingSettingsSmoke.cs:83-86` asserts. `UseMegaRegionalFarBackdrop` (GQC:57) is set by the launcher (:1323) and read nowhere. | The preset supplies ResolutionScale (matrix); the far-rendering user file overrides it. Delete `UseMegaRegionalFarBackdrop` and its launcher write. | GQC.cs, `scripts/ui/GameplayFarRenderingDefaults.cs`, `world_size_launcher.gd:1323`, `scripts/Tools/FarRenderingSettingsSmoke.cs` | `far_rendering_settings_smoke_test.gd` asserts per-preset values and user precedence. |
| 3 | Every `ring_lod_*.tres` has `UseScreenSpaceError = true`, so authored `LodNOutMeters` are only a fallback (`RingTerrainLodCoordinator.cs:920-940`). The Mega Ring profile wins on any preset (Coord:893). | Mark the authored bands "fallback only" in the resources (or remove them); document TargetPixelError / ActiveLevelCount as the knobs. Mega Ring stays outside presets (stated contract exception). | `resources/voxel/ring_lod_*.tres`, Coord | `lod_projected_error_contract_test.gd` asserts per-preset TargetPixelError / ActiveLevelCount. |
| 4 | Mesh-cloud preset values (radius, ShadowMapResolution, Projected*) are inert while Sunshine is primary (`RingVolumetricCloudLayer.cs:426, 837-858`). CloudQuality still gates distant rain curtains (`RingDistantRainCurtains.cs:79-81`) and the Sunshine-suspended fallback. | Keep CloudQuality (≥ Low on every preset); drop the inert Tuning fields. | GQC.cs Tuning | Contract test C7 (curtains eligible on every preset). |
| 5 | The laptop tier returns one fixed grass profile before the preset switch (`RingGrassQualityProfile.cs:72-73`). | The laptop tier uses the preset table, with laptop caps applied as a clamp. | `RingGrassQualityProfile.cs` | `grass_gpu_geometry_test.gd` (presets × tiers): laptop Low < Medium < High. |
| 6 | Water SSR: Low off, Medium reduced, every other preset identical, Ultra and Cinematic included (`GQC:668-673`). | Per-preset values (matrix); Medium off is allowed. | GQC.cs | `water_settings_smoke_test.gd`: values monotonic by preset. |
| 7 | Worker counts disagree: pause menu 2/2/4/6/16/24 (`RingLodPauseMenu.cs:975-991`), written after `ApplyUserPreset` (:683-688); LOD profile 1/2/3/8/16/24; grid VoxelWorkers 2/2/4/6/6/8; save baseline 3/8 (`RingWorldSaveManager.cs:1056`). | One owner: the LOD profile. The menu slider reads and edits it; the save baseline reads it. | `RingLodPauseMenu.cs`, `RingWorldSaveManager.cs`, `ring_lod_*.tres` | Headless: after `ApplyUserPreset(p)`, the coordinator's MaxConcurrentWorkerBuilds equals the profile and the menu shows it. |
| 8 | Planet light: 6 km / 4 cascades on every preset (`scripts/day_side_atmosphere.gd:143-144`, `.Shadows.cs:365-401`), more than the Low sun (4 km). | Per-preset planet distance and cascades (matrix), still under the near planet map when the heightfield is on. | `.Shadows.cs`, `day_side_atmosphere.gd` | `shadow_settings_smoke_test.gd`: per preset. |
| 9 | Low and Medium share the 4096 atlas (GQC:727, 734). | Low 2048, High 4096 (matrix). | GQC.cs | shadow settings smoke |
| 10 | With HeightShadowEnabled, the near map forces 400 m / 2 cascades and ignores the preset distance and the MaxDistance / CascadeCount / FadeStart overrides (`.Shadows.cs:301-321`, `.LocalShadows.cs:140-156`); the POI extension can reach 20 km (`.LocalShadows.cs:82-104`). | With the heightfield, the preset scales the near-map distance and the heightfield resolution instead (matrix); the menu rows keep "(near map)". | `.Shadows.cs`, `.LocalShadows.cs`, `RingHeightShadowSettings.cs` | `terrain_shadow_lod_gameplay_test --height-shadow=on` per preset. |
| 11 | Launcher tier values overwritten by the preset: StarLight distance and splits (launcher:1652-1657), SSR steps (:1661), macro values (:1672-1676), mesh-cloud values (:1690-1699), Sunshine steps / BlurPower (:1704-1707), caster coverage (:1759), grid workers (:1609-1612), far scale (:1716-1718). | Delete those writes. Keep only what the preset does not own: memory caps, streaming radii, CloudMask*. | `world_size_launcher.gd` | `terrain3d_launcher_smoke.gd`; the contract test checks the preset values win. |
| 12 | World load clamps the saved preset to Low..VeryHigh and re-presets only the coordinator (`RingWorldSaveManager.cs:992-999`); GraphicsQuality keeps the launcher preset, and the pause menu reads GraphicsQuality (:291). Laptop baseline High (:1049-1062) vs launcher Medium (launcher:1753-1756). | Load calls `GraphicsQuality.ApplyUserPreset`, which drives the coordinator. Clamp to Low..Ultra. One tier-baseline table (laptop Medium, desktop Very High) shared by launcher and save manager (`hardware_profile.gd`). | `RingWorldSaveManager.cs`, `world_size_launcher.gd`, `hardware_profile.gd` | Isolated `world_loading_smoke_test` with saved Ultra and Low slots: after load both controllers report the saved preset. |
| 13 | "Desktop High" (`resources/voxel/ring_lod_very_high.tres:7`) surfaces through CurrentPresetName into logs, pause-menu status, perf monitor and certification monitor; the menu says "Very High". "Ultra / Video" and "Cinematic / RTX 5090" are hard-coded (GQC:195-196). | Rename to "Very High" everywhere. The menu lists Low / Medium / High / Very High / Ultra; Cinematic5090 stays command-line only (`--graphics-preset=cinematic-5090`). Update the tools and docs that grep for "Desktop High" (`lod_projected_error_contract_test.gd:4`, `terrain_altitude_lod_flight_test.gd:15` + `.ps1` + `terrain_altitude_lod_a1000_capture.md:14`, docs). | `ring_lod_very_high.tres`, GQC.cs, `RingLodPauseMenu.cs:285-290`, tools, docs | Grep gate: no "Desktop High" under scripts/resources/tools; `visual_settings_ui_smoke_test.gd` checks the 5 menu items. |
| 14 | No preset scaling for trees, water FFT, haze quality, planet light, contact / height / vegetation shadows, far viewport, AA/FSR, POIs or weather (rain / lightning). | Matrix 4.1-4.2. POIs only follow world systems. | GQC.cs plus owners | Contract test plus per-subsystem settings smokes |
| 15 | Haze forced OFF every session: `LodPauseMenu._Ready` calls `SetAtmosphericHaze(HazeEnabledByDefault = false)` after GQC enabled it (`RingLodPauseMenu.cs:33, 109, 595-601`), and saves capture the false (`RingWorldSaveManager.cs:283, 1014-1015`). This violates C3. | `HazeEnabledByDefault = true`. The session toggle leaves the menu (it becomes a debug switch). A saved `false` is migrated to true once. Presets scale haze quality and never disable it. | `RingLodPauseMenu.cs`, `RingWorldSaveManager.cs` | Contract test C3 after boot and after load. |
| 16 | **Claim corrected.** `TerrainCloudShadowEnabled` (false in the factory file, :69) is "terrain shadows inside clouds" (terrain sunlight visibility shading the clouds), not cloud shadows on terrain (`RingLiveTuningDrawer.cs:657`). Cloud shadows on terrain are on (`CastCloudShadows` :12, `PreferGpuCloudShadows` :55). No contract violation. | Optional Very High / Ultra feature. Relabel the menu row so it is not confused with cloud shadows. | `RingLiveTuningDrawer.cs:657` | Contract test C6 |

## 6. New methods (need user approval before adoption)

Savings are measured on the 4070 where stated; A1000 numbers are estimates.

| ID | Method | Measured / estimated saving | Risk | Cost to build | Recommendation |
|---|---|---|---|---|---|
| N1 | **Heightfield + near map as the default** on all presets (scaled near-map distance) | 4070 1080p VH: GPU -0.5 to -1.7 ms on the ground and at 1 km; CPU -0.1 to -1.5 ms; shadow draws 2.1k→1.4k (forest). **+0.5 ms at 30 km**; neutral at night. docs/shadow-revamp-plan.md: -1.9 to -3.9 ms on the user's saved pose. | Blockers B1 (low sun), B2 (POI receivers and casters), B3 (+1.7 ms forest night with a low planet), B4 (A1000 runs) are open. | Medium: the blockers plus per-preset distances; the code exists. | Yes, after B1-B3. Highest-value shadow item, and it makes Low's sun near map cheap. |
| N2 | **FSR2 / adaptive resolution as part of presets** (both already exist; today Native for everyone) | FSR2 Quality: GPU -1.5 to -1.8 ms at 1080p, **-2.9 to -3.9 ms at 1440p** on the 4070. Wall did not move at 1080p (CPU-bound). On the A1000 (GPU-bound) the 2026-10-15 ranking measured 50% scale = -12.6 ms. | Ghosting and shimmer on thin geometry (grass, wires); the haze and cloud compositors must be FSR-aware (they are, the drawer exposes it). | Small: preset default plus menu wording. | **Required for the A1000 Medium target.** Very High at 1440p: optional (Quality brings coast to about 15 ms). |
| N3 | **Half-resolution clouds plus jitter** on Low / Medium | 4070: -1.0 to -1.6 ms; A1000 -5.2 ms at spawn (10-15 ranking). | Moderate ghosting on cloud edges (the jitter history was tuned in the cloud-history work). | Small (preset values). | Yes, Low and Medium. |
| N4 | **4x4 interleaved raymarch** on Low / Medium (2x2 is already on everywhere) | 4070: -0.2 to -1.1 ms. | Temporal smearing in fast camera motion (interleave max-pixel-motion clamp exists). | Small. | Yes on Low, trial on Medium. |
| N5 | **GC configuration** (raise the gen0 budget via runtimeconfig `System.GC.Gen0Size` / `DOTNET_GCgen0size`, or `GCSettings.LatencyMode = SustainedLowLatency` during gameplay) | Target: cut the 0.5/s stationary 14 ms gen1 hitches by about 5-10x and the first-minute / flight GC count. Not measured: needs an A-B. | Higher RSS (+100-300 MB). Gen0 size support must be verified on this .NET runtime. Fewer but possibly longer pauses if survivors grow. | Small (project config) plus a hitch A-B. | Trial first; it complements the allocation fixes (WO-4) and does not replace them. |
| N6 | **Frame-setup CPU reduction**: stop per-frame RenderingServer writes when nothing changed (dirty checks, MultiMesh / instance-parameter batching, fewer instances) | Measured: LocalWeatherEffects owns the whole stationary cost. CPU render -8.7 ms, wall 14.5 → 10.5 ms (4070 Very High 1080p), and its stationary hitches go away. Expected to apply to every preset and the A1000. | Low: make the per-frame weather update change-driven. Rain, lightning and wind must still update (contract C7 / C8). | Small-medium: bisect the methods, then a dirty-check fix (WO-6). | **Yes, first priority.** Strictly this is a bug fix, not a new method: no visible change. Listed so you know it comes first. |
| N7 | **Far-viewport decimation or lower resolution** | Measured weakly: far GPU is only 0.8-1.5 ms. Rendering it every 2nd frame (UPDATE_ONCE re-arm probe) moved GPU by -0.2 ms and did not reduce the reported render CPU. Half resolution was not probed separately. | Arch / horizon shimmer or lag during fast turns. | Medium. | **Not supported by the measurements yet.** Revisit after N6, because the far viewport's CPU appears to be driven by the same per-frame updates. Low / Medium resolution scale (matrix) is enough for now. |
| N8 | **Terrain sector merging / draw-call reduction** | Near terrain is the largest GPU item (3.2-5.4 ms) and 0.8-1.2 ms CPU; 1.4-2.9k draws. No direct probe of merging yet. | Seams, culling granularity, edit invalidation. | Large. | Measure first (shadow-pass and main-pass draw split, Terrain3D vs proxies), then decide. Not proposed for adoption now. |
| N9 | **GPU-built macro shadows** | The macro map costs ≤ 0.3 ms at runtime; the CPU build is about 200 ms once at load. | - | Large. | Not supported: the gain is a one-off 200 ms at load. Fold into load-time work instead. |
| N10 | **Shader warmup to remove hitches** | 0-4 pipeline compiles per minute with warm caches, so no measurable hitch source. Cold caches are unmeasured; the Phase C background shader warmup already exists. | - | Small (measurement). | Measure a cold-cache first minute (fresh profile) before proposing anything. |
| N11 | **Async compute** for clouds / shadows | Not supported: the 4070 at Very High 1080p is CPU-bound, and the A1000 is bound by raw GPU work that async does not reduce. Godot 4.8-dev7 does not expose a separate compute queue for compositor effects. | High (engine work). | Large. | Not proposed. |
| N12 | **Main-thread streaming budget for flight** (tree / terrain installs time-sliced against a per-frame ms budget; move tree placement to workers) | Flight 72 m/s today: node process 11 ms median, BiomeDecorations dominant. Target ≤ 3 ms brings flight from 36 to about 60 fps on the 4070. | Pop-in and coverage lag at high speed (the altitude coverage invariant applies). | Medium-large. | Yes. This is what makes the 4070 "stable", so it is arguably a fix rather than a new method; listed here because it changes streaming behaviour visibly. |

## 7. Work orders (ordered; sized for implementer / lead)

Priority order: WO-0, **WO-6 (LocalWeatherEffects, the largest and cheapest win)**, WO-1, WO-2, WO-4, WO-5, WO-3, WO-7, WO-8, WO-9. Every gate is a measured A-B on the 4070 using the committed harness (WO-0). The A1000 checks are flagged **[A1000: user]** and are run by the user. Use one profile label per task, `-Cache reuse`, and prune at the end.

1. **WO-0 Harness (implementer, small).**
   - Commit the capture harness as `tools/quality_preset_capture.gd` with a `.ps1` runner (`-Label -Preset -Res|-Maximized -Poses -Labels -Cache`).
   - It already has: the A-X-A labels, coast / 30 km / storm poses, the flight leg, `WarmStallProbe` integration, the escape / drawer input guard, per-window `state` with pause detection, and screenshots.
   - Add an `--a1000` mode with the power gate from `shadow_harness_common.gd`.
   - Gate: reproduces section 2.1 within ±0.5 ms (forest, coast).
2. **WO-1 Contract test (implementer).**
   - `tools/quality_preset_contract_test.gd` (GPU) plus the headless half (section 1).
   - It must FAIL today on C3 (haze off), which proves it detects the violation.
   - Gate: fails on HEAD for C3 only; passes after WO-2.
3. **WO-2 Preset ownership fixes 1, 2, 4, 7, 11, 12, 13, 15 plus Ultra in the menu (implementer; lead review).**
   - No value retuning yet. Clouds keep today's values by moving them into the Very High tuple, so Very High is pixel-identical.
   - Gates:
     - Contract test passes on all 5 presets.
     - Very High 1080p forest / coast within the noise floor of section 2.1 (identical values).
     - Screenshots of Very High unchanged (visual-golden-check, 4070 local baseline).
4. **WO-3 Matrix retune (lead designs in steps, implementer applies).**
   - Order: clouds (1), shadows (9, 8, 10 without the heightfield), water SSR / FFT, trees, rain / lightning, far scale, SSAO.
   - Gates per step:
     - 4070 A-B at forest / coast / 1 km / night / storm.
     - Very High stays ≥ today's quality.
     - High / Medium / Low show monotonic cost.
     - **[A1000: user]** Medium at forest / coast with power logged.
5. **WO-4 Allocation and GC (lead investigates, implementer fixes).**
   - Run `dotnet-trace` (the `menu_warm_stall_probe.ps1 -Trace` path) on stationary, first-minute and flight runs.
   - Rank the allocators: expected SunshineCloudBridge `RefreshWeather` / far-weather publishing (Variant / Array marshalling per call), BiomeDecorations, TerrainLodCoordinator, GlobalTerrainLod3.
   - Then trial N5 if approved.
   - Gates:
     - Stationary hitches > 25 ms ≤ 2 per minute (today about 30).
     - Stationary allocation ≤ 2 MB/s.
     - Flight allocation ≤ 50 MB/s.
6. **WO-5 Flight / first-minute streaming budget (lead; deep if the coverage invariant fights the budget).**
   - Time-slice tree and terrain installs and deferred mesh / collider installs against a per-frame budget (N12 if approved; the budget alone is a fix).
   - Gates:
     - Flight 72 m/s at 150 m: wall p95 ≤ 20 ms on the 4070 Very High.
     - 240 m/s: p95 ≤ 25 ms.
     - `terrain_altitude_lod_flight_test` invariant still passes.
     - First 30 s after load ≥ 50 fps median.
7. **WO-6 LocalWeatherEffects per-frame cost (lead, then implementer; do this FIRST, it is independent of the preset work).**
   - In `RingLocalWeatherEffects._Process` (`scripts/RingLocalWeatherEffects.cs:360-410`, `.RainFog.cs`), bisect which call costs 5.5 ms of frame setup plus 3.2 ms of far-viewport CPU. Use a temporary debug early-return per call, driven by the harness `proc_off` / custom labels: `PublishVegetationWind`, `UpdateRainHaze` / `PublishRainHazeFrame`, `UpdateParticleMotion`, `UpdateSnowParticleProfile`, `UpdatePrecipitationTransform`, `UpdateWetness`, `UpdateLightning`.
   - Make the offending write change-driven.
   - Preserve: rain / snow / lightning / wind behaviour (contract C7 / C8; `rain_precipitation_profiles_test`, `shared_lightning_*_smoke`, `vegetation_wind_state_test`, `rain_haze` GPU tests).
   - Gates:
     - Frame setup ≤ 1.5 ms stationary at forest / coast / night.
     - Far-viewport render CPU ≤ 1.5 ms.
     - 4070 Very High 1080p wall ≤ 11.5 ms at forest.
     - Stationary hitches per minute halved or better.
     - Storm pose still shows rain haze and lightning (screenshots plus diagnostics).
8. **WO-7 Heightfield default N1 (after B1-B3; lead).**
   - Gates:
     - The shadow-revamp plan's gates.
     - 4070 A-B at all 7 poses, the 30 km regression included (≤ +0.2 ms).
     - **[A1000: user]** B4.
9. **WO-8 FSR2 / adaptive resolution in presets N2 and cloud N3 / N4 (implementer, after approval).**
   - Gates:
     - 4070 1440p forest / coast at Very High ≤ 14.5 ms wall if adopted there.
     - **[A1000: user]** Medium 1080p (or the agreed resolution) GPU ≤ 14 ms at forest / coast / storm.
10. **WO-9 Final acceptance.**
    - Gates:
      - 4070: Very High at 1080p and maximized, all 7 poses plus both flights. Wall median ≤ 14.5 ms, p99 ≤ 20 ms, ≤ 1 hitch per minute stationary, flight 72 m/s p95 ≤ 20 ms.
      - **[A1000: user]** Medium: the same poses at the agreed resolution, power-gated (P0 / 35 W).

## 8. Questions for the user

**Answered (2026-10-24, user):**
1. FSR may be used to reach the targets, on any preset.
2. Physical haze is the default on every preset.
3. N1, N2, N3, N4, N5 and N12 are approved. N6 is a bug fix and needs no approval.
4. Fast flight (240 m/s) is inside the 60 fps target.
5. Targets are for a Release build. Measure and report how much the Debug build costs on top of it.
6. The A1000 is unavailable for now. Skip every [A1000: user] gate and keep it listed as pending.

The original questions:

1. What resolution is the A1000 Medium 60 fps target at (1920x1080 native, or 1600x900 as in earlier rankings)? Does an FSR2 internal resolution (N2) count?
2. Haze model for the presets: Legacy, or Physical once calibrated (the haze plan's open calibration decision)? Physical costs +0.5-0.85 ms on the 4070.
3. Which of N1-N5, N6 and N12 do you approve for adoption? N7-N11 are not recommended now.
4. Is flight at 240 m/s (hyper tier) inside the 60 fps target, or only walking and the 72 m/s tier?
5. Should the targets hold for the editor's Debug C# build (how you play today), or for an exported Release build? Release CPU numbers will be lower.
6. Can you run the [A1000: user] checks after WO-3 and WO-8 (undocked, 35 W)?

## Appendix: runs behind these numbers (4070, 2026-10-24)

Raw output is in `.godot/quality_presets/<run>/` (report.json, stall_frames.csv, stall_detail.txt, console.log, power_log.csv). These folders are pruned by `tools/prune_test_output.ps1`.

| Run | What | Notes |
|---|---|---|
| `vh_1080` | Very High 1080p, 7 poses × 18 labels A-X-A, flights 72 and 240 m/s | Editor open for the first-minute trace, forest and coast |
| `vh_1080_b` | Clean first minute, forest proposal labels, frame-setup labels | World paused by a pause-menu keypress after about 7 min; later windows discarded |
| `vh_1080_c` | Coast proposal labels, flights | Forest windows paused (discarded); LiveTuningDrawer opened mid-run, so coast / flight CPU is contaminated (GPU deltas only) |
| `vh_max` | Maximized 2560x1369, 7 poses, 3 labels, flight | Clean |
| `med_1080` | Medium 1080p, 7 poses, 4 labels, flight | Clean |
| `vh_setup`, `vh_weather`, `diag_bridge*` | Frame-setup owner search | Clean; LocalWeatherEffects reproduced twice |

Scratch harness (move into tools/ in WO-0): `C:\Users\Kamron\AppData\Local\Temp\claude\c--Projects-Ringworld\51bfbfb7-1c03-4e63-9d2c-bddaef37ebe5\scratchpad\qp\` (`quality_baseline_capture.gd`, `run_qp.ps1`, `analyze_report.py`, `analyze_probe.py`).
