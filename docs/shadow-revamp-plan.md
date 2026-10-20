# Shadow revamp plan (all shadow and occlusion types)

Status: draft 2026-10-19, plan only. Nothing is implemented. Needs user sign-off on the decisions below before S4.

## Handoff (2026-10-19)

**State:** plan written; no code, settings or shader changed. The inventory below was re-verified against the working tree (HEAD 62fae19 plus the user's uncommitted `resources/clouds/gameplay_cloud_defaults.tres` edit).

**Update (2026-10-19, 4070 desktop):** S1 done (sun map MaxDistanceMeters 150-20000 + CascadeCount 2/4, planet light enable/distance/cascades; Shadows tab "Sun map" and "Planet light" groups; defaults unchanged; far viewports positional atlas 0, no measurable VRAM change in this build; cloud trials still need A1000 captures). S0 harness committed (9002cb8; A1000 baseline still to capture). S3 done: `materials/ring_shadow_compose.gdshaderinc` + all receivers ported, pixel-identical (`tools/shadow_compose_identity_gpu_test.gd` 44 cases max diff 0; `terrain_surface_include_regression` 0 px; S0 quality captures within run-to-run noise), "Shadow debug view" in Shadows > Debug (`ShadowDebugMode` 0-5). Kept inconsistencies for S5: Terrain3D/cards/voxel/ocean proxy lack the hub term; voxel has no macro; oceans no planet macro; trees use the product path with sun-only cloud; standing grass has no direct cloud; ocean 0.8 cloud-cap order differs. Pre-existing intermittent failure (also at HEAD before S3): `shadow_settings_smoke_test` `portableHighClamped=False` (passes on some runs). S2 committed (147dc85). S4a done: height clipmap lattice, CPU fill (Debug, 2 workers: L0 1.4 s, L1 1.15 s, recentre strips ~0.15-0.17 s), aperture carving, GPU upload + max-mips (bit-exact vs CPU); Texture2DArrayRD + sampler2DArray globals verified in 4.8-dev7; mip-1 cell covers texel-centre u = uv*N - 0.5 in [2x,2x+2). Nothing runs in gameplay yet. S4b done: build march shader vs double-precision reference (height_shadow_reference_test, 136 gates; lit/shadow 100% on ridge, seam, cliff, cone, bore, 2 km wall, real mountain; low-sun tips within 1 texel; curvature shortens along-ring shadows). Magnitude gate carries a half-texel slope allowance on hard cliffs. Cost (4070, whole level): noon L0 0.23 ms / L1 0.17 ms; 3 deg sun L0 3.3-3.9 ms / L1 0.9-1.2 ms -> S4c must slice adaptively at low sun and feed a tight outsideMax (pyramid max + 200 m) or the A1000 misses the 0.3 ms gate. Cap-unresolved and window-exit-unresolved share G < 0.

**Update (2026-10-19, 4070 desktop, S2):** S2 done. Producer crossfade lives in `SunshineClouds.gd` (runtime `RDShaderSource` pass, no reimport needed): with `GpuShadowCrossfadeEnabled` the receivers bind a per-cascade display texture D; on publish D is resampled once into the new map's frame (the bridge pushes the receiver frame/offset every frame via `set_gpu_cloud_shadow_display_frame`), then D = mix(prev, new, t) each frame over `GpuShadowCrossfadeSeconds` (0 = publish interval). Off = receivers bind the front map exactly as before (`sunshine_shadow_slice_equivalence_test` byte-identical). Receivers: small-angle series for atan/sin/cos (exact fallback beyond 0.05 rad) and far UVs derived from near coordinates through new global `ring_cloud_shadow_far_from_near` (ring-aligned cascades only). Menu: Shadows tab > Cloud shadow state. New test `tools/cloud_shadow_crossfade_test.gd`.

**S0 status (2026-10-19, tooling built and validated on the RTX 4070 desktop; baseline NOT yet captured on the A1000):** the harness exists, no production behaviour changed. Everything run here is stamped `machine: rtx4070 (non-authoritative)`. What exists:
- `tools/shadow_attribution_test.gd` (new, the `--shadow-only` idea became its own tool; the haze matrix tool stays untouched): labels `sun_map_off, planet_map_off, cloud_shadow_gen_off, cloud_receivers_off, macro_off, tree_shadows_off, casters_500m, pssm2_500m, atlas_2048, baseline_repeat` (baseline is the A window) at poses `ground_horizon, forest_noon, emitter_rim, dawn_5deg, alt_1km, alt_12km, night_planet_lit, eclipse_hub` (clocks and cloud time paused, DaySideAtmosphere auto-exposure off). Every label is an existing runtime switch or a node property the test restores: `SetShadowTuningOverride` (Enabled / TerrainMacroEnabled / AtlasSize), `atmosphere.planet_light_cast_shadows`, `SunshineCloudBridge.GpuShadowGenerationEnabled` / `CloudShadowReceiversEnabled`, `BiomeDecorationLayer.TreeSunShadowDistanceMeters = 0` + `MaximumTreeSunShadowBatches = 0`, `casters_500m` = `SetTerrainShadowCoverageMeters(500)` with the graphics controller's `_process` paused, `pssm2_500m` = sun `directional_shadow_mode` PSSM 2 + max distance 500 + split 0.2. Each window records a state snapshot, and a label whose snapshot equals baseline is flagged `NO EFFECT at pose` (e.g. `planet_map_off` by day). Coast has no fixture (TODO); the S0 sentence "cave mouth / overhang" and a dedicated 2/5/10 km hill pose also have none (the depth bands of the ground poses cover distant hills).
- Timing: `scripts/diagnostics/RingPerformanceGpu.cs` gained an opt-in `ConfigureEngineTimestamps` (default off, inert: the readback path is unchanged unless the harness turns it on) and `tools/ShadowTimingBridge.cs` collects its markers (`CloudShadowNear/Far`, `TerrainCloudSunVisibility`, ...) plus per-viewport `viewport_get_measured_render_time_gpu/cpu` and `get_frame_setup_time_cpu()`. **Godot 4.8-dev7 only exposes `vp_begin_<viewport rid>` / `vp_end_<viewport rid>` engine timestamps (one GPU interval per measured viewport); there are no shadow / opaque / sky regions and no frame-profiling API (`RenderingServer`/`RenderingDevice` list checked)**. Shadow cost therefore comes from label deltas (A-X-A-X-...-A, odd rounds reversed) and the custom markers, not from an engine shadow-pass timestamp. The harness disables the scene's `RingPerformanceMonitor` (it re-configures the same timestamp bridge every frame and drains its events).
- Power log: `nvidia-smi ... -lms 500` redirected to `power_log.csv` next to `report.json` (the `-f` flag buffers until exit). Machine-aware rule: on the A1000 a window must be P0 / enforced 35 W / memory 5300-5700 MHz for every sample, else it is `invalid` and excluded from the per-label medians; on any other GPU the same fields are recorded and nothing is rejected.
- `tools/shadow_quality_capture.gd` (+ `tools/shadow_depth_capture_effect.gd`, a test-only depth CompositorEffect): per pose full / sun_off / all_off captures, visibility `(full - sun_off) / (all_off - sun_off)`, shadowed fraction / mean visibility / penumbra estimate per depth band, still and slow-pan sequences with a flicker/shimmer metric (shadows-on minus shadows-off). Poses: the attribution set plus `emitter_bore_noon, dawn_3/8/15deg, dusk_3/8/15deg, eclipse_totality`.
- `tools/shadow_harness_common.gd`: shared boot, poses, time-of-day search, power-log parsing and stats. Each tool has a README block at the top (flags, A1000 rule).
- Output (never committed): `.godot/shadow_baseline/<machine>/attribution/<stamp>/{report.json,power_log.csv}` and `.../quality/<stamp>/{baseline.json,*.png}`; `--out=<dir>` overrides.

**Run on the A1000** (editor closed, no other Godot process, check `nvidia-smi` shows 35 W / P0 first; the dock may cap at 20 W, run undocked or verified):
`Godot_console.exe --path . --resolution 1920x1080 --script tools/shadow_attribution_test.gd` (about 2 h: 8 poses x 3 rounds x 21 windows x ~12 s = ~103 min of sampling plus pose setup and idle waits; add `--rounds=2 --seconds=8` for ~1 h), then `Godot_console.exe --path . --resolution 1920x1080 --script tools/shadow_quality_capture.gd` (about 25-30 min). `--quick` runs each tool in a few minutes for a sanity check. Read `report.json -> poses.<pose>.summary.<label>.delta_ms` (median over rounds of X minus the neighbouring A windows; `baseline_repeat` is the noise floor) and the `power` verdict per window.

**Next steps:** capture the A1000 baseline with the two tools above, then S1-S2 (settings and cloud-shadow wins), then S3 (shared composition include, behaviour-identical), then the new systems S4-S7. Each step is independently testable and committable.

**Open decisions for the user** (details in "Decisions needed"): near shadow-map range and atlas size; heightfield height source vs voxel edits; planet-light near map at night; commit the working-copy cloud defaults; contact-shadow look on grass; trees past 480 m receiving terrain shadows; optional terrain-scale AO.

**Gotchas:**
- *Measurement.* Main-viewport run-to-run noise is 3-6 ms, larger than most single savings here. Use per-pass GPU timestamps (engine + `RingPerformanceGpu` markers), A-B-A-B-A rounds, >= 8 s per window after streaming has settled, and an `nvidia-smi` log next to every run. Run undocked, or with the dock verified at the 35 W limit (the WD19TB once caused a 20 W cap); nothing else running. Reject any window not at P0 / 35 W / memory clock ~5500 MHz. Grass/tree fills keep 4-9 threads busy for 1-3.5 min after a teleport (`perf-ranking-2026-10-15.md`), so wait for zero builds in flight before sampling. Dynamic Boost to 50 W is unconfirmed: record `enforced.power.limit`, never assume it.
- *Stale numbers.* `docs/perf-ranking-2026-10-15.md` measured 1600x900, an 8192 atlas, 12 km sun range and the old 1024 px / 10 Hz / 64-sample cloud shadow. The live A1000 config is now Medium (6 km, 4096, Hard filter, 30 m first split) and the cloud shadow is adaptive and sliced. Re-measure in S0 before trusting any saving in this file.
- *Sunshine reimport.* Godot does not reimport `.glsl` files whose include changed, and `--headless --import` may not recompile Sunshine shaders. If S2 or S4 touches `SunshineCloudShadowCompute.glsl`, `SunshineClouds.gd` buffer layouts, or `materials/ring_terrain_cloud_sun_shared.gdshaderinc` (included by `SunshineCloudsCompute.glsl`): delete `.godot/imported/SunshineClouds*.glsl-*.md5`, touch the `.glsl` files, run a windowed `--path . --import` (segfault at exit is harmless). New compute passes should compile at runtime from `RDShaderSource` like `scripts/Atmosphere/RingAtmosphereLuts.cs` to avoid this.
- *Terrain3D DLL.* A Godot run or import can delete the tracked `addons/terrain_3d/bin/~libterrain.ringworld.windows.debug.x86_64.dll`. Restore with `git show HEAD:<path> > <path>` before committing.
- *Editor.* Never run Godot (tests, imports, captures) while the user's editor is open; one Godot process at a time.
- Auto-exposure is on in visual-test captures; disable it for matched comparisons. The root viewport must stay LDR 2D (tools read root screenshots).

## Goals

- Every shadow type at least the current quality (concrete checks in S0), with a measurable GPU saving on the A1000 and a CPU saving in RenderingServer frame setup.
- Pay for shadow detail where it is visible: sharp near field, cheap and stable mid/far terrain shadows, no 6-12 km directional cascades.
- One shared shadow-composition function for all receivers (like `RingHaze` did for haze), so near map, contact, terrain heightfield, macro and cloud terms combine identically on terrain, proxies, Terrain3D, voxel/smooth terrain, ocean, trees, grass, emitters and POIs.
- Quality gains where cheap: grass gets shadows (contact), trees beyond 480 m and distant hills at 2-10 km get correct terrain shadows, cloud shadows stop popping on publish.
- All new controls in the grave-key menu with the existing validated-override and persistence conventions (AGENTS.md).

## Current state (re-verified 2026-10-19)

Live A1000 config: laptop tier, so the launcher picks preset 1 = **Medium** (`scripts/world_size_launcher.gd:1688`). User shadow overrides (`user://gameplay_shadow_defaults.tres`): `AtlasSize 4096, DepthBits 16, Filter 0 (Hard), NearCascadeMeters 30, SunStabilizationEnabled false`.

| # | Type | Where | Current behaviour | Cost (A1000) |
|---|---|---|---|---|
| 1 | Sun directional map (StarLight) | `RingGraphicsQualityController.cs:708` (Medium), `:407` (max distance clamped to >= 1000 m), `.Shadows.cs:137-190` | Godot PSSM 4 splits (default mode, never changed). Medium: 6 km, 4096 atlas, splits 30 m / 360 m / 1680 m / 6 km, blend splits, fade 0.88. High: 12 km / 8192 / SoftLow. Suspended below the local horizon (-18/-16 deg hysteresis), at sun energy < 0.001 (eclipse totality) and underground (`.LocalShadows.cs:47-72`) | **Measured 10-15 (stale config):** -6.8 ms spawn, -9.5 to -10 ms airborne/emitters at 8192/12 km; 8192->4096 -3.4 ms. **Estimate now:** 2.5-5 ms GPU + an unmeasured share of the 8.2 ms CPU frame setup |
| 2 | Planet light (night) | `scripts/day_side_atmosphere.gd:1342-1366`, scene `VoxelRingIntegrationTest.tscn:138` | Second directional, 4 splits (0.04/0.14/0.42), 6 km, casts when energy > 0.001 (planet-lit night only). Shares the directional atlas: during twilight overlap each light gets a quarter. Not in the grave-key menu | Unmeasured; est 1.5-3 ms on planet-lit nights |
| 3 | Terrain casters into the map | `RingTerrainProxySector.cs:5283-5326` (ShadowsOnly copies), `RingTerrainLodCoordinator.cs:87,590-631` (`TerrainShadowCoverageMeters` = active light range), `RingGraphicsQualityController.LocalShadows.cs:92-117` | Every proxy tier inside the active light range casts; Terrain3D window (384 x 2 m = 768 m) casts with its default; hull walls/roof cast (`RingTerrainGlobalLod.cs:1520,1571`), voxel chunks (`VoxelChunkNode.cs:201`) and smooth terrain (`SmoothTerrainGrid.cs:2766`) cast | Part of #1 |
| 4 | Trees | `RingBiomeDecorationLayer.cs:112-113` (480 m, 128 batches), `.GpuShadows.cs:51-128` | LOD0/1 cast via authored-LOD2 shadow-only mirrors, LOD2 via billboard; impostors (LOD3+) and trees past 480 m cast nothing and receive no terrain-map shadow beyond 6 km. Suppressed during fast travel/altitude | Part of #1 |
| 5 | Grass | `RingStandingGrassLayer.cs:820,1721` | Casts none. Receives map + cloud (cards: per-vertex cloud, `ring_grass_cards.gdshader:157`) | 0 |
| 6 | Emitters / POIs | `EmitterBoreLayer.cs:93` (bore casts), stand-in/beam/far layers Off; eclipse hub meshes Off | Bore pit (~185 m deep, aperture is a shader discard in terrain) casts in the map; nothing else | Part of #1 |
| 7 | Cloud shadow producer (Sunshine) | `RingSunshineCloudBridge.cs:69-88,2104-2125`, `SunshineClouds.gd:870-950`, `SunshineCloudShadowCompute.glsl:530-580` | Near map: HEAD 1024 px / 10 Hz, **working copy 512 px / 5 Hz / 4 slices** (uncommitted user edit), 40 km radius, adaptive 250 m steps (8-64 samples, clear-air skip). Far: 128 px / 100 km / 2.5 Hz. Slices publish atomically (hard swap, no crossfade); wind drift is compensated between publishes (`ResolveGpuShadowUvOffset`, bridge `:2152`) | **Measured 10-15 (old 1024/64-sample):** generation off -5.8 ms; 512/5 Hz ~-1.7 ms (noisy). **Estimate:** HEAD config 2.3-3.1 ms/frame, working copy ~0.3-0.4 ms/frame; far < 0.05 ms |
| 8 | Cloud shadow receivers | `materials/ring_cloud_shadow.gdshaderinc:74-190` | Per receiver: ring-curved projection with `atan`, `sqrt`, `sin`, `cos` (twice in the near/far blend band), 1-2 taps. Per fragment on Terrain3D/proxy/global/voxel/ocean; per vertex on trees/grass. Haze P9 reuses the camera-column value | Unmeasured; est 0.2-0.5 ms |
| 9 | Analytic far cloud field | `materials/ring_far_cloud_field.gdshaderinc`, `ring_terrain_proxy.gdshader:66-84` | Procedural value noise, unrelated to Sunshine weather, max'd with the map on proxies/global/ocean | Small, unmeasured |
| 10 | Macro terrain shadow (sun + planet) | `RingTerrainMinMaxPyramid.cs:300`, `RingTerrainGlobalLod.cs:136-142,1794`, `.PlanetShadows.cs`, `materials/ring_terrain_macro_shadow.gdshaderinc` | Whole-ring CPU mask ~4 km/texel (Certified 7,670 x 78), Medium strength 0.36, fades in 3.5-6.5 km, 10 steps / 96 km, walls analytic. Held during the eclipse hub event | CPU worker 190-245 ms per rebuild (4070); GPU ~0 |
| 11 | Terrain shadowing clouds | `scripts/RingTerrainCloudSunVisibility.cs`, `scripts/render/RingTerrainCloudSunVisibilityGpu.gd`, `ring_terrain_cloud_sun_shared.gdshaderinc` | Solar-visibility field for Sunshine and far cloud receivers | Has GPU marker `TerrainCloudSunVisibility`; unmeasured |
| 12 | Cloud self-shadowing | `SunshineCloudsCompute.glsl` light march (`LightingSteps` 30 in the dev .tres) | Owned by `docs/cloud-perf-methods-plan.md` Method 1 (light-map bins, not implemented). Out of scope here; S2 keeps the producer compatible with it | Several ms (cloud plan) |
| 13 | Point/omni lights | `EmitterBoreLayer.cs:96-99` (260/420 m), `RingLocalWeatherEffects.cs:583-591` (lightning, 6 km) | No shadows. No player lights exist. Positional atlas unused | ~0 (atlas VRAM unverified) |
| 14 | Eclipse / hub event | `EclipseHubLightModel.cs:63-93`, `day_side_atmosphere.gd:446` | Hub light is the StarLight re-aimed toward the camera zenith; map follows it, macro rebuilds held | Part of #1 |
| 15 | Underground / caves | `RingGraphicsQualityController.LocalShadows.cs:68`, `day_side_atmosphere.gd:575-584` | Fully enclosed: sun map suspended, sun/planet energy scaled by access; cave mouths rely on the map from voxel/smooth casters | - |
| 16 | Ocean / water | `ring_ocean_proxy.gdshader:187-200,610`, `ring_ocean_global.gdshader:56,158` | Receives map + cloud + macro + analytic wall shadow via `combine_ring_shadow_visibility` | Part of #8 |
| 17 | AO | `scripts/RingAmbientOcclusionController.cs:13-24,87-104` | Godot SSAO on (1.2 m, intensity 2.15, light affect 0.38); SSIL/SDFGI/VoxelGI off; far viewport SSAO/SSIL off (`RingFarViewportColorPipeline.cs:37`) | Within noise (perf-ranking) |
| 18 | Far viewport (1/1000) | `RingScaledFarViewport.cs:1020-1034,1118-1132` | Far sun/planet lights `ShadowEnabled = false`; far terrain gets macro + far cloud + analytic | 0 for shadows |
| 19 | Edits / digging | `RingTerrainGlobalLod.cs:1183` (edit -> macro rebuild), pyramid dirty cells | Map is live geometry; macro mask rebuilds on edits | - |

Existing composition: `combine_ring_shadow_visibility` (`ring_cloud_shadow.gdshaderinc:55-72`) takes min(map, 1-cloud, 1-terrain), with `shadow_overlap_darkening` blending toward the product. `ring_terrain_direct_visibility` (`ring_terrain_lighting.gdshaderinc:41-83`) already shares it between Terrain3D and grass cards. 14 shaders call `sample_local_cloud_shadow` directly, each with its own glue.

**Godot 4.8 limits that shape the design:** directional cascades are re-rendered every frame, with no caching and no per-cascade update rate (only positional lights are cached); the only way around that would be a custom shadow pass that re-draws the scene ourselves, which is not practical without engine access to instance lists. Max 4 splits, fitted by bounding sphere. One directional atlas is shared by all shadowed directional lights. Custom shaders and compositor effects cannot sample the directional atlas, so composition must happen in `light()` from `ATTENUATION`. No hardware ray tracing. Usable: `Light3D.shadow_caster_mask` (verify in 4.8-dev7), the Forward+ depth prepass and `EFFECT_CALLBACK_TYPE_PRE_OPAQUE` compositor effects (run after the prepass, before the opaque pass), `Texture2DRD` globals.

## Design

Near-to-far ownership of sun (and planet) visibility:

| Range | Term | Source |
|---|---|---|
| 0-~1.5 m screen-space | contact | Bend-style screen-space shadows from the prepass depth (S6); covers grass, small props, crevices |
| 0-~400 m (decision 1) | geometry | Godot map, **PSSM 2**, trees/POIs/bores/overhangs/caves + terrain casters only within range |
| 0-~8 km | terrain | GPU heightfield shadow-height clipmap (S4), ring-curvature aware, rebuilt on sun step / recentre / edits, sliced over frames (optional 3rd level to ~32 km) |
| > ~8 km | terrain | existing macro mask (4 km/texel) and analytic walls |
| everywhere | cloud | Sunshine near/far maps with a crossfaded display texture (S2) |

Heightfield output per texel is a *shadow-line height* `H(x)` (the lowest height at that column that sees the sun; RG16F: height above the floor, blocker distance). Any receiver at height `h` is occluded by terrain when `h < H`; penumbra width grows with blocker distance (`d * tan(sun angular radius + softening)`). This makes one texture valid for terrain, trees, grass, water, POIs and flying objects, and needs no knowledge of the receiver mesh. Terrain receivers use the heightfield's own surface height plus a slope bias (no LOD/geomorph mismatch acne). Curvature: a straight sun ray's clearance above the curved floor is `h0 + d tan(e) - d^2 / (2R)` (6 m at 8 km, 100 m at 32 km for R = 5000 km), evaluated in double-precision canonical coordinates on the CPU side and float local offsets on the GPU.

Shared API (fixed in S3, extended in S4-S6):

```glsl
// materials/ring_shadow_compose.gdshaderinc
struct RingShadowTerms { float geometry; float contact; float terrain; float cloud; }; // visibilities, 1 = lit
RingShadowTerms ring_shadow_terms(vec3 world, float attenuation, vec2 screen_uv, float receiver_height_offset, uint flags);
float ring_shadow_sun_visibility(RingShadowTerms t);   // combine(min(geometry, contact), cloud, terrain) via combine_ring_shadow_visibility
// flags: RING_SHADOW_TERRAIN_RECEIVER, RING_SHADOW_PER_VERTEX (terrain+cloud only), RING_SHADOW_PLANET, RING_SHADOW_NO_CONTACT
```

Per-vertex/per-instance callers (trees, grass, impostors) get terrain + cloud once per instance in the GPU culling compute (mirroring `RingVegetationHaze`), geometry + contact per fragment. Debug view: global `ringworld_shadow_debug_mode` (0 off, 1 sun visibility, 2-5 single terms).

## Steps

Order: S0, S1, S2, S3, S4, S5, S6, S7, S8. Savings are estimates until S0's harness measures them.

### S0 - Measurement harness + quality baseline (no production change)
- `tools/shadow_attribution_test.gd` (or a `--shadow-only` matrix in `tools/terrain_render_attribution_test.gd`, which already has `directional_shadows_off`, pose matrix and rounds): labels `baseline, sun_map_off, planet_map_off, cloud_shadow_gen_off, cloud_receivers_off (strength 0), macro_off, tree_shadows_off, casters_500m (coverage), pssm2_500m, atlas_2048, baseline_repeat`; poses `ground_horizon, forest_noon, emitter_rim, coast, dawn_5deg, alt_1km, alt_12km, night_planet_lit, eclipse_hub` (paused clock, fixed weather seed, fixed sun).
- Timing: extend `scripts/diagnostics/RingPerformanceGpu.cs` to also record Godot's engine timestamps (list the 4.8-dev7 names first; expect shadow / opaque / sky regions), plus existing `CloudShadowNear/Far`, `TerrainCloudSunVisibility`; per-viewport `viewport_get_measured_render_time_gpu/cpu` and `RenderingServer.get_frame_setup_time_cpu()`. A-B-A-B-A, >= 3 rounds, 8-10 s windows, median + IQR per window.
- Power log: `nvidia-smi --query-gpu=timestamp,pstate,clocks.gr,clocks.mem,power.draw,enforced.power.limit,temperature.gpu,clocks_event_reasons.active --format=csv -lms 500` written next to the report; the report marks windows invalid when off P0 / 35 W / ~5500 MHz memory.
- Quality: `tools/shadow_quality_capture.gd`, auto-exposure off, three captures per pose with no shader change: full, sun energy 0, all shadows off. Visibility ~ (full - sun_off) / (all_off - sun_off) per pixel. Poses: shadow edge at 10/50/200 m (bore rim, tree trunk, cliff), distant hill bands at 2/5/10 km (depth-band masks), dawn/dusk 3/8/15 deg, cave mouth and overhang, emitter bore interior at noon, night planet light, eclipse totality and hub, 1 km and 12 km altitude, coast/water. Also 120-frame still and slow-pan sequences for flicker/shimmer.
- "At least the same quality" contract per pose: shadowed-area fraction per depth band within 3% of baseline (or higher where a gain is intended and approved); penumbra width at 10/50 m not wider than baseline + 1 px, at 200 m not wider than baseline; no new acne or peter-panning in review; still-camera flicker and pan shimmer <= baseline; cloud-shadow per-publish delta <= baseline (S2 target: half). User signs off side-by-side images.
- Saving: none. Deliverable: baseline JSON + images on the A1000 (goldens are machine-specific).

### S1 - Low-risk settings wins
- Make planet-light and sun-map structure tunable: new validated override keys in `RingGraphicsQualityController.TryValidateShadowOverride` (`MaxDistanceMeters`, `CascadeCount` 2/4, `PlanetShadowsEnabled`, `PlanetShadowDistanceMeters`, `PlanetCascadeCount`), lower the 1000 m clamp at `RingGraphicsQualityController.cs:407` to ~150 m, forward planet values to `day_side_atmosphere.gd`.
- Cloud: commit (decision 4) the near 512 px / 5 Hz / 4 slices; try far 2.5 -> 1 Hz and adaptive step 250 -> 300-350 m against S0 captures.
- Check VRAM for unused positional shadow atlases on the main and far viewports; set size 0 where no shadowed positional light exists.
- Menu: Shadows tab "Sun map" group (max distance, cascades) and "Planet light" group; Clouds tab already has map resolution/rate/slices. Persistence via `GameplayShadowDefaults` (old files load unchanged).
- Tests: `tools/shadow_settings_smoke_test.gd` / `ShadowSettingsSmoke.cs` (new keys, invalid values, old-file load), `sun_shadow_ownership_smoke_test`, `terrain_lod_shadow_coverage_smoke_test`.
- Saving: 0.1-0.5 ms (cloud) if the working copy is not yet counted; larger only if the user accepts shorter planet range.

### S2 - Cloud shadows: crossfade, cheaper receivers
- Producer: on publish keep the previous front map; a tiny compute pass resamples the previous map into the new map's frame once (centre shift + wind offset are known in the bridge), then each frame writes `display = mix(prev, new, t)` over the publish interval into the texture receivers already bind. Receivers unchanged; rate can drop to ~2 Hz without pops. New pass compiles from `RDShaderSource`.
- Receivers: replace `atan`/`sin`/`cos` in `ring_cloud_shadow_coordinates` with a small-angle series (|angle| <= 0.02 rad over 100 km), and derive far UVs from near coordinates when the cascades share axes (one projection instead of two in the blend band).
- Files: `SunshineClouds.gd`, `RingSunshineCloudBridge.cs`, `materials/ring_cloud_shadow.gdshaderinc`, `GameplayCloudDefaults.cs`, `RingLiveTuningDrawer.cs` (Clouds tab: "Shadow crossfade" toggle, "Crossfade seconds").
- Tests: `sunshine_shadow_slice_equivalence_test` (crossfade off is byte-identical), new `cloud_shadow_crossfade_test` (max per-frame visibility delta, no lag > 1 interval), `CertifiedCloudShadowProjectionSmoke` (series vs exact < 0.5 m at 40/100 km), `ring_cloud_shadow_receiver_cache_smoke_test`, `terrain_storm_shadow_parity_smoke_test`. Reimport gotcha if the shadow `.glsl` changes.
- Saving: producer 0.1-0.2 ms at 2 Hz, receivers 0.1-0.3 ms (est); quality gain (no pop).

### S3 - Shared composition include (behaviour-identical)
- `materials/ring_shadow_compose.gdshaderinc` with the S3 API (geometry + cloud + existing macro terms, contact/heightfield = 1); port Terrain3D, proxy, global, voxel, ocean x2, trees x3, grass x3, emitter stand-in, scale anchor. Keep `combine_ring_shadow_visibility` semantics and `shadow_overlap_darkening`.
- Add `ringworld_shadow_debug_mode` (project.godot global, default 0).
- Tests: S0 captures pixel-identical with debug 0; `tree_lighting_shadow_gpu_test`, `terrain_near_shadow_gpu_test`, `terrain_macro_shadow_uv_gpu_test`, `terrain_storm_shadow_parity_smoke_test`.
- Saving: 0 (enabler).

### S4 - GPU heightfield shadow producer (not wired)
- `scripts/Shadows/RingTerrainHeightShadow.cs` + `materials/shadows/ring_height_shadow_build.comp`. Height clipmap, 2 levels: L0 1024^2 at 4 m (+-2 km), L1 1024^2 at 16 m (+-8 km), toroidal, centred on the camera's ground footprint; level texel size grows with altitude.
- Heights from `RingTerrainSampler` on worker threads (strips on recentre, full fill after teleport, falls back to map + macro until ready), plus emitter aperture carving (`EmitterSiteApertures` radii; bore walls become cliffs), analytic structural walls via `RingTerrainShadowGeometry`, and edit deltas per decision 2.
- Build: per texel march toward the sun with a max-height mip, curvature-correct clearance; output RG16F (shadow-line height, blocker distance); sliced rows per frame, atomic front/back publish with the S2 crossfade. Triggers: sun step (default 0.1 deg, ~1.3 s at the 4500 s day), recentre, edit dirty rects, planet light at night; hold while `EclipseHubLighting.HoldMacroShadows`.
- Globals `ringworld_height_shadow_l0/l1` (`Texture2DRD`) + params, declared in `project.godot`. GPU marker `HeightShadow` in `RingPerformanceGpu`.
- Tests: `tools/height_shadow_reference_test.gd` vs a double-precision CPU reference (`RingTerrainShadowGeometry.SampleStraightRay`): >= 99% texel agreement outside penumbrae, low-sun curvature cases, ring wrap seam, recentre continuity, build-time budget.
- Menu (Shadows tab, "Terrain height shadows"): enable, L0 texel, levels, range, sun step, slices per frame, penumbra softness.
- Saving: costs ~0.1-0.3 ms average (sliced build), est.

### S5 - Wire heightfield to receivers, shrink the sun map
- Compose include adds the terrain term for all receivers; macro takes over beyond L1 with a crossfade; near caves: ignore the heightfield for receivers more than ~2 m below its surface inside the map range (map is authoritative for cave mouths and overhangs).
- Sun map: PSSM 2, max distance per decision 1 (default 400 m), fade 0.8; `TerrainShadowCoverageMeters` follows (proxy casters only inside range); tree shadow distance <= map distance.
- Tests: S0 quality contract at all poses; `terrain_shadow_lod_gameplay_test`, `terrain_lod_shadow_coverage_smoke_test` (new coverage), `sun_shadow_stability_smoke_test`, `terrain_shadow_hitch_test`, eclipse captures, flight across the main/far handoff.
- Saving: 1.5-3.5 ms GPU at ground, 3-5 ms at altitude (est from the stale -6.8/-10 ms), plus fewer caster culls in frame setup (est 0.5-2 ms CPU).

### S6 - Screen-space contact shadows
- `scripts/render/RingContactShadows.gd` compositor effect at `PRE_OPAQUE`: Bend-style wavefront march on the prepass depth toward the sun, R8 at internal render resolution, published as global `ringworld_contact_shadow`; sampled in the compose include for the primary light only. Length ~1.5 m world, fades out by ~150 m; off while sun shadows are suspended. Verify grass cards write prepass depth.
- Menu: enable, length, thickness, strength, fade distance, grass strength.
- Tests: `contact_shadow_gpu_test` (box on plane: length within 10%, no shadow from sky/far viewport depth), FSR2 and render-scale cases, S0 grass poses.
- Cost: +0.4-0.8 ms (est). May allow atlas 4096 -> 2048 (decision 1, ~-0.3-0.8 ms).

### S7 - Night/planet light, per-instance vegetation shadows
- Heightfield builder serves the planet direction when sun shadows are suspended; planet near map PSSM 2 at <= 250 m or off (decision 3); twilight overlap keeps the planet on macro only.
- Grass/tree instances get terrain + cloud visibility in the GPU culling compute (next to `RingVegetationHaze`), with per-vertex fallback.
- Tests: night planet-lit poses, `terrain_shadow_lod_gameplay_test` night/day return, vegetation haze/shadow parity.
- Saving: 1-2.5 ms on planet-lit nights, 0.1-0.3 ms vegetation receivers (est).

### S8 - Optional follow-ups (each needs a go)
- Build the macro mask on the GPU from the min-max pyramid in the S4 format (removes 190-245 ms CPU rebuilds).
- Terrain sky-visibility AO from the heightfield (valley/canyon darkening).
- Retire the analytic far cloud field where the far map covers.

## Decisions needed

**Answered 2026-10-19 (user):** 1 = 400 m near map (2 cascades); atlas size decided after S6. 2 = procedural sampler only (edits visible inside the map range). 3 = planet light keeps a PSSM 2 near map at <= 250 m. Still open: 4 (no working-copy cloud defaults edit on the 4070 desktop; revisit on the A1000), 5, 6, 7.

1. Near map range 300 / 400 / 500 m (2 cascades), and atlas 4096 vs 2048 once contact shadows land.
2. Heightfield heights: procedural sampler only (voxel/smooth edits visible only inside the map range) vs merging edit deltas (more work, needed for large excavations seen from afar).
3. Planet light at night: PSSM 2 near map (<= 250 m) or none (heightfield + contact only).
4. Commit the working-copy cloud defaults (512 px / 5 Hz / 4 slices) as the dev factory file.
5. Contact shadows on grass: accept the new look and its default strength.
6. Trees past 480 m receive terrain heightfield shadows (new, correct, changes the look); optional canopy height in the heightfield for distant forest casting.
7. Optional heightfield terrain AO (S8).

## Risks

- Voxel edits / caves: heightfield ignores caves by design; cave-mouth rule in S5 must be tested at dug pits and overhangs.
- Ring curvature and wrap: canonical double-precision addressing, wrap only at lookup (`far-ring-heightmap-shadow-plan.md`).
- Seams: map fade vs heightfield (terrain shadow continuous across the 400 m line; object shadows end there, as trees already do at 480 m); heightfield L1 -> macro crossfade; main/far viewport handoff keeps macro authoritative.
- Flying/altitude: map becomes useless above ~1 km; heightfield window must follow the ground footprint and texel size grow with altitude.
- Streaming/teleport: heightfield fill lag; fall back to macro + map until ready, no black frames.
- Bores: aperture carving must match the shader discard radius or bore interiors go dark from afar.

## Cost budget

Estimates for the A1000 at 1920x1080, Medium, ground pose by day unless stated. S0 replaces these with measured values.

| Type | Current | Target |
|---|---|---|
| Sun map (+ casters) | 2.5-5 ms GPU (est; stale measured 6.8-10 ms at 8192/12 km) | 0.8-1.5 ms (PSSM 2, 400 m) |
| Contact shadows | 0 | +0.4-0.8 ms |
| Terrain heightfield | 0 | +0.1-0.3 ms build + ~0.1 ms receivers |
| Macro mask | ~0 GPU, 190-245 ms CPU per rebuild | unchanged (S8 optional) |
| Cloud producer | 0.3-0.4 ms (working copy; 2.3-3.1 ms HEAD) | 0.15-0.25 ms + 0.03 ms crossfade |
| Cloud receivers | 0.2-0.5 ms | 0.1-0.25 ms |
| Planet map (planet-lit night) | 1.5-3 ms | 0-0.6 ms |
| SSAO, point lights, far viewport | unchanged | unchanged |
| **Total, day ground** | **~3.0-5.9 ms** | **~1.7-3.2 ms (saving ~1.3-2.7 ms, mid ~2 ms)** |
| Total, day 1-12 km altitude | est 4.5-6 ms | ~0.8-1.2 ms (saving ~3-5 ms) |
| CPU frame setup (shadow culling) | unmeasured share of 8.2 ms | est -0.5-2 ms |

Against HEAD's committed cloud defaults (1024 px / 10 Hz) add another ~2-2.7 ms of saving. Gate: a step that does not show its saving in per-pass timestamps on the A1000 (35 W, P0) is not worth its complexity and is reported back before continuing.
