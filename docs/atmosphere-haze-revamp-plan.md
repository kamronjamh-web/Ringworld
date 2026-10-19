# Atmosphere haze revamp plan (Tier 1 + Tier 2)

Status: approved 2026-10-18. Light shafts / froxel volumetrics are out of scope (see P9 for the plug-in point).

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

### P4 - Settings, presets, persistence
- `AtmosphereVisualSettings` (defaults so old .tres load), `AtmosphereVisualDefaults.cs`, `RingLiveTuningDrawer.Atmosphere.cs`.
- Controls: visibility km, ground mist density, mist height, aerosol density, aerosol height, Rayleigh strength, forward scattering g, sun warmth, haze brightness, multi-scatter/ambient, quality 4/8/16, cloud ambient from sky, sky tint.
- Presets: Natural V ~180 km, low mist, g 0.6; Cinematic V ~60 km, more mist/aerosol, g 0.75, warmer.
- Tests: atmosphere menu/settings smoke tests + old-file migration case.

### P6 - Ordering fixes
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

### P10 - Far arch
- Keep the remote interval in `ring_atmosphere_first_air_paths_shared`; `L = S_near + T_near*(S_remote + T_remote*L_surf)`; S_remote uses the remote point's local day; eclipse visibility applies globally.
- Far viewport (1/1000 scale) and sky. Setting: arch veil strength.
- Tests: remote-shell ray-frame cases; atmosphere_visual_test arch noon / night-side / terminator.

## Decisions (approved)

1. Sky palette fully derived from the model; optional tint multipliers default neutral.
2. Natural visibility re-anchored to ~180 km under Koschmieder.
3. Clouds in caves/underwater: attenuate by Godot fog in the cloud composite.
4. Eclipse darkens far-arch in-scatter too (global event); arch also uses its own local day.
5. Mist altitude above the nominal floor (valley pooling deferred to the light-shaft work).
6. Keep the Legacy A/B toggle until P8.

## Cost budget

A1000 laptop is GPU-bound at ~40-44 ms (Sunshine 21-26 ms). Estimated: Tier 1 +0.3-0.6 ms (minus the rain overlay pass); Tier 2 roughly neutral at steady state. Measure each step on the same machine (`terrain_render_attribution_test --haze-only`).
