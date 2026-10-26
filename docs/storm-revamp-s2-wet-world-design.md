# Storm revamp S2 design: wet world (lead, 2026-10-25, RTX 4070)

Work orders for `docs/storm-revamp-plan.md` step S2. Line refs are the working tree on 2026-10-25 (main 3110b7e plus the uncommitted S0/S1/S5 work). Direction: immersion and spectacle, exaggerated storms, no gameplay effects.

## 1. Current state

**Wetness is one camera-global scalar, and only Terrain3D reads it.**
- Integrator: `RingLocalWeatherEffects.UpdateWetness` (`scripts/RingLocalWeatherEffects.cs:841-849`), `SurfaceWetness` (`:105`), gain 0.12/s x intensity, drying 0.012/s x (1 - intensity) (`:52-53`). Snow gives no gain. It is the rain intensity *at the camera*, so a storm 2 km away wets nothing and the ground under a roof is as wet as an open field.
- Forwarding: `RingTerrain3DGameplay.UpdateSurfaceAppearance` (`scripts/Terrain/Surface/RingTerrain3DGameplay.Wetness.cs:25-32`) -> `RingTerrain3DSurface.SetSurfaceWetness` (`RingTerrain3DSurface.Wetness.cs:16-21`) -> material uniforms `surface_wetness`, `surface_wetness_strength`, `surface_dry_roughness`, `surface_wet_roughness` on every prepared owner (`:46-68`).
- Shader: `materials/terrain3d_ring_surface.gdshader:51-54` (uniforms), `:259-263` (wet = wetness x strength x ground_facing; albedo x (1 - 0.15 wet)), `:287-295` (roughness to wet roughness, SPECULAR 0.18 -> 0.50, `terrain_direct_specular_weight = wet`), `:358-376` (GGX direct highlight only while wet). Darkening is only 15%.
- Menu: Surface terrain tab "Wet roughness" and "Rain wetness strength" (`scripts/ui/RingLiveTuningDrawer.SurfaceTerrain.cs:191-192`, `GameplaySurfaceTerrainSettings.cs:27-28`). Test: `tools/terrain3d_gameplay_native_test.gd:28-64` asserts the per-owner forwarding.
- Debug HUD prints it (`scripts/Voxel/LOD/RingLodDebugHud.cs:138`).

**Not wet at all:** voxel/smooth terrain (`voxel_ring_terrain.gdshader:122-123`, fixed ROUGHNESS/SPECULAR), terrain LOD proxy and global (`ring_terrain_proxy.gdshader:184-185`, `ring_terrain_global.gdshader:130-131`), POI architecture (`ring_poi_architecture.gdshaderinc:51-55`), trees (`ring_tree.gdshader:83-84`, `imported_tree.gdshader:107-114`, impostor `:111-117`), grass (`ring_standing_grass.gdshader:311-313`, `ring_grass_cards.gdshader:18` is `specular_disabled`), water (no rain response at all: `ring_ocean_proxy.gdshader:463-465` builds the normal from FFT/analytic waves only), construction/fallback StandardMaterial3D (`RingStormLightTint.cs` inventory). No puddles, ripples, drips or lens droplets exist.

**What the current look shows** (`.godot/captures/storm_light/wo6_final/d00_down.png` vs `clear_down.png`, heavy storm at the fixture): the storm reads through darkness and haze only. Wet Terrain3D has no visible gloss because under D = 1 there is no sun and the sky reflection is dim and uniform. Consequence for the design: under a storm the wet world must be carried by darkening, puddle shapes, ripples, SSR reflections and lightning glints; the gloss pays off after the storm when the sun returns.

**Pieces to build on**
- Storm map: `ringworld_storm_map` is Sunshine's world-anchored macro weather map (1 km texels, about 100 km; R coverage, G storm, **B precipitation potential**, A influence: `addons/SunshineClouds2/RingMacroWeatherMap.glsl:6-11`). Published by `RingSunshineCloudBridge.StormLight.cs:80-131`. `ring_storm_light.gdshaderinc:33-45` samples it but ignores B. CPU and GPU agree by construction; sculpt v2 does not change the layout (`storm-revamp-s4-sculpt-v2-design.md:201`).
- Precipitation occlusion map (S1): 512 px over 128 m (0.25 m texels) around the player (`PrecipitationOcclusionFrame.cs:20-23`), active in the Below phase only (`RingPrecipitationOcclusion.cs:157,172-173`); `ring_precip_exposure` / `ring_precip_cover` (`materials/ring_precip_occlusion.gdshaderinc:20-37`) return exposed outside the map.
- Altitude phase and column query: `scripts/Weather/PrecipitationAltitude.cs:15-61`.
- Rain streaks (S1 WO5/6, in flight): raster CompositorEffect `scripts/render/RingRainStreaksComposite.gd` + `ring_rain_streaks.glsl(inc)`, CPU half `RingLocalWeatherEffects.RainStreaks.cs` (496-byte payload, 5x5 intensity grid at 60 m from `QueryLocalWeather`, `:129-167`). Chain order Sunshine -> streaks -> haze is enforced by `EnsureRainHazeCompositorLast` (`RingLocalWeatherEffects.RainFog.cs:191-207`) and asserted by `StreakCompositorBeforeHaze` (`RainStreaks.cs:313-324`: haze must be last).
- Engine SSR is already on in the gameplay scene (`scenes/VoxelRingIntegrationTest.tscn:105-109`, steps per preset in `RingGraphicsQualityController.cs:507`). Puddles with roughness about 0.03 get SSR reflections of trees, POIs and terrain for free.
- Full-screen post-tonemap precedent: the underwater view is a CanvasLayer 0 ColorRect with a `hint_screen_texture` shader (`scripts/day_side_atmosphere.gd:722-736`, `materials/underwater_no_goggles.gdshader`), drawn after the whole 3D frame and before the HUD layers.
- Lightning: `LocalStormFlash` OmniLight3D (`VoxelRingIntegrationTest.tscn:659`). Wet direct specular on point lights gives night glints for free.
- Trees: CPU placement records with resident colliders near the player (`RingBiomeDecorationLayer.ImportedForest.cs:67-91`, `CachedTree` in `TreeStreaming.cs:64`).
- S5 WO7 wants "aftermath drips decay tau 90 s near foliage/roof edges" (`storm-revamp-s5-rain-audio-design.md:41`).
- S3 (designed, not built): a deterministic per-location rain history (`TimeSinceRainSeconds`, `RecentRainAmount`, 8 lags of the system-geometry rain proxy), plus `FatDrop`, `HailIntensity` and `HailSize` (`storm-revamp-s3-storm-arrival-design.md` 2.3, 2.5, 4). Main decided it is the single source of truth for S2 wetness. S3 owns mist and fog; S2 owns drips, the fat-drop/hail ripples and the optional hail whitening.

## 2. Architecture

### 2.1 Wetness map: S3 rain history plus local drying (the per-location source of truth)
**Main's decision (2026-10-25):** S3's deterministic rain history (`docs/storm-revamp-s3-storm-arrival-design.md` 2.3 and 2.5) is the single source of truth for "did it rain here, how much, how long ago". S2 builds no runtime integrator. It adds only local shelter (2.2) and drying on top. Consequences:
- The map is **stateless**: each texel is recomputed from the history, so there is no persistence, no cold start, and no rewind handling. Load, rewind and teleport are correct by construction, and a paused clock gives identical frames.
- `RingLocalWeatherEffects.UpdateWetness` / `SurfaceWetness` (`RingLocalWeatherEffects.cs:841-849`) is retired as a wetness source. Terrain3D stops reading it; its owner removes it (and the HUD line) at batch close.

Rejected:
- Camera scalar (today): not per location.
- An S2 runtime accumulation map (the first draft of this doc: an ODE integrated per texel on the GPU): a second "did it rain" owner that would disagree with S3's mist, rainbow and drip clocks, and it needed persistence and cold-start rules.
- CPU evaluation of the history on a grid: 8 lags x systems x thousands of points per refresh is several ms of CPU.
- A fine 0.25 m history: shelter is static, so it needs no history (2.2).

**Inputs**
- **History (S3).** `RecentRainAmount` (exponentially weighted, tau 600 s) and `TimeSinceRainSeconds` come from the system-geometry rain proxy `P x I(n)` at lags {15, 30, 60, 120, 240, 480, 960, 1500} s (S3 2.3). The proxy is a closed-form ellipse function, so the GPU evaluates S3's own formula per texel:
  - S3 exposes the per-lag geometry its observer cache already builds (`BuildNearbyWeatherSystems(x, t - lag, 20 km)`, which covers the +-8 km window). Requested accessor: `RingVolumetricCloudLayer.StormArrival.cs` `TryGetRainHistorySnapshot(out RainHistorySnapshot)` with lag times plus, per lag, up to 32 systems as (centre, ellipse normaliser M, enveloped radii, P).
  - S2 uploads the snapshot as a UBO (8 lags x 32 systems x 48 B, about 12 KB).
  - `scripts/render/ring_rain_history.glslinc` is a GLSL port of the proxy and of the history reduction, pinned by a parity test against `StormArrivalMath` at 64 points.
  - **Requirement on S3:** `TimeSinceRainSeconds` must be continuous: interpolate the threshold crossing linearly between adjacent lags on the unthresholded proxy, not the nearest lag. Otherwise the gloss steps visibly at lag boundaries. S3's observer value and the S2 texel use the same reduction.
- **Rain now** (ripples, active film, drips during rain): the actual local precipitation, i.e. storm map B through the same threshold as the streak gate (`RainStreaks.cs:153`). Ripples and streaks then start and stop together. This is the instantaneous field, not an integrator.
- **Fat drops and hail** (S3 `FatDrop`, `HailIntensity`, `HailSize` at the observer): camera-level globals. They only drive near effects within 40 m (3.1), where the km-scale fields are constant.

**Map**
- `RingWetWorld`: 256 x 256 RGBA16F, 64 m texels -> 16.4 km square (+-8.2 km), 512 KB. Camera-centred, snapped to whole texels (world-anchored, no swimming). Channels: R **film** (gloss), G **soak** (darkening), B **standing** water (puddle level), A **rain now**.
- Update: one compute dispatch (`scripts/render/ring_wet_map.glsl`) on the render thread at 2 Hz and whenever S3 publishes a new snapshot. Pattern: `RingTerrainHeightShadow.Gpu.cs:94-209`. A 0.5 s exponential display filter on the result is allowed (it smooths the 0.25 s snapshot lattice; it is not a rain integrator).
- Drying on top, closed form, per texel. k = DryingSpeed x (0.3 + 0.7 sun + 0.05 wind_mps); sun is the clear-sky fraction from R and D of the storm map at the texel x the camera day factor, and wind is the mean wind.
  - film = max(rain_now_term, smoothstep(0.02, 0.15, Recent) x exp(-k x TSR / 90 s)). Gloss is gone 2-3 min after rain in sun, about 8 min overcast and calm.
  - soak = max(0.8 x rain_now_term, smoothstep(0.05, 0.4, Recent) x exp(-k x TSR / 300 s)). Darkening lingers 6-10 min.
  - standing = smoothstep(0.35, 0.8, Recent) x exp(-k x TSR / 600 s). Puddles need sustained heavy rain (Recent rises over minutes of rain, so puddles grow while it rains) and last about 15 min.
  - rain_now_term = smoothstep(0, 0.3, rain_now) x smoothstep(0, 0.05, Recent + rain_now). The first seconds of rain are covered by fat-drop spots (3.1).
  - **"Specular dries first"**: film decays 3x faster than soak, plus the gloss curve threshold in 2.3. The lags end at 1500 s, which is longer than every drying tail.
  - Snow: S3's proxy has no temperature. While `Snowing` at the camera, rain_now_term = 0 and the history counts as dry (v1 limitation: one flag for the whole map).
- Beyond the window: receivers fall back to rain now only (film = soak = rain_now_term, standing 0), with a 1 km blend. The far LODs and far ocean only ever see that.
- Dry fast path: when S3 reports no system with P > 0 inside 20 km at any lag and rain now is 0 at the camera, `ring_wet_params.w = 0`. Every receiver then skips all wet work (one uniform branch), so clear weather costs nothing.
- CPU mirror: pure `scripts/Weather/WetnessModel.cs` (the same closed form) evaluated at the camera from S3's `ObserverStormArrival` history, the CPU rain intensity and the S1 cover probe. Consumers: StandardMaterial3D wetness, lens droplets, drip activity. A parity test pins CPU vs GPU at the camera texel.

### 2.2 Shelter: the S1 occlusion map, used with a normal offset
Shelter is static, so W(x) = W_map(x) x exposure(x) is exact for fixed roofs. No second history map.
- Up-facing (n.up > 0.5): `ring_precip_exposure(world + up x 0.05)`.
- Walls: sample at `world + horizontal(n) x 0.6 m`, so a facade under open sky is exposed while its own roof does not shelter it; a wall under a balcony or eave stays dry. Windward boost: film x (0.35 + 0.65 x windward), windward from `ring_wind_mean_vector` and wind speed (storm wind soaks one side of a building).
- Undersides (n.up < -0.25): 0.
- Below the map's far plane: 0 (deep caves). Outside the map horizontally (> 64 m): exposed, with a fade over the last 8 m of the map.
- Map inactive (InCloud/Above phase): exposed everywhere (observer is in or above cloud; the near shelter detail is not readable).
- **Known limitation:** a large POI hall interior more than 64 m away reads wet until approached; the 64 m edge moves with the player. If the user check shows it, WO10 adds a shelter memory (R8, 1024^2 at 0.5 m, min-accumulated from successive occlusion maps). Enlarging the S1 map is the S1 owner's call.

### 2.3 Shader include API (`materials/ring_wetness.gdshaderinc`)
Globals (`project.godot [shader_globals]`, defaults = dry):
- `ring_wet_map` (sampler2D, RGBA16F, filter_linear, repeat_disable; 1x1 zero placeholder until ready)
- `ring_wet_map_frame` (camera offset from the map corner in around/across metres, texel 64 m, size 256)
- `ring_wet_frame` (mat4: columns 0/1 = around/across axis + camera canonical metres mod P, P = 16384 m (computed in double on the CPU), column 2 = up axis + ring radius, column 3 = camera centre world + P). Same arc correction as `ring_storm_level` (`ring_storm_light.gdshaderinc:50-60`). Floating-origin rebases only republish it.
- `ring_wet_params` (strength, wet roughness, puddle amount, valid)
- `ring_wet_clock` (ripple phase wrapped with `RingWaterWindResponse.BoundedPhase`, ripple layers from the preset, window half extent, far blend)
- `ring_wet_events` (S3 at the camera: FatDrop weight, HailIntensity, HailSize, hail cover)
- `ring_wet_ripple_tex` (RGBA8 256^2 with mips: ring distance, direction xy, time offset; generated in code at startup, no asset) and `ring_wet_noise_tex` (R8 256^2 tiling puddle noise).

```glsl
struct RingWet { float film; float soak; float standing; float rain; };
RingWet ring_wet_sample(vec3 world, vec3 normal_world, vec3 up_world);  // map (or far fallback) x exposure; zero when !valid
float ring_wet_gloss(RingWet w);                                         // smoothstep(0.25, 0.85, film) x strength
void  ring_wet_apply(RingWet w, float porosity, inout vec3 albedo, inout float roughness, inout float specular);
float ring_wet_puddle(RingWet w, vec2 plane_m, float flatness, float cavity, float view_distance);
void  ring_wet_apply_puddle(float puddle, RingWet w, vec2 plane_m, vec3 up_world, float view_distance,
                            inout vec3 albedo, inout float roughness, inout float specular, inout vec3 normal_world);
vec3  ring_wet_direct_specular(vec3 N, vec3 L, vec3 V, float roughness, float specular, float gloss);  // for light() paths without one
float ring_wet_fat_drop_spots(vec2 plane_m, float view_distance);     // 0..1 dark spot mask from first fat drops (3.1)
vec2  ring_wet_plane(vec3 world);   // world-anchored metres mod P for noise and ripples (tile sizes divide P: noise 51.2 m, ripples 4 m)
```
- Darkening: `albedo *= 1 - 0.55 x smoothstep(0, 0.5, soak) x porosity x strength`. Porosity per receiver: soil 1, grass and bark 0.6, concrete 0.5, metal (METALLIC) 0.
- Gloss: `roughness = mix(roughness, min(roughness, wet_roughness), gloss)`, `specular = mix(specular, max(specular, 0.5), gloss)`. Default wet roughness 0.18 (exaggerated; Terrain3D today uses 0.35).
- ALBEDO already carries `haze.T` in every receiver, so `ALBEDO *= scale` is exact; the haze EMISSION is untouched.
- **Dry identity:** with `valid = 0` or a zero sample, every output is bitwise identical to today's.
- `materials/ring_rain_ripples.gdshaderinc`: `vec2 ring_rain_ripple_gradient(vec2 plane_m, float rain, float view_distance)`. Up to 4 layers of the baked ripple atlas (Lagarde's technique: per-texel ring distance + time offset, expanding ring sin wave fading with age), offset in space and time; active layers scale with rain (1 drizzle .. 4 downpour), capped by the preset; zero beyond 40 m.

### 2.4 Receiver list (no per-subsystem toggles; everything follows the include)
| Receiver | File | Sample | Darkening / gloss | Puddles + ripples | Direct wet specular |
|---|---|---|---|---|---|
| Terrain3D near ground | `terrain3d_ring_surface.gdshader` | fragment | yes, porosity soil 1 / rock 0.5 by slope | yes; cavity from 4 height taps (`_height_maps`, as `:174-176`) | existing GGX (`:358-376`), weight = gloss |
| Voxel / smooth terrain, caves | `voxel_ring_terrain.gdshader` | fragment | porosity from `soil_surface` (UV2 material) | yes, flatness + noise | add `ring_wet_direct_specular` |
| Terrain LOD proxy | `ring_terrain_proxy.gdshader` | fragment | yes | averaged: roughness drop by standing x flatness, no normals | add (cheap) |
| Terrain global far | `ring_terrain_global.gdshader` | fragment (far fallback) | yes | no | no |
| POI architecture (both variants) | `ring_poi_architecture.gdshaderinc` | fragment | porosity 0.5, metal 0; walls windward | flat up-facing faces (roofs, plazas) | existing GGX via `poi_specular` |
| Trees | `ring_tree`, `imported_tree`, `imported_tree_impostor` | vertex | darkening x 0.5, roughness x 0.6 | no | existing Blinn term, f0 0.04 -> 0.1 by gloss |
| Standing grass | `ring_standing_grass.gdshader` | vertex (as storm D, `:278`) | darkening x 0.4, SPECULAR .05 -> .25 | no | existing direct specular |
| Grass cards | `ring_grass_cards.gdshader` | vertex | darkening only (`specular_disabled`) | no | no |
| Near ocean, lakes, rivers | `ring_ocean_proxy.gdshader` | fragment | no darkening | ripples into the normal before `:463`; roughness + 0.08 x rain beyond 40 m | existing |
| Far ocean | `ring_ocean_global.gdshader` | fragment | no | roughness + 0.08 x rain (rain-matte water) | no |
| Emitter stand-in, scale anchor | `ring_emitter_standin`, `ring_scale_anchor` | fragment | yes | no | engine (no light()) |
| Construction concrete | new `materials/ring_construction_surface.gdshader` replacing the StandardMaterial3D at `SmoothConstructionOverlay.cs:60` | fragment | yes (and storm light per fragment; unregister from `RingStormLightTint`) | flat tops | engine |
| Other StandardMaterial3D (glass, fallback surfaces, voxel fluid) | `RingStormLightTint.cs` | camera (CPU mirror) | albedo and roughness by film/soak at the camera | no | engine |

Not receivers: sky, clouds, haze, streaks, curtains, mist and low fog (S3 owns them; S3 WO-E1/E2 read the same rain history). `RingLocalWeatherEffects.SurfaceWetness` stays for the HUD until its owner retires it; Terrain3D stops reading it.

## 3. Puddles, ripples, drips, lens droplets

**Puddles.** `level = standing x puddle_amount - (1 - (0.55 x cavity + 0.45 x noise))`; `mask = smoothstep(0, 0.06, level) x flatness`, flatness = smoothstep(0.92, 0.985, n.up); a rim band (level -0.12..0) adds darkening and gloss. Inside: albedo x 0.3, roughness 0.03, SPECULAR 0.5, normal -> up plus ripples. Fill order follows cavities on Terrain3D, so puddles grow from hollows outward and shrink back the same way while drying. Mask beyond 150 m becomes an averaged gloss. Reflections: radiance map + engine SSR (already on).

**Ripples.** Driven by the local rain channel (A) x exposure, so they stop under a roof edge and at a storm edge. Water uses the same gradient in the FFT/analytic axes; `RingWaterWindResponse` stays as is (wind only), its `BoundedPhase` provides the long-clock-safe ripple phase.

### 3.1 Fat drops and hail (S2 owns the surface side; S3 owns the signals, S3 WO-C1/C2 the falling drops and pellets)
- **Fat-drop spots** (spectacle: the first big drops before the rain): while `FatDrop` > 0 on exposed up-facing surfaces within 60 m, a hashed world-anchored spot lattice (one cell evaluation, 0.25 m cells) darkens round 3-8 cm spots. Each spot has a hashed appearance time, so their density ramps with FatDrop x time in the band. Spots merge into full soak as rain_now arrives. Their gloss follows film. Beyond 60 m they fade. Deterministic on the sim clock.
- **Fat-drop ripples**: the ripple function takes (rain, fat, hail). Fat drops use one sparse layer with large rings (x2 radius, x0.5 rate). They also run where rain_now is still 0, on water and on existing puddles.
- **Hail ripples**: hail adds a high-rate layer of sharp, short-lived crowns (x1.5 amplitude, x0.6 lifetime) on water and puddles, scaled by HailIntensity. On water, hail also raises roughness (+0.15 x hail) to a white-noise chop.
- **Hail whitening (optional, WO11):**
  - a hail cover term `C = smoothstep(0.2, 0.7, RecentHail) x exp(-k_melt x TimeSinceHail / 240 s)`, where k_melt = DryingSpeed x (0.5 + sun) x (1 + 2 x warmth);
  - it requires S3 to add `RecentHailAmount` / `TimeSinceHailSeconds` to the history snapshot. HailAt is a deterministic per-system swath field, so the GPU evaluates the swath ellipses at the lags exactly like the rain proxy;
  - applied as `albedo = mix(albedo, 0.75, C x exposure x flatness)` with roughness 0.6, on up-facing terrain, POI roofs and grass tips;
  - stored in a second R8 map only while any swath exists;
  - until S3 exposes swaths it is not built: a camera-level cover would end at a visible radius.
- Hail cover suppresses puddle ripples, not standing water.

**Drips (visual; audio is S5 WO7).**
- Eaves: in the streak raster pass, candidates on a world-anchored 0.5 m lattice within 15 m; a candidate is an eave when its own column is exposed and a neighbour 0.5 m away has cover more than 1 m higher. Drops start at the neighbour's roof height, accelerate under gravity to the candidate's map height, and end in a S1 splash. During rain they stream (dribble); after rain their rate follows gloss(film) at the eave, so they taper in 1-3 min.
- Trees: the CPU publishes up to 16 canopy discs (nearest imported trees with resident colliders within 20 m: centre, radius about 0.4 x width, canopy base about surface + 0.35 x height). Drops spawn inside the discs and fall to the map height (trees are excluded from the map, so it is the ground). Rate: 0.5 x rain during rain, then gloss(film) afterwards.
- Rendering: the S1 streak technique (lit by scene light, after Sunshine, before haze, deterministic hashed phases). Budget 128 (Low) .. 512 (Ultra) instances.
- Shared signal: `RingWetWorld.DripActivity` (gloss at the listener x near eave/tree within 6 m) and `TryGetDripSources`. Gloss is film, which is derived from S3's `TimeSinceRainSeconds`. S5 WO7 / S3 WO-C3 key drip one-shots to the same S3 clock, so this signal is a convenience (with drip positions), not a second clock.

**Lens droplets.**
- Placement: a CanvasLayer 0 ColorRect (`RingLensDroplets.cs` + `materials/ring_lens_droplets.gdshader`), as the underwater view. It runs after the whole compositor chain (Sunshine -> rain streaks -> rain haze), after FSR/TAA, before the HUD.
- Rejected, a CompositorEffect after the haze: it breaks the haze-last invariant owned by the streak agent (`RainFog.cs:191-207`, `RainStreaks.cs:313-324`), and POST_TRANSPARENT runs before the temporal upscaler, so screen-locked drops would smear under FSR2/TAA.
- Look: two procedural layers (small static beads; larger runners that slide down with clear trails) refracting a mip-blurred copy of the final image, plus a soft overall blur at high lens wetness. Drops brighten with the lightning frame automatically.
- Lens wetness L (CPU, `LensWetnessModel`):
  - Gain: rain_cam x exposure_cam x (0.04 + 0.5 x smoothstep(0.1, 0.7, look_up)) per second.
  - Emerging from cover while rain > 0.2: L = max(L, 0.25 + 0.4 x rain).
  - Decay tau 5 s.
  - Underwater or snow: 0.
  - The ColorRect is hidden at L = 0, so it costs nothing when unused. Its clock is the weather sim clock.

## 4. Grave-key tunables
New "Wet world" group under the Rain group (resource `user://gameplay_wet_world_defaults.tres`, `GameplayWetWorldSettings/Defaults.cs`, `RingLiveTuningDrawer.WetWorld.cs`, live-tuning-setting skill):

| Setting | Range | Default |
|---|---|---|
| Wetness | 0-1.5 | 1.0 (darkening + gloss amount) |
| Wet gloss | 0.05-0.6 | 0.18 (wet roughness) |
| Puddles | 0-2 | 1.0 |
| Drying speed | 0.25-4 | 1.0 (scales k in the closed form; the rain history itself is S3's) |
| Lens droplets | 0-1 | 0.7 (0 = off) |

- Retire "Wet roughness" and "Rain wetness strength" from the Surface terrain tab. When the new file is missing, migrate them (Wetness = RainWetnessStrength, Wet gloss = WetRoughness). Old surface files still load. "Dry roughness" stays: it is a terrain look.
- Graphics preset, not the menu: ripple layers (Low 1, Medium 2, High 3, Very High/Ultra 4) and drip budget.
- Debug tooling, not the menu: global `ring_wet_debug_mode` (film, soak, standing, rain, exposure), a map overlay like `RingPrecipitationOcclusion.Debug.cs`, and test hooks `RingWetWorld.ForceUniform(film, soak, standing, rain)` / `LensWetnessOverride`.

## 5. Perf budget
**All rows are estimates. Nothing here was measured.** A standalone 4070 prototype was prepared but never ran: the GPU lock was held by the sculpt-v2 and S4 sprite runs for more than 30 minutes.
- Prototype: `C:\Users\Kamron\AppData\Local\Temp\claude\c--Projects-Ringworld\cdeefb6a-c6d9-4c7c-8ffc-710238138cd9\scratchpad\s2_proto`. It contains `bench.gd`, `lens.gdshader`, `puddle.gdshader`, and `run_bench.ps1`, which takes the lock and uses the `storm_s2` profile.
- What it measures: root-viewport GPU time for base, puddle + 4-layer ripples, and lens droplets, with SSR on and off. Each case is the median of 240 frames in an A-B-A-B-A pattern at 2560x1440.
- WO4 and WO7 run it first. If it shows more than twice these numbers, they stop and report.
- Anchor: the S0 storm-light receivers cost +0.03 ms on the 4070 for one map fetch per fragment.
- A1000 rows assume the 4070 is about 3x faster at equal resolution, and that the A1000 renders 1080p (0.56x the pixels).

| Item | 4070 1440p | A1000 1080p High (estimate) |
|---|---|---|
| Wet map compute, 2 Hz (65k texels x 8 lags x up to 32 ellipses) | about 0.03-0.06 ms per dispatch | about 0.1-0.2 ms per dispatch; slice over 2 frames if it spikes |
| Receivers, map + exposure fetch (dry fast path off) | about 0.03-0.05 ms (S0 storm-light fetch measured +0.03) | 0.08-0.12 ms |
| Puddles + ripples (heavy rain, ground view) | 0.08-0.15 ms (noise fetch + up to 4 ripple fetches on puddle pixels within 40 m) | 0.15-0.3 ms (Low/Medium: 1-2 layers) |
| Water ripples | under 0.05 ms | about 0.1 ms |
| Drips (512) | under 0.03 ms | about 0.06 ms |
| Lens droplets (only while L > 0) | 0.1-0.2 ms (screen copy + mip chain + 2 procedural layers) | 0.2-0.4 ms |
| Clear weather | about 0 (one uniform branch) | about 0 |
| Engine SSR on extra smooth pixels | 0-0.2 ms (SSR is already on; puddles raise the number of pixels that trace) | 0.1-0.4 ms |

Gates: dry world +0.02 ms or less on the 4070 (`compare` no visible change); heavy rain, everything on, +0.4 ms or less on the 4070 at 1440p (A1000 stand-in +0.9 ms at 1080p High).

## 6. Tests
Headless:
- `wetness_model_test` (closed form): film dries before soak, standing needs sustained rain and lingers, sun and wind speed it up, drying-speed scaling, snow counts as dry, output continuous in TimeSinceRain (no steps at lag boundaries when fed S3's interpolated value).
- `rain_history_parity_test` (headless half): the C# reference of the GLSL history reduction equals `StormArrivalMath` at 64 points and 8 lags.
- `wet_map_frame_test`: snapping, floating-origin rebase invariance, around wrap at the circumference, plane coordinate tiles divide P.
- `lens_wetness_model_test`: look-up gain, emergence burst, decay, underwater zero.
- `wet_world_settings_smoke`: round trip, sanitise, migration from the surface-terrain keys.
- `QualityPresetContractHeadlessTest`: ripple layers and drip budget per preset.
- `terrain3d_gameplay_native_test` updated: wetness forwarding retired, dry roughness still forwarded.

GPU:
- New `wet_world_gpu_test.gd` (synthetic storm map via `ForceUniform` and a manual occlusion frame):
  - map texels vs S3 `QueryStormArrivalAtTime` + CPU `WetnessModel` within 0.02 at 64 points, with a production storm timeline (develop, mature, dissipate);
  - load and rewind give the same map as a continuous run (stateless);
  - fat-drop spots appear only while FatDrop > 0, and hail ripples only while HailIntensity > 0 (forced via S3's `RING_STORM_ARRIVAL` override);
  - roof fixture: ground under the roof dry, the facade under open sky wet, the underside dry;
  - every receiver class (terrain, voxel, proxy, POI, tree, grass, water, construction) changes under forced wetness;
  - puddles only on flat ground;
  - paused clock gives two identical frames (ripples, drips, lens).
- Dry identity: `shadow_compose_identity_gpu_test` (44 cases) and `terrain_surface_include_regression` 0 px with valid = 0.
- Water: `water_settings_smoke_test`, `water_fft_gpu_smoke_test` unchanged when dry.
- Lens: hidden (no draw) at L = 0; visible after a scripted look-up in rain.

Captures: `tools/wet_world_capture.gd` + `.ps1` (pattern `storm_light_capture`, profile `storm_s2`), at the fixture pose:
- clear;
- S3 `timeline` sequence: approach, gust front, fat-drop spots, core, trailing, clearing (shared with S3's capture so mist and wet ground are reviewed together);
- rain 0/30/90 s;
- rain stops, sun returns: +30/+120/+300/+600 s (gloss gone before darkening, puddles last);
- POI plaza and overhang (dry line);
- water surface;
- look-up lens sequence;
- night lightning glints on wet ground.

ffmpeg checks:
- `sheet` of the drying sequence;
- `flicker` on the walk under a roof, the 2 Hz map steps during drying and lag-boundary crossings, and the lens on/off;
- `compare` of dry vs the pre-S2 baseline (no change).

Perf: `perf-capture` A-B-A with timestamps `GPU.WetMap`, `GPU.LensDroplets` and drips under the streak scope.

## 7. Work orders (implementer-sized)
1. **WO1 wet map producer + model.**
   - **Gated on S3 WO-A1/A2** (rain history) plus a small S3 addition: `TryGetRainHistorySnapshot` and continuous `TimeSinceRainSeconds` (2.1). Until then WO1 can be built and tested against a synthetic snapshot.
   - Files: `scripts/Weather/WetnessModel.cs`, `WetMapFrame.cs`, `RingWetWorld.cs` (+ `.Gpu.cs`, `.Debug.cs`), `scripts/render/ring_wet_map.glsl` + `ring_rain_history.glslinc`, the globals in `project.godot`, a skeleton `materials/ring_wetness.gdshaderinc` (sample, gloss, apply), the scene node, and a read-only storm-map accessor in `RingSunshineCloudBridge.StormLight.cs` (RID, front origin, texel, size, layer, circumference).
   - Do not add any rain integrator; do not edit S3 files beyond the agreed accessor (S3 owner adds it).
   - Accept: the headless model, parity and frame tests; GPU parity; placeholder bound before the first build (lesson of the 2DArray placeholder, `StormLight.cs:199-230`); `flicker` clean over a drying ramp.
2. **WO2 terrain receivers.**
   - Files: Terrain3D (replace the camera-global uniforms, remove forwarding in `RingTerrain3D*.Wetness.cs`, keep dry roughness), voxel, proxy, global; `ring_wet_direct_specular` in their light().
   - Accept: dry identity 0 px; forced wetness visibly darker/glossier on all four; native test updated.
3. **WO3 POI, vegetation, misc receivers.**
   - Files: POI include, three tree shaders, two grass shaders, emitter stand-in, scale anchor; `ring_construction_surface.gdshader` for concrete; CPU-mirror wetness in `RingStormLightTint`.
   - Accept: identity; coverage cases in `wet_world_gpu_test`; `poi_haze_gpu_test`, `grass_appearance_gpu_test` and `storm_light_gpu_test` unchanged when dry.
4. **WO4 puddles + ripples on ground.**
   - Files: `ring_rain_ripples.gdshaderinc`, texture generation in `RingWetWorld`, puddles in Terrain3D (with cavity), voxel, POI, and the averaged proxy gloss.
   - Also fat-drop spots and the fat-drop/hail ripple layers (3.1), read from `ring_wet_events`.
   - Accept: puddles only on flat ground, growing from cavities; ripples scale with rain and stop under cover; fat-drop spots and hail crowns appear only with their signals; paused-clock determinism; perf row.
5. **WO5 water rain response.**
   - Files: `ring_ocean_proxy` ripples + roughness, `ring_ocean_global` roughness.
   - Includes fat-drop and hail ripples and hail chop on water.
   - Accept: water smokes unchanged when dry; capture shows ripples on ocean and lake.
6. **WO6 drips.**
   - Files: CPU tree-disc query (new public method in `RingBiomeDecorationLayer.ImportedForest.cs`), `RingWetWorld.DripActivity`, `scripts/render/ring_wet_drips.glslinc`, and a hook in the streak pass (payload + wet-map binding).
   - **Gated:** after S1 WO5/WO6 land and the streak files are released.
   - Accept: drips only under eaves/canopies; taper 1-3 min after rain; zero when dry; S5 notified of the signal.
7. **WO7 lens droplets.**
   - Files: `RingLensDroplets.cs`, `LensWetnessModel.cs`, `materials/ring_lens_droplets.gdshader`.
   - Accept: model test; look-up/emergence capture; `flicker` on/off; zero draws at L = 0; perf row.
8. **WO8 menu, persistence, presets, migration.**
   - Files: settings resource/defaults, drawer partial, hooks in `RingLiveTuningDrawer.cs`, preset rows in `RingGraphicsQualityController`.
   - Accept: settings smoke, preset contract, migration.
9. **WO9 perf, captures, docs, prune.** A-B-A table, capture sheet for the user check, plan doc handoff, `tools/prune_test_output.ps1 -Apply`.
10. **WO10 (conditional) shelter memory**, only if the user check shows the 64 m edge.
11. **WO11 (optional) hail whitening**, gated on S3 adding hail history (`RecentHailAmount`, `TimeSinceHailSeconds`, swath geometry) to the snapshot. Accept: white cover only inside hail swaths, exposed and up-facing; melts in sun; `flicker` clean.

Order: (S3 WO-A1/A2 + snapshot accessor) -> WO1, then WO2/WO3 (parallel-safe; different files), then WO4, then WO5, WO7 (independent), WO8, then WO6 when unblocked, then WO9.

## 8. File ownership and conflicts
| Area | Owner now | S2 action |
|---|---|---|
| `RingLocalWeatherEffects*` (incl. `RainFog.cs`, `RainStreaks.cs`), `RingRainHazeComposite*`, `RingRainStreaksComposite.gd`, `ring_rain_streaks.glsl(inc)`, `RainStreakLattice.cs`, rain settings and drawer files | S1 streak implementer | No edits. Read `PrecipitationIntensity`, `Snowing`, `PrecipitationStart` only. WO6 waits for release. The lens stays out of the compositor chain, so the haze-last invariant is untouched. |
| `addons/SunshineClouds2/*`, `RingVolumetricCloudLayer.cs`, `RingSunshineCloudBridge.CameraIndependentClouds.cs` | sculpt v2 | No edits. The storm map is read only through the S0 bridge accessor. Risk: a change to the macro map channels or capacity (the design says none). |
| `RingSunshineCloudBridge.StormLight.cs`, `ring_storm_light.gdshaderinc`, `RingStormLightTint.cs` | S0 (done; balance pass after V3) | Additive only: a storm-map accessor, an optional `ring_storm_sample_at_map` returning vec4, a wetness term in the tint. Coordinate with the balance pass. |
| Receiver shaders (terrain, POI, trees, grass, ocean, emitter, scale anchor) | uncommitted S0 edits, no active owner | WO2-WO5 edit on top. Re-check `git diff` before starting. Run the S0 gates after. |
| `RingPrecipitationOcclusion*`, `ring_precip_occlusion.gdshaderinc` | S1 (WO2b done) | Read only. |
| `RingWeatherAudio`, `RingWeatherAmbience`, `RainSoundField` | S5 | None. Publish `DripActivity` (with positions) for WO7; the clock is S3's `TimeSinceRainSeconds`. |
| `scripts/Weather/StormArrival.cs`, `RingVolumetricCloudLayer.StormArrival.cs` | S3 | No edits. Request: `TryGetRainHistorySnapshot`, continuous `TimeSinceRainSeconds`, and (for WO11) hail history. S2 ports the proxy to GLSL with a parity test. Mist and fog are S3's; S2 adds no atmosphere term and, in v1, no surface steam. |
| `RingLocalWeatherEffects.UpdateWetness` / `SurfaceWetness` | S1 streak implementer (file owner) | Retired as a source. Removal is a one-liner for the file owner at batch close. |
| `scenes/VoxelRingIntegrationTest.tscn`, `project.godot`, `RingLiveTuningDrawer.cs`, `RingGraphicsQualityController.cs` | shared, several agents | Small appends (2 nodes, a globals block, 2 hook lines, preset rows). Main sequences these. |
| Terrain3D wetness plumbing, `GameplaySurfaceTerrain*`, `RingLiveTuningDrawer.SurfaceTerrain.cs`, `RingBiomeDecorationLayer.ImportedForest.cs`, `SmoothConstructionOverlay.cs` | no active owner | S2 owns while in flight. |

## 9. Risks
- Two rain fields:
  - The aftermath comes from S3's system-geometry proxy (no local noise).
  - "Rain now" comes from the 1 km macro map B (with noise), which matches the streaks.
  - They can disagree by a few hundred metres at a rim. The max() in 2.1 means the ground is never dry where it visibly rains.
- S3 dependency and schedule: WO1 waits for S3 A1/A2. If S3 slips, WO2-WO5 can still proceed on a forced/synthetic map.
- S3 history must be continuous in time (2.1); a stepped TimeSinceRain shows as gloss pops.
- The snow flag is camera-global.
- Large interiors beyond 64 m (2.2).
- SSR cost grows with smooth pixels (measured in WO9).
- Puddle noise aliasing at grazing angles (mip the noise; fade the mask by distance).
- Construction concrete moving to a ShaderMaterial changes its look slightly in clear weather (needs a `compare`).
- With 8 parallel tracks, shared-file merges are the main schedule risk.

User check: heavy storm walk from an open field under a POI overhang and out again (dry line, ripples stop, lens burst on exit); look up in rain; stand by a lake; let the storm pass at noon (gloss fades before darkening, puddles linger, drips taper); night lightning on wet ground; Low preset.

## 10. Implementation notes (S2 batch, 2026-10-26, RTX 4070)

Built: WO1-WO5, WO7, WO8 (WO6 drips, WO10 shelter memory and WO11 hail whitening are not built). Deviations from the text above, all deliberate:
- **Amount driving soak, film and puddles.** S3's `RecentRainAmount` weights recent lags, so a 15 minute storm reads about 0.3 two minutes after the rain stops, which ended soak and puddles long before their drying tails. The model uses `max(Recent, PriorRain)` for film and soak and `PriorRain` alone for standing water. `PriorRain` (`WetnessModel.PriorRain`, `rw_prior_rain` in `ring_rain_history.glslinc`) is the mean wetness of the history lags older than `TimeSinceRainSeconds`, ramped continuously (weight `clamp((lag - tsr) / (0.25 lag + 15), 0, 1)`). Puddles therefore still need sustained rain (they form after minutes) and now last through their 600 s tail.
- **Horizon fade.** Film, soak and standing fade out over the last quarter of the 1500 s history horizon (at overcast calm the puddle tail is still 0.47 at 1500 s and would pop).
- **Puddle level** is `0.55 x standing x amount - (1 - (0.55 cavity + 0.45 noise))`. Without the 0.55 any standing level above 0.73 floods all flat ground into one sheet.
- `ring_wet_puddle` returns `vec2(mask, rim)` (rim band), not a float.
- `ring_wet_wind` global (world wind direction and speed) added for the windward wall boost. `RingWetWorld.SunOverride` and `ForceUniform` / `SetSnapshotOverride` / `RequestReadback` are test hooks.
- Dry fast path: `ring_wet_params.w = 0` when no system with potential reaches the 16 km window at any lag, the camera is not rained on, no 3x3 probe (6 km) shows precipitation and S3 reports no FatDrop / hail / core; held 4 s so the 0.5 s display filter fades out.
- Construction concrete is `materials/ring_construction_surface.gdshader` (storm ambient and sun fade per fragment, wet world); glass and fallback surfaces follow the CPU mirror through `RingStormLightTint.ApplyWetness`.
- Ripple layers and the drip budget are preset rows (`RingGraphicsQualityController.PresetWetWorld`); the drip budget is carried but unused until WO6.
- `RingTerrain3DSurface.SetSurfaceAppearance` takes the dry roughness only; `SetSurfaceWetness` and the three terrain wet uniforms are gone (`SurfaceWetness` stays in `RingLocalWeatherEffects` for the HUD until its owner retires it).
- No `RingWetWorld.Debug.cs` map overlay: `ring_wet_debug_mode` (1 film, 2 soak, 3 standing, 4 rain, as albedo) is the debug view.

Tests: `wetness_model_test`, `rain_history_parity_test` (C# transcription of the GLSL), `wet_map_frame_test`, `lens_wetness_model_test`, `wet_world_settings_smoke_test`, `QualityPresetContractHeadlessTest` (ripple layers and drips), `terrain3d_gameplay_native_test` (dry roughness only, retired uniforms absent), `wet_world_gpu_test.gd` (synthetic and `--world`), `wet_world_capture.gd/.ps1` (`--mode=timeline|movie|night|perf|scout`).
