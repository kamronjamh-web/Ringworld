# Performance ranking: RTX A1000 laptop (2026-10-15)

Measurement only; nothing was changed. Raw data, harness and per-run results stay on the measuring machine (scratchpad `perf_rank/`).

## Setup

- Engine and build: Godot 4.8-dev7, Vulkan Forward+, C# Debug build.
- Display: 1600x900 window, native resolution, FXAA, Medium preset. VSync off and no fps cap.
- Settings: the user's own preferences (grass maxed out, 8192 shadow atlas). Clouds use the factory `.tres` (see below).
- World: a fresh Certified `surface_runtime_generation_v1` (gen-17 v5) world in the real game scene.
- Method: A-B-A ablations, at least 300 frames per window (400 frames in the clean runs).
- Clean runs: `ablate_spawn4`, `ablate_spawn5`, and the first half of `ablate_poses`. Earlier runs were taken during GPU throttling, or while a leftover editor game instance was running, and are treated as noisy.
- Noise: deltas under about 2 ms are noise.

## Laptop GPU throttling

After about 45–60 minutes of sustained load, the platform drops the GPU from P0/35 W to P5/20 W, and the memory clock falls from 5501 to 810 MHz. Frames become about 3.3x slower: spawn goes from 42 to 142 ms, and high altitude reaches about 115 ms. The driver reports "SW Thermal Slowdown + SW Power Cap" while the GPU core sits at about 59 °C. Grass/tree streaming fills keep 4–9 threads busy for about 1–3.5 minutes after every teleport or pose change (not while stationary; see correction below), which adds chassis heat during heavy travel.

## Why the game tops out at about 30 FPS

The cost is real, not a VSync snap or a limiter: VSync on or off gives the same frame time, and nothing sets `max_fps`. There are two layers:

1. **GPU (healthy state):** about 44 ms at spawn, about 40 ms at the coast, about 25 ms near emitters and when airborne.
2. **CPU floor of about 20–23 ms on the main thread:** this caps the game near 45 FPS even when the GPU is nearly idle.

| Main-thread component | Cost |
|---|---|
| `_process` | 5.3 ms |
| Physics | 1.7 ms |
| **RenderingServer frame setup** | **8.2 ms** |
| Main viewport render CPU | 2.9 ms |
| **Far-ring SubViewport render CPU** | **4.8 ms** |

## Ranked systems (healthy GPU)

| # | System | Spawn | Elsewhere |
|---|---|---|---|
| 1 | Sunshine volumetric clouds | GPU −25.8 ms (raymarch 10.6, cloud shadow 9.8 per dispatch) | Coast −21; high altitude 86 of 115 ms (throttled) |
| 2 | Grass rendering (392k roots, tile cap 1018/1018 full) | GPU −9.6 | under 2 ms airborne |
| 3 | Directional sun shadows (8192 atlas, 12 km range) | GPU −6.8 | −9.5 to −10 near emitters and airborne |
| 4 | Resolution-dependent passes | 50% scale saves 12.6 ms GPU | −7.7 to −8.3 |
| 5 | RenderingServer frame setup (CPU) | 8.2 ms | 7.4–8.1 ms |
| 6 | Far-ring outer SubViewport | GPU 2.6 + render CPU 4.8 + script 2.6–3.7 (weather atlas about 2 ms) | similar |
| 7 | Shadow atlas 8192 → 4096 | GPU −3.4 | – |
| 8 | Grass/tree initial fill after arriving (not idle churn) | 4–9 threads for 1–3.5 min after each teleport; 0 builds when truly stationary | oscillating ±0.75 m: 228 grass builds/min (hysteresis gaps) |
| 9 | Main-thread scripts | `_process` about 5.3 ms total | – |
| 10 | Terrain3D surface | −1.4 to −2.6 | – |
| 11 | SSR | −1.1 | – |
| 12 | Physics | 1.7 ms | – |
| 13 | Realtime sky radiance | −0.5 | – |
| 14 | Water FFT | about 0 inland | 0.44 ms GPU at the coast |
| 15 | Within noise | trees (distant at spawn), global terrain, emitters, weather, minimap, HUD, SSAO, AA, GPU markers, perf monitor | – |

**Memory:**

- VRAM: about 1.3–1.5 GB, of which the shadow atlas is 128 MiB.
- RAM: working set 3.1–3.3 GB.
- GC: about 10 MB/s of managed allocation, with a gen2 collection every 15–20 s. This is a likely hitch source but was not profiled.

## Clouds in detail

In editor and dev builds, `GameplayCloudDefaults.ResolvePath` (`scripts/ui/GameplayCloudDefaults.cs:90`) loads `res://resources/clouds/gameplay_cloud_defaults.tres`. Exported (template) builds read the `user://` file instead. The factory file overrides the graphics preset:

| Setting | Factory `.tres` | Code default |
|---|---|---|
| `RaymarchSteps` | 328 | 128 |
| `LightingSteps` | 30 | 8 |
| `GpuShadowVerticalSamples` | 64 | 4 |
| Near shadow | 1024 px / 40 km / 10 Hz | – |
| `CloudResolutionScale` | 0 (native) | native in every preset, Low included (`RingGraphicsQualityController.cs:615-621`) |
| `NearCloudRangeExtensionMeters` | 92 km | – |

**Where the cost goes:**

- **Raymarch** (`SunshineCloudsCompute.glsl:1075`): every in-cloud step runs a nested sun-light march of up to 30 samples, at full resolution, every frame.
- **Near cloud-shadow map** (`SunshineCloudShadowCompute.glsl:305`): 1024² × 64 samples, about 67M density evaluations per dispatch. It costs 8–11 ms on about 45% of frames, which causes a recurring frame-pacing spike.
- **Unmarked work:** turning Sunshine off saves 21–26 ms, while the GPU markers only account for about 16 ms. The remainder is likely compositor copies, history/depth resolves, and shadow sampling in other shaders.

**Existing grave-key knobs, measured at spawn:**

| Change | Saving |
|---|---|
| Half-resolution raymarch | −5.2 ms |
| `RaymarchSteps` 64 | −5.4 ms |
| Near shadow 512 px / 5 Hz | about −1.7 ms (noisy) |
| Cloud shadow generation off | −5.8 ms |

**Cloud levers, best payoff first:**

1. **Laptop-tier values** (steps about 128, `LightingSteps` 8–12, shadow vertical samples 8) in `ApplySunshineCloudQuality` (`RingGraphicsQualityController.cs:608-640`) and the factory `.tres`. Estimated −8 to −12 ms, and it removes the 10 ms spike. Low to moderate visual risk.
2. **Half-resolution raymarch** on the laptop tier. −5.2 ms at spawn, more at altitude. Moderate ghosting risk.
3. **Amortize the near shadow map over several frames** (scheduler at `RingSunshineCloudBridge.cs:1785-1800`). −3 to −4.5 ms average and no spike. Low risk.
4. **Shorter rays:** range extension, max step, and an altitude-aware step budget. Moderate risk.
5. **Checkerboard raymarch.** High effort.

## Other levers

- Grass back toward the defaults (115 m radius, 18k instances, 128 tiles; `RingStandingGrassLayer.cs:65-83`), and no grass shadow casting. Up to −9.6 ms GPU.
- Native-profile the 8.2 ms RenderingServer frame setup. This is the biggest part of the CPU floor.
- Sun shadow distance (`world_size_launcher.gd:1528`) and atlas size (`project.godot:56`). −3 to −7 ms.
- Far-ring viewport update rate, including the weather atlas (`RingScaledFarViewport.cs:500`). −2 to −5 ms CPU.
- Add hysteresis to grass/tree streaming (cap competition, tier downgrades, tree cache keep band, translation-only rebase). See the correction section below.
- FSR2 / render scale on the laptop tier (`RingLodPauseMenu.cs:850-868`). −5 to −12 ms.
- Realtime sky (−0.5 ms) and SSR (−1.1 ms).

## Startup log entries

These are warnings with C# backtraces, not exceptions:

- The native voxel mesher DLL is not built, so the managed fallback is used. This matters for the cave/smooth backend.
- A Terrain3D deprecation warning (`RingTerrain3DSurface.cs:164`).
- A spawn collision-settle timeout on slow startups. It falls back benignly.
- RID and ObjectDB leak warnings at exit. These come from the harness quitting fast.

None of these affects steady-state performance.

Not measured: caves/smooth backend, hitches and the flight route (skipped), and native per-pass GPU timings.

## Correction (2026-10-15, churn investigation)

The "540 grass / 850 tree builds per minute while stationary" figure was wrong. Truly stationary runs (90–180 s, user prefs and defaults, three poses) produced 0 grass builds and 0 tree builds. The profiling reports were captured within ~20 s of a teleport, inside the 60 s telemetry history, so they measured the initial streaming fill: about 1018 grass tiles and 1798 tree tiles. That fill takes 84–221 s with the user's maxed grass prefs and about 62 s with defaults.

The real issue is missing hysteresis when the player moves:

- **Grass** (`RingStandingGrassLayer.cs`):
  - The cap truncation leaves zero safe movement (517–519, 1081–1082), so tiles swap at the cap ring.
  - Tier boundaries use raw distance (511, 1092) and rebuild on any tier change (533–535, 553).
  - Downgrades rebuild even though tiers are nested subsets.
  - A ±0.75 m oscillation causes 228 builds/min, and 72 of 76 re-added tiles are byte-identical.
- **Trees** (`RingBiomeDecorationLayer.TreeStreaming.cs`):
  - The cache cap of 768 (:128) is smaller than the desired set of ~1798, so tiles are evicted the moment they leave.
  - Every floating-origin rebase forces a full replan and a GPU refresh (199–201, 256).
  - The "Trees build" telemetry also counts disk-cache loads (1045–1051).

Fix designs and the invariants that the new grass system must follow are recorded in the session notes (churn investigation). In short:

- add fast, drop slow, with hysteresis H = clamp(tile/2, 2, 8) m;
- downgrades filter existing tiles and never rebuild;
- a tree keep band of R + 1 tile, with cap = max(setting, desired + 2×perimeter), about 1 MB;
- a translation-only rebase shifts the GPU sources instead of replanning;
- split the telemetry into generated vs disk-loaded;
- counter tests for stationary (0 builds), small moves and oscillation (at most one cycle), rebase (0), and cell crossing (bounded by the perimeter).
