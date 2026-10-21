# Phase C P0 measurements: live world at menu altitude (2026-10-21)

Measurement only. No gameplay code was changed. New tooling:

- `tools/menu_altitude_perf_capture.gd` (SceneTree driver), `tools/MenuAltitudePerfProbe.cs` (typed helper), `tools/menu_altitude_perf_capture.ps1` (isolated-profile runner).
- Rerun on another machine (for example the A1000 laptop), from the project root:
  - Load only, cold profile: `.\tools\menu_altitude_perf_capture.ps1 -Label cold1 -Cache none -ScriptArgs '--load-only'`
  - Load only, warm (profile reused): `.\tools\menu_altitude_perf_capture.ps1 -Label cold1 -OutLabel warm1 -Cache reuse -ScriptArgs '--load-only'`
  - Full altitude capture: `.\tools\menu_altitude_perf_capture.ps1 -Label copy1 -OutLabel fly1 -Cache copy -ScriptArgs '--plan=a:s16,a:m,a:s23,b:s16,b:m,c:s16,c:m,c:s23'`
  - Add `--pause-clocks` for the old "clocks paused" comparison. `--cycle=keep|<fraction>` overrides the brightest-daylight auto pick.
  - Edit `-GodotExe` if the console exe is elsewhere. Results land in `.godot/phase_c_p0/<label>/` (`results.json`, PNGs, `console.log`).
- Safety: the runner copies `worlds/slot_N` plus the `gameplay_*.tres` preferences (and with `-Cache copy` the shader/pipeline/ring caches) into `.godot/phase_c_p0/appdata_<label>` and points `APPDATA` there. The GDScript refuses to run unless `user://` is inside a `phase_c_p0` directory. The real saves were not touched (the real `slot_1` mtime is unchanged).

## Setup

- Device: `Vulkan 1.4.341 - Forward+ - Using Device #0: NVIDIA - NVIDIA GeForce RTX 4070` (desktop, 12 GB). All numbers below are from this machine only.
- Godot 4.8-dev7 mono, C# Debug build, editor-binary runtime (so the factory cloud `.tres` applies, see `docs/perf-ranking-2026-10-15.md`). 1600x900 window, VSync off, no fps cap, High preset as loaded from the copied user preferences.
- World: real slot 1 ("World 1", `surface_runtime_generation_v1`, Standard 5000 km / 318 km, seed 1337, `ringworld_surface_weather_lung_v8`), copied. Save spot arc 25563.96 km, across 157.29 km, on the ground (about 7 m).
- Pose: scripted noclip, 16 000 m above terrain (held by Q/E bang-bang with a 350 m deadband; measured mean 15.98 to 16.02 km, 15.85 km stationary after settle), camera pitched 25 degrees down. The Explorer pitch stays 0 and only the Camera3D is tilted, so the level noclip path is not pitched into the ground. Moving = W held at 1100 m/s along +around (noclip warp tier, measured 1100 m/s, 33 km per 30 s window, starting 18 km behind the save spot). Stationary = at the save spot. The 23 km sample is at 23.15 km AGL.
- This is the normal noclip path (key events through `Input.parse_input_event`), not `SetBenchmarkCanonicalPosition`, so there is no forced anchor recenter per frame.
- Day: `RingDayNightCycle` scanned for the highest daylight (fraction 0.2, daylight 1.0), re-applied at the start of each step. Clocks (celestial and cloud) were running in the main run. A short paused-clock run is included below.
- Windows: 20 s stationary, 30 s moving, after 20 s settle (30 s after a variant change). Screenshots are taken after the measured window, since readback stalls the frame.
- Variants are cumulative runtime changes: a = as loaded; b = a + Sunshine `CloudResolutionScale` 1 (half), `RaymarchSteps` 64, `GpuShadowGenerationEnabled` off, then `ApplyTemporalQualityTuning(true)`; c = b + `RingTerrainLodCoordinator.ApplyQualityPreset(Low)` + Terrain3D `GenerationWorkerCount` 1 (via `ApplySurfaceSettings`).
- Grass and trees need no hold. Verified in every sample: `StandingGrass.StreamingSuspended = true` (it suspends above 350 m AGL) and `BiomeDecorations.Visible = false` (hidden above `MaximumViewerAltitudeAboveTerrainMeters` 12 km).
- Metrics: "wall" is the frame-to-frame time of the main loop (the real frame time, since VSync is off). "GPU" is the sum of `viewport_get_measured_render_time_gpu` over the main viewport and the active SubViewports (far-ring viewport, minimap). Timestamped passes only, so unmarked GPU work is not included. "CPU setup/vp/far" come from the game's `RingPerformanceMonitor`. The `Performance.TIME_PROCESS` value did not match the wall time (it showed 45 to 60 ms against 8 ms walls), so it is not reported.

## 1. Cold start to ready (`WORLD_LOADING_REPORT`)

Same slot, loaded through the real `open_saved_world` path (launcher, loader overlay, `disable_3d` while loading). Times in ms.

| Run | Profile | elapsed (ready) | preparing | terrain | surroundings | ready | first_world_frame | frame max |
|---|---|---|---|---|---|---|---|---|
| cold1 | new profile, no shader/pipeline/ring caches | 33 042 | 4 536 | 9 019 | 17 289 | 2 186 | 1 974 | 3 137 |
| cold2 | new profile, no caches | 32 534 | 4 503 | 8 667 | 17 282 | 2 071 | 1 871 | 2 761 |
| warm1 | cold1's profile reused (caches now populated) | 27 329 | 4 466 | 5 604 | 16 758 | 489 | 267 | 2 488 |
| copy1 | user's real shader/vulkan/ring caches copied in | 29 930 | 4 628 | 6 159 | 18 631 | 501 | 281 | 2 608 |
| 3 later loads of copy1's profile (smoke, fly1, paused1) | warm | 27 553 to 28 011 | about 4 460 to 4 740 | about 5 780 | about 16 900 to 17 200 | about 510 | about 295 | 2 450 to 2 660 |

- The OS file cache could not be flushed (no admin, 96 GB RAM), so "cold" here means cold application caches only (no Godot shader cache, no Vulkan pipeline cache). The project files and the slot were in the OS cache for every run. True cold-disk loads will be slower than cold1/cold2; rerun on the laptop after a reboot.
- Ready is about 27 to 28 s warm and 32.5 to 33 s with empty shader/pipeline caches. The extra 5 s of the cold runs lands in `terrain` (+3.4 s) and `ready` (+1.6 s). The slowest frames of those stages show 60 to 210 pipeline compilations.
- `surroundings` (about 17 s) dominates in every run and is independent of caches. Its systems are the far ring (`ScaledFarViewport` "far terrain"), `GlobalTerrainLod3` ("global terrain"), the cloud layer, the tree streamer and the emitter sites. All were `ready: true` at the end; the loader does not say which one held the stage open longest.
- `preparing` (about 4.5 s) is scene load (`VoxelRingIntegrationTest.tscn` about 3.0 s of that) plus instantiate 75 ms, configure 26 ms, resource warmup 0.75 s, attach 0.6 to 0.75 s.
- `terrain` (5.6 to 9 s): saved state restore and the Terrain3D surface startup.
- Largest initializers: `LiveTuningDrawer` 163 ms, `ScaledFarViewport` 85 ms, `GlobalTerrainLod3` 68 ms, `Lod1Terrain2m` 67 ms, `GraphicsQuality` 63 ms.
- Memory at ready: working set 2.8 to 3.3 GB, process dedicated VRAM about 2.03 to 2.10 GB (Windows GPU Process Memory counter), RenderingServer video memory 1.58 to 1.65 GB.
- Startup frames show hitches of 2.4 to 3.1 s (the worst at `terrain`, with `_process` 430 ms to 3.1 s) and 1.8 to 2.5 s at `surroundings`/`ready`. A background loader that renders a menu on top would see those.

## 2. Frame cost at 16 km (RTX 4070, clocks running)

Source: `.godot/phase_c_p0/fly1/results.json`. Wall = real frame time (ms). p95 in brackets.

| Variant | Stationary 16 km wall (p95) | Stationary GPU sum | Moving 1.1 km/s wall (p95) | Moving GPU sum | Stationary 23 km wall (GPU) |
|---|---|---|---|---|---|
| a: as loaded | 7.99 (10.0) | 6.72 | 12.41 (17.0) | 6.89 | 8.05 (3.69) |
| b: a + low-cost clouds | 7.89 (9.9) | 5.00 | 12.23 (16.4) | 5.32 | not run |
| c: b + LOD Low + 1 worker | 7.64 (9.4) | 3.68 | 9.08 (12.5) | 4.22 | 7.62 (3.17) |

| Component (ms, mean) | a stat | a moving | b stat | b moving | c stat | c moving | a 23 km |
|---|---|---|---|---|---|---|---|
| GPU main viewport | 5.21 | 5.53 | 3.66 | 3.98 | 2.34 | 2.87 | 2.25 |
| GPU far-ring viewport | 1.51 | 1.31 | 1.29 | 1.30 | 1.29 | 1.30 | 1.40 |
| CPU RenderingServer setup | 2.97 | 4.27 | 3.09 | 4.28 | 3.10 | 3.48 | 3.11 |
| CPU main viewport render | 1.09 | 1.18 | 1.06 | 1.16 | 0.95 | 0.99 | 0.84 |
| CPU far viewport render | 0.98 | 1.15 | 0.92 | 1.13 | 0.93 | 0.78 | 1.04 |
| Physics | 0.61 | 0.74 | 0.62 | 0.73 | 0.63 | 0.59 | 0.61 |
| Draw calls | 422 | 494 | 420 | 496 | 268 | 292 | 312 |

Paused clocks (`paused1`, stationary 16 km): a 7.60 wall / 6.84 GPU, b 7.36 / 4.99, c 8.09 / 3.52. Clocks running costs nothing measurable.

Memory (process, per sample, stable across variants):
- Working set 2.78 to 3.19 GB (3.25 GB at the end of the run), private bytes 5.0 to 5.5 GB.
- Process dedicated VRAM 2.08 GB (stationary and variants b/c), 2.34 GB in the first moving/23 km samples. RenderingServer video memory 1.12 to 1.85 GB; it grows during motion (1.56 GB stationary to 1.75 to 1.85 GB moving) and shrinks with variant c (1.12 to 1.42 GB).
- nvidia-smi reported 3.3 to 3.6 GB total used against a 1.2 GB idle baseline, which matches the 2.1 GB process figure. GPU clocks stayed at 2835 MHz, temperature 52 to 54 C, no throttle reasons.

Drift check: a repeat of `a:s16` at the end of the run was contaminated (the live-tuning drawer was open in its screenshot, wall 10.1 ms, p99 45 ms, max 366 ms). I did not find what opened it. Treat that sample as invalid. The first and paused-clock runs of `a:s16` (7.99 and 7.60 ms) agree.

## 3. Interpretation

- At 16 km the 4070 is CPU/main-thread bound, not GPU bound. Stationary frames are 7.6 to 8.0 ms (about 125 fps) with only 5.2 ms of main-viewport GPU, and moving frames are 12.4 ms (80 fps) with the same GPU cost. The GPU timestamp sum may undercount (see the perf-ranking note on unmarked work), but the wall time bounds it at 8 ms. This machine has a lot of headroom; the A1000 was 2 to 3x slower in earlier captures, so the GPU numbers need a laptop rerun before any conclusion about the laptop.
- What dominates stationary GPU at 16 km: main viewport 5.2 ms (clouds, terrain, atmosphere, sky) and the far-ring viewport 1.5 ms. What dominates CPU: RenderingServer frame setup (3.0 ms stationary, 4.3 ms moving), then main/far viewport render CPU (about 1 ms each), physics 0.6 to 0.7 ms and the remaining main-thread scripts.
- Motion adds 4.4 ms of CPU: setup +1.3, physics +0.1, draw calls +72 and the rest in script/streaming work (terrain tile and LOD updates while crossing 33 km). This is the largest cost of the live world behind the menu, and it is not GPU work.
- Savings per variant (stationary/moving, wall):
  - b (low-cost clouds): wall -0.1/-0.2 ms (noise), GPU -1.7/-1.6 ms. Wall does not move on this GPU because it is CPU bound. On a GPU-bound machine this is about 1.6 to 1.7 ms of 6.7.
  - c over b (LOD Low plus 1 worker): GPU -1.3/-1.1 ms, draw calls -150/-200, and moving wall -3.2 ms (12.2 to 9.1; p95 16.4 to 12.5). Almost all of the moving CPU saving is the LOD coordinator Low preset (fewer sectors and uploads); the worker count is not separable from this data.
  - c total: GPU 6.7 to 3.7 (-45%), moving wall 12.4 to 9.1 (-27%).
  - 23 km (above the 20 km switch): GPU 3.7 (a) and 3.2 (c). The 20 km systems remove about 3 ms of GPU compared with 16 km at variant a (main viewport 5.2 to 2.25 ms), and the cloud raymarch is no longer visible there.
- The Sunshine cloud cost at 16 km is small on this GPU (a to b is -1.7 ms GPU, against 20+ ms at ground level in the October perf ranking), so the clouds are not the main GPU cost at menu altitude on the 4070. Re-check on the A1000 before changing the plan's "clouds are the main GPU cost" statement.
- Grass and trees cost nothing at 16 km (suspended, verified).

## 4. Visual problems at 16 km

Screenshots are in `.godot/phase_c_p0/fly1/` (`<variant>_<kind>_t<n>.png`; `a_s16_t0` was overwritten by the invalid repeat and shows the open tuning drawer, so use `paused1/a_s16_t0.png` or the `a_m` shots). Key shots: `a_m_t1`, `b_m_t1`, `c_m_t1`, `b_s16_t0`, `c_s16_t0`, `a_s23_t0`, `c_s23_t0`.

- Terrain coarseness: the land near the track is a flat, hazy blue-grey and brown mesh with almost no relief. A jagged sawtooth seam separates the detailed brown land from the flat cyan-white sea/haze at the centre of the view, and a repeating zig-zag edge runs along the far coast. Stipple/moire contour rings are visible on the lower-left land. The coarse far ring (LOD11 and beyond) has low resolution and big flat polygons as seen from 16 km.
- Variant c (LOD Low) is clearly worse: large flat wedge-shaped polygons and hard straight edges cut across the land and sea (`c_m_t1`, `c_s16_t0`), with banding and a visible rectangular tile outline at the bottom. It also drops the distant clouds. It would need review before use behind a menu.
- Cloud handoff: at variant a there are dozens of puffy volumetric clouds with soft shadows. At variant b (64 steps, half-res) the distant clouds disappear (same position and time: `a_m_t1` has many, `b_m_t1` has about 5) and the remaining ones are softer. Variant c shows none or few. At 23 km (`a_s23_t0`, `c_s23_t0`) no clouds are drawn, only dark shadow patches and diamond-shaped tiles on the ground, a hard difference from 16 km (the 20 km handoff).
- Sky and lighting: the sky is a dark navy gradient with a bright haze band at the horizon, and a washed-out cyan haze over the sea. At 23 km the sky turns black with stars. The haze is thick enough that mid-distance land loses colour. The ring arch and planet-side sky is dark.
- Pop-in: flying at 1.1 km/s there is no obvious pop-in in the three stills per run, but stills cannot show it. A video or frame-diff pass is needed to quantify pop-in. The loading hitches in section 1 are a separate issue.
- The HUD and minimap are drawn over everything, so a menu overlay would need to hide them.

## 5. Which cold-start stages could run at a low budget behind the menu

From the stage breakdown (warm, about 28 s):
- `preparing` (4.5 s): scene load, instantiate, attach. Mostly main-thread and synchronous, so it cannot be spread out much. Do it at boot, during the splash. Resource warmup (0.75 s) is already budgeted.
- `terrain` (5.6 s warm, 9 s cold): Terrain3D surface and saved-state restore. Already staged (`UseStagedStartup`) and worker-based, so a lower per-frame budget is feasible, and `GenerationWorkerCount` 1 is plausible here. The cold-only extra (+3.4 s) is pipeline compilation, which should be warmed behind the menu.
- `surroundings` (17 s, independent of caches): the far ring, global terrain LOD, cloud layer, tree streamer and emitter sites all use `StartupComplete` flags and staged work. This is the stage to run at a low budget. Needs per-owner timing first, because the report does not say which system keeps the stage open. At menu altitude trees are suspended anyway (above 12 km AGL), so waiting for them (`BiomeDecorations`) is wasted time behind the menu.
- `ready` (0.5 s warm, 2.1 s cold): spawn settle and first world frame; not needed behind the menu if the camera is the cruise pose.
- The 2.4 to 3.1 s hitches during `terrain` and `surroundings` would be visible behind a rendered menu. They need splitting or deferring.
- With `disable_3d` set, loading is cheaper than it would be with the menu renderer active, so the real background mode could be slower than these numbers.

## Limitations

- One machine (RTX 4070), one world, one weather state. Single run per variant (no repeats except the invalid drift check), so differences under about 0.3 ms wall are noise.
- Variants were applied cumulatively at runtime in one process, not as separate launches. Settling time after each switch was 30 s.
- The OS file cache could not be dropped for the cold loads.
- GPU timestamps cover only the viewports Godot reports. Frame setup/compositor copies are in the CPU numbers.
