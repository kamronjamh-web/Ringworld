# R1-6 design: sky radiance cadence

Lead design, 2026-11-17. Part of `docs/reflections-plan.md` section 10 (read 0, 2.5, 9.2, 10.3 and the R1-6 paragraph of 10.4 first). No code has changed for this design.

Engine lines cite the Godot `master` copy read for this design: `servers/rendering/renderer_rd/environment/sky.cpp` ("sky"), `sky.h`, `servers/rendering/renderer_rd/storage_rd/material_storage.cpp` ("ms"), `servers/rendering/renderer_rd/shaders/environment/sky.glsl`, `scene/resources/material.cpp`, `render_forward_clustered.cpp` ("rfc"), `doc/classes/Sky.xml`, `doc/classes/ProjectSettings.xml`. As in the rest of the plan, `master` (4.8 beta) was read, not the dev7 commit the game runs. **Every number marked "est." is an estimate; every "verified" is from reading code, not from a run.**

## 0. Summary

- **Recommended design (D, "live globals, refresh by epoch").** Keep the one Sky that the main and the far viewport share today, in Realtime mode. Move every sky-shader input that can change from one frame to the next out of the sky material into global shader parameters, so the far viewport's visible sky still follows them every frame. The sky material then holds only static values plus one `radiance_epoch` uniform. The engine re-renders the radiance map only when the sky material is written, so a small controller decides when to write the epoch: when an input has moved by more than a threshold, on discrete events, and at least every 0.5 s.
- **Expected saving (est.):** 0.13 to 0.17 ms at standing poses by day and in the wet; 0.10 to 0.14 ms at dawn and dusk; little or none while flying fast through twilight (no regression). The target, under 0.09 ms averaged, is met at every measured pose if the per-rebuild cost is the measured 0.18 ms.
- **What stays identical:** the visible sky (same values, now read from globals), the far image at a static pose, and everything while the cadence switch is off. What changes: the radiance map, and so the sky part of ambient light and reflections, lags the live inputs by at most one threshold step.
- **One measurement does not fit the code** (section 1.4): by the code, Incremental mode should still do work every frame, but the run measured it as free. Order R1-6a settles this before anything else is built. The design does not depend on how it resolves.

## 1. Engine facts (verified by reading `master`)

### 1.1 What marks the sky's radiance map dirty

`SkyRD::setup_sky` (sky:1005-1101) runs for every viewport render whose Environment draws, or is lit or reflected by, a sky (rfc:2150-2161). It sets `sky->reflection.dirty` when:
- the sky material's uniforms were re-uploaded (`material_data->uniform_set_updated`, sky:1093-1096). `SkyMaterialData::update_parameters` sets that flag on every update (sky:190-195). An update is queued by **any** `material_set_param` (ms:2574-2590), and `ShaderMaterial.set_shader_parameter` forwards every call without comparing values (`material.cpp`). **Writing a sky uniform, even with an unchanged value, dirties the map.**
- the shader uses `TIME` and time advanced, or uses `POSITION` and the camera moved (sky:1040-1044, 1098-1101). `ring_local_sky.gdshader` and its includes use neither: checked by search.
- any of seven fog values of the Environment being rendered differs from the value stored on the Sky at the last call (enabled, density, light colour, light energy, sun scatter, aerial perspective, sky affect; sky:1046-1086). These are compared even while fog is off.
- the directional-light buffer changed, but only if the shader reads `LIGHT*` or fog sun scatter is on (sky:1104-1197). The ring sky reads no `LIGHT*`, and depth fog is off on the surface by default (`enable_global_depth_fog = false`, `day_side_atmosphere.gd:162`).
- the material changed, the mode or radiance size changed, or the map was (re)created (sky:1088-1091, 1680-1711, 1516-1597).

**Not a dirtying event:** changing a non-texture global shader parameter (ms:1895-1913 only marks the global buffer dirty). Changing a texture global queues an update for the materials that read it. Changing a texture's contents in place (the sky-view LUT, the storm map) dirties nothing.

### 1.2 What each mode does (`SkyRD::update_radiance_buffers`, sky:1282-1400)

- **The map is re-rendered only while it is dirty, in every mode** (sky:1305). Realtime does not re-render a clean sky. Today's per-frame cost therefore means something dirties the sky every frame (1.3).
- **Realtime** (dirty): one render of the sky shader with `AT_CUBEMAP_PASS` into the base octmap, then the fast filter: a 5-level downsample to 160 x 160 and a filter into each of the 7 roughness layers (`REAL_TIME_ROUGHNESS_LAYERS`, sky.h:262). With array reflections (the default) every layer's mip chain is then regenerated (sky:1365-1369, 376-424). The map is clean again after one frame.
- **Size of that render:** with array reflections a 256 map is allocated as 2 x 256 + 2 x 64 = **640 x 640** texels (sky:1535-1546; the arithmetic is mine). The sky shader therefore runs about 410,000 times per rebuild at `hp.quality = 4`, with the LUT or analytic haze, two 40 km storm-path evaluations and the planet.
- **Incremental:** a dirty flag is acted on only once the previous pass has finished all layers (`processing_layer >= max_processing_layer`, sky:1305). It then re-renders the base and sets the layer counter to 1. Each later frame importance-samples **one** layer (32 GGX samples, 8 layers by default, sky:1390-1398). A change that arrives mid-pass waits for the pass to end, so the roughest layer (the one ambient reads) lags a change by up to about 2 x 8 frames. The layers also come from different moments in that window. The first frame after a mode change runs a full Quality rebuild (sky:1296-1300).
- **Quality:** base plus all importance-sampled layers in one frame whenever the map is dirty.
- **Changing the process mode or radiance size** frees the map and rebuilds it (sky:1696-1702, 1680-1686, 1027-1028). With the next frame in Incremental that rebuild is a full Quality one. Switching modes at run time is a spike plus a texture swap.

### 1.3 What can change the realtime cost

- `radiance_size`: Realtime is fixed at 256 (`REAL_TIME_SIZE`, sky.h:261; `Sky.xml`: another size is ignored with a warning). **No lever.**
- `rendering/reflections/sky_reflections/roughness_layers` and `ggx_samples`: not used by Realtime (fixed 7 layers; the fast filter does not importance-sample). **No lever.**
- `texture_array_reflections` (default true, not overridden in `project.godot`): with false the map is one texture with mips; the fast filter writes the mips directly and the 7 per-layer mip chains are skipped (sky:400-422). This is cheaper (est.), but project-wide (probes too) and the engine documents it as lower quality ("jitter noise and upscaling artifacts"). **A look change; not part of R1-6** (section 3, D-b).
- `fast_filter_high_quality`: only raises the cost.
- The cost of the sky shader's cubemap branch. This is the game's own lever (section 3, D-c).

### 1.4 What dirties the ring sky every frame today

- `day_side_atmosphere.gd:_process` calls `_update_sky` every frame. `_set_sky_param` skips unchanged values, but `ring_sky_material.set_shader_parameter("ring_radius_meters", ...)` (line 1313) is written uncached every frame. **That alone dirties the map every frame** (1.1). Many cached uniforms also change every frame whenever anything moves: `sun_direction`, `sun_color` (from the sun light colour), `sky_top_color` and `sky_horizon_color` (day factor), `local_surface_up`, the camera ratios and height, `gas_giant_spin_radians`, `starfield_rotation_radians`.
- **Fog flip between the two viewports** (likely; to be confirmed by R1-6a): the far Environment (`RingScaledFarViewport.cs:1030-1039`) has engine-default fog values with fog off. The main Environment gets day- and night-blended fog values every frame (`day_side_atmosphere.gd:504-518`). Both render the same Sky, so each `setup_sky` call sees the other Environment's stored values and marks the map dirty. The map is then probably rebuilt **twice per frame**: in the far viewport, then again in the main one. In water and caves the main Environment really has fog on (`_apply_underwater_environment`, `_apply_underground_fog_coupling`), and the two rebuilds differ: fog is applied in the cubemap pass (`sky.glsl:279-303`).
- **The measurement that does not fit.** If the map is dirty every frame, Incremental should importance-sample a layer every frame. That is est. 0.10 to 0.15 ms (the +0.98 ms of Quality spread over 7 layers), not the measured -0.18 ms. Possible explanations: dev7 differs from `master` here; the metric `gpu_total_p50` does not count the viewport where the work lands; or something in the mode switch keeps Incremental idle. R1-6a measures this directly. The recommended design never uses Incremental, so its outcome only changes the size of the saving (one or two rebuilds per frame today).

### 1.5 The far viewport shares the Sky (verified, `RingScaledFarViewport.cs:1027-1039`)

The whole-ring far Environment reuses the main `Environment.Sky` with `BGMode.Sky`: it draws the visible sky with the same material and reads the same radiance map for its own ambient (source copied: Sky). After R1-0 it reads it for reflections too (pinned to Background, which with a sky background is the radiance map). The regional far Environment has a colour background and no Sky, so it does not touch the map. The main Environment's background is black (`RingFarViewportColorPipeline.ConfigureMainEnvironment`), so **the main view never draws the sky**: its Sky only feeds ambient and, after R1-0, reflections. A viewport that draws a sky always calls `update_radiance_buffers` for it (rfc:2155-2161), whatever its ambient source.

## 2. What the radiance map depends on

Inputs of `ring_local_sky.gdshader` in the cubemap pass, and how they change:

| Input | Path today | Change rate in play |
|---|---|---|
| Sun direction (and the hub-blended light), sun colour | material | day cycle 4,500 s: 0.08 deg/s |
| Local up, hub direction (`-outward`), camera height, across and radial ratios | material | travel; at 36.84 km/s over R = 5,000 km local up turns 0.42 deg/s |
| Legacy palette (`sky_top_color`, `sky_horizon_color`), `atmosphere_factor`, `ring_interior_factor` | material | day factor, altitude, eclipse tint, hub tint, underground and underwater access |
| Eclipse coverage and solar transmittance | material | eclipse contacts: minutes |
| Hub level, colour, halo, in-scatter | material | hub events |
| Giant direction, angular radius, spin; `planet_haze_enabled` | material | slow (spin 0.01 deg/s) |
| Stars (visibility, rotation) | material | **not in the cubemap pass** (`!AT_CUBEMAP_PASS`), but writing them dirties the map |
| Haze model (sigma, heights, quality, storm vector, field profile) | material via `RingAtmosphereModel.ConfigureMaterial`, plus globals | settings revisions; `ringworld_haze_storm` blends with `cloud_lighting_blend_speed` |
| Sun irradiance, ambient day and night, night light scale, solar visibility, hub day | globals | per frame while they move |
| Storm: `ringworld_storm_light`, `_light_mega` (incl. megastorm flash in `.z`), `ringworld_storm_frame`, `ringworld_megastorm_darkness`, storm map | globals and a texture array | storm arrival is minutes; the flash is a pulse of a few frames |
| Sky-view LUT and its key | texture globals, rebuilt in place | every frame the sun or camera moves (`RingAtmosphereLuts.RequestSkyView`) |

Lightning: only the megastorm flash (`ringworld_storm_light_mega.z`) reaches the sky shader, and only if the storm-path helpers fold it into K. That was not traced to the end; R1-6c treats the flash as an input either way. Natural-storm bolts (the overlay) are not in the sky shader (search).

How much the map's content matters for the image:
- Ambient: `ambient_light_sky_contribution` = 0.35 (x 0.16 in a storm) mixes the sky irradiance with the per-frame ambient colour and energy. The colour, energy and contribution are Environment properties written every frame and **do not dirty the map** (1.1). So a step of x % in the map steps ambient by at most 0.35 x % (verified mixing: R1-1 design section 1; magnitudes est.).
- Reflections after R1-0 and R1-1: the sky term is the map itself, scaled by Fresnel. A grazing water reflection of the horizon sky shows a step in the map at full size. **Reflections set the thresholds, not ambient.**

## 3. Designs compared

**A. One Sky; switch the process mode by a rule.** Rejected. A mode change frees and rebuilds the map (1.2): a full Quality rebuild (+~1 ms est.) plus a texture swap on every switch. And no cheap mode exists to switch to: a dirty map is rebuilt in every mode, so a cheap mode needs the dirtying stopped first, and once it is stopped Realtime is already cheap.

**B. Two Skies: a visible one for the far viewport, a throttled one for main ambient and reflections.** Rejected. A viewport that draws a sky always updates that sky's radiance map when it is dirty (rfc:2155-2161), and the visible sky's material is written every frame. The far viewport would keep paying 0.18 ms for its own map, and main would pay again at the throttled cadence. Making the far map cheap (a trivial cubemap branch, or Quality at size 32) changes the far viewport's ambient and its far-water reflection (it touches the far image), and splits main and far ambient that are one map today. The visible far sky does not need a map, but the engine gives no way to draw a sky without one.

**C. Incremental with controlled invalidation.** Rejected. Even with perfect invalidation, a refresh spreads over 8 frames, and the roughest layer, the one ambient reads, comes last. A change during a pass waits for it to end: worst-case lag about 16 frames, with layers from different moments (1.2). That is a visible ambient lag at a teleport, a lightning flash or an eclipse edge. Each refresh also costs more in total than a Realtime one (+0.98 ms per full Quality pass, measured). Controlled invalidation also needs the per-frame writes to stop, which is the work of D anyway.

**D-a (recommended). One Sky, Realtime, live inputs as globals, refresh by epoch.**
- Every input in section 2 that `_process` can change between two frames becomes a global shader parameter. The sky shader reads it from there in both passes. The far viewport's visible sky reads the same values every frame, so it stays bit-identical (32-bit floats either way).
- The sky material keeps only static values (palette constants, planet colours, seed, haze model parameters written on a settings revision) plus `uniform float radiance_epoch`.
- A controller (section 4) writes `radiance_epoch` when the map should refresh. The engine then rebuilds the map that frame in Realtime from the live globals, so the map is exactly today's map as of that frame.
- Stray dirtiers are removed: the uncached `ring_radius_meters` write, and (if R1-6a confirms it) the fog flip between main and far.

**D-a' (fallback if D-a's churn is judged too large): snapshot uniforms.** The material keeps all of today's uniforms as the radiance snapshot, written only on a refresh. A parallel global block carries the live values for the visible pass, and the shader picks `AT_CUBEMAP_PASS ? uniform : global` per input. Tests that read `ring_sky_material` keep working (they see the snapshot), but every dynamic input exists twice and the per-pass selection is easy to get wrong. Prefer D-a.

**D-b. `texture_array_reflections = false`.** A cheaper rebuild (est.), but a look change to every sky reflection and every reflection probe. Not part of R1-6; if the budget is still tight after R3, measure it as a separate study with stills for the user.

**D-c. A cheaper cubemap branch** (for example one 40 km storm-path evaluation instead of two, or a lower haze quality in the radiance pass). It changes the map's content, and with D-a rebuilds are rare, so it buys little. Revisit only if R2 makes the cubemap branch much heavier.

## 4. The recommended design in detail

### 4.1 Shader and script split (order R1-6b)

- **Global block.** The global parameters `ringworld_sky_live0` to `ringworld_sky_liveN`, as packed `vec4`s, are declared in `project.godot [shader_globals]` with defaults equal to today's uniform defaults. One packer in `day_side_atmosphere.gd` writes them, on change only. One include, `materials/ring_local_sky_live.gdshaderinc`, unpacks them as named accessors, so the shader body changes only by name substitution.
- **Live, so global:** `local_surface_up`, `ring_axis_direction`, `camera_radial_ratio`, `camera_across_ratio`, `sky_camera_height_meters`, `ring_interior_factor`, `sky_top_color`, `sky_horizon_color`, `atmosphere_factor`, `sun_direction`, `sun_color`, `hub_direction`, `hub_level`, `hub_color`, `hub_halo_intensity`, `hub_in_scatter`, `eclipse_coverage`, `eclipse_solar_transmittance`, `ring_atmosphere_planet_direction`, `star_visibility`, `starfield_rotation_radians`, `giant_direction`, `planet_angular_radius_degrees`, `gas_giant_spin_radians`, `planet_haze_enabled`, `planet_enabled`, `day_start_dot`, `day_full_dot`.
- **The rule for any future input:** if `_process` can write it, it is live. Static uniforms stay on the material and are written only on change (the existing `_set_sky_param` cache). `ring_radius_meters` joins the cache. `RingAtmosphereModel.ConfigureMaterial` writes stay as they are: they happen on registration and on settings revisions, and each one is a correct refresh.
- **Epoch:** `uniform float radiance_epoch;`, declared and read harmlessly (for example `COLOR += vec3(radiance_epoch * 0.0)`, or a comment that the engine keeps it). The engine dirties on the write whether or not the shader uses it (1.1), so this is robust either way.
- The existing globals the sky reads (haze irradiance, storm, LUT) stay as they are.
- **Tests that read live values from `ring_sky_material`** (`tools/celestial_runtime_integration_smoke_test.gd`, `tools/celestial_visual_smoke_test.gd`, `tools/eclipse_darkness_parity_test.gd`) read a `get_sky_live_inputs()` dictionary on the atmosphere node instead. `celestial_visual_smoke_test` duplicates the material for a preview viewport: its visible pass now follows the live globals, which match the source scene. `tools/haze_consistency_gpu_test.gd` parses the sky source and turns uniforms into its own globals: it must learn the include.

### 4.2 Update rule (order R1-6c; all thresholds est., tuned in R1-6e)

New class `scripts/Atmosphere/RingSkyRadianceCadence.cs`. Pure logic, testable headless. `day_side_atmosphere.gd` feeds it once per frame, after the live values are packed, and writes `radiance_epoch += 1` on "refresh". Refresh this frame when any of these holds since the last refresh:

| Trigger | Threshold (est.) | Why |
|---|---|---|
| First frame, scene (re)load, `RingAtmosphereModel.Revision` or `StormRevision` changed, haze model changed, LUT key valid flag changed | always | discrete |
| Teleport: camera displacement > 1 km in one frame | always | discrete; up and height also catch it |
| Sun direction (also the hub-blended light), hub direction, giant direction | 0.02 deg | sub-texel at the sharpest layer (640 texels across the octmap, est. 0.5 deg/texel); standing at noon: a refresh about every 0.25 s |
| Local up | 0.02 deg | 1.75 km of travel at R = 5,000 km |
| Camera height | max(1 m, 0.2 % of height) | haze path |
| Across position | 100 m | sidewall geometry (half width 159 km) |
| Relative luminance of the published haze sun irradiance and haze ambient (day, night) | 0.4 % | the twilight guard: near the horizon sky radiance changes e-fold in est. 1.5 deg of sun, so the angle rule alone would step it |
| `atmosphere_factor`, `ring_interior_factor`, eclipse coverage and transmittance, solar visibility, hub level, day and weight, night light scale, each component of `ringworld_haze_storm`, `ringworld_storm_light` and `ringworld_storm_light_mega` .x/.y | 0.003 absolute | events, storm arrival, caves, water |
| Megastorm flash (`ringworld_storm_light_mega.z`) | any change, and every frame while > 0 | lightning; rare and short |
| Legacy palette colours, sun colour | 0.4 % relative | legacy model and colour shifts |
| Planet spin | 0.2 deg | negligible content |
| Age | 0.5 s | catch-all for in-place texture contents (storm map, megastorm darkness) and anything not tracked |

No minimum interval. A refresh costs one Realtime rebuild in one frame, which is what every frame costs today. In fast flight through twilight the rule may fire every frame, which is today's cost.

Debug switch (not a menu row): an exported property `sky_radiance_cadence_enabled` on the DaySideAtmosphere node. The `ab_matrix` op `{"node": ..., "set": {...}}` reaches it. Off means a refresh every frame (today's behaviour). Default off until R1-6e accepts, then on. There is also a counter, `sky_radiance_refresh_count`, for the bench and tests.

### 4.3 How ambient and reflections stay continuous

The engine offers no blend between two radiance maps. Continuity rests on step size:
- a refresh copies the full live state, so the error never accumulates past one threshold;
- ambient sees at most 0.35 x the map step (0.06 x in a storm);
- the per-frame ambient colour and energy, the sun light and the exposure keep moving smoothly every frame because they are not in the map.

At a 0.4 % map step the ambient step is at most 0.14 %, and a reflected-sky step at most 0.4 %. Est.: both are below one 8-bit code value after AgX in mid-tones. R1-6e checks this with movies, not by argument.

### 4.4 Events

- **Teleport:** refreshed the same frame (displacement, up and height triggers). The LUT is rebuilt on the render thread the same frame, as today. The first frame matches today's.
- **Eclipse onset and totality:** coverage and transmittance are tracked at 0.003, so a refresh comes every few frames through the fast part of the contact, and the irradiance trigger adds more.
- **Lightning:** megastorm flashes refresh every frame while the flash is non-zero; natural bolts do not reach the sky.
- **Storm arrival:** the storm vectors blend at `cloud_lighting_blend_speed`, so refreshes come every few frames while the blend moves. Storm-map motion inside the 40 km path is covered by the 0.5 s age (est. well under 0.4 % per 0.5 s).
- **Water and caves:** main fog is on, so the engine dirties the map through the fog values every frame (1.1, 1.4). Cost and look are as today: no saving there, and no regression.

### 4.5 The fog flip (order R1-6d; only if R1-6a shows a second rebuild per frame)

While the main Environment's fog is off at the end of `_process`, its six fog values (density, colour, energy, sun scatter, aerial perspective, sky affect) are set to the same constants the far Environment is built with, written only on change. They are then equal in both Environments, and fog off makes them unused (`sky.glsl:282`, scene fog likewise).
- The base values are computed as today into locals. `_apply_underwater_environment` and `_apply_underground_fog_coupling` lerp from those locals, not from the Environment's stored values. The fog-on image (water, caves, debug depth fog) is therefore unchanged.
- `_publish_enclosed_cloud_fog` already ignores the values while fog is off.

## 5. Expected saving (est.)

Per-rebuild cost r = 0.18 ms (measured as the Realtime total; if R1-6a shows two rebuilds per frame today, r is about 0.09 ms per rebuild and the baseline is 2r).

| Situation | Refresh rate (est.) | Average cost | Saving |
|---|---|---|---|
| Standing, day (sun 0.08 deg/s) | every ~0.25 s (1 in 15 at 60 fps) | 0.01 ms | 0.17 ms |
| Standing, dawn or dusk (irradiance trigger) | 1 in 4 to 1 in 6 | 0.03 to 0.05 ms | 0.13 to 0.15 ms |
| Storm blending in, eclipse contact | 1 in 2 to 1 in 4 | 0.05 to 0.09 ms | 0.09 to 0.13 ms |
| Fly 36.84 km/s by day (up 0.42 deg/s) | 1 in 3 | 0.06 ms | 0.12 ms |
| Fly fast through twilight; water; caves | every frame | as today | 0 |
| Clocks held (bench stills) | 0.5 s age only | under 0.01 ms | about 0.18 ms |

## 6. Acceptance (R1-6e)

**Cost.** Use `tools/perf_bench.ps1 -ScenarioFile tools/fixtures/perf_bench_reflection_costs.json`, kind `ab_matrix`, baseline = switch off, variant = switch on (op on the DaySideAtmosphere node). Scenarios:
- `sky_radiance_plains` with clocks held;
- the same pose at noon and at dawn (sun elevation about 2 deg) with clocks **running** (`hold_clocks: false`);
- wet plains;
- the lifted coast.

Accept if:
- every variant delta of GPU p50 is at most -0.09 ms, except where the rule fires every frame by design (report those);
- `wall_p95` is not worse than the noise (0.03 ms on GPU; read the run's spread).

Also report `sky_radiance_refresh_count` per window. For `fly_36840` (kind `fly`) use two runs, on and off; report the deltas and the refresh rate.

**Identity.**
- With the switch off: final frames byte-identical to today's tree (before R1-6b) at three poses with clocks and noise held. This proves the global move changed nothing.
- With the switch on, clocks held: byte-identical to switch off after settling, the far viewport texture included.
- Headless: a unit test of the controller (each trigger, the age cap, teleport), and an invariant smoke test that runs the scene for N frames with clocks running and asserts that the sky material is written only on refresh frames. That catches a future uncached write.

**Movies** (Movie Maker, `--fixed-fps 30`, switch on and off, same path and seed; `tools/media/ffmpeg_tools.ps1 flicker` on each, `compare` on matching frames; with the reflection model at 1 and the B1 mirror sphere in view, because reflections are the strictest viewer):
1. sunrise and sunset, clocks running (a raised celestial time scale is a harsher test and acceptable);
2. a passing storm and its trailing edge (`tools/wet_world_capture.gd` views);
3. an eclipse contact;
4. `fly_36840` through dusk;
5. a teleport pair;
6. a megastorm frame with flashes.

Accept if the on-run's flicker list has no periodic spikes and no spike above the off-run's largest plus noise, and if on/off matching frames show no structured difference in the heatmap. Judge the heatmap, not SSIM: cloud noise alone gives about 0.997.

## 7. Work orders

**R1-6a Evidence (Lead or implementer, GPU, no game code).**
- Use the per-pass timestamp harness of the perf-capture skill (RenderingServer frame profile) at `plains_eye`, clocks held. Record the GPU time of the "Setup Sky" scope per viewport (far and main) in Realtime, Incremental and Quality.
- Repeat in Realtime with `ring_radius_meters` no longer written every frame (temporary local edit, not committed), and with the far Environment's fog values set equal to main's (temporary).

The answers: how many rebuilds per frame today, and in which viewport; whether the uncached write and the fog flip are the dirtiers; why Incremental measured free.
- Escalate if a clean sky still costs time in Realtime: that would contradict 1.2 and mean dev7 differs. Return to the Lead before R1-6b.

**R1-6b Live globals and epoch (implementer).**
- Files: `materials/ring_local_sky.gdshader`, new `materials/ring_local_sky_live.gdshaderinc`, `project.godot [shader_globals]`, `scripts/day_side_atmosphere.gd` (`_update_sky` packer, cache `ring_radius_meters`, `get_sky_live_inputs()`), the four tests named in 4.1.
- Change: as in 4.1. No cadence yet: the epoch is written every frame, so behaviour equals today.
- Do not change the sky math, the radiance size or mode, the far viewport, `RingAtmosphereLuts`, or any other shader.
- Verify:
  - `tools/run_tests.ps1 -Files <the files> -Label r1-6b` (`-DryRun` first; expect the celestial, eclipse and haze-consistency tests);
  - the byte-identity check of section 6 (switch-off frames against a capture taken before the change) at three poses.
- Escalate if:
  - any value cannot move to a global without a visible difference;
  - a test depends on writing sky uniforms to drive the sky (it would need a design answer);
  - the global count in `project.godot` hits an engine limit.

**R1-6c Controller (implementer).**
- Files: new `scripts/Atmosphere/RingSkyRadianceCadence.cs`, `scripts/day_side_atmosphere.gd` (feed it, write the epoch, the switch and the counter), new headless test `sky_radiance_cadence_test` and the invariant smoke test `sky_material_write_invariant_test`, both registered in `tools/test_manifest.json`.
- Change: the rule of 4.2 with the thresholds as named constants. Switch default off.
- Do not add a menu row. Do not change the process mode.
- Verify: the two tests; `-Since HEAD`; one `ab_matrix` run of `sky_radiance_plains` with the switch as the variant.
- Escalate if: an input in section 2 cannot be read on the main thread without a sync (for example only the render thread knows it); or the saving with clocks held is under 0.12 ms (that points back to R1-6a).

**R1-6d Fog flip (implementer; only if R1-6a shows it).**
- Files: `scripts/day_side_atmosphere.gd` (fog value flow per 4.5), `scripts/Voxel/LOD/RingScaledFarViewport.cs` (name the far fog constants once and share them).
- Verify: underwater and cave stills byte-identical to before (the fog-on image); `tools/eclipse_darkness_parity_test.gd` and the underground environment smoke test; one frame-profile run showing a single rebuild per refresh frame.
- Escalate if any fog value is read anywhere while fog is off.

**R1-6e Acceptance and default on (Lead).** Section 6, tune the thresholds, then switch default on.

**Order:** R1-6a first. R1-6b and R1-6c in sequence (both touch `day_side_atmosphere.gd`), R1-6d alongside R1-6c if needed, then R1-6e. Files shared with other open orders: `day_side_atmosphere.gd` is also named by R1-1b (it publishes `ringworld_sky_reflection`). Sequence the two edits; do not run them in parallel.

## 8. Risks

- **A missed live input** gives a radiance map that lags it by up to 0.5 s and then steps. Mitigations: the write-invariant test catches material writes; the 0.5 s age bounds global-only inputs; the movie list covers each event class.
- **R2 and R3 add moving content** (cloud decks, the far mirror's sky) to the radiance pass. Moving clouds in the sharp layers would step at the cadence. R2 must declare each new input to the controller, keep cloud content to the blurred layers, or lower the age for it.
- **Engine version:** read on `master`. The dirty logic is what matters to the design. Whether dev7 renders octmaps or cubemaps does not change it, and R1-6a confirms the behaviour on the real build.
- The `ab_matrix` stills are taken at a held pose, so they cannot show a cadence step. Only the movies can.

## 9. Open questions

For the primary agent:
1. D-a (globals) or D-a' (snapshot uniforms): D-a is cleaner but changes four tests and one harness. Recommended: D-a.
2. Is the 0.5 s age cap acceptable as the guard for untracked inputs, or should storm-map motion get its own trigger (a revision counter in the storm map publisher)?

For the user (with the R1-6e movies): none expected. This change is meant to be invisible; if a movie shows a step that the thresholds cannot remove without losing the saving, that goes to the user with the clip.
