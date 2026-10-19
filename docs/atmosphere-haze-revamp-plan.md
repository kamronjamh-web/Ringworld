# Atmosphere haze revamp plan (Tier 1 + Tier 2)

Status: approved 2026-10-18. Light shafts / froxel volumetrics are out of scope (see P9 for the plug-in point).

## Handoff (updated 2026-10-19)

**Done:** P0, P0b, P1, P2a, P2b, P5, P3, hub over-blue fix, P3b (built-in lighting convention), terrain/grass albedo retune, Haze brightness slider (2026-10-18); P10 far-arch remote air, P4 controls (no preset retune), P9 camera-column cloud dimming, P6 rain finding (2026-10-19). `HazeModel` default is still **Legacy**; select Physical in the grave-key Atmosphere tab to A/B. All new Physical controls are in the Atmosphere tab's "Physical haze" group (hidden in Legacy): mist density/height, aerosol height, Rayleigh strength, ground bounce, multi-scatter gain, sky tint RGB, quality, cloud shadowing of haze. Use these to try calibration options (decision 1) live.

**Verified (Physical):** object, cloud and sky haze agree exactly (`tools/haze_consistency_gpu_test.gd --model=physical`, 15 contracts; `--p4-controls` 16). Horizon/zenith convergence to the sky is 0.000 at 800 km. Night/eclipse sky dark. Clouds hazed at transmittance-weighted depth. Far viewport tonemapped once. From local night the sunlit far arch is a bright, detailed band under a blue veil (`arch_visible_from_night`, `atmosphere_visual_test` `*_night_arch` capture).

**Not done in P9:** storm grey-Mie haze ("Storm haze" slider) and per-target terrain cloud shadow. **Not done in P4:** preset retune (waits on decision 1), "Cloud ambient from sky" menu toggle (bridge flag `CloudAmbientFromSky` exists). **P6 remainder:** cave/underwater cloud fog, deferred with spec (see P6).

**Open decisions (need the user) before flipping the default to Physical:**
1. *Physical calibration vs dark terrain.* At 12 km the Physical haze reads milky. Root cause (measured, not a bug): live terrain albedo is ~0.04-0.065 (bright patches 0.17), ~3x below Earth-like, while the haze is calibrated to the real sun (E = pi*energy, Earth Rayleigh). Options: (A) `Haze brightness` ~0.35-0.45 (sky darkens ~2.5x with it, convergence holds); (B) Density ~0.4 (thinner, paler sky); (C) physically right: raise terrain/biome albedo ~2.5x and cut `day_sun_energy` 4 -> ~1.6 (retune trees/grass/water/clouds/goldens). Note the albedo retune done for P3b makes this worse: to reproduce the old pi*A^2 look the terrain albedo scale default is **0.15** (`ringworld_terrain_albedo_scale`, Terrain3D tab "Terrain albedo"), so effective ground albedo is ~0.006-0.025. Physical haze will look very milky over it at altitude. Option C would supersede the retune (scale back toward 1, raise base albedo, lower sun energy).
2. *(Resolved)* Far arch at local noon: the arch above ~37 deg is on the far night side and vanishes against daylit air; user accepted the physical result (no ringshine, no arch veil). Night-side visibility is now tested.

**Known test state:** `terrain_surface_include_regression` golden differs (terrain lighting changed by P3b; not regenerated, goldens are machine-specific). Pre-existing failures unrelated to haze: `night_horizon_attribution_test` (grass_hidden), `cloud_history_targeted_gameplay_gpu_test` (2026-10-19: gate 1 fixed by pinning interleave off in the fixture and allowlisting ring_perf_controls x/z/w by offset from the buffer end; gate 2 "no-flash baseline was not stable/converged" still fails at changed_fraction ~0.024 vs 0.01 limit. Not warmup (300 frames identical), fresh path is deterministic; scattered cloud-edge pixels, bisected 2026-10-19: fails identically at every testable commit back to the Godot 4.8 merge 4251ba8, so NOT a haze regression (P5 adds ~0.0007). Likely root cause: the bilinear history tap in `ring_cloud_history_reproject` (`addons/SunshineClouds2/SunshineCloudsHistory.glslinc` ~237-265, used at SunshineCloudsCompute.glsl ~1758/1770-1790 and ~1838/1850) reads history at a constant sub-pixel offset for a STILL camera, so history converges to 0.95*blur(H)+0.05*C and smears cloud edges (max 0.5 RGB). Forcing nearest history passes gate 2 (then stops at "point light did not visibly illuminate production clouds", uninvestigated). Next: log |screen_position*size| for a still camera; make previous-frame matrices exactly consistent with the current ray construction (suspect `current_camera_inv_projection` + jitter at ~line 59 vs previous view/projection), or treat reprojected motion < ~1e-3 px as zero (nearest history); keep bilinear for real motion; do not loosen thresholds), `lightning_visibility_stress_test --lightning-cloud-occlusion` and `sunshine_inside_cloud_seam_test` (cloud-mask material null at HEAD). Some celestial smokes crash at exit after PASS (intermittent, pre-existing).

**Gotchas:** Godot does not reimport `.glsl` files whose include changed; run `--headless --import` after editing shared includes. The root viewport must stay LDR 2D (83 tools read root screenshots); far content enters via `scripts/render/RingFarBackdropComposite.gd`. Auto-exposure is on in visual-test captures, so disable it for matched brightness comparisons.

**Next:** resolve decision 1 (try the Physical haze sliders in game), retune presets accordingly, flip default to Physical (spec in P3 notes: settings default, smoke tests, `--model=legacy` flags), measure on the A1000, then P7-P8 (LUTs; P8 removes Legacy), P9 storm haze, P6 cave/underwater cloud fog. Perf on the A1000 laptop is unmeasured for everything since P2a (4070: ~+0.3 ms Physical; far backdrop composite possibly +0.6-1.0 ms on horizon shots).

## Goals

- Physically grounded haze: `exp(-tau)` transmittance, in-scatter that converges to the sky radiance in the view direction.
- One haze function shared by sky, opaque/forward materials, Sunshine clouds and the far cloud mask.
- Clouds hazed at a transmittance-weighted depth, consistent with terrain.
- Far arch keeps visible detail (Halo-like) under a blue veil (~2 vertical airmasses), descending into a saturated horizon band.
- All new controls in the grave-key Atmosphere tab with existing persistence conventions.

## Current-state problems (summary)

- Smoothstep distance ramp with opacity caps (`materials/ring_atmosphere.gdshaderinc:390-397`); Natural max opacity ~0.56.
- Haze asymptote ~(0.09,0.12,0.16) vs horizon sky (0.50,0.60,0.69) from a separate CPU palette: distant things fade to a smudge, not the sky.
- Separate sky haze function (`sample_ring_sky_atmosphere`), different phase/boost terms.
- No altitude-dependent density along the ray; camera-local day only; no cloud/storm darkening.
- Sunshine hazes at the EMA'd densest-sample distance (`SunshineCloudsCompute.glsl:1705,1950-1956`).
- Rain haze overlay is overwritten by the post-transparent cloud composite; clouds ignore cave/underwater fog.
- Cloud ambient is white; small cloud/terrain mismatches (opacity multiplier, day-term formula).
- Far-arch remote air shell is discarded.

## Shared API (fixed in P1)

```glsl
struct RingHaze { vec3 T; vec3 S_sun; vec3 S_amb; };
// consumer: L = L_surf * T + S_sun * vis + S_amb   (vis = 1 now; P9 cloud term; future shaft volume)
```

## Steps

Each step is independently testable and committable. Order: P0, P1, P2a, P2b, P5, P3, P4, P6, P7, P8, P9, P10.

### P0 - Consistency harness + colour-space check (no production change)
- `tools/haze_consistency_gpu_test.gd`, compute probe `#include`ing the production shared include, spatial probe quad via `sample_ring_atmosphere`, sky probe rendering `ring_local_sky` to a small SubViewport.
- Cases: distance {1,10,50,200,800 km} x elevation {0,2,10,45,90 deg} x {noon, dusk, night, eclipse}.
- Records terrain-path vs cloud-path (T,S) agreement, sky radiance vs object in-scatter at large distance (horizon convergence), monotonic T in distance, zero density = identity.
- `--report` writes current mismatches as a baseline; default mode asserts (expected to fail until P3).
- Relative comparisons only (valid on both machines).
- Determine whether far-viewport sky reaches the main viewport as linear HDR or tonemapped LDR (`RingScaledFarViewport.cs:951-972, :2255`). If double-tonemapped, add P0b (far viewport linear/HDR) before P3.

### P1 - Physical haze math (include only, not wired)
- New `materials/ring_haze_shared.gdshaderinc` (portable, no uniforms).
- Density layers in local altitude above the nominal floor: mist sigma_M0*e^(-h/H_mist) (~400 m), Rayleigh sigma_R(lambda)*e^(-h/H_R) (~8 km), aerosol Mie sigma_A*e^(-h/H_A) (~1.5 km).
- Optical depth: reuse `ring_atmosphere_path_interval_shared` for the air interval, then N-point Gauss-Legendre on exact height `R - |radial(p)|` (N = 4/8/16 by quality); flat closed form when interval < 20 km.
- Phase: Rayleigh (3/16pi)(1+mu^2) + Cornette-Shanks Mie (g = forward scattering).
- Asymptotic in-scatter: L_inf_sun = E_sun*T_sun(h_mid)*(sigma_sR*P_R + sigma_sM*P_M)/sigma_t; L_inf_amb = ambient*albedo_ms; S = L_inf*(1-T).
- Day weight uses the `ringworld_effective_solar_day` formula (`celestial_lighting.gdshaderinc:12`); Sunshine gets the same formula CPU-side.
- Tests: extend `tools/ring_atmosphere_ray_frame_test.gd` with tau vs double-precision GDScript quadrature, including horizontal around-ring cases, scales 1 and 1/1000.

### P2a - Spatial consumers switch
- `sample_ring_atmosphere` -> `ring_haze_eval` returning RingHaze; delete legacy far-shroud / legacy-cylinder branches, `atmosphere_night_density_factor`, visibility-distance/cap uniforms (ensure every material binds a ray frame first; check `SmoothTerrainGrid.cs:2635`, `RingVolumetricCloudLayer`).
- Callers: `albedo *= T; emission += S*energy_scale` - terrain lighting include, grass (per-vertex), trees/impostors, proxy/global/voxel terrain, hull, ocean (also attenuate specular), emitters/beam/scale-anchor, far cloud clipmap/mesh/layer.
- `RingAtmosphereModel.cs`: physical properties; `ApplyProfile` maps art controls instead of overwriting exports; visibility via Koschmieder sigma = 3.912/V.
- Temporary `HazeModel {Legacy, Physical}` A/B toggle (removed in P8).
- Tests: `tree_atmosphere_gpu_test`, `cloud_spatial_atmosphere_gpu_test`, `RingAtmosphereModel.RunContractSelfTest`, `atmosphere_visual_test` (+horizon, 2 deg rows).

### P2b - Clouds + far cloud mask switch
- Bridge profile 10 -> 16 vec4 (`RingSunshineCloudBridge.cs:1073`, `SunshineClouds.gd:2791`), adds opacity multiplier, layer sigma/H, g, E_sun, ambient, quality.
- `RingAtmosphereCompute.glslinc` returns T,S; Compute :1955 / Fill :249 become `color = color*T + S`; `ring_cloud_transmittance_mask:209` likewise per sample.
- Done notes: profile slots 10-15 are packed only by `RingAtmosphereModel.PackCloudProfile` (bridge, far mask, test helper); layout documented at `ring_haze_from_profile` in `ring_haze_shared.gdshaderinc` (model flag, opacity multiplier, sigma/H/omega per layer, g, quality, day, E_sun x tint, ambient day/night; cap height reuses slot 1.x). Lighting values reach C# through `day_side_atmosphere.gd` -> `RingSunshineCloudBridge.SetHazeLighting` -> `RingAtmosphereModel.HazeSun*/HazeAmbient*`. Physical day = max(geo*vis, hub) (same as `ring_haze_sample`); the Legacy slots keep geo*max(vis, hub) (differs only during the eclipse hub event). The mask shader recomputes the day from the live globals. GenericData grew to 304 floats: every shader including `CloudsInc.comp` must be reimported (open the editor or run `--import`), stale imports read the old layout.
- Tests: `cloud_atmosphere_gpu_test`, `cloud_haze_color_space_test`, `fallback_cloud_mask_atmosphere_gpu_test`, `cloud_haze_gameplay_test` (profile 16, keep alpha invariance); P0 cloud==terrain passes.

### P5 - Transmittance-weighted cloud depth
- In the march: `w = newdensity*terminalDensityWeight*(1 - clamp(density_prev,0,1))`, accumulate `Sw`, `Swt`; `finalDensityDistance = Sw > eps ? Swt/Sw : traveledDistance` (replaces `min(traveled, highestDensityDistance)`).
- Keep the `.b` slot semantics (history agreement, interleave, post blur fade, haze). Mirror in `SunshineCloudsInterleaveFill.glsl`.
- Tests: cloud history / terrain-history / mountain-depth / interleave + motion regressions, `cloud_haze_gameplay_test`; new P0 case: thin near veil over dense far cloud.
- Risk: history acceptance shifts; watch the 0.95 moving-history fast-turn limit.

### P3 - Unify sky
- `ring_local_sky.gdshader`: `color = S(dir, shell exit) + T*(space + sun + stars + planet)`; delete `sample_ring_sky_atmosphere`, CPU top/horizon palette, boosts; planet haze becomes a weak second light in S. Optional per-channel sky tint (default neutral). Cubemap pass uses N=4.
- `day_side_atmosphere.gd`: stop pushing palette; keep eclipse/hub tints as multipliers.
- Cloud ambient derived from model zenith L_inf x tint ("Cloud ambient from sky" toggle).
- Haze ambient (`ringworld_haze_ambient_day/night`) derived from the model's sky irradiance, not the environment fill ambient. P2a finding: the fill ambient is art-boosted, making physical haze milky at altitude and ~7x too bright at night.
- Flip the HazeModel default to Physical once P3 passes the convergence contracts.
- Done notes (P3, default NOT flipped yet): `ring_local_sky.gdshader` branches on `atmosphere_haze_model`. Physical: celestial background (space, sun disk/halo, hub halo, stars, giant) is composed first, then `color = color*T + S` with `S = S_sun + S_amb (+ hub in-scatter)` from `ring_haze_eval_sky` (camera at origin, metres, `sky_camera_height_meters` pushed by `day_side_atmosphere.gd`, cubemap pass N=4); `ring_haze_uniform_params` (ring_atmosphere.gdshaderinc) is shared with `ring_haze_sample`. Legacy branch (palette + `sample_ring_sky_atmosphere`) is unchanged and must keep that call (sky registration greps for it). The palette is still pushed until P8 (smoke tests read it); Physical ignores it. `day_side_atmosphere.gd` publishes the sun irradiance with `haze_brightness`, `haze_sky_tint` and the eclipse/hub tints, and derives the haze ambient from the model via `scripts/Atmosphere/RingHazeSkyIrradiance.cs` (double-precision port of the haze math: 48 upper-hemisphere directions, closed-form isotropic multi-scatter with ground bounce `haze_ground_albedo` and `haze_multiscatter_gain`); night ambient = giant light (live) + `haze_night_floor`. Cloud ambient follows the mean sky radiance when `RingSunshineCloudBridge.CloudAmbientFromSky` and the model is Physical (`CloudSkyAmbientGain` calibrates the reference pose to the authored cloud ambient). Contracts in `haze_consistency_gpu_test`: all Physical convergence contracts pass exactly (object, cloud and sky integrate the same interval at 800 km), plus `cpu_sky_parity`, `sky_night_dark`, `sky_eclipse_dark`.

Known issue found in P2a (outside haze scope, pending user decision): custom `light()` functions in terrain/ocean/hull/voxel/surface shaders add `ALBEDO * LIGHT_COLOR * NdotL`, and Godot multiplies DIFFUSE_LIGHT by albedo again, giving pi*albedo^2*E instead of albedo*E. Terrain is ~35-40% darker than built-in lighting at typical albedos, which exaggerates haze veiling.
- Tests: P0 horizon convergence passes; `atmosphere_visual_test` re-baseline; re-run `night_horizon_attribution_test`.

### P3b - Fix double-albedo terrain lighting (approved 2026-10-18)
- Custom `light()` functions that add `ALBEDO * LIGHT_COLOR * NdotL` to DIFFUSE_LIGHT (Godot multiplies by albedo again): `ring_terrain_lighting.gdshaderinc` (`ring_terrain_direct_diffuse`, Terrain3D + grass), `ring_terrain_proxy`, `ring_terrain_global`, `ring_hull_global`, `ring_ocean_proxy`, `ring_ocean_global`, `voxel_ring_terrain`, `ring_surface_daynight`. Target the built-in convention (`LIGHT_COLOR / PI * NdotL`), matching `ring_tree`.
- Expect terrain/water/hull to brighten at typical albedos; retune exposure/albedo with before/after captures for user sign-off.

- Albedo retune (user decision: keep P3b): the old `pi*A^2` lighting was a strong per-channel darkening/saturating curve on dark albedos, so a pure scalar cannot restore hue exactly. Shader globals `ringworld_terrain_albedo_scale` (terrain / proxy / global / voxel albedo and the terrain colour grass blends toward) and `ringworld_grass_albedo_scale` (blade / card colour) are persisted in `GameplaySurfaceTerrainSettings` (Terrain3D tab > appearance, sliders "Terrain albedo" / "Grass albedo"). Defaults chosen by measurement with `tools/atmosphere_visual_test.gd --quick --terrain-albedo=X`: terrain 0.15 matches the pre-P3b noon lower-frame luma within ~7% (grass 1.0). Hull, water and trees are untouched. `Haze brightness` (0-2) is in the Atmosphere tab (`AtmosphereVisualSettings.HazeBrightness` -> `DaySideAtmosphere.haze_brightness`).

### Far-arch visibility findings (2026-10-18)
- In Physical, the arch above ~37 deg elevation at local noon is on the ring's night side; under daylit local air it reads as sky. This is physically correct (Legacy only showed it because its sky and arch haze were unrelated).
- Physical ringshine (light from the lit day arch onto the night side) is ~1.3e-3 E_sun (thin arch: width 318 km vs R 5000 km): ~0.2% contrast against a noon sky, i.e. invisible. A Halo-like read would need a ~120x art gain. User decision: no ringshine, no arch veil; keep the physical model.
- From the local night side the opposite (sunlit) arch is bright against dark local air and must stay clearly visible. Add a night "looking up the arch" capture/contract (P4 or P10).
- P3b also removes T applied twice to far-arch direct light (ALBEDO*T multiplied again inside light()), restoring day-arch detail to Legacy parity.

### P4 - Settings, presets, persistence
- `AtmosphereVisualSettings` (defaults so old .tres load), `AtmosphereVisualDefaults.cs`, `RingLiveTuningDrawer.Atmosphere.cs`.
- Controls: visibility km, ground mist density, mist height, aerosol density, aerosol height, Rayleigh strength, forward scattering g, sun warmth, haze brightness, multi-scatter/ambient, quality 4/8/16, cloud ambient from sky, sky tint.
- Presets: Natural V ~180 km, low mist, g 0.6; Cinematic V ~60 km, more mist/aerosol, g 0.75, warmer.
- Tests: atmosphere menu/settings smoke tests + old-file migration case.
- Done notes (P4 controls, presets NOT retuned, defaults unchanged): Atmosphere tab group "Physical haze" (visible only for the Physical model): Ground mist density (x sigma_M0, 0-5, 1), Mist height (m, 50-2000, 400), Aerosol height (m, 200-5000, 1500), Rayleigh strength (x sigma_R, 0-3, 1; does not rebalance sigma_A so visibility shifts), Ground bounce albedo (0-0.5, 0.1), Multi-scatter gain (0-3, 1), Sky tint R/G/B (0-2, 1), Quality 4/8/16 (8). Heights are the base H0; the altitude-falloff factor still multiplies them. Stored in RingAtmosphereModel statics via SetPhysicalControls (spatial uniforms, PackCloudProfile and RingHazeSkyIrradiance read them; Revision bump recomputes the ambient); albedo, gain and tint are forwarded to DaySideAtmosphere by RingGraphicsQualityController. Test: `haze_consistency_gpu_test -- --model=physical --p4-controls` (contract p4_controls_reach_consumers).

### P6 - Ordering fixes
**Update 2026-10-19 (lead review):** rain is already correct. `scripts/render/RingRainHazeComposite.gd` is a POST_TRANSPARENT effect kept LAST by `RingLocalWeatherEffects.EnsureRainHazeCompositorLast`, so it runs after the far backdrop and Sunshine and veils sky, far arch, terrain, vegetation and clouds exactly once (`tools/rain_cloud_compositing_gpu_test.gd` asserts this). The `ring_rain_haze.gdshader` overlay is only a fallback. Decision: do NOT fold rain into the shared haze function (12 consumers, a 17th profile slot, per-pixel cost when dry). A haze-lit rain tint can come later via the composite (P9 storm haze).
Remaining P6 (deferred, low visible value: `underground_fog_sky_affect` defaults to 0): fade Sunshine clouds by Godot exp fog at cloud depth in caves/underwater. Spec: append `vec4 ring_enclosed_fog` to GenericData (304 -> 308 floats, mandatory reimport); in SunshineCloudsPostCompute after the existing density lerp: `density *= 1 - clamp(sky_affect*weight,0,1)*(1-exp(-fog_density*min(cloud_dist, linear_depth)))`; bridge `SetEnclosedFog(density, sky_affect, weight)` fed from day_side_atmosphere (weight = max(cave_fog_weight, underwater_factor)); "Cloud fog (caves/water)" slider 0-1; tests in rain_cloud_compositing_gpu_test `--enclosed-fog`. Original text below is superseded for rain.

- Rain folded into the shared function as a near extinction term (global `ringworld_rain_haze` + tint), applies to sky/terrain/clouds/vegetation; retire `ring_rain_haze` overlay; RainHaze tab drives the global.
- Cave/underwater: Sunshine post composite attenuates cloud alpha by Godot exp fog at cloud depth.
- Tests: P0 rain case; emitter/cave fixtures re-run.

### P7 - LUT infrastructure
- `scripts/Atmosphere/RingAtmosphereLuts.cs`: RenderingDevice compute on the render thread, Texture2DRD/Texture3DRD globals.
  - Transmittance 3D 64(h) x 128(mu_up) x 8(curvature class a = 1 - d_axis^2).
  - Multi-scatter 32x32 (h, sun cos), Hillaire isotropic 2nd-order approximation.
  - Sky-view 192x108 lat-long about camera local up.
- Shaders in `materials/atmosphere/`. Rebuild on sun angle (~0.25 deg quantised), solar visibility delta > 0.01, weather/storm, settings; slice across frames.
- Test: `tools/atmosphere_lut_gpu_test.gd` vs P1 analytic.

### P8 - Consumers sample LUTs
- Sky-view LUT as asymptote + LUT/analytic transmittance (no froxel AP volume: forward per-material consumers, per-vertex grass, separate far viewport, compute paths, 900 km ranges).
- Sunshine gets a sampler binding (ABI change). Remove the Legacy toggle.

### P9 - Cloud/weather feedback
- S_sun *= column visibility (cloud shadow at camera and target; camera-only global `ringworld_air_sun_visibility` for others).
- Storm factor / mean coverage feed LUT rebuilds (dim E_sun, raise grey Mie).
- Settings: cloud shadowing of haze, storm haze. Future froxel shafts replace `vis` near the camera here.
- Done notes (P9, Physical only; camera-only subset): signal = `RingSunshineCloudBridge.CameraDirectSunTransmittance` (cloud shadow at the camera, smoothed in the bridge by LightingResponseSpeed, again in `day_side_atmosphere.gd` at `cloud_lighting_blend_speed`) blended by the new `Cloud shadowing of haze` slider (`haze_cloud_shadowing`, `AtmosphereVisualSettings.CloudShadowing`, default 1, Physical haze group). `current_air_sun_visibility` (also shader global `ringworld_air_sun_visibility`, informational) multiplies only the published `ringworld_haze_sun_irradiance` (`haze_sun_irradiance_published`), so sky, spatial materials and the Sunshine profile (`SetHazeLighting`) all get it; the CPU ambient multi-scatter keeps the undimmed sun and is dimmed by `current_cloud_ambient_transmittance` as before (no double dimming); T and S_amb untouched. Test: `air_sun_visibility_scales_s_sun` in `haze_consistency_gpu_test --model=physical` (v = 0.3 through the production E, S linear in E_sun per path, object = cloud = sky). Not done: storm grey-Mie haze and the `Storm haze` slider, per-target terrain cloud shadow, LUT rebuild hooks (no LUTs yet).

### P10 - Far arch
- Keep the remote interval in `ring_atmosphere_first_air_paths_shared`; `L = S_near + T_near*(S_remote + T_remote*L_surf)`; S_remote uses the remote point's local day; eclipse visibility applies globally.
- Far viewport (1/1000 scale) and sky. Setting: arch veil strength.
- Tests: remote-shell ray-frame cases; atmosphere_visual_test arch noon / night-side / terminator.
- Done notes (P10, Physical only, no new setting): `ring_haze_eval_target` (ring_haze_shared.gdshaderinc) = `ring_haze_eval` (near air) + `ring_haze_eval_remote` over the remote interval [upper_vacuum.y, outer.y] (`ring_haze_remote_interval`; skipped when the ray is shorter than 0.98 sqrt(cap (2R - cap)) = 438 km, the shortest ray that can leave and re-enter air). Same `ring_haze_height` frame as the near interval. The remote S uses the remote path-midpoint's own day (smoothstep(-0.2, 0.35, dot(up_remote, to_sun)) with hub blend toward the remote zenith, eclipse visibility and hub day applied globally), its own phase and sun transmittance. Combined as `T = T_near T_remote`, `S = S_near + T_near S_remote`. Used by `ring_haze_sample` only; the sky (`ring_haze_eval_sky`) and the cloud profile path are unchanged. Tests: ray-frame remote cases (tau within 0.5% of double reference, scales 1 and 1/1000, T_total = T_near T_remote, near targets bit-unchanged), `arch_visible_from_night` contract, `--labels=` and `night_arch` capture in `atmosphere_visual_test`. Finding: from the local night side the arch is sunlit only beyond ~35 deg elevation (remote day >= 0.5); below that the near arch is the remote night side and reads dark. At local noon the arch/sky luminance contrast is only ~1.2-3.

## Decisions (approved)

1. Sky palette fully derived from the model; optional tint multipliers default neutral.
2. Natural visibility re-anchored to ~180 km under Koschmieder.
3. Clouds in caves/underwater: attenuate by Godot fog in the cloud composite.
4. Eclipse darkens far-arch in-scatter too (global event); arch also uses its own local day.
5. Mist altitude above the nominal floor (valley pooling deferred to the light-shaft work).
6. Keep the Legacy A/B toggle until P8.

## Cost budget

A1000 laptop is GPU-bound at ~40-44 ms (Sunshine 21-26 ms). Estimated: Tier 1 +0.3-0.6 ms (minus the rain overlay pass); Tier 2 roughly neutral at steady state. Measure each step on the same machine (`terrain_render_attribution_test --haze-only`).
