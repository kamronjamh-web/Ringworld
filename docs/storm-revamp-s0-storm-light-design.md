# Storm revamp S0 design: storm light (lead, 2026-10-25, RTX 4070)

User request: no sun shadows under storms; heavy storms darken the ground and the air under them, scaled by how heavy the storm is; no blue sky visible when looking up from under a storm.

Captures (contact sheets): `.godot/captures/storm_dark/run2/seqA` (ground and shadow debug), `seqB` (looking up), `seqC` (seen from outside), each with `report.json`. Scratch harness: `.godot/storm_dark_capture.gd`. It should become `tools/storm_light_capture.gd`, covering the distance sweep, auto exposure on/off and the outside shots.

## Root causes
**(3) Clear sky above storms is the main bug, and it causes most of (1).**
- At 6 and 12 km inside a MajorFront/StormTower, looking up shows blue sky and the sun disc.
- The StormTower sculpt is pillars only. `cumulonimbus` is false for profile 2, so there is no anvil and no deck. That leaves about 4.5 km pillars in 28 km cells, covering 5-10% of the footprint.
- The background threshold is capped at `mix(0.70, 0.90, coverage)` (`SunshineCloudsCompute.glsl` `sampleScene`, `weatherBackgroundThreshold`), so it never closes, even at coverage 0.985.
- Cumulonimbus anvils leave gaps between cells the same way. Nimbostratus is fine.
- The grey rain veil hides all this at 0 km. Once the veil is reserved for violent storms, the gaps will show more.
- A storm 25-45 km away is effectively invisible from outside.

**(1) Shadows under storms.**
- Because of (3), the shadow compute lets the sun through: sun visibility is 1 at 6 and 12 km, with crisp tree shadows.
- Standing grass passes cloud 0 to `ring_shadow_terms` (`ring_standing_grass.gdshader`, light about line 348, fragment about 308-318). Cloud reaches only albedo (x0.68) and AO, so grass keeps full sun and full shadow contrast under any cloud.
- Nothing ties shadow strength to storm heaviness: `ring_shadow_sun_visibility` (`ring_shadow_compose.gdshaderinc:69`) is `min(close, cloud, terrain)`.

**(2) No darkness.**
- Auto exposure is always on (`day_side_atmosphere.gd:67-71`, `_configure_eye_adaptation`, ISO 50-160). Mean luma under a storm with it on vs off: ground 28 vs 13, sky looking up 110 vs 55. With it on, the storm sky is brighter than a clear sky (69).
- Ambient dimming is camera-global only. `current_cloud_ambient_transmittance` scales Environment ambient, colour and sky contribution everywhere (`:424-429`, `:486-514`). It is 0.27 at 6-12 km even where the ground is in full sun, and 1.0 for a storm seen from outside.
- `CameraDirectSunTransmittance` is always 1.00 on the GPU shadow path, so P9 camera-column dimming is inactive in production. Only the `1 - 0.85*storm` cap applies.
- Sunshine's storm ambient colour follows the camera's `_localStormFactor` (`RingSunshineCloudBridge.cs:1180-1182`), so a storm seen from outside keeps clear-sky ambient.

## Design: one per-location term D(x)
**Source**
- `D = max(G, heavy(R))`, the storm level at the receiver's ground point. It is the same value as `g_ws_storm` (`RingWeatherSystems.glslinc` `ring_ws_apply_macro`) and the CPU's `_localStormFactor`.
- It comes from the existing world-anchored `RingMacroWeatherMap`: 1 km texels, about 100 km around the camera, R = coverage, G = storm. CPU and GPU agree by construction.
- Beyond the map it falls back to the far weather map; the bake (`ring_far_weather_cloud_bake.gdshader:78`) writes G = storm factor.

**Consumers**, all through `materials/ring_storm_light.gdshaderinc`:
- **Sun and shadow fade** (`ring_shadow_compose`):
  - Add a `storm` field to `RingShadowTerms`. The default 0 gives bitwise-identical output.
  - With `F = smoothstep(fade_start, fade_end, D)`: `close = mix(close, 1, F)` and `visibility *= 1 - F`.
  - Applies to both the DEFAULT and COMBINE_PRODUCT paths.
  - Debug mode 6 shows D.
- **Ambient**: `AO_cloud = min(1 - k*occlusion, A(D))`, with `A(D) = 1 - 0.9*strength*D` and AO_LIGHT_AFFECT 0. Taking the min, not the product, stops the terms compounding.
- **Camera-global cleanup**:
  - While the map is valid, the Environment ambient stays at its clear values; the old path is only a fallback.
  - Eye adaptation: `max_sensitivity = lerp(max, min, F(D_cam)*strength)`, so storms never brighten the image.
- **Haze**: march D along the view ray (4 taps over the first 40 km). This replaces the camera-only `1 - 0.85*storm` cap in `ring_haze_sample` and the sky, so objects and sky stay consistent.
- **Sky closure**:
  - Add a storm deck in `RingStormShapes.glslinc`: a nimbostratus base over the whole system footprint, x system mask x influence x lifecycle, eroded by `mediumShapes`. The shadow compute gets it automatically.
  - Lift the background threshold toward 1.05 with storm level in both `sampleScene` copies (main about line 476, shadow about 324).
  - Optional: make the Sunshine storm ambient colour per sample.

**Coverage**
- POIs, vegetation, water and terrain all get this through the include, with no per-subsystem toggles.
- What remains camera-local: Godot StandardMaterial3D props and the 35% sky-ambient share.

**Tunables** (Clouds tab, "Lighting & history"; these replace "Storm ambient floor" and "Storm ambient occlusion"):
- "Storm darkness" (0..1, default about 0.85).
- "Storm shadow fade" (0..1, default 0.35; the end is start + 0.35).

## Primary decisions (2026-10-25, matching the user's emphasis on darkness)
- **Eye adaptation is capped under storms.** It must never brighten them.
- **Grass darkens in cloud shadow like terrain (WO-1).** Accepted.
- **Environment ambient stays clear while the map is valid.** The StandardMaterial3D objects that remain visible in gameplay (player body, construction pieces, held items, anything else found) are inventoried and darkened by D at the camera, through one small shared path. Ring materials must not be dimmed twice.

## Perf gates
- Receivers under 0.05 ms.
- Haze taps at most 0.15 ms on the A1000 (vertex-rate fallback for terrain).
- Storm deck: Sunshine at most +0.3 ms on A1000 High in a storm (on the 4070, use +0.15 ms as a stand-in).
- Optional: skip the shadow-map pass when D >= fade_end across the whole shadow radius.

## Work orders
1. **WO-1, grass direct cloud.**
   - Change: in `ring_standing_grass.gdshader`, `terms.cloud = 1 - grass_cloud_occlusion` in light(), and drop the x0.32 albedo dimming.
   - Accept: `shadow_compose_identity_gpu_test` (grass cases updated deliberately, others 0 diff); debug view 5 shows cloud on grass.
2. **WO-2, publish D.**
   - New partial `RingSunshineCloudBridge.StormLight.cs`; avoid `CameraIndependentClouds.cs`.
   - `SunshineClouds.gd` exposes the front macro-map RID, layer and frame.
   - Globals `ringworld_storm_map` (Texture2DArrayRD), `ringworld_storm_map_frame`, `ringworld_storm_map_size` and `ringworld_storm_light` (strength, fade start, fade end, valid) in `project.godot`.
   - Far-bake G channel and CPU `GetStormLevelAt`.
   - Two settings (live-tuning-setting skill); retire the old two from the menu.
   - Accept: settings smoke; CPU vs shader D within 0.01 at 16 points; valid = 0 when the map is off.
3. **WO-3, include plus receiver ports.**
   - Ports: terrain3d_ring_surface, ring_terrain_global/proxy, voxel_ring_terrain, ring_grass_cards, ring_standing_grass, ring_tree, imported_tree (+ impostor), ring_poi_architecture.gdshaderinc, ring_ocean_global/proxy (sky reflection x A(D)), ring_emitter_standin, ring_scale_anchor.
   - Accept:
     - D = 0 is bitwise identical (`shadow_compose_identity_gpu_test` 44 cases, `terrain_surface_include_regression` 0 px).
     - New `storm_light_gpu_test`: at D = 1, sun visibility is 0, lit and shadowed pixels differ by at most 1/255, and AO = A_min. Below fade start, the output is unchanged.
4. **WO-4, `day_side_atmosphere.gd`.**
   - Remove the camera-global ambient dimming when the map is valid; add the eye-adaptation cap and the StandardMaterial3D path.
   - Build on the uncommitted WO-0 edits in that file.
   - Accept: ground luma under a storm is at most 0.35x clear (linear) with auto exposure on.
5. **WO-5, storm deck plus background lift** (Sunshine).
   - Order: after S4 WO-A, before WO-B, by the same file owner.
   - Accept:
     - No blue-sky pixels in the up60, up85 and y180 shots at 0, 6, 12 and 18 km with rain off. The blue-minus-red chroma threshold must hold for at least 99% of sky pixels.
     - No tree shadows at 6 and 12 km.
     - The 45 km outside view shows a dark storm mass.
     - `flicker` shows no spikes on a 10 s edge orbit.
     - Perf gate as above.
     - Requires a reimport.
   - 5b: per-sample storm ambient (needs a `CloudsInc.comp` GenericData slot and a reimport of every compute).
6. **WO-6, haze path darkening** (`ring_atmosphere.gdshaderinc`, `ring_local_sky.gdshader`).
   - Accept: `haze_consistency_gpu_test --model=physical` (all contracts, plus `storm_path_identity` with D = 0 bitwise identical, plus `storm_path_object_eq_sky`); the outside capture shows a darker band under the storm.
7. **WO-7 (optional):** storm shadow-map skip, measured with the perf-capture skill.

## Status: WO-1 to WO-4 implemented (2026-10-25, RTX 4070, uncommitted)
- **WO-1:** `ring_standing_grass.gdshader` feeds the cloud shadow to the direct light (COMBINE_PRODUCT) and no longer dims the albedo (x0.32). Only the 4 grass cases of `shadow_compose_identity_gpu_test` change (deliberately).
- **WO-2:** `scripts/RingSunshineCloudBridge.StormLight.cs` publishes the globals `ringworld_storm_map` (Texture2DArrayRD over Sunshine's macro map, 1x1x2 placeholder until it exists), `ringworld_storm_map_frame` (texel m, front layer, heavy-overcast start, 0), `ringworld_storm_map_size`, `ringworld_storm_frame` (mat4: around/across axes + camera position in map metres, up axis + ring radius, centre) and `ringworld_storm_light` (strength, fade start, fade end, valid). `GetStormLevelAt(around, across)` is the CPU mirror. The far bake writes G = far storm level; no receiver reads it yet (the map covers every receiver's range, see open issues). Settings "Storm darkness" (0.85) and "Storm shadow fade" (0.35, end = start + 0.35) are in Clouds > Lighting & history; the legacy `StormAmbientFloor` / `StormAmbientOcclusionStrength` keys are still accepted on load but have no menu row (the bridge keeps them for the fallback path).
- **WO-3:** `materials/ring_storm_light.gdshaderinc` (D, shadow fade F, ambient A(D)); `ring_shadow_compose.gdshaderinc` gets `RingShadowTerms.storm` (directional lights only) and debug mode 6 (set the global `ringworld_shadow_debug_mode` to 6; the shadow menu still lists 0..5). Receivers take `AO = min(cloud AO, A(D))`: terrain3d_ring_surface, ring_terrain_global/proxy, voxel_ring_terrain, ring_grass_cards, ring_standing_grass (D per vertex), ring_tree, imported_tree (+ impostor), ring_poi_architecture, ocean global/proxy (also x A(D) on the sky reflection), ring_emitter_standin, ring_scale_anchor. emitter_standin and scale_anchor have no custom light(): they are dimmed through AO_LIGHT_AFFECT = 1 only (direct and ambient together by A(D)).
- **WO-4:** `day_side_atmosphere.gd` keeps the Environment ambient (and fog / sky-contribution tints) at its clear value while the storm map is valid, caps the eye-adaptation maximum sensitivity toward the minimum by F(D at the camera) x strength, and keeps the haze ambient on A(D) at the camera. `scripts/RingStormLightTint.cs` scales the albedo of registered StandardMaterial3D (construction pieces concrete and glass, SmoothTerrainGrid fallback surface, VoxelChunkNode fluid) by A(D at the camera). Ring ShaderMaterials never register.
- **Tests:** `tools/storm_light_gpu_test.gd` (synthetic map), `tools/storm_light_capture.gd` + `.ps1` (full-game distance sweep, auto exposure on/off, outside view, CPU vs shader D probe with `--probe-only`, A-B-A receiver cost with `--perf-only`). Captures under `.godot/captures/storm_light/`.

## Status: WO-6 and balance retune (2026-10-25, RTX 4070, uncommitted)
- **Path darkening:** `ring_storm_path_level` (4 taps over the first 40 km of the view ray, read through `ring_storm_level_offset`, camera at the map's camera texel) feeds `ring_haze_apply_storm_path` in `ring_haze_sample`, the vegetation-atlas haze path and `ring_local_sky.gdshader`: `S_sun *= 1 - F(Dp) * strength`, `S_amb *= ring_storm_sky(Dp)`. While the map is valid `day_side_atmosphere.gd` publishes the haze sun with the cloud-column shadow only and the clear ambient (Sunshine's CPU profile keeps the camera-capped values). Contracts `storm_path_identity` and `storm_path_object_eq_sky` in `haze_consistency_gpu_test.gd` (compute harness defines `RING_STORM_NO_MAP` with a synthetic level).
- **Balance:** the sun fades to a shadowless residual `1 - strength` (not 0), receivers' ambient `A = 1 - 0.55 * strength * D`, the sky / haze radiance `1 - 0.97 * strength * D`, Sunshine's sun scaled by `1 - F(D at camera) * strength` through the driver's directional light multiplier (`UpdateStormCloudLighting`), eye adaptation gives up 0.9 x F x strength of its range. Default "Storm darkness" 0.9. Reason: with the sun at 0 the ground was 0.11x clear while the cloud deck stayed bright; ground = albedo x sky, so the sky has to dim harder than the ground.
- **Not done:** a distant storm (25-45 km) still shows no dark mass or band from outside; that needs the Sunshine storm deck (WO-5).
- **Global fix:** the `Texture2DArrayRD` global is only bound once it holds a valid RID (a Texture2DArrayRD without one resolves to a 2D texture and fails uniform-set creation). The ~400 "needs to be 2DArray, actual 2D" errors per run are NOT from this global: they persist with it never registered.

## Storm-owned D (2026-10-29, RTX 4070, uncommitted; user decisions "storm-owned D" and "fronts dark by rain")
Problem: D was `max(G, heavy(R))`, and G = `0.65 * influence + 0.55 * precipitation` counts every system. A dry manual Cs or As therefore darkened the sky and sun disc and took the sun off receivers, as if it were a storm.

**Rule.** Only storms and raining fronts darken the world:
- `D = max(owned * max(G, heavy), front, heavy * (1 - smoothstep(0, 0.25, influence)))`
- **owned** is the S6 section 3.8 ownership weight over sculpt systems (StormTower / Cumulonimbus, natural or manual): 1 within 1.3 ellipse radii, 0 by `RING_STORM_SUPPORT` (1.45). Inside a storm, D is bit-for-bit the old D.
- **front** is the rain term of a non-sculpt front (MajorFront / ExceptionalFront, natural or manual, flag `RenderWeatherFlagFront` = 32): `smoothstep(0.25, 0.65, P_front)`.
  - `P_front` is the front's own precipitation, with the same per-system falloff and coverage gate as the macro precipitation. It is capped by the point's precipitation potential (map B), so D follows the rain that actually falls there.
  - Without that cap, a manual dry deck that raised the coverage over a nearby natural front lifted the front's term to 0.17.
  - Heavy frontal rain goes dark, drizzle stays grey, and a dry front gives 0.
- **The heavy term** keeps plain heavy cluster overcast only where no system is present (map influence below 0.25).
  - S6's `ring_ws_eval_owned_storm_level` scales it by `1 - influence` instead.
  - With that factor, a dry deck that raised the coverage leaked through it: a manual dry As at influence 0.83 gave D 0.17.
- Every other system (dry Cs/As/Ns, banks, clusters, and any non-front system even when it rains) gives D = 0. Its overcast darkens the ground only through the WO4 layer cloud shadows.

**Where.**
- **Macro map.** `RingMacroWeatherMap.glsl` writes the storm-light layers 4 / 5 (front / back) = `(owned, front, 0, 0)`. `SunshineClouds.gd` has 6 array layers and `macro_map_storm_light_layer_offset` = 4.
  - G itself is unchanged, so the cloud computes are untouched.
- **Receivers.** `ring_storm_light.gdshaderinc` `ring_storm_owned_level` reads the layer at `ringworld_storm_map_frame.w` (front + 4). An index of 0 means the map has no such layer and reads owned 1, front 0, the old D. GLSL twins: `ring_rain_streaks.glslinc storm_level` and `ring_wet_map.glsl` (layer in `prev.w`).
- **CPU.** `RingSunshineCloudBridge.StormLight.cs`:
  - `EvaluateStormLightTerms` and `StormOwnedLevel` serve `GetStormLevelAt`.
  - `SmoothCameraStormLevel` sets `_localStormFactor` (camera D). The front and system-free heavy terms are smoothed; the ownership weight is not, because it is already a spatial ramp. So wherever `owned` is 1, camera D equals the old value, transients included.
- **Camera D consumers follow automatically:** eye-adaptation cap, Sunshine sun factor and camera cloud ambient colour, StandardMaterial3D tint, wet world, haze storm factor and rain streaks. Under a non-storm deck the cloud ambient no longer goes to the storm colour.
