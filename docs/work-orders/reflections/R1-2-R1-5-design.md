# R1-2 and R1-5 design: water reflection composition and the water march

Lead design, 2026-11-17. Part of `docs/reflections-plan.md` section 10. Read 10.2, 10.4 and 10.5 first; the order names here match 10.4. The sibling design for the other receivers is `docs/work-orders/reflections/R1-1-design.md`. Its R1-0 provides the switch this design builds on.

Engine lines cite the Godot master copy read for this design (`scene_forward_clustered.glsl` = "sfc", `render_forward_clustered.cpp` = "rfc", `render_scene_data_rd.cpp` = "rsd", `scene_forward_lights_inc.glsl` = "lights", `forward_clustered/scene_forward_clustered_inc.glsl` = "cinc"). Master is 4.8 beta; the game runs 4.8-dev7, which was not read. Game lines cite the working tree of 2026-11-17. On that date none of the files this design edits had uncommitted changes. **Every value marked "est." is an estimate, not a measurement.**

## 0. Orders

| Order | What | Files | Can start | Depends on |
|---|---|---|---|---|
| R1-2a | New composition at model 1, old path unchanged at model 0; near and far water | `materials/ring_ocean_proxy.gdshader`, `materials/ring_ocean_global.gdshader`, `materials/ring_water_appearance.gdshaderinc`, new `tools/water_reflection_model_contract_test.gd`, `tools/test_manifest.json` | when R1-0 has landed | R1-0 (`ringworld_reflection_model` registered, `materials/ring_reflection_model.gdshaderinc`) |
| R1-2c | Stills of model 0 against model 1 for water; they feed R1-7 | probe tooling (R1-1-0) | after R1-2a | R1-1-0, R1-2a |
| R1-5a | Horizon march at model 1, old march unchanged at model 0 | `materials/ring_ocean_proxy.gdshader`, the new contract test, an additive model-1 variant of `tools/water_ssr_live_controls_gpu_test.gd` | after R1-2a | R1-2a |
| R1-5c | Cost and look of the march: matrix and probe, flicker movies; choose the camera distance for High | perf bench fixtures | after R1-5a | R1-5a |
| R1-8 (water part) | After sign-off: preset values, menu rows, retired keys, delete the old paths and uniforms | settings code and tests (section 5.3) | after R1-7 | user sign-off and the user's answer on the rows |
| R3a-3 (water part) | Clouds composited into water hits | `ring_ocean_proxy.gdshader` (section 4) | after R3a-2 and R1-5a | R3a-2 |

Settings, menu rows and retired keys are **not** part of R1-2a or R1-5a. Plan 10.4 lists them as "R1-2b at sign-off"; here they are folded into R1-8. Until then the old uniforms and rows stay. The model 1 path ignores the ones that have no physical meaning.

## 1. Engine facts (verified by reading)

1. **Transparent pass.** The fragment code is the same as the opaque pass, without separate specular (rfc:2512). `RADIANCE` is mixed over the sky term at sfc:1741-1743. Output: `frag_color = emission + ambient + diffuse + direct_specular + indirect_specular` (sfc:3183).
   - The transparent pass builds its scene data with `p_opaque_render_buffers = false` (rfc:2507). This sets `ss_effects_flags = 0` (rfc:769-785). **The engine SSR, SSAO and SSIL never touch the water**, so there is no double reflection.
   - No ReflectionProbe, VoxelGI or SDFGI exists in the repo.
2. **Order of operations for the indirect specular:**
   - sky sample × horizon² × background energy (sfc:1716-1736);
   - `RADIANCE` mix (sfc:1741);
   - specular occlusion (sfc:2256-2263);
   - Fresnel and energy terms (sfc:2317-2341): `E = ec·((f90 − f0)·X + f0·Y)`, `f0 = 0.16·S²` (lights `F0()`), `f90 = clamp(50·f0, metallic, 1)`, `X, Y` from the DFG LUT `prefiltered_dfg(roughness, NdotV)` (cinc:508), `ec = 1 + f0·(1/Y − 1)` (cinc:514).
   - `X = ∫G·Fc` and `Y = ∫G`, so for roughness → 0, `E` = Schlick(f0, f90).
3. **Today's F0 is a third of water's.** `ring_water_dielectric_specular()` (`ring_water_appearance.gdshaderinc:46-48`) assumes a linear SPECULAR → F0 mapping.
   - Proxy: `S = 0.25 · 0.88 · 0.92 = 0.202` (`ocean_specular` 0.92), so **F0 = 0.0066 and f90 = 0.33**.
   - Global: `S = 0.194`, so F0 = 0.0060 and f90 = 0.30.
   - The haze and storm factors multiply S, so they act squared on F0.
   - **F0 = 0.02 needs `S = sqrt(0.02 / 0.16) = 0.353553`, which gives f90 = 1.0 exactly.**
4. **Linear-scale property.** With base F0 exactly 0.02, `S = 0.353553·sqrt(k)` gives `f0 = 0.02·k` and `f90 = k`, so `E = k·E0` for k ≤ 1. The only deviation is `ec`, under 0.2 %. Scaling S by sqrt(k) is therefore an exact linear scale on the whole indirect specular, grazing part included.
5. **Specular occlusion.** For a dielectric it is `SO = min(8·lum(ambient·AO), 1)` (sfc:2256-2263), applied to sky and `RADIANCE` alike, before Fresnel. Plan 10.1 relies on it as the only thing that dims reflections at night, in storms and in caves.
   - A per-material `render_mode specular_occlusion_disabled` exists on master (`shader_types.cpp:263`). Whether dev7 has it was not checked.
   - **This design does not use it** (section 2.4).
6. **The water has no direct sun glint today.** `light()` (`ring_ocean_proxy.gdshader:661-742`) writes only `DIFFUSE_LIGHT`. With custom light code the engine adds no specular light (lights:121-176).
   - After R1-0 and R1-2a, the sun glint is the sun disc of the radiance map. That disc is not shadowed by local clouds or terrain (open point, section 7).
7. **Units.** There are no physical light units, so `IBL_exposure_normalization` is 1 (rsd:251-258). Emission is scaled by the exposure normalization, which is 1.0. The screen texture is linear HDR before tonemap, in the same units as the sky term.
8. **The radiance pass already handles storms.** Under a storm it darkens its sky (`ring_haze_apply_storm_path`: `S_amb *= ring_storm_sky`) and hides the sun disc and halo with the same sun fade the receivers use (`ring_local_sky.gdshader:250-255`, `:313-319`).
9. **Where `ring_ocean_global` runs:**
   - Main viewport: `GlobalTerrainLod3` (`RingTerrainGlobalLod.cs:1621`), beyond about 4 to 5.5 km. Its environment is black at model 0.
   - Scaled far viewport (`RingScaledFarViewport.cs:1218`). Its environment has a Sky background, so with the source pinned to Background (R1-0) it **reflects the radiance map at both model values**.
   - `ring_ocean_proxy` runs only in the main viewport (`RingTerrainProxySector.cs:6776`).
   - The regional far viewport is off in gameplay (`RegionalFarLodsEnabled` is never set).
10. **The user's water values** (snapshot `slot3_2026-11-16`, `gameplay_water_defaults.tres`) override the High preset:
    - coarse steps 32, refinements 8, ray 1,000 m, camera distance 695 m;
    - depth tolerance near 2.0, far 3.92;
    - bias 0.01, edge fade 0.06, roughness cutoff 0.37, strength 1.0.
    - The Fresnel rows are not overridden.
11. **Measured** (plan 9.2, lifted coast pose, user's values, RTX 4070, 1600 x 900):
    - water SSR off: −0.09 ms;
    - 12 steps with a 60 m ray: −0.10 ms;
    - Very High values: −0.02 ms;
    - reflected source Sky: +0.08 ms at the coast (+0.05 and +0.07 ms on the wet plains).
    - So the whole water march costs about 0.1 ms at this pose. A waterline pose and a lake are not yet measured.

## 2. R1-2: the composition

### 2.1 Target

Pixel radiance at model 1:

```
L = S_in·(1 − F·a)                         camera-path in-scatter, minus the share already inside a hit
  + (1 − F)·T·(lit body + inland transmitted)
  + F·a·L_hit                              hit from the march (screen colour), through EMISSION
  + k·(1 − a)·E0·L_sky                     engine sky term, through SPECULAR
```

where:
- `F` = `ring_water_env_reflectance(ROUGHNESS, NdotV)`, a fit of the same split-sum integral the engine's LUT holds;
- `a` = hit confidence × `storm_ssr_keep`;
- `k` = `ring_haze_specular_scale(haze)` (mean T; 1 in Legacy);
- `E0` = the engine's E at F0 0.02.

### 2.2 Energy

- The body (optical water and foam) is weighted by `1 − F`.
- `F` must match the engine's E, not Schlick. Example at roughness 0.16 and NdotV 0.05: the Karis fit gives 0.63, Schlick 0.78. That would put the transmitted weight 15 points off at grazing.
- Karis against the engine LUT: within a few % (est.).
- Foam keeps its specular: it is wet, and its roughness already mixes toward 0.48. So the foam albedo also takes `1 − F`.
- Non-physical scales ignored at model 1: `fresnel_strength` (0.88; physical is 1.0, so about 14 % more reflection than the tuned look), `ocean_specular`, `water_ssr_strength`, and the `fresnel_f0` uniform (the constant 0.02 is used).

### 2.3 Haze on a hit

- A screen pixel holds `L_hit·T(d_ch) + S(d_ch)` along the camera ray.
- The mirror path is camera→water→hit, of length `d_cw + d_wh`. That is the distance to the mirror image and close to `d_ch` for the grazing rays water reflects.
- So the hit already carries the hit→water and water→camera haze. **Apply no T to a hit.** Reduce the in-scatter emission by `F·a`, so the camera-path in-scatter is not counted twice.
- Sky misses get `k` (the water→camera T) and the full in-scatter. That is right, because the radiance map is the sky seen from the camera.
- Error: the hit path is slightly short (triangle inequality), so it carries slightly too little haze (est. negligible within 1 km).
- The same rule holds for hits at infinity (backdrop pixels already carry the full atmosphere path), for cloud composites (section 4) and later for far-mirror samples.

### 2.4 Hit through EMISSION, not RADIANCE; no `specular_occlusion_disabled`

Plan 10.4 names `RADIANCE` plus `render_mode specular_occlusion_disabled`. This design replaces both with the exactly equivalent route of fact 4:

- `EMISSION += F·a·L_hit`
- `SPECULAR = 0.353553·sqrt(k·(1 − a))`
- `RADIANCE` is not written

Reasons:
1. **A render mode cannot branch on the model.**
   - Proxy: adding it would not change model 0. Its environment is black at model 0, and occlusion × black = 0.
   - `ring_ocean_global`: it **would** change model 0 in the scaled far viewport, which reflects the radiance map at both values (fact 9), wherever `8·lum(ambient·AO) < 1` (night, deep storm, cloud-shadow AO).
   - The alternative would be a second shader file that differs only in its render mode, swapped in by C#. `RingWaterModel.Ocean.cs:206`, `:307` and `:332` gate on the shader path, so that means more edits for no gain.
2. **Consistency with R1-1.** Specular occlusion is how night, storm and cave dimming reaches every other receiver (plan 10.1). With the hit in `EMISSION`, the sky term keeps that occlusion in near and far water alike, so the 4 to 5.5 km handoff has no seam. The hit is immune to occlusion, so a lit POI or the planet seen in the water does not vanish at night.
3. **No dev7 compile risk.** Nothing new is needed from the engine.

Cost of the change: the hit is weighted by the fit `F`, while the sky term uses the engine LUT. The mismatch is a few % (est.) on the hit part only.

When R1-1b has landed, multiply `k` by `ringworld_sky_reflection` (underground and underwater) so cave water does not mirror the sky. If R1-1b is not in yet, leave a one-line TODO naming R1-1b.

### 2.5 Storm

- **Drop** `storm_reflection` and `mix(1.0, 0.02, ...)` from `SPECULAR` at model 1. The radiance pass already darkens the sky and hides the sun under a storm (fact 8); keeping them dims twice.
- **Keep** `storm_ssr_keep` on `a`: the screen texture has no near clouds (until section 4).
- **Keep** `storm_reflection` in `AO` (ambient only, unchanged).

### 2.6 Continuity

- **River mouths.** `inland_weight` ramps as today. The body and the inland emission both use `1 − F`. The reflection term does not depend on the kind. No seam.
- **Near and far ocean.** `ring_ocean_global` gets the same model at 1: no constant blue, `ALBEDO = water_tint·(1 − F)·T`, `SPECULAR = 0.353553·sqrt(k)`, no march. Main-viewport and far-viewport water then reflect the same sky with the same F.
- **Expected tone change (est.).** Far water sits at `far_water_roughness` 0.46, where E is about 0.17 at grazing, against today's fixed `0.37 × blue`. Far ocean and lakes beyond about 1 km will reflect less sky toward the horizon. The physical lever is the far roughness. Question for the user (plan 10.6).
- R1-0's check "far textures identical at both values" holds for R1-0 alone. **R1-2a deliberately changes the far-viewport water at model 1** (water pixels only). At model 0 the far image stays identical.

### 2.7 Water tab

Nothing changes before sign-off. Whether the rows Fresnel F0, Fresnel strength and SSR strength go is the user's decision (plan 10.6); the design keeps them for now. At model 1 they have no effect, with one exception: `water_ssr_strength = 0` still disables the old march in R1-2a, because the early-out at `ring_water_ssr` line 80 is kept. R1-5a's new march ignores strength entirely.

## 3. R1-5: the march

### 3.1 Parameterisation

A screen-uniform, perspective-correct march (McGuire and Mara 2014). It costs the same per step as today and is cheaper in maths: no matrix product per step.

- Start: `p0` = the biased surface point in view space, `z0 = −p0.z`, `uv0` = its screen position.
- End: the horizon, always (the old `water_ssr_max_ray_distance` is ignored at model 1).
  - `h1 = PROJECTION_MATRIX · vec4(dir, 0)`. Require `h1.w > 1e-6`; this is today's `dir.z < 0` check.
  - `uv1 = h1.xy / h1.w · 0.5 + 0.5`.
- Along the segment: `uv(σ) = mix(uv0, uv1, σ)` and `1/z(σ) = mix(1/z0, 0, σ)`, so `z(σ) = z0 / (1 − σ)`.
  - This is exact for a straight line under any projective map, jitter included. For a perspective projection `h.w = −z_view`.
- Clip σ to the screen with a slab test, margin half a texel. `σ_max` ≤ 1; `reaches_horizon = σ_max ≥ 1`.
- **Adaptive step count:** `steps = clamp(ceil(seg_px / 3.0), 4, water_ssr_coarse_steps)`, where `seg_px = length((uv1 − uv0)·σ_max·VIEWPORT_SIZE)`. Distant water pixels have short segments and use few steps. The old uniform becomes the maximum.
- Effect: for a receding ray the world step grows roughly as z²/z0. Steps are dense where pixels are dense, and the far shore and the vanishing point are reached with N ≤ 12.
- Depth: compute only the z and w rows of the inverse projection for `(ndc, raw, 1)`. Raw depth 0 (sky, backdrop) is "infinitely far": the ray is always in front of it, and `previous_valid` stays true.
- Keep the bracket (front at the previous sample, behind at this one), the start seed and the binary refinement (in σ, `water_ssr_refinement_steps`).
- On a rejected bracket: miss (fall back to the sky term), as today.
- Future near-field limit (R3): a later order adds a uniform for a finite end point, using the same formulas with `1/z1 = 1/(−(p0 + L·dir).z)`. Not part of R1-5a.

### 3.2 Thickness and acceptance (constants est.; tune on R1-5c stills; none goes in the menu)

```glsl
float tol = max(water_ssr_depth_tolerance_near, 0.03 * scene_hi);
bool same_surface = scene_lo > 0.0 && max(scene_lo, scene_hi) <= 2.0 * min(scene_lo, scene_hi);
float allowed = same_surface ? max(tol, ray_hi - ray_lo) : tol;   // ray_lo/hi = ray depth at the final bracket ends
bool accept = gap_hi <= allowed;
float depth_agreement = 1.0 - smoothstep(0.5 * allowed, allowed, gap_hi);
```

Why both terms:
- At 600 m and 2 m eye height, one pixel of grazing terrain spans about 200 m of depth. A fixed far tolerance punches holes there.
- The depth-ratio test still rejects thin silhouettes, such as a trunk in front of a far shore (ratio about 30).
- `water_ssr_depth_tolerance_far` is ignored at model 1.

### 3.3 Confidence

| Term | Model 1 |
|---|---|
| `distance_confidence` (line 154) | Dropped: no fixed ray length |
| `camera_confidence` | `1 − smoothstep(0.8·D, D, camera_distance)`, D = `water_ssr_camera_distance`. Today it is a linear ramp from the camera, so the High defaults give half confidence at 125 m |
| edge, roughness | Unchanged |
| depth agreement | As in 3.2 |
| step-ratio debug | `step_index / steps` |

### 3.4 Hit at infinity

When `reaches_horizon` holds, no bracket was accepted, and the depth at `uv1` is 0 (a backdrop pixel), return that pixel as a hit:
- colour = `textureLod(water_screen_texture, uv1, roughness · 4.0)`;
- confidence = edge(uv1) · camera · roughness · up-guard · height-guard;
- up-guard = `smoothstep(0.001, 0.004, dot(dir, up_view))`;
- height-guard = `1 − smoothstep(50.0, 150.0, eye_height_above_water)`;
- `up_view` = the smooth surface normal in view space, and `eye_height_above_water = dot(camera_world_position − world_position, smooth_water_normal_world)`.

This is safe because backdrop content is kilometres to thousands of kilometres away, and the parallax is 2h/D (plan 4.1). It is the D = ∞ case of the far mirror, for one extra texture read. It puts the arch and the far ring into the water when they are on screen.

Failure cases:
- **Vanishing point off screen** (looking down at water): the sky term. The arch slides out at the top edge, softened by the edge fade. R2c covers the off-screen arch.
- **Missing layers:** near clouds, rain, lightning and the megastorm wall are not in the screen texture, so the water shows clear sky where they should be. `storm_ssr_keep` fades this under storms. Otherwise it is the same error as the sky term, until section 4.
- **Sun disc in the backdrop:** a sharp HDR glint that may sparkle. It does not double with the sky term, because the sky term is weighted by `1 − a`. Check in a flicker movie; clamp only if seen, and flag it as a question.
- **Downward rays (steep wave facets):** the vanishing point lands on water pixels whose depth texel is 0 (deep ocean without a drawn bed, where the backdrop shows "through" the sea), which gives a wrong colour. The up-guard prevents this.
- **High camera:** the height-guard. The thresholds assume the backdrop is at least about 10 km away (est.).
- **Rays toward the camera:** rejected, as today.

### 3.5 Presets and budget

- Preset values change in R1-8, not R1-5a. Proposed (est.):

| Preset | Max steps / refinements | Camera distance |
|---|---|---|
| Low, Medium | off | unchanged |
| High | 12 / 4 | 600 m (400 m if the lake-from-a-cliff pose measures above +0.2 ms) |
| Very High | 16 / 4 | 800 m |
| Ultra video | 24 / 5 | 1,000 m |

- The user's own overrides (32 / 8 / 695 m) persist across presets. At model 1 they mean "at most 32 steps". The adaptive count and cheaper steps should keep their cost at or below today's 0.1 ms at the coast (est.; R1-5c measures it).
- Budget rule: no more than +0.2 ms over today at any measured pose on High. Today's water march is about 0.1 ms at the lifted coast (fact 11). The risk poses are a waterline view and a lake seen from a cliff, where most of the screen traces with full segments.

## 4. Clouds in water hits (plan 10.5, consumption points 2 and 3; order R3a-3)

Input: the R3a-2 include `ring_cloud_reflection.gdshaderinc`, sampled by world direction through the published (previous-frame, unjittered) cloud view-projection. It returns premultiplied cloud colour, coverage, transmittance-weighted distance and a valid flag. The exact premultiplication is settled in R3a-0; if the colour is not premultiplied, use `cloud_rgb · coverage`.

- **Where to composite.** Only in the model 1 path, on the colour a hit returns, before it is weighted by `F·a`:
  - **Hit at infinity:** sample by `dir_world` (the reflected direction). `hit_colour = backdrop · (1 − coverage) + cloud_colour`. This is the main gain: clouds in lakes before the far mirror exists.
  - **Finite hit:** sample by the camera-to-hit direction. The cloud data is already limited to cloud in front of the opaque depth along that camera ray, so the same formula applies without a depth test. This matters mostly for low cloud and fog banks; near hits under cloud base get coverage of about 0.
  - **Sky-term misses:** nothing. Off-screen directions stay with the radiance map (R2d).
- **Haze.** None extra. The cloud colour already carries haze at the cloud distance along the camera ray, which is the same argument as 2.3.
- **Hit distance.** On an infinity hit with coverage above about 0.5 (est.), the hit distance becomes the cloud distance.
  - Values at or above 65,000 m, or non-finite, mean infinity (half-float limit, plan 10.5).
  - Use that distance in the height-guard instead of the 10 km assumption: `parallax = 2h / D_cloud`, with the fade between 0.5 and 1.5 degrees (est.).
  - Debug mode 3 can show it.
- **Night.** The cloud colour is dark but coverage stays valid. Composite by coverage regardless of brightness, so stars and the planet in the backdrop are hidden behind clouds.
- **Validity.** Where the include reports invalid (first frame, resize, teleport, history reset, cloud pass off), do not composite, and keep `storm_ssr_keep` on that pixel: `keep = mix(storm_ssr_keep, 1.0, cloud_valid)`.
- **When `storm_ssr_keep` can go:**
  - when the stills of R3a verification show the composited cloud reproduces a closed storm and the megastorm wall in the water (both are in the Sunshine image);
  - then `keep = 1` wherever the data is valid;
  - delete the term entirely in R1-8, or in R3-3 if the far mirror lands first.
- **Lightning and rain haze** are separate post-transparent effects and are not in the cloud data. A flash in the water is R3's job. The rain haze composites over the whole screen, water included, so it needs nothing here.
- **Cost (est.):** one or two filtered reads per reflecting pixel, within the 0.03 ms R3a budget of plan 10.3. Measure in the matrix.

## 5. Tests

### 5.1 New

- `tools/water_reflection_model_contract_test.gd`: headless, tier gate, tags `water`, with a manifest entry (`test-scaffold` skill). It asserts on the shader text:
  - Proxy:
    - includes `ring_reflection_model.gdshaderinc`;
    - the composition is inside `if (ringworld_reflection_model >= 0.5)` with an `else`;
    - the model 1 block contains `ring_water_env_reflectance(`, `RING_WATER_SPECULAR` inside `sqrt(` and `(1.0 - reflectance)`, and does not contain `reflection_color` or `fresnel_strength`;
    - the old tokens of `tools/water_ssr_contract_test.gd` are still present.
  - Global: the model 1 block exists after the old `SPECULAR` line and has no `vec3(0.12, 0.28, 0.42)`.
  - Neither shader has `specular_occlusion_disabled` or `RADIANCE`.
  - R1-5a adds: `ring_water_ssr_horizon(`, `same_surface`, the infinity path, and that model 0 still calls `ring_water_ssr(`.
- A model 1 variant of `tools/water_ssr_live_controls_gpu_test.gd` (R1-5a). It is an additive user argument `--reflection-model=1` that sets the global before the first capture (through `RingReflectionModel` or `RenderingServer.global_shader_parameter_set`) and restores 0 in `_cleanup`. It also places a second marker about 600 m ahead that must be hit (proves the far shore). It has its own manifest entry. **The default path of the test is unchanged.**

### 5.2 Must NOT change before R1-8 (they run at model 0 and must pass as they are)

- `tools/water_ssr_contract_test.gd` (its tokens stay valid in the `else` branch);
- `tools/water_optics_contract_test.gd` (guards the refracted and volume part, which neither model changes);
- `tools/water_rendering_smoke_test.gd` (rows list :196, strength probe :205-225, `WaterFresnelF0` NaN probe :241);
- `scripts/Tools/WaterSettingsSmoke.cs`, run by `tools/water_settings_smoke_test.gd` (strength, max ray, far tolerance and preset assertions :25-67, :177-183, :320-340);
- `tools/water_ssr_live_controls_gpu_test.gd` default path (strength slider, `_medium_ssr` ray distance);
- `tools/inland_water_render_gpu_test.gd` (bank band and LOD colour agreement at model 0);
- shadow-debug identity: `tools/shadow_compose_identity_gpu_test.gd`, `tools/height_shadow_receiver_gpu_test.gd`;
- `tools/wet_world_gpu_test.gd`, `tools/water_pattern_gpu_test.gd`, `tools/water_depth_visual_test.gd`, `tools/water_fft_lod_seam_gpu_test.gd`, `tools/water_fft_lod_ownership_visual_test.gd`, `tools/megastorm_blast_shader_gpu_test.gd`, `tools/EmitterBoreApertureTest.cs`.

Any failure in this list after R1-2a or R1-5a means model 0 is no longer identical: stop and report, do not edit the test.

### 5.3 Change in R1-8 (after sign-off and the user's answer on the rows)

- **If the rows go:**
  - `water_rendering_smoke_test.gd` drops the retired rows from its list and moves the round-trip probe to `WaterSsrEdgeFade` or `WaterSsrRoughnessCutoff`, and the NaN probe to another float key.
  - `WaterSettingsSmoke.cs` moves its probes to `WaterSsrRoughnessCutoff` (it also differs by preset), takes the new preset table, and adds a case: a saved `.tres` with retired keys loads with an empty `LastError` and loses them on the next save. Today unknown keys set `LastError` (`GameplayWaterDefaults.cs` `LoadInto`), so a silent `RetiredKeys` drop is needed.
  - `water_ssr_live_controls_gpu_test.gd` drops the strength and ray-distance sliders.
- **In any case:** `water_ssr_contract_test.gd` drops the old-path tokens when the old path is deleted, and `inland_water_render_gpu_test.gd` is re-baselined against the model 1 look. If the bank band then fails because of the sky reflection, that is a look question for the user, not a threshold to relax.

## 6. Work orders

### R1-2a. New composition at model 1 (near and far water)

ROLE: implementer. SIZE: M. DEPENDS ON: R1-0 landed (`ringworld_reflection_model` registered in `project.godot` as a float; `materials/ring_reflection_model.gdshaderinc` declares it).

OBJECTIVE
At `ringworld_reflection_model >= 0.5`, the water's reflection leaves `ALBEDO`:
- the hit goes through `EMISSION`;
- the sky goes through the engine with physical F0 0.02;
- the body is weighted by `1 − F`.
At model 0 the present code runs unchanged, and the output must be the same.

FILES/SYMBOLS
`materials/ring_water_appearance.gdshaderinc`, `materials/ring_ocean_proxy.gdshader` (`fragment()` :611-649), `materials/ring_ocean_global.gdshader` (`fragment()` :131-133), new `tools/water_reflection_model_contract_test.gd`, `tools/test_manifest.json` (one entry; the file has other agents' uncommitted edits, so add only your hunk).

REQUIRED CHANGES
1. `ring_water_appearance.gdshaderinc`, additive only. Keep every existing uniform and function. Append:
   ```glsl
   const float RING_WATER_F0_PHYSICAL = 0.02;
   const float RING_WATER_SPECULAR = 0.353553; // sqrt(0.02 / 0.16): Godot f0 = 0.16 x SPECULAR^2, f90 = clamp(50 f0) = 1
   // Karis 2014 fit of the split-sum DFG integral the engine's LUT holds (prefiltered_dfg), with f0 0.02 and f90 1.
   float ring_water_env_reflectance(float roughness, float n_dot_v) {
       const vec4 c0 = vec4(-1.0, -0.0275, -0.572, 0.022);
       const vec4 c1 = vec4(1.0, 0.0425, 1.04, -0.04);
       vec4 q = roughness * c0 + c1;
       float a004 = min(q.x * q.x, exp2(-9.28 * n_dot_v)) * q.x + q.y;
       vec2 ab = vec2(-1.04, 1.04) * a004 + q.zw;
       return clamp(RING_WATER_F0_PHYSICAL * ab.x + ab.y, 0.0, 1.0);
   }
   ```
2. `ring_ocean_proxy.gdshader`:
   - Add `#include "res://materials/ring_reflection_model.gdshaderinc"` after line 37.
   - Move line 639 (`float storm_reflection = ring_storm_ambient(storm_water_level);` with its comment) up to directly after line 618, so it is defined before the branch. It feeds `AO` at :645, so the value is unchanged.
   - Wrap lines 619-642 (from `reflection_color *= ...` to the end of the `SPECULAR` statement, without the moved line) in `if (ringworld_reflection_model >= 0.5) { NEW } else { <those lines verbatim> }`. Lines 611-618 and 643-658 stay outside and unchanged.
   - NEW block:
   ```glsl
   float final_roughness = max(base_roughness, specular_aa_roughness);
   float n_dot_v = clamp(dot(water_normal_view, VIEW), 1e-4, 1.0); // engine: clamp(dot(normal, view), 1e-4, 1)
   float reflectance = ring_water_env_reflectance(final_roughness, n_dot_v);
   float haze_k = ring_haze_specular_scale(haze); // TODO R1-1b: * ringworld_sky_reflection
   float hit_weight = clamp(ssr.a, 0.0, 1.0) * storm_ssr_keep; // water_ssr_strength, fresnel_* and ocean_specular ignored
   vec3 body = mix(optical_water, foam_color, foam);
   ALBEDO = body * (1.0 - reflectance) * haze.T;
   EMISSION = ring_haze_inscatter(haze) * (1.0 - reflectance * hit_weight) + ssr.rgb * (reflectance * hit_weight);
   if (inland_weight > 0.0)
       EMISSION += inland_transmitted * (1.0 - reflectance) * (1.0 - foam) * haze.T;
   ROUGHNESS = final_roughness;
   // Sky term: f0 = 0.02 k (1 - a), f90 = k (1 - a): exactly linear (design 1, fact 4). Storm factors are in the radiance pass.
   SPECULAR = RING_WATER_SPECULAR * sqrt(max(haze_k * (1.0 - hit_weight), 0.0));
   if (water_ssr_debug_mode != 0) {
       vec3 debug_value = water_ssr_debug_mode == 1 ? vec3(ssr.a)
           : water_ssr_debug_mode == 2 ? vec3(ssr.a > 0.001 ? 1.0 : 0.0) : vec3(ssr_step_ratio);
       ALBEDO = vec3(0.0); EMISSION = debug_value; SPECULAR = 0.0; // unlit at model 1
   }
   ```
   - The march call (:611-612) is unchanged in R1-2a: model 1 still uses `ring_water_ssr`. Its strength early-out (:80) stays, so strength 0 disables it at both values.
3. `ring_ocean_global.gdshader`:
   - Add the include after line 28.
   - After line 132 (end of the `SPECULAR` statement) and before `METALLIC` (:133), insert:
   ```glsl
   if (ringworld_reflection_model >= 0.5) {
       // Same reflection model as the near water (R1-2a): no fixed blue, physical F0, sky term through the engine.
       float n_dot_v = clamp(dot(NORMAL, VIEW), 1e-4, 1.0);
       ALBEDO = water_tint * (1.0 - ring_water_env_reflectance(ROUGHNESS, n_dot_v)) * haze.T;
       SPECULAR = RING_WATER_SPECULAR * sqrt(ring_haze_specular_scale(haze));
   }
   ```
   - Lines 103-132 stay textually unchanged. At model 1 their `ALBEDO` and `SPECULAR` are simply overwritten.
4. New `tools/water_reflection_model_contract_test.gd` (section 5.1, R1-2a part) and its manifest entry (headless, gate, tags `water`, marker `WATER_REFLECTION_MODEL_CONTRACT=PASS`, expected_s 1, timeout_s 30). Validate with `tools/testing/validate_manifest.ps1`.

DO-NOT-CHANGE CONSTRAINTS
- No `render_mode` change in either shader; no `RADIANCE` write.
- No uniform removed, renamed or re-defaulted.
- No change to `light()`, the optics, volume, foam, FFT, wet, tremor or blast code, the shadow-debug branch (:650-658 proxy, :136-144 global), the march, any C# file, the menu, presets, `ring_local_sky.gdshader`, R1-0 or R1-1 files.
- Do not edit any test listed in 5.2.

VALIDATION STEPS
1. Shaders compile: the gate run below loads both.
2. `tools/run_tests.ps1 -Files materials/ring_ocean_proxy.gdshader,materials/ring_ocean_global.gdshader,materials/ring_water_appearance.gdshaderinc,tools/water_reflection_model_contract_test.gd -Label r1_2a -DryRun`. Report the selection.
3. Run it, then `tools/run_tests.ps1 -Tests water_ssr_live_controls_gpu_test,water_pattern_gpu_test,water_depth_visual_test,height_shadow_receiver_gpu_test,water_fft_lod_ownership_visual_test -Label r1_2a` (extended tier, not selected by `-Files`).
4. Report the `results.md` summary line and every non-pass row. Every 5.2 test must pass; known failures stay known.
5. Model 0 identity (the Lead runs it in R1-2c, but the implementer runs the quick form): probe stills at `refl_coast_lift` and one lake pose, before the change and after it at model 0, with exposure held (R1-1-0). Use `tools/media/ffmpeg_tools.ps1 compare`; the heatmap must show no structure on water. Delete the frames afterwards and run `tools/prune_test_output.ps1 -Apply`.

ESCALATION CONDITIONS
- The include from R1-0 does not exist, or the global is not a float: stop, do not declare the global yourself.
- A 5.2 test fails.
- The model 0 compare shows structure on water.
- The shader compiler rejects `VIEW` or `ROUGHNESS` reads at that point.

### R1-2c. Water stills, model 0 against model 1

ROLE: Lead (stills judged by the Lead, sheet for the user in R1-7). SIZE: S. DEPENDS ON: R1-1-0, R1-2a.

OBJECTIVE
A before/after record of the water at every condition the sign-off needs, and proof that model 0 is unchanged.

STEPS
1. Probe with the R1-1-0 model loop:
   - poses: `refl_coast_lift`, a lake looking across (with the arch in view, and from a cliff), a river bank along the river (`tools/river_site_capture.gd` sites), a river mouth (kind 1 to 2 to 3), and the ocean across the 4 to 5.5 km proxy-to-global handoff;
   - times: noon, low sun (glint), night;
   - weather: a real storm and its aftermath (`tools/wet_world_capture.gd` trailing-edge views), and a light overcast below the sun-fade start.
2. Water SSR debug views (confidence, hit mask) at the coast and the lake, model 1.
3. Sheets (`ffmpeg_tools.ps1 sheet`): model 0 against model 1 per pose. Compare non-water pixels between models: they must match (only R1-1 receivers may differ, per the R1-1 design).
4. Check in the stills:
   - night sky term dimming (specular occlusion) at the handoff;
   - the sun glint in cloud shadow (open point, section 7);
   - the far-water tone at the horizon (question for the user);
   - no step at river mouths or at the handoff.
5. Keep cited stills under `.godot/audit_baseline/common/reflections/r1_2/`; delete the rest; prune.

### R1-5a. Horizon march at model 1

ROLE: implementer. SIZE: M. DEPENDS ON: R1-2a.

OBJECTIVE
At model 1 the water march reaches the far shore and the horizon with the same or fewer steps, and returns backdrop pixels as hits at infinity. At model 0 the old march runs unchanged.

FILES/SYMBOLS
`materials/ring_ocean_proxy.gdshader` (new function beside `ring_water_ssr` :78-162, the call at :611-612, the model 1 debug branch from R1-2a), `tools/water_reflection_model_contract_test.gd`, `tools/water_ssr_live_controls_gpu_test.gd` (additive argument only), `tools/test_manifest.json` (one new variant entry).

REQUIRED CHANGES
1. Add `vec4 ring_water_ssr_horizon(vec3 surface_view, vec3 ray_direction, vec3 up_view, float eye_height, float roughness, mat4 projection, mat4 inverse_projection, vec2 viewport_px, out float step_ratio, out float hit_kind)` after line 162, implementing 3.1 to 3.4 exactly.
   - Early-outs as today: enabled, roughness cutoff, camera distance, `ray_direction.z`. **No strength test.**
   - Depth fetch: a helper that returns −1 for raw depth ≤ 0 or > 1, which the march treats as infinitely far. Use only the z and w rows of the inverse projection.
   - Return `vec4(colour, confidence)`.
   - `hit_kind`: 1 for a geometry hit, 0.5 for a hit at infinity, 0 for a miss.
2. Lines 611-612 become:
   ```glsl
   float ssr_step_ratio;
   float ssr_hit_kind = 0.0;
   vec4 ssr;
   vec3 ssr_origin = VERTEX + water_normal_view * water_ssr_ray_bias;
   if (ringworld_reflection_model >= 0.5)
       ssr = ring_water_ssr_horizon(ssr_origin, normalize(reflect(normalize(ssr_origin), water_normal_view)),
           normalize((VIEW_MATRIX * vec4(smooth_water_normal_world, 0.0)).xyz),
           dot(camera_world_position - world_position, smooth_water_normal_world),
           base_roughness, PROJECTION_MATRIX, INV_PROJECTION_MATRIX, VIEWPORT_SIZE, ssr_step_ratio, ssr_hit_kind);
   else
       ssr = ring_water_ssr(ssr_origin, water_normal_view, base_roughness, PROJECTION_MATRIX, INV_PROJECTION_MATRIX, ssr_step_ratio);
   ```
   The old call's arguments are the same as today. `ring_water_ssr` is not edited.
3. In the model 1 debug branch: mode 2 shows `vec3(ssr_hit_kind)`, so hits at infinity show as grey.
4. Contract test: add the R1-5a tokens (5.1).
5. Live-controls test: the additive `--reflection-model=1` path and the 600 m marker (5.1), plus a manifest variant `water_ssr_live_controls_model1_gpu_test` (gpu, extended, tags `water`).

DO-NOT-CHANGE CONSTRAINTS
- `ring_water_ssr` and every model 0 line.
- The R1-2a composition (apart from debug mode 2 at model 1).
- No uniform, preset, menu or C# change.
- The default path of the live-controls test.
- Do not edit any test in 5.2.

VALIDATION STEPS
1. Run `-Files materials/ring_ocean_proxy.gdshader,tools/water_reflection_model_contract_test.gd,tools/water_ssr_live_controls_gpu_test.gd -Label r1_5a -DryRun`, then run it.
2. Then `-Tests water_ssr_live_controls_gpu_test,water_ssr_live_controls_model1_gpu_test -Label r1_5a`.
3. Model 0 identity compare as in R1-2a step 5.
4. Report the `results.md` summary and non-pass rows.

ESCALATION CONDITIONS
- The 600 m marker is not hit at model 1 with the user's values.
- A 5.2 test fails.
- The model 0 compare shows structure.
- The hit mask shows hits at infinity on downward-facing water or below the horizon (guard failure).

### R1-5c. Cost and look of the march

ROLE: Lead. SIZE: S. DEPENDS ON: R1-5a.

STEPS
1. Matrix (`tools/fixtures/perf_bench_reflection_costs.json`, kind `ab_matrix`). Add poses:
   - a waterline view at a lake (eye height, pitch about −3);
   - a lake seen from a cliff (about 100 m up, pitch −20);
   - a river along its course;
   - and keep `water_coast_lift`.
2. Variants, at each pose:
   - model 0 against model 1;
   - at model 1, water SSR off;
   - at model 1, camera distance 400, 600 and 800 m;
   - at model 1, max steps 12 / 16 / 32.
   - All with the user's values, which are the baseline.
3. Budget: model 1 minus model 0 for the water, at most +0.2 ms at every pose. Choose the High camera distance for R1-8 from the table (3.5).
4. Probe stills and 10 s flicker movies (`ffmpeg_tools.ps1 flicker`) walking at the lake and river and turning at the coast. Look for shimmer on thin reflections, sun-glint sparkle from hits at infinity, and the arch leaving at the top edge.
5. Write the numbers into plan section 9. Keep only the cited evidence; prune.

### R1-8 (water part). After sign-off

ROLE: implementer, with a Lead work order written at the time. Content, for the record:
- `ringworld_reflection_model` default 1, then delete the switch: in the proxy, keep the model 1 block and `ring_water_ssr_horizon` and delete `ring_water_ssr`, the old composition and `reflection_color`. In the global, delete the old lines 105-107, 115 and 131-132, now overwritten.
- Remove the dead uniforms: `ocean_specular`; `fresnel_f0`, `fresnel_strength` and `water_ssr_strength` if the user approves removing the rows; `water_ssr_max_ray_distance`, `water_ssr_depth_tolerance_far`.
- Remove the dead helpers `ring_water_dielectric_specular` and `ring_water_schlick_fresnel`.
- Preset table 3.5 in `RingGraphicsQualityController.ApplyWaterSsrPresetDefaults` (:770-783).
- If the rows go: drawer rows (`RingLiveTuningDrawer.Water.cs:98-99, 103, 106, 110`); controller properties (`RingGraphicsQualityController.cs:142-143, 155, 158, 162`); `ApplyWaterModelTuning` (:693-703); `RingWaterModel.ApplyTuning` parameters (:114-126) and pushes (:256-257, :269-276); `GameplayWaterDefaults` keys and limits plus a silent `RetiredKeys` drop in `LoadInto`.
- Rename rows: "SSR coarse steps" to "SSR max steps", "SSR near depth tolerance" to "SSR min thickness (m)". Update the group text at `RingLiveTuningDrawer.Water.cs:101` ("misses show the sky reflection").
- Tests per 5.3.
- Offer the user a reset of their water SSR overrides to the new High values (plan 10.6).

## 7. Open points and decisions

1. **Water rows** (Fresnel F0, Fresnel strength, SSR strength): with the user. The design keeps them until R1-8.
2. **Far water** at roughness 0.46 reflects less sky toward the horizon than today's fixed blue (est. 0.17 against 0.37 at grazing). Accept the physical result, or study the far roughness? (Plan 10.6, user question 3.)
3. **Sun glint** comes from the radiance-map sun disc and is not shadowed by local clouds or terrain. Judge it in R1-2c. An analytic glint in `light()` would double-count with the radiance map, so it belongs to R2 or R4.
4. **Specular occlusion on the sky term** at night and in storms (est.: noticeable only where `8·lum(ambient·AO) < 1`). This is shared with every other receiver by design. If the night stills show water darker than the lit world implies, the fix belongs to the R1-1 visibility design, not to a water-only render mode.
5. **The user's SSR overrides** (32 / 8 / 695 m): offer a reset after R1-5c (plan 10.6).

## 8. Verified and estimated

- **Verified by reading:**
  - every engine fact in section 1;
  - every game line reference;
  - the user's overrides;
  - the test assertions listed in 5.2;
  - that the render mode would change model 0 only for `ring_ocean_global` in the scaled far viewport.
- **Measured (plan 9.2):** the water and sky-source costs in fact 11.
- **Estimated:**
  - the Karis fit against the engine LUT;
  - the thickness, ratio, guard and cloud thresholds;
  - the preset values;
  - all costs after the change;
  - the far-water tone outcome;
  - the size of the night specular-occlusion effect.
