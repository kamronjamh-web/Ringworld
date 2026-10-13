# Underground Mode + Extended Cave LOD — Implementation Spec

## Status (2026-10-13)

| Phase | State |
|---|---|
| P1 detection core, P2 settings + drawer | Done (commit `8171c81`) |
| P3a CPU participants, P3b render participants, P3c lighting + proxies | Done (commit after `8171c81` on `cave-perf-underground-mode`) |
| P4 cave LOD profile (coarse radius 2, ~4 km) | Next |
| P5 fog coupling | Not started |
| P6 benchmark + human review | Not started |

Implementation notes that differ from the original spec below:
- Probe sample budget default is 24 (not 160), and the probe is skipped above `ExitDepthMeters`.
- Participants live in `scripts/Underground/Participants/`; the controller drives them each frame. The far viewport is suspended only in the hide tier. "Far terrain" and "audio/weather" are single groups.
- Hide tier and shadow suspension require the atmosphere's `underground_lighting_darkness` to be 1 and sun energy ~0 on the previous frame (`HideOrderingViolations` must stay 0).
- Cave ambient (0.16, 0.20, 0.30) @ 0.035 and fog colour (0.05, 0.07, 0.11) are untuned first guesses.
- Smokes: `tools/underground_mode_smoke_test.gd`, `tools/underground_mode_settings_smoke_test.gd`, and `-- --underground-mode-roundtrip-smoke-test --smooth-terrain-backend` (the flag keeps the Certified preset on the smooth backend).
- Pre-existing failures unrelated to this work: `smooth_terrain_pilot_smoke_test.gd`, `sunshine_menu_smoke`, `sunshine_shadow_toggle_smoke`, `distant_rain_curtains_smoke`, `rain_surface_behavior`.

## Cave roadmap and decisions

Agreed order of cave action items:
1. Underground mode + longer cave LOD (this document).
2. Cheaper cave composer math (per-column hoisting, 4^3 brick narrow band). Est. Supermassive 6 s -> 2.5-3 s.
3. Remove install hitches (12-24 ms): collision build off the main thread or time-sliced.
4. Skip more uniform chunks: octant subdivision of the interval proof; later the deferred fine 1 m chunk skip.
5. Port caves to `surface_runtime_generation_v1` (Terrain3D heightmap surface, generator v17). Digging from the surface into caves is REQUIRED, so the heightmap-to-voxel handoff (holes + seam) is part of the port.
6. POI system: modular megastructure kit (Blender modules with sockets, carve SDFs, deterministic placement replacing chamber anchors). Loosely Halo-ring inspired, partly to mostly running (active lighting, gravity lifts, hard-light bridges, maintenance drones), kept original. Open questions: POI scale (room / cavern / both) and who models.
7. Natural cave mouths (reuses the item 5 handoff).

User decisions for this feature: D1 truly dark designed caves; D2 faint cave ambient; D3 hide far terrain below 128 m with openness 0; D4 underground coarse radius 2.

Scope: `scenes/VoxelRingIntegrationTest.tscn` (smooth backend, launched by `scripts/world_size_launcher.gd`), designed so the same controller works under `surface_runtime_generation_v1` (Terrain3D) once caves are ported. Working-tree state (uncommitted perf work) treated as current. All line refs are to the current working tree.

---

## 0. Key findings that shape the design

1. **Existing "underground" is the structural cavity only, not the designed caves.**
   `RingUndergroundEnvironmentController.IsUnderground` is true only for `[StructuralBottom, TerrainBottom=-8192)` (`scripts/Voxel/Integration/RingUndergroundEnvironmentController.cs:58-66,167-176`). Designed caves live in the terrain domain 24 m – 7,680 m below the local surface (`CAVE_AND_DEEP_STRUCTURE_IMPLEMENTATION_CHECKLIST.md` §7). So in every designed cave today `CurrentSkyAccess == 1`, direct sun/sky ambient are full strength, and darkness relies on shadow maps (sun shadow max distance 4,000 m, `VoxelRingIntegrationTest.tscn:117-128`).
   Consequence: **hiding surface LOD meshes can leak sunlight into caves** (they are shadow casters). Sun/sky must be faded by the enclosure blend *before* any shadow caster is hidden.
2. `HasOpenSkyColumn` (`RingUndergroundEnvironmentController.cs:122-134`) is an exhaustive 1 m column scan from -8192 to surface — fine for the cavity, far too expensive as a per-frame cave probe. Density convention: `density > 0` = air, `<= 0` = solid (also `RingVoxelExplorerController.cs:2376-2405`).
3. `IsAuthoritativeCaveVoid` (`RingVoxelExplorerController.cs:2376`) is a body-sized 6-probe void test for save/spawn safety, private; useful precedent for probe style but not an enclosure signal.
4. **Cave LOD distance is governed almost entirely by the coarse tier.** With the stitched clipmap active (scene enables near+medium+bridge+coarse, `VoxelRingIntegrationTest.tscn:270-284`):
   - Medium radii are **ignored** — fixed 4x4x4 clipmap (`SmoothTerrainGrid.cs:1560-1562`).
   - Bridge tiers 4/8/16/32 m are **fixed** 4x4x4 shells (`SmoothTerrainGrid.cs:1811-1820`), i.e. 32 m tier spans ~4 km.
   - Coarse (2,048 m sectors, 64 m spacing) honours `CoarseVisualRadius*` (`SmoothTerrainGrid.cs:1920-1944`); radius 1 guarantees only ≥2,048 m to the far edge in the worst case (focus at sector edge). A 6 km cavern needs radius 3 to guarantee ≥6,144 m.
   - Stitched mode, bridges and coarse are all gated by `IsCoarseUndergroundFocus()` (`SmoothTerrainGrid.cs:1946`, `:2237-2242`) which is depth ≥ `CoarseUndergroundActivationDepthMeters` (256) **with no hysteresis**; flipping it clears all bridge/coarse presentation and switches medium layout (full rebuild).
   - Any change to radius knobs triggers `RefreshDesired()` (`SmoothTerrainGrid.cs:1066-1083`); shrinking removes coarse entries immediately (`RemoveCoarseEntry`, `:1934`) — this is the thrash vector on toggle.
5. **Precedents for "suspend, don't free"** exist via fast-travel/altitude logic: `RingTerrainProxySector.SetFastTravelSuspended` + `SetTransitVisibility` (`RingTerrainProxySector.cs:753-791`) driven by `RingTerrainLodCoordinator.ApplyFastTravelTierPolicy` (`RingTerrainLodCoordinator.cs:824-835`); tree altitude suppression (`RingBiomeDecorationLayer.cs:266-276`); sun-shadow suspension reasons (`RingGraphicsQualityController.LocalShadows.cs:40-62`).
   Several existing "Enabled=false" paths **free** state and must NOT be reused: Water FFT disposes its provider (`RingWaterFftController.cs:148`); grass `SuspendStreaming` resets/retire renderers (`RingStandingGrassLayer.cs:914-930`); Sunshine `SetProviderActive(false)` releases shadow textures and hands local-cloud ownership back to the legacy renderer (`RingSunshineCloudBridge.cs:489-520`); toggling `RingScaledFarViewport.Enabled` breaks staged startup / owned SubViewports (`RingCertifiedCloudDiagnosticCaptureDirector.cs:384`).

---

## 1. Detection: `RingUndergroundModeController` (single owner)

New files (namespace `RingworldTechDemo.Underground`, folder `scripts/Underground/`):

- `UndergroundEnclosureStateMachine.cs` — **pure C#, no Godot nodes** (unit-testable headless).
- `UndergroundEnclosureProbe.cs` — pure ray-march over an `IUndergroundProbeSource`.
- `IUndergroundProbeSource.cs` — backend seam:
  ```csharp
  public interface IUndergroundProbeSource {
      bool IsAvailable { get; }
      ulong Revision { get; }                       // edit revision + domain identity hash
      bool TryGetSurfaceHeight(double around, double across, out float height);
      bool TryGetDensity(double around, double vertical, double across, out float density); // >0 air
  }
  ```
  - `SmoothUndergroundProbeSource` — `VoxelChunkGridManager.TerrainSampler` + `SmoothTerrainGrid.CaptureLodSurfaceEditSnapshot().GetSurfaceHeight` for surface; `SmoothTerrainGrid.TryQueryEditableDensityAtCanonicalPosition` (`SmoothTerrainGrid.cs:1027`) for density; `Revision = SmoothTerrainGrid.EditRevision`.
  - `Terrain3DUndergroundProbeSource` (stub now) — surface from `RingTerrain3DGameplay.SampleCollisionHeight` (`RingTerrain3DGameplay.Streaming.cs:248`); density = `vertical < surface ? -1 : +1` until caves are ported, then the cave field. Controller never references a backend type directly.
- `RingUndergroundModeController.cs` — Node in scene (`UndergroundMode`, sibling of `UndergroundEnvironment`), `ProcessPriority = -110` (before `UndergroundEnvironment` at -100 and DaySideAtmosphere).

### Signal
- **Depth** `d = surfaceHeight(cameraColumn) - cameraVertical` (camera = explorer canonical + head offset 1.55 m).
- **Sky openness** `o ∈ [0,1]`: 17 rays from the camera — 1 vertical, 8 at 45° elevation, 8 at 10° elevation (catches horizontal tunnels toward a mouth). March step 2 m for first 32 m, then step = clamp(dist/8, 2, 16) m, max `ProbeDistanceMeters` (default 512). A ray **escapes** when a sample is in air (`density > 0`) *and* above `surfaceHeight(sampleColumn) + 4 m`; it **blocks** on the first `density <= 0`. `o = escaped / 17`.
- Budget: round-robin, `ProbeSampleBudgetPerFrame` (default 160 density samples); a full sweep completes in ~3–6 frames. Cache sweep result keyed by (camera 2 m cell, `Revision`).
- Thin-plug miss (<2 m roof) yields a false "open" → stays in Surface mode (fail-safe direction for performance; lighting authority in the structural cavity remains the exhaustive check).

### State machine (hysteresis + blend)
States: `Surface`, `Entering`, `Underground`, `Exiting`. Outputs: `Mode`, `EnclosureBlend` (0..1, smoothstep of linear ramp), `Openness`, `DepthMeters`, event `ModeChanged(old,new)`, and `SuspensionActive` (true only when `Mode==Underground && EnclosureBlend>=0.999`).

| Param (persisted) | Default |
|---|---|
| EnterDepthMeters | 24 (matches weather deep shelter, `RingWeatherAmbience.cs:345`, and the 24 m shallow cave band) |
| ExitDepthMeters | 12 |
| EnterOpennessMax | 0.06 (0 of 17 rays) |
| ExitOpennessMin | 0.18 (≥3 rays) |
| EnterHoldSeconds | 1.0 |
| ExitHoldSeconds | 0.15 |
| BlendSeconds | 1.0 |
| DeepHideDepthMeters | 128 (see §2 tiering) |

Rules:
- Enter when `d ≥ EnterDepth && o ≤ EnterOpennessMax` continuously for EnterHold → ramp blend 0→1. **Suspend at blend = 1 (late)**.
- Exit when `d ≤ ExitDepth || o ≥ ExitOpennessMin` for ExitHold → **resume all participants in the same frame (early)**, then ramp blend 1→0. Lighting/fog ramp masks the 1–2 frame resume.
- **Forced instant exit** (blend=0, synchronous resume): noclip speed > `FastTravelSpeedThresholdMetersPerSecond` (750), focus jump > 64 m in a frame (teleport/floating-origin rebase), spawn settle / loading active, probe source unavailable, master toggle off.
- Debug override `ForcedMode { Auto, Surface, Underground }` (not persisted) for smokes/menu.

The existing `RingUndergroundEnvironmentController` is unchanged (cavity lighting authority). DaySideAtmosphere's effective sky access becomes `min(CurrentSkyAccess, 1 - EnclosureLightingWeight * EnclosureBlend)` — **decision D1**.

---

## 2. Surface-system inventory and suspension mechanisms

Participant pattern: `IUndergroundParticipant { string Group; void SetUndergroundSuspended(bool suspended); string DescribeState(); }` implemented by small adapter classes inside `scripts/Underground/Participants/` that call a minimal new API on each system. The controller owns the list; systems never read the controller (except the GDScript atmosphere which reads `EnclosureBlend` like it reads `CurrentSkyAccess` today, `day_side_atmosphere.gd:939-942`). Group toggles come from settings. Cost column: measure with `RingPerformanceMonitor` (CPU scopes via `RingPerformanceTelemetry.MeasureCpu`, viewport GPU via `ViewportGetMeasuredRenderTimeGpu`, `RingPerformanceMonitor.cs:60`; far viewports reported separately); "scope" names the existing telemetry row.

| System (node) | Cost source / scope | Suspension (new API) | Must keep ticking / risks |
|---|---|---|---|
| **Sunshine clouds** (`SunshineCloudsDriver`, `SunshineCloudBridge`) | Compositor raymarch GPU (largest local GPU item) | New `RingSunshineCloudBridge.SetRenderSuspended(bool)`: detach compositor + `driver.update_continuously=false`; keep `Enabled`, ownership publish, shadow textures. Do NOT call `SetProviderActive` (`:489-520`). On resume: reattach, force one history reset (weight 0 for 1 frame). | Temporal history stale → ghosting on resume (reset). `CloudProviderController` must not re-assert while suspended (add a guard). Weather sim continues. |
| `RingVolumetricClouds` (weather source) | Weather sim CPU + local mesh clouds (yielded: `SunshineCloudsPrimaryRenderer=true`) | Do **not** suspend `_Process` — it owns `CloudSimulationTimeSeconds` used by water/weather. Optionally hide local mesh clouds only. | Clock continuity. |
| `LocalVolumetricCloudRenderer` | FogVolume GPU when enabled | Add external flag OR'd into existing `FastTravelSuspended` visibility (`RingLocalVolumetricCloudRenderer.cs:300`). | Temporal reprojection reset on resume. |
| `RingCloudLayer`, `AnalyticGlobalRing` | Usually dormant (SetProcess(false) at init; hidden by far viewport, `RingScaledFarViewport.cs:842-843`) | None needed; verify. | — |
| `TerrainCloudSunVisibility` | CPU trace + GPU gen every 0.5 s | New `Hold` flag: skip generation without `Invalidate` (current idle path invalidates, `RingTerrainCloudSunVisibility.cs:155-158`). | Sun moves while held → validity check (`SunValidityDegrees`) regenerates on resume; acceptable 0.5 s latency during blend. |
| `CloudProviderController` | Provider switching | No suspension; must ignore provider changes while render-suspended. | — |
| `LocalWeatherEffects` (+`LocalStormFlash`) | Rain particles/curtains, lightning | New `SetUndergroundSuspended`: hide particles, freeze emitters, **force flash energy 0**. The 6 km omni flash has no shadows (`VoxelRingIntegrationTest.tscn:623-629`) and lights cave interiors through rock today. | Lightning scheduling continues (audio sync). |
| `WeatherAudio` / `WeatherAmbience` | Audio + per-frame surface queries | Ambience already mutes at deep shelter >24 m (`RingWeatherAmbience.cs:336-348`); add suspend to skip detail emitter/surface queries; thunder: low-pass + −12 dB instead of mute. | Stream phases keep playing (no restart pops). |
| `OutsideAmbience` (gd) / `WaterAmbience` (gd) | Audio | Volume fade by `1 - EnclosureBlend`; stream keeps playing. | — |
| `WaterFftController` | FFT GPU cascades + CPU | New `ExternalDormant` flag forcing demand status `dormant` (scheduler already supports, `RingWaterFftController.cs:193-209`) — never `Enabled=false` (disposes provider, `:148`). | Wind clock keeps advancing (it already does before the Enabled check). Underground lakes: none exist yet; flag D6. |
| `StandingGrass` (GPU grass) | "Grass update (inclusive)" | New hold mode: `Visible=false`, skip planning/builds, **no Reset** (current `SuspendStreaming` resets, `RingStandingGrassLayer.cs:918-930`). | Edits made underground invalidate tiles on resume via existing invalidation path. |
| `BiomeDecorations` (trees) | "Trees update (inclusive)", GPU shadow mirrors | Reuse altitude-suppression branch (`RingBiomeDecorationLayer.cs:266-276`) via external flag, but keep forest colliders and do not reset `_lastAroundCell` unless moved > 1 tile. | Tree colliders near shallow cave mouths. |
| Surface LOD proxies `Lod1..Lod11`, bridge proxies, via `TerrainLodCoordinator` | "Terrain proxy update/build/install", draw calls, shadow casters | Coordinator gains `UndergroundFreeze` / `UndergroundHide` OR'd into `ApplyFastTravelTierPolicy` (`RingTerrainLodCoordinator.cs:824-835`): Freeze → `SetFastTravelSuspended(true)` (stops rebuilds, keeps meshes); Hide → `SetTransitVisibility(true)`. | **Tiered**: Freeze at Underground; Hide only when `d ≥ DeepHideDepth && o == 0` (distant cave mouth would otherwise show a terrain hole). Hide only after sun faded (D1). **Structural sidewall proxies** (`_structuralWallMesh`) own long-range wall visuals in the structural cavity — keep walls visible when `IsUnderground` (cavity). |
| `GlobalTerrainLod3` | "Terrain global LOD update", macro shadows | Hide + skip segment service (new flag); keep morph snapshot. | Macro terrain shadows irrelevant at blend=1. |
| `ScaledFarViewport` | Two SubViewports `UpdateMode.Always` (`RingScaledFarViewport.cs:853,974`) — far GPU rows | New `SetRenderSuspended(bool)`: SubViewports → `UpdateMode.Disabled`, hide composite, pause cloud-mask/weather atlas refresh. Never toggle `Enabled`. | Resume shows last frame for 1 frame; force `UpdateMode.Always` + immediate refresh. |
| `StarLight` / `PlanetLight` shadows | Directional shadow cascades (4 splits to 4–6 km; cave geometry renders into them) | Add suspension reason "underground enclosed" in `RingGraphicsQualityController.UpdateLocalShadowHandoff` (`LocalShadows.cs:40-62`) and planet light in `day_side_atmosphere.gd:847`. Only at blend=1 (energy already 0). | Must resume one frame before blend leaves 1. |
| `DaySideAtmosphere` (gd) | Environment compose per frame | Consumer, not suspended: applies enclosure sky-access + fog coupling (§3). | Must keep running. |
| `RingDayNightCycle` | Cheap | **Never pause** (`Paused`, `RingDayNightCycle.cs:43`) — time of day must advance. | — |
| Sky (`WorldEnvironment`) | Sky shader/radiance | Keep. Optional: lower sky radiance process mode while blend=1. | Visible through mouths. |
| Smooth surface streaming (`SmoothTerrainGrid` surface/collision ownership) | — | Not suspended: same grid owns caves; altitude-aware residency already exists. | — |
| `ScaleAnchors`, `TerrainMinimap`, HUDs | Minor | Leave. | — |

Cave-mouth rule: nothing is suspended until blend=1, and every participant resumes at exit trigger (blend still 1). Freeze-only for depth < 128 m keeps the outside world drawn through any mouth beyond probe range.

---

## 3. Cave LOD profiles (SmoothTerrainGrid)

### Mechanism (no fighting with persisted streaming settings)
Add an override layer, not writes to exported properties:
```csharp
public readonly record struct CaveLodProfile(int CoarseRadiusAround, int CoarseRadiusAcross, int CoarseRadiusVertical,
    int WorkerBonus, float InstallBudgetMilliseconds, int MaxInstallsPerFrame);
public void SetCaveProfileOverride(CaveLodProfile? profile);   // via ICaveLodProfileTarget
```
- Effective getters `EffectiveCoarseRadius*`, `EffectiveMaxWorkers = clamp(MaxWorkers + bonus, 1, ProcessorCount-2)`, etc., used at `SmoothTerrainGrid.cs:1081,1385,1929` and in `ApplyWorkerCountChange`/install loop. Exported values remain the Surface profile and keep the TerrainStreaming drawer authoritative (`GameplayTerrainStreamingDefaults.Apply`).
- **Retain margin**: new `CoarseRetainMarginSectors` (default 1). In `RefreshCoarseVolumetricDesired`, entries outside desired but within (effective radius + margin) are kept resident, hidden, not rescheduled; removed only beyond. Growth reuses existing entries (no regeneration). Same for bridge/medium is unnecessary (fixed shells).
- **Activation hysteresis**: `IsCoarseUndergroundFocus` latches: enter at `depth ≥ CoarseUndergroundActivationDepthMeters`, exit at `depth < Activation - CoarseActivationHysteresisMeters` (default 64). Removes the existing stitched-mode flip thrash at the 256 m boundary.
- Controller policy: apply Underground profile at `Entering` start (gives loads the 1 s head start); revert to Surface profile only after `ShrinkDelaySeconds` (10 s) in Surface mode. Retained sectors make re-entry free.

### Profiles
| Knob | Surface (current) | Underground (proposed) |
|---|---|---|
| Coarse radius around/across | 1 / 1 | 2 / 2 (≥4 km guaranteed); "Max": 3 / 3 (≥6 km) — D4 |
| Coarse radius vertical | 1 (4 slabs = 8 km) | 1 |
| Medium/Bridge | fixed clipmap | unchanged |
| Worker bonus | 0 | +2 (doc warns 16 workers starved main thread, `SmoothTerrainGrid.cs:51`) |
| Install budget / cap | 1.5 ms / 8 | 3.0 ms / 12 (main thread freed by surface suspension) |
| CoarseUndergroundActivationDepth | 256 | 256 (option 64 only when proxies are Hidden — avoids surface duplication; D7) |

### Cost estimates (to be confirmed by §5 perf benchmark)
Coarse sectors ≈ (2r+1)² × (rv+2): r=1 → 36, r=2 → 100, r=3 → 196 (plus across-edge duplicates; structural-bottom/edge culling reduces). Each sector samples a 36³ ≈ 46.7 k density lattice regardless of uniform rejection; only mixed sectors mesh. Work and memory scale ~linearly: r1→r2 ≈ 2.8×, r1→r3 ≈ 5.4× coarse work. Measure per-sector worker ms (`Metrics.MaxWorkerMilliseconds`, benchmark `densityMs`) and bytes (`CaptureCaveLodTelemetry().Coarse.RetainedBytes`, `SmoothTerrainGrid.cs:283`). GPU cost is modest (render-only, 64 m spacing) but adds shadow-cascade load unless sun shadows are suspended (they are at blend=1).

### Fog coupling
In `day_side_atmosphere.gd` (fog compose `:360-372`, underground apply `:422-440`): when `EnclosureBlend > 0`, lerp fog toward `underground_fog_density = ln(1/0.02) / VisibleDistance`, where `VisibleDistance = FogEndFraction × guaranteedCoarseReach` (`r × 2048 m`, published by the controller from the effective profile; default fraction 0.9). Fog color = underground ambient color so the LOD edge fades into darkness; `fog_sky_affect`, `aerial_perspective` → underground values. Lerp by blend, not a mode switch (exponential fog only; no FOG_MODE_DEPTH pop).

---

## 4. Tuning menu + persistence

Follow the TerrainStreaming pattern (`scripts/ui/RingLiveTuningDrawer.TerrainStreaming.cs`, `GameplayTerrainStreamingDefaults.cs`, `GameplayTerrainStreamingSettings.cs`):
- `scripts/ui/GameplayUndergroundModeSettings.cs` — `[GlobalClass] Resource`.
- `scripts/ui/GameplayUndergroundModeDefaults.cs` — `user://gameplay_underground_mode_defaults.tres`, sanitize/clamp, `Set…`, `Apply(RingUndergroundModeController)`, atomic temp-file save, `HasSavedFile`.
- `scripts/ui/RingLiveTuningDrawer.Underground.cs` — `InitializeUndergroundModeTuning()`, `ProcessUndergroundModeTuning(delta)` (0.75 s debounced save, 5 s retry), `FlushUndergroundModeDefaults()`; hook at `RingLiveTuningDrawer.cs:159,210,246`; section in the "Terrain" tab after `BuildTerrainStreamingSection` (`:912`).

Controls:
- Master: Underground mode enabled; Mode override (Auto/Surface/Underground, not persisted); live status label (mode, blend, depth, openness, suspended groups, coarse desired/ready, cave bytes).
- Enclosure: enter depth, exit depth, enter openness max, exit openness min, enter hold, exit hold, blend seconds, probe distance, probe samples/frame, deep-hide depth.
- Groups (toggles): Clouds, Weather FX/lightning, Water FFT, Grass, Trees, Far terrain freeze, Far terrain hide, Far viewport, Sun/planet shadows, Surface audio, Darken sky light in caves (D1 weight 0..1).
- Cave LOD (Underground profile): coarse radius around/across (0–4), vertical (0–3), worker bonus (0–8), install budget ms (0.25–8), installs cap (1–32), retain margin (0–2), shrink delay s (0–60).
- Fog: couple fog to cave distance (toggle), fog end fraction (0.5–1.0).
- Save / Reset buttons.

---

## 5. Validation

### Automated (headless)
1. **`UndergroundModeSmoke`** (`scripts/Tools/UndergroundModeSmoke.cs` + `tools/underground_mode_smoke_test.gd`, prints `UNDERGROUND_MODE_SMOKE_TEST_PASS`):
   - State machine: openness oscillating ±0.02 around both thresholds at 60 Hz for 30 s → ≤1 transition; enter requires full hold; exit within ExitHold; blend reaches 1 in BlendSeconds ±1 frame; forced exit sets blend 0 in one step; `SuspensionActive` only at blend=1; `ModeChanged` fires exactly once per transition.
   - Probe with synthetic `IUndergroundProbeSource` lambdas: sealed roof → o=0; vertical shaft → o>0 (vertical ray escapes); horizontal tunnel to a mouth 200 m away → 10° rays escape; mouth 600 m away → o=0 (documents freeze-only tier); 1 m plug → documented fail-safe "open"; sample budget never exceeded per frame.
2. **Settings smoke** (`GameplayUndergroundModeSettingsSmoke`, mirrors `TerrainStreamingSettingsSmoke`): save/load round trip, clamps, corrupt file → defaults + LastError.
3. **Suspend/resume round trip** (scene-level, `--underground-mode-roundtrip-smoke-test` handled by the controller, like `--underground-environment-smoke-test`): capture each participant's `DescribeState()` + key invariants (proxy active sector counts, grass renderer count, Sunshine shadow texture RIDs, water provider instance identity, far SubViewport update modes, day-night cycle fraction, cloud sim clock); force Underground for N frames → assert suspended state, clock/time advanced, **no instance freed**; force Surface → assert states equal the captured baseline (except clocks advanced) within 2 frames; repeat 20 toggles → no leaked nodes (child counts stable).
4. **Cave profile smoke** (add to `SmoothTerrainRuntimeSmoke.cs` near coarse tests `:441-456`): radius 1→2 increases coarse desired, existing entries keep generation/identity (no reschedule); 2→1 with margin 1 keeps entries resident hidden; 10 rapid toggles → bounded schedule count; activation hysteresis: focus oscillating ±16 m around 256 m depth → no `ClearBridgeVolumetricPresentation`.

### Perf measurement
`--underground-mode-benchmark` (extend the cave viewpoint benchmark, `SmoothTerrainRuntimeSmoke.cs:700-808`, which already finds megacavern/supermassive anchors): at a megacavern viewpoint, run Surface vs Underground profile: time-to-`IsQuiescent` (`SmoothTerrainGrid.cs:521`), coarse resident/visible, `TotalEstimatedBytes`, peak bytes, max install ms. GPU run (non-headless, integration scene, mode forced off/on, 600 frames each after quiescence): mean/p95 frame ms, viewport GPU ms, far viewport GPU ms, draw calls, primitives from `RingPerformanceMonitor`; export via `CaptureSlowdown()` JSON. Acceptance: Underground mode frame time ≤ Surface frame time at the same viewpoint while loading r=2 coarse; quiescence time reported.

### Human checks
- Walk from surface into a shallow cave and back out: no cloud/terrain/water pop at exit; audio fades, no restart.
- Stand in a tunnel looking out a mouth (near and >512 m): outside terrain present (freeze tier).
- Megacavern/supermassive interior: far walls visible, fog fades at LOD edge, no holes at coarse boundary; toggle in/out repeatedly — no rebuild storms.
- Storm overhead while underground: no lightning flash light in cave; thunder muffled.
- Stay underground 2+ minutes, exit: time-of-day advanced, clouds consistent (no ghosting), water waves continue.
- Fast travel (noclip) through caves: forced exit, no frozen surface.
- Tuning: sliders apply live; persisted after restart; Reset restores defaults.
- Structural cavity: sidewalls remain visible at range.

---

## 6. Phasing (implementer-sized, each independently testable)

1. **P1 Detection core** — `IUndergroundProbeSource`, smooth adapter, Terrain3D stub, `UndergroundEnclosureProbe`, `UndergroundEnclosureStateMachine`, `RingUndergroundModeController` node added to the scene, read-only. Test: smoke #1.
2. **P2 Settings + drawer** — resource/defaults/drawer partial with enclosure params, master toggle, mode override, status label. Test: smoke #2 + manual drawer.
3. **P3a Cheap CPU participants** — grass hold, trees hold, TerrainCloudSunVisibility hold, weather ambience/audio, outside/water ambience fades, water ExternalDormant. Test: round-trip smoke (subset).
4. **P3b Render participants** — Sunshine `SetRenderSuspended` + provider guard + history reset, LocalVolumetric flag, LocalWeatherEffects (incl. flash), far viewport `SetRenderSuspended`, GlobalTerrainLod3 hide. Test: round-trip smoke + GPU perf delta.
5. **P3c Lighting + proxies** — DaySideAtmosphere enclosure sky access (after D1), shadow suspension reason, coordinator Freeze/Hide tiers with structural-wall exception. Test: round-trip + cavity lighting capture (`--capture-cavity-lighting`) still passes.
6. **P4 Cave LOD profile** — override layer, retain margin, activation hysteresis, worker bonus; controller applies profiles with shrink delay. Test: smoke #4.
7. **P5 Fog coupling** — atmosphere fog lerp + menu controls. Test: manual + atmosphere smokes (`AtmosphereSettingsSmoke`).
8. **P6 Benchmark + human review** — perf benchmark, tune defaults, update `CAVE_AND_DEEP_STRUCTURE_IMPLEMENTATION_CHECKLIST.md` Gate 4.

---

## 7. Decisions needed from the user

- **D1** Should enclosure darken direct sun + sky ambient inside designed (terrain-domain) caves? Today only the structural cavity is dark; designed caves rely on shadow maps. Hiding shadow-casting surface LODs without darkening will leak sunlight. Recommended: yes (weight 1.0), which visibly changes current cave lighting.
- **D2** What lights a 6 km cavern's far walls? With near-black underground ambient (`day_side_atmosphere.gd:51-53`), far walls are invisible regardless of LOD. Need an underground ambient level / cave fog-glow art decision.
- **D3** Far-terrain tiering: freeze-only at all depths (safe, less GPU win) vs hide below 128 m (bigger win, possible hole through distant mouths).
- **D4** Underground coarse radius default: 2 (≥4 km, ~2.8× coarse work) or 3 (≥6 km guarantee, ~5.4×); or add a 128 m ultra-coarse tier later.
- **D5** Terrain3D backend: when caves are ported, should Terrain3D's own clipmap be hidden/frozen underground (needs a Terrain3D participant)?
- **D6** Underground water bodies: if planned, Water FFT dormancy must become demand-based rather than group-suspended.
- **D7** Lower coarse activation depth (to cover 24–256 m caves) only while far terrain is hidden?

## USER DECISIONS (2026-10-13) — binding
- D1: YES. Enclosure blend fades sun + sky ambient to underground values in designed caves (truly dark). Must happen before any surface shadow caster is hidden.
- D2: Faint cave ambient. Add a tunable dim cave ambient color/energy + fog color so large shapes read as silhouettes; blended by EnclosureBlend; exposed in the Underground tuning section. (Emissive cave features / POI lighting come later.)
- D3: Hide far terrain below 128 m depth with openness 0; freeze-only when enclosed but shallower.
- D4: Underground coarse radius 2 (~4 km).
- D5–D7 deferred.
