# R1-1 design: the sky as the reflected environment

Lead design, 2026-11-17. Part of `docs/reflections-plan.md` section 10 (read 10.2 and 10.4 first; order names here match 10.4). Water is not in this design: `ring_ocean_proxy.gdshader` and its `SPECULAR` belong to R1-2a (`docs/work-orders/reflections/R1-2-R1-5-design.md`).

Engine lines cite the Godot master copy that was read for this design (`scene_forward_clustered.glsl` = "sfc", `render_scene_data_rd.cpp` = "rsd", `render_forward_clustered.cpp` = "rfc"). The engine's `F0()` was not in that copy; f0 = 0.16 x SPECULAR^2 is taken from the game's own mirrors of it (`terrain3d_ring_surface.gdshader:327-328`, `ring_wetness.gdshaderinc:254`). **Every magnitude marked "est." is an estimate from engine formulas (Karis DFG fit), not a measurement.**

## 0. Orders

| Order | What | Files | Can start | Depends on |
|---|---|---|---|---|
| R1-1-0 | Probe tooling and "before" stills | `tools/perf_bench_reflection_probe.gd`, `tools/fixtures/perf_bench_reflections.json` | now | none |
| R1-0 | Switch `ringworld_reflection_model`, main reflected source, far pins, contract test | `RingReflectionModel.cs` (new), `RingFarViewportColorPipeline.cs`, `RingScaledFarViewport.cs`, `project.godot`, `materials/ring_reflection_model.gdshaderinc` (new), test | now | none |
| R1-1a | Holds: trees, grass cards | `imported_tree.gdshader`, `imported_tree_impostor.gdshader`, `ring_grass_cards.gdshader` | now | none |
| R1-1b | Sky visibility global, include, POI/construction/stand-ins/anchors/voxel | see order | now (merge after R1-0) | R1-0 (model global) |
| R1-1d | Terrain | `terrain3d_ring_surface*.gdshader`, `ring_terrain_proxy*.gdshader`, `ring_terrain_lighting.gdshaderinc` | last (files in use by T4-4b and FR-1) | R1-1b |

R1-7 (sign-off) and R1-8 (cleanup) are in the plan, 10.4.

## 1. Engine facts the design rests on (verified by reading)

- Reflection source (rsd:199-213): `Background` with a Color background gives `SCENE_DATA_FLAGS_USE_REFLECTION_COLOR` (the background colour x background energy: black today); `Sky`, or `Background` with a Sky background, gives the radiance map. It is a runtime UBO flag: **no shader recompile**.
- The radiance map is already set up and updated every frame because the ambient source is Sky (rfc:2153-2161). Switching the reflection source adds no radiance work, only one octmap read per lit fragment (plan 10.3 measured +0.05 to +0.08 ms).
- Sky term (sfc:1699-1735): `ref_vec = mix(reflect(-view, n), n, roughness^2)`, a horizon factor `min(1 + dot(ref, n), 1)^2`, times background energy (1.0 in gameplay; nothing sets it outside test scenes).
- `ambient_light_sky_contribution` mixes ambient only (sfc:1762-1764). **It never reaches reflections.**
- `RADIANCE` (sfc:1741-1743) is mixed over the sky term before specular occlusion and before SSR. `RADIANCE = vec4(0, 0, 0, 1 - v)` multiplies only the sky term by `v`; SSR hits (sfc:2304-2306) and direct light are untouched; `v = 1` is bit-identical to not writing it (`a * 1 + 0 * 0`). The same code serves the opaque and the transparent pass.
- Specular occlusion without bent normals (sfc:2256-2262): `occ = min(8 x lum(ambient x AO), 1)`, and for metals `max(min((1 - roughness) x metallic x occ x 10, 1), occ)`. AO here already includes SSAO (sfc:2095-2102). By day the game's ambient luminance is est. 0.1 to 0.4, so it barely bites unless AO is below about 0.5. It is the only thing that dims reflections at night, in storms and in caves.
- Fresnel (sfc:2333-2341): `(f90 - f0) x DFG.x + f0 x DFG.y`, `f90 = clamp(50 x f0, metallic, 1)`. Consequence: scaling `SPECULAR` by sqrt(T) scales f0 by T, but once f0 > 0.02 the grazing f90 term escapes it. `RADIANCE` does not have that problem.
- `render_mode specular_disabled` only affects the light functions. **The sky term still applies** with the default `SPECULAR` 0.5 (f0 0.04).
- Est. for very rough dielectrics (roughness 0.9 to 1): sky term about 0.5 x f0 x sky radiance at most angles.
- Not usable as a dry sky-visibility signal: the precipitation occlusion map is active only while precipitation is present (`RingPrecipitationOcclusion.cs:16`).

## 2. Receiver audit (working tree, 2026-11-17)

f0 = 0.16 x S^2. No receiver writes `RADIANCE` or `IRRADIANCE` today.

| File:lines | Dry S / R / M / AO / AO_LIGHT_AFFECT | Wet | T or storm in S | Sky visible after the switch (est.) |
|---|---|---|---|---|
| `terrain3d_ring_surface.gdshader:323-338` | S .18 (f0 .0052), R `surface_dry_roughness` .92 - slope x .08, M 0, AO min(1 - cloud x .72, storm), 0 | S to .5 (f0 .04), R to wet gloss .18; puddles R .06/.12 within 150 m; far puddle average R .12 | no: T only on ALBEDO (`ring_terrain_lighting.gdshaderinc:35-37`); storm only on the direct glint | dry faint (est. 0.25 % of sky radiance, seen in shadow); wet clear, intended, **unhazed at km range**. Pending diff: line endings only. |
| `terrain3d_ring_surface_ground.gdshader:482-497` | same | same | same | same. The pending T4 diff does not touch these lines but adds a rock detail `NORMAL` within 60 m (about :398-401) that now shows faintly in the reflection. |
| `ring_terrain_proxy.gdshader:224-255` | S .18, R mix(`roughness_value` 1.0, >= .9, grass), M 0, same AO | same; river lines R .06, S .5 (:237-239) | no (ALBEDO x T :246) | dry faint, wet clear; **far river lines become near-mirror unhazed sky lines km away**. Line endings only. |
| `ring_terrain_proxy_ground.gdshader:330-335` | same | same | no | same. The pending diff (relief colour :270-284) does not touch the outputs. |
| `voxel_ring_terrain.gdshader:121-156` | S .18, R mix(1, .9, grass), M 0 | + puddles | no (ALBEDO x T :147) | faint at the surface; **caves: leak** (section 3) |
| `ring_terrain_global.gdshader:169-181` | S .18, R 1; rivers .06 / .5 | yes | no | far viewport: already reflects the sky today, unchanged. Main: only if `GlobalTerrainLod3` draws in gameplay (unverified). Shared with far: do not edit. |
| `ring_hull_global.gdshader:173-182` | R .92, S .25, M .18, AO 1 - ambient_override | | metal part via ALBEDO x T | faint; AO 0 above the wall top removes it. Shared with far: do not edit. |
| `imported_tree.gdshader:126-146` | S unset (.5, f0 .04), R texture x `roughness_factor` (.9 simplified), M texture x `metallic_factor` (from the imported StandardMaterial3D, `ImportedTreeCatalog.cs:264`; values not audited), AO cloud/storm, 0 | R x .6, leaves held at .71 for SSR; direct highlight f0 .04 to .1 x T (:182-183) | no (ALBEDO x T) | **clear**: B1 shows teal crown interiors and bluish trunks; no canopy visibility term |
| `imported_tree_impostor.gdshader:232-244` | same (layers; `tree_parameters.g` metallic) | same | no | clear |
| `ring_tree.gdshader:93-99` | S .04 to .1 (f0 < .0016), R .92 | floor .71 | no | none |
| `ring_grass_cards.gdshader:191-198` | S unset (.5), R 1, M 0, AO cloud/storm, 0; `specular_disabled` | darkening only; :61 "cards have no specular" | no | faint to clear (B1: blue flecks in tree shadow); contradicts the shader's own contract |
| `ring_standing_grass.gdshader:326-335` | S (.05 to .25) x (1 - terrain_lighting) (f0 <= .01), R 1, AO blade/cloud/storm, 1 - terrain_lighting | | no | none |
| `ring_poi_architecture.gdshaderinc:71-88` | M `metallic_value` (metals R .26 to .45), R `roughness_value`, S `specular_value` x sqrt(mean T), AO cloud x base AO (floor .45) / storm, 0 | wet + puddles | **yes** (dielectric via sqrt T, metal via ALBEDO x T); storm via AO only | clear on metals, faint on rough dielectrics. Leaks: undersides, overhangs, shafts (emitter bore) and interiors reflect the lower-hemisphere haze (R2a); caves |
| `ring_emitter_standin.gdshader:104-123` | M .72, R .48, S .68 (f0 .074), AO cloud/storm, 1; no `light()`, so S also drives the sun highlight | wet | metal part only (ALBEDO x T) | clear (metal megastructures); the dielectric part is unhazed at 11 to 700 km |
| `ring_scale_anchor.gdshader:53-72` | same pattern | wet | same | clear |
| `ring_construction_surface.gdshader:39-45` | S .5, R .9, M 0, AO storm, 0; no haze by design | wet + puddles | | faint outdoors (physical); **caves: leak** |
| `ring_ocean_proxy.gdshader:636-646` | S .2024: f0 .0066, f90 .33 | | yes (haze, storm, fade) | clear, and counted twice (the reflection is also in ALBEDO, :618-628). **Owned by R1-2a, not changed here.** |
| `ring_ocean_global.gdshader:125-136` | far viewport | | | already reflects the sky today; unchanged in R1-1 |
| `ring_gust_debris.gdshader:65-68` | S 0 | | | none |
| StandardMaterial3D in the main world | construction glass (alpha .38, R 1); `SmoothTerrainGrid` fallback (only without a terrain material); `EmitterBoreGeometry` fallback (M .5, R .4; only if the POI material is missing); `VoxelChunkNode` fluid (R .12; gameplay use unverified) | | | faint to clear; cannot write `RADIANCE`, so in caves they keep the ambient-occluded residual |

## 3. What is physically right, and what is a leak

- **Dry terrain**: right. The authored f0 .0052 is an aggregate for grass-covered ground; the shader comment shows the intent was a dry environment term. Keep.
- **Wet ground and puddles**: right under an open sky.
- **Haze**: the reflected light reaches the eye through the same air as the diffuse light, so the sky term must be multiplied by T. All terrain receivers miss this today (POI has it). Without it, wet ground and river lines kilometres away mirror an unhazed sky.
- **POI, construction, stand-ins under open sky**: right. Known leaks: downward reflections show the radiance map's lower hemisphere, haze where ground should be (R2a); enclosed POI interiors have no visibility signal (out of scope).
- **Trees**: f0 .04 is right for outer leaves, but the visible change in B1 is in crown interiors and on trunks, which cannot see the sky. Without a crown visibility term the unoccluded value is wrong for most changed pixels. **Hold.**
- **Grass cards**: the card model is diffuse plus backlight with "no specular"; a sky-only sheen without the matching sun term is inconsistent, and the visible sky fraction of a blade layer is unknown. **Hold.**
- **Night**: dark radiance map, planet included: right. Unverified risk: the radiance pass draws the sun disc without a horizon or ground gate (`ring_local_sky.gdshader:242,253-255`); below the horizon the Physical haze only attenuates it by air. Glossy metals and undersides could show a sun from below at night. Check with a night still; the fix (a horizon gate in the cubemap pass) changes the far image and the ambient, so it belongs to R2.
- **Storms**: the radiance pass already darkens and hides the sun (`ring_local_sky.gdshader:250-255,312-320`); storm AO lowers ambient and so occlusion. Megastorm: `ring_storm_ambient` includes the darkness K. Check one frame.
- **Ordinary overcast**: the map has no clouds (D7, R2d, R3a); cloud-shadow AO dims the reflection only partly. Known, no change.
- **Underground (must fix)**: fading `ambient_light_sky_contribution` does not reach reflections, and the Physical haze ignores `atmosphere_factor` (the script feeds `current_atmosphere_factor * underground_sky_access`, `day_side_atmosphere.gd:654-662`), so the cave radiance map stays the daylight sky. Only the ambient occlusion is left (cave ambient luminance est. .0012, occlusion est. .01). Est.: voxel cave walls +4 to 17 % over the cave ambient; SPECULAR .5 surfaces +30 to 60 %; metals several times the cave ambient (the x10 metal boost). The structural cavity (ambient about 1e-6) is safe.
- **Underwater**: same mechanism; ambient sky contribution goes to .02 (`day_side_atmosphere.gd:675-681`), reflections do not.
- Rejected mechanisms for caves: the background energy multiplier (it also scales the radiance generation of the Sky shared with the far viewport, rfc:2106-2161 and `RingScaledFarViewport.cs:1027`, and the ambient cubemap term, sfc:1764); toggling the reflection source per frame (a pop).

## 4. Design

**Two globals, one tool.**
- `ringworld_reflection_model` (float, 0 = today, 1 = new; default 0). Owned by `RingReflectionModel` (R1-0). At 1 the main Environment reflects the Sky; at 0 it keeps `Background`. Not a menu row; the capture tooling sets it.
- `ringworld_sky_reflection` (float, default 1): the share of the sky the camera's surroundings can see = underground sky access x (1 - underwater factor), published by `day_side_atmosphere.gd` beside the ambient rule it mirrors. Not a menu row.
- Receivers write `RADIANCE = vec4(0, 0, 0, 1 - v)` through one helper. With the environment black (model 0) every such write is a no-op, so it needs no branch.

**Per class.**

| Class | Change | Order |
|---|---|---|
| Trees (imported, impostor) | `RADIANCE = vec4(0, 0, 0, 1)` (hold; wet-bark SSR kept) | R1-1a |
| Grass cards | `SPECULAR = 0.0` (hold) | R1-1a |
| POI include, construction | `RADIANCE = ring_sky_reflection_radiance(1.0)` (T already in POI S/ALBEDO; construction is near) | R1-1b |
| Emitter stand-ins, scale anchors | same, plus `SPECULAR *= sqrt(mean T)` at model 1 only (`mix` by the model global) | R1-1b |
| Voxel terrain | `RADIANCE = ring_sky_reflection_radiance(mean T)` | R1-1b |
| Terrain3D x 2, proxy x 2 | `RADIANCE = ring_sky_reflection_radiance(mean T)` | R1-1d |
| Water | not in R1-1 | R1-2a |
| Standing grass, `ring_tree`, gust debris, hull, terrain_global, ocean_global, sky | no change (never edit shaders shared with the far viewport in R1-1) | |

Why `RADIANCE` and not `SPECULAR` for terrain: it scales the sky term exactly, including the grazing f90 part, and leaves SSR hits and the direct wet glint alone. (The direct wet glint is not hazed either; pre-existing, out of scope.)

**Identical afterwards.** At model 0: everything. At model 1: the far and regional far textures; the backdrop pixels in the main view at fixed exposure; trees, impostors (including wet SSR), grass cards; all ambient light (no ambient, background-energy or sky-shader edits); sealed caves (black on every game shader). Water at model 1 changes only through R1-2a; between R1-0 and R1-2a it shows the engine sky term on top of its ALBEDO reflection, so no water stills count for sign-off before R1-2a.

**Not identical, by design (correction to plan section 6 R1-1):** dry terrain gains a faint sheen at model 1 (f0 .0052).

**Proof.** Every receiver order is a no-op at model 0: probe stills before and after the order must match (`tools/media/ffmpeg_tools.ps1 compare`; judge the heatmap, not only SSIM). Until R1-0 exists the probe's runtime "sky" variant previews model 1; afterwards the probe compares model 0 and model 1 in one run (R1-1-0, step 2).

## 5. Far environments

- Order of creation: `CreateFarEnvironment` (`RingScaledFarViewport.cs:492`) runs before `ConfigureMainBackground` (:509). The scene sets no `reflected_light_source` (`VoxelRingIntegrationTest.tscn:129-138`), so both far environments copy `Background` today.
- **Whole-ring far** (:1031-1038): background Sky with the main Sky resource, so `Background` resolves to the radiance map. A later copy of `Sky` (a second creation in a session that reuses the Environment sub-resource) also resolves to the radiance map. Same result either way.
- **Regional far** (:1150-1156): background Color (0, 0, 0, 0), no Sky, so `Background` resolves to reflection colour 0. A later copy of `Sky` would set the radiance flag with no Sky, sampling the engine's default radiance texture (content not verified). The pin closes this.
- Re-sync: none for the reflected source. Later writes are ambient colour and energy only (`RingScaledFarViewport.EclipseAmbient.cs:32-40`) and volumetric fog (`RingScaledFarViewport.cs:3318`, `:3590-3591`).
- **Pin**: both constructors use `ReflectionSource.Background` as constants. Same enum, background mode, colour and energy give the same UBO flags and colour (rsd:199-213): bit-identical by construction. The model switch adds no radiance update, and R1-1 edits neither the far shaders nor `ring_local_sky`.
- Runtime check: the probe report lists main (Color; Background at 0, Sky at 1), far (Sky; Background) and regional (Color; Background) at both model values. Do not judge the far image from the main screen: auto exposure is on (`day_side_atmosphere.gd:67,826`) and shifts the composited backdrop.

## 6. Risks and coverage

1. **Auto exposure** moves the whole frame between variants. Stills hold exposure (`storm_auto_exposure_enabled = false` on the DaySideAtmosphere node during capture; precedent `tools/cloud_layers_capture.gd:282-283`).
2. **Caves and underwater**: covered by `ringworld_sky_reflection` for game shaders. StandardMaterial3D outliers keep the residual.
3. **POI metals**: undersides, shafts (`emitter_bore_noon`) and the possible night sun from below show lower-hemisphere content until R2a. Expected to be the largest sign-off question.
4. **Tree metallic factors** from glTF are unaudited. Harmless under the hold; audit before lifting it.
5. **Shared files**: terrain shaders are in use by T4-4b and FR-1 (R1-1d last). `project.godot` carries another lead's uncommitted global `ringworld_ground_rock_detail_normal` (about lines 86-89): each order adding a global adds its own entry as a separate minimal hunk and does not touch that one. `RingFarViewportColorPipeline.cs`, `RingScaledFarViewport.cs` and `day_side_atmosphere.gd` are free of other lanes.
6. **New shader variants**: writing `RADIANCE` recompiles each receiver once.
7. **Unaffected by construction** (own environment or own world): minimap (`RingTerrainMinimap.cs:315`), precipitation map camera (own env, Unshaded draw, `RingPrecipitationOcclusion.cs:368-375`), loading and menu scenes, Sunblocker demo, the tests that set `Disabled` themselves (`contact_shadow_gpu_test.gd:158`, `grass_card_color_match_gpu_test.gd:119`, `terrain_near_shadow_gpu_test.gd:107`, `tree_billboard_match_gpu_test.gd:809`, `tree_leaf_mip_preview.gd:100`). `haze_consistency_gpu_test.gd:1366` calls `ConfigureMainEnvironment` on an env without sky or receivers. `wet_world_capture.gd:430-433` restores the value it finds; its "norefl" shot becomes a real A/B. `RingCavityLightingCaptureDirector` shares the main env (structural cavity: negligible; manual, not in the manifest).
8. **Covering tests**: far-viewport tag; water (`water_rendering_smoke_test`, `water_ssr_contract_test`, `inland_water_render_gpu_test`); wet-world (`wet_world_gpu_test`); poi (`poi_haze_gpu_test`, `emitter_far_standin_test`, `emitter_bore_gameplay_test`); vegetation (`tree_billboard_match_gpu_test`, `imported_tree_gpu_regression`, `grass_card_color_match_gpu_test`); terrain (`terrain_near_lod_parity_gpu_test`, `terrain_rock_material_gpu_test`, `proxy_gpu_sector_parity_test`); voxel-caves (`underground_mode_smoke_test`; `tools/underground_environment_lighting_smoke_test.gd` runs from `scenes/UndergroundEnvironmentRuntimeSmoke.tscn` and **has no manifest entry**); atmosphere-haze (`haze_consistency_gpu_test`). Isolated test environments with a non-black colour background (for example `terrain_rock_material_gpu_test`, 0.1 / 0.12 / 0.17) can change only where T < 1.

## 7. Work orders

Common rules for every order:
- Tests: `tools/run_tests.ps1 -Files <the order's files> -DryRun`, report the selection, then `tools/run_tests.ps1 -Files <the order's files> -Label r1_<order>`. Report the `results.md` summary line and non-pass rows; do not re-triage `known-fail` rows.
- Probe: `tools/perf_bench.ps1 -Label reflections_r11 -ScenarioFile tools/fixtures/perf_bench_reflections.json`. Until `tools/perf_bench.ps1` learns the probe kinds, it prints `PERF_BENCH=FAIL ... did not run`; read the driver verdict in `console.log`.
- Stills: compare with `tools/media/ffmpeg_tools.ps1 compare`; keep only cited evidence under `.godot/audit_baseline/common/reflections/r11/`; run `tools/prune_test_output.ps1 -Apply` at the end.
- Line numbers are the working tree of 2026-11-17; anchor on the quoted code if they moved.

### R1-1-0. Probe tooling and "before" stills

ROLE: implementer. SIZE: S. DEPENDS ON: none.

OBJECTIVE
The probe produces comparable stills (fixed exposure) at all poses R1-7 needs, reports the environment configuration of all three viewports, and a "before" set exists for the no-op checks.

FILES/SYMBOLS
`tools/perf_bench_reflection_probe.gd` (variant loop :135-155, setup report), `tools/fixtures/perf_bench_reflections.json`.

REQUIRED CHANGES
1. Hold exposure during the stills: set `storm_auto_exposure_enabled = false` on the DaySideAtmosphere node before the first still and restore it afterwards (pattern `tools/cloud_layers_capture.gd:282-283`). Record it in `result["setup"]`.
2. Record in `result["setup"]` for each variant: main Environment `background_mode`, `reflected_light_source`, `ambient_light_source`; the far and regional Environments (`WorldEnvironment` children `FarWorldEnvironment` and `FarRingRegionalWorldEnvironment` under the `RingScaledFarViewport` node) `background_mode` and `reflected_light_source`; after R1-0 the model value, after R1-1b `published_sky_reflection` from DaySideAtmosphere (skip silently if absent).
3. New `reflection_probe` scenarios: forest (`forest_interior_eye`), wet 300 m (`alt300_plains` with `force_wet`), night (`time: {"mode": "elevation", "value": -30.0, "rising": false}` at `plains_arch` and `emitter_rim`), emitter bore (the `emitter_bore_noon` pose of `tools/shadow_harness_common.gd:343`, if the probe can use it; else place one), a megastorm frame if the bench can force one, and a designed cave.
4. Run the probe on the current tree before any R1 code change; keep the stills as the "before" set.
5. **When R1-0 has landed** (separate small step): replace the `bg` / `sky` reflected-source loop with model 0 / model 1 through `RingReflectionModel` (`load("res://.../RingReflectionModel.cs").new().call("SetModel", ...)`), restoring 0 at the end. Until then the `sky` variant is the model-1 preview.

DO-NOT-CHANGE CONSTRAINTS
No game code. Existing scenario names and output paths unchanged.

VALIDATION STEPS
A quick run completes and writes stills for every scenario; the report shows main (1, 0, 3), far (2, 0), regional (1, 0); two consecutive runs of one pose give an empty heatmap (exposure really held).

ESCALATION CONDITIONS
No designed cave or bore pose reachable within the bench: report and propose a pose. Exposure cannot be held (stills of one pose differ run to run beyond cloud noise).

### R1-0. Switch, main reflected source, far pins

ROLE: implementer. SIZE: S. DEPENDS ON: none.

OBJECTIVE
One transitional switch decides whether the main view reflects the sky. At 0 nothing changes; at 1 the main Environment's reflected source is Sky. The far viewports never change.

FILES/SYMBOLS
New `scripts/Voxel/LOD/RingReflectionModel.cs`; `scripts/Voxel/LOD/RingFarViewportColorPipeline.cs` `ConfigureMainEnvironment` (:46-50); `scripts/Voxel/LOD/RingScaledFarViewport.cs:1037` and `:1155`; `project.godot` `[shader_globals]`; new `materials/ring_reflection_model.gdshaderinc`; new test (C# class in `tools/` plus GDScript wrapper, `test-scaffold` skill); `tools/test_manifest.json`.

REQUIRED CHANGES
1. `RingReflectionModel : RefCounted` (instantiable from GDScript, like `RingFarViewportColorPipeline`): static state `Model` (0 or 1, sanitized; default 0) and the registered main Environment (checked with `GodotObject.IsInstanceValid`). `Register(Environment)` stores it and applies; `SetModel(float)` sanitizes, writes the global `ringworld_reflection_model` with `RenderingServer.GlobalShaderParameterSet`, and applies. Apply: `ReflectedLightSource = Model >= 0.5 ? Sky : Background`. Instance methods forward to the statics for GDScript. A static `SourceFor(float model)` for the test.
2. `ConfigureMainEnvironment` calls `RingReflectionModel.Register(mainEnvironment)` after setting the background, with a one-line comment: with a colour background the engine reflects that colour (black); the Sky resource stays for ambient and, at model 1, for reflections.
3. `RingScaledFarViewport.cs`: add `internal const Godot.Environment.ReflectionSource FarReflectionSource = Background` and `RegionalFarReflectionSource = Background`; use them at :1037 and :1155 instead of copying the main value. Comment: whole-ring far (Sky background) resolves to the radiance map, regional far (transparent black) to no reflection, exactly as before; deliberately not copied from main.
4. `project.godot`: add `ringworld_reflection_model={ "type": "float", "value": 0.0 }` as its own hunk next to `ringworld_night_light_scale` (:194). Do not touch the pending `ringworld_ground_rock_detail_normal` entry (about :86-89).
5. `materials/ring_reflection_model.gdshaderinc`: include guard plus `global uniform float ringworld_reflection_model;` only. Every shader that needs the model includes this file (R1-1b, and R1-2a for the water: tell the R1-2 lead), so the global is declared once per shader.
6. New headless test `reflection_environment_contract_test` (manifest: tags `far-viewport`, `render-core`; mode headless; tier gate): `ConfigureMainEnvironment` on a new Environment gives Color, black, `Background`; `SetModel(1)` gives `Sky`; `SetModel(0)` gives `Background`; the two far constants equal `Background`; ends at model 0.

DO-NOT-CHANGE CONSTRAINTS
At model 0 every pixel is unchanged. Far textures unchanged at both values. No menu row, no settings persistence. No shader edits other than the new include.

VALIDATION STEPS
- Tests: the new test, `haze_consistency_gpu_test`, far-viewport tag, `water_rendering_smoke_test`, `cloud_haze_gameplay_test` (run pattern above). `tools/testing/validate_manifest.ps1`.
- Probe at model 0: stills identical to the R1-1-0 "before" set. Report shows far and regional `Background` at both values.
- Cost: matrix scenario `sky_radiance_plains` in `tools/fixtures/perf_bench_reflection_costs.json` at model 0 and 1; the delta should match plan 10.3 (+0.05 to +0.08 ms).

ESCALATION CONDITIONS
Any pixel change at model 0. The far environment values differ from the pins at runtime. GDScript cannot reach the class.

### R1-1a. Holds: trees and grass cards

ROLE: implementer. SIZE: S. DEPENDS ON: none.

OBJECTIVE
Trees, impostors and grass cards look exactly as today at both model values until a visibility term exists.

FILES/SYMBOLS
`materials/imported_tree.gdshader` (after `AO_LIGHT_AFFECT = 0.0;` :146), `materials/imported_tree_impostor.gdshader` (after :244), `materials/ring_grass_cards.gdshader` (after `ROUGHNESS = 1.0;` :191).

REQUIRED CHANGES
1. Both tree shaders: `RADIANCE = vec4(0.0, 0.0, 0.0, 1.0);` with the comment: "Reflections R1-1a hold: the sky term stays black as before the sky source; crown interiors and trunks cannot see the sky and there is no crown-visibility term yet (docs/work-orders/reflections/R1-1-design.md). SSR on wet bark is unaffected."
2. Grass cards: `SPECULAR = 0.0;` with the comment: "Cards have no specular: the engine's environment term ignores render_mode specular_disabled, so it is zeroed here (R1-1a)."

DO-NOT-CHANGE CONSTRAINTS
No other output, no uniform, no direct-light code. Do not touch the water (R1-2a).

VALIDATION STEPS
- Probe: `bg` stills identical to "before" at every pose; `sky` variant (or model 1): trees, impostors and grass identical to the `bg` variant (heatmap empty on them).
- Tests: `tree_billboard_match_gpu_test`, `imported_tree_gpu_regression`, `grass_card_color_match_gpu_test`, `grass_card_layer_gpu_smoke`, `imported_tree_catalog_smoke`.

ESCALATION CONDITIONS
Any change at model 0; a tree pixel changes under the sky variant (would mean `RADIANCE` is not honoured on that path, for example the shadow or depth pass, or a variant compile issue).

### R1-1b. Sky visibility and haze for POI, construction, stand-ins, anchors, voxel terrain

ROLE: implementer. SIZE: M. DEPENDS ON: R1-0 (model global and include); can be written in parallel and merged after it.

OBJECTIVE
Surfaces that should reflect the sky do so only as far as the camera's surroundings can see it (not in caves, not underwater), and distant ones through the haze.

FILES/SYMBOLS
`project.godot`; new `materials/ring_sky_reflection.gdshaderinc`; `scripts/day_side_atmosphere.gd` (`_apply_underground_lighting` :573-577, its caller :556, publish pattern `_publish_night_light_scale` :1193-1195); `materials/ring_poi_architecture.gdshaderinc` (includes :18-22, after `AO_LIGHT_AFFECT = 0.0;` :88); `materials/ring_construction_surface.gdshader` (include after :8, after :45); `materials/ring_emitter_standin.gdshader` (`SPECULAR` :116, after `AO_LIGHT_AFFECT = 1.0;` :123, `haze` from :93); `materials/ring_scale_anchor.gdshader` (`SPECULAR` :65, after :72, `haze` from :45); `materials/voxel_ring_terrain.gdshader` (`haze` :146, after `AO_LIGHT_AFFECT = 0.0;` :156); `tools/underground_environment_lighting_smoke_test.gd` (after :65); `tools/test_manifest.json`.

REQUIRED CHANGES
1. `project.godot`: add `ringworld_sky_reflection={ "type": "float", "value": 1.0 }` as its own hunk next to `ringworld_reflection_model`. Do not touch `ringworld_ground_rock_detail_normal`.
2. `materials/ring_sky_reflection.gdshaderinc` (include guard): includes `ring_reflection_model.gdshaderinc`; declares `global uniform float ringworld_sky_reflection;` and
   `vec4 ring_sky_reflection_radiance(float visibility) { return vec4(0.0, 0.0, 0.0, 1.0 - clamp(ringworld_sky_reflection * visibility, 0.0, 1.0)); }`
   with a comment: share of the sky radiance map this fragment may reflect; the camera's sky access (underground, underwater, published by day_side_atmosphere.gd) times what the caller knows (haze transmittance); 1 writes nothing visible.
3. `day_side_atmosphere.gd`: at the top of `_apply_underground_lighting` (before the `if access >= 0.999: return` at :577) publish `clampf(sky_access, 0, 1) * (1.0 - clampf(current_underwater_factor, 0, 1))` into a public `published_sky_reflection` and the global, writing the global only when the value changes. Comment: reflections follow the same sky access as `ambient_light_sky_contribution`, which the engine does not apply to reflections.
4. POI include and construction: `RADIANCE = ring_sky_reflection_radiance(1.0);` (haze is already in POI `SPECULAR`/`ALBEDO`; construction is near).
5. Stand-in and anchor: `SPECULAR = surface_specular * mix(1.0, sqrt(dot(haze.T, vec3(1.0 / 3.0))), ringworld_reflection_model);` (POI convention, model 1 only) and `RADIANCE = ring_sky_reflection_radiance(1.0);`.
6. Voxel terrain: `RADIANCE = ring_sky_reflection_radiance(dot(haze.T, vec3(1.0 / 3.0)));`.
7. Test: after `_apply_underground_lighting(environment, 0.0)` assert `published_sky_reflection` is 0; call it with 1.0 and assert 1; set `current_underwater_factor = 0.5` and assert 0.5. The test has no manifest entry today: register it (headless, gate, tag `voxel-caves`, marker `UNDERGROUND_LIGHTING_PROPERTY_SMOKE_TEST`, scene `scenes/UndergroundEnvironmentRuntimeSmoke.tscn`) per the `test-scaffold` skill.

DO-NOT-CHANGE CONSTRAINTS
At model 0 every pixel is unchanged (the `RADIANCE` writes are no-ops under a black environment; the stand-in `mix` is exactly 1). No change to the POI `poi_specular` used by `light()`, to ambient, fog or eye adaptation. No menu row.

VALIDATION STEPS
- Probe at model 0: identical to "before". At model 1 (or the `sky` variant): POI metals, construction and stand-ins gain the sky reflection; the cave still is black on game surfaces; `emitter_rim` shows the stand-in highlight dimmed with distance only at model 1.
- Tests: `poi_haze_gpu_test`, `emitter_far_standin_test`, `emitter_bore_gameplay_test`, `wet_world_gpu_test`, `underground_mode_smoke_test`, the registered underground lighting test; `validate_manifest.ps1`.

ESCALATION CONDITIONS
Any change at model 0. A shader fails to compile because a global is declared twice (fix through the includes, do not duplicate). The cave still shows sky on a game shader at model 1.

### R1-1d. Terrain

ROLE: implementer. SIZE: S. DEPENDS ON: R1-1b; **start only when T4-4b and FR-1 have landed their changes to these files.**

OBJECTIVE
Wet ground and river lines kilometres away are seen through the haze, not as an unhazed sky mirror.

FILES/SYMBOLS
`materials/ring_terrain_lighting.gdshaderinc` (`ring_terrain_apply_atmosphere` :28-38); `materials/terrain3d_ring_surface.gdshader` (call :314, `SPECULAR = terrain_specular;` :326); `materials/terrain3d_ring_surface_ground.gdshader` (call :473, :485); `materials/ring_terrain_proxy.gdshader` (`haze` :245, `SPECULAR = surface_specular;` :251); `materials/ring_terrain_proxy_ground.gdshader` (:325, :331). `ring_ground_surface.gdshaderinc`: no change.

REQUIRED CHANGES
1. `ring_terrain_lighting.gdshaderinc`: add `ring_terrain_apply_atmosphere_t(inout vec3 albedo, out vec3 emission, out vec3 transmittance, ...)`; the existing function calls it and drops T.
2. Terrain3D shaders: use the `_t` variant; after the `SPECULAR` line write `RADIANCE = ring_sky_reflection_radiance(dot(transmittance, vec3(1.0 / 3.0)));`.
3. Proxy shaders: after the `SPECULAR` line write `RADIANCE = ring_sky_reflection_radiance(dot(haze.T, vec3(1.0 / 3.0)));`.
4. Include `ring_sky_reflection.gdshaderinc` where needed (once per shader).

DO-NOT-CHANGE CONSTRAINTS
At model 0 every pixel is unchanged. No change to `SPECULAR`, `ROUGHNESS`, the direct wet glint, the ground-feature code or the other leads' lines. Do not touch `ring_terrain_global.gdshader` or `ring_hull_global.gdshader` (shared with the far viewport).

VALIDATION STEPS
- Probe at model 0: identical to "before". At model 1: the change against R1-1b's model-1 stills is confined to distant terrain, distant wet ground and far river lines (dimmer, hazed).
- Tests: `terrain_near_lod_parity_gpu_test`, `terrain_rock_material_gpu_test`, `proxy_gpu_sector_parity_test`, `terrain_near_shadow_gpu_test`, `wet_world_gpu_test`.

ESCALATION CONDITIONS
Any change at model 0. Near-proxy parity breaks at model 1. The pending ground-feature diff has changed the anchor lines so that the edit would touch another lead's code.

## 8. Questions for the user (with the R1-7 sheet)

1. Trees and grass cards keep today's look (no sky sheen) until a visibility term exists. Accept as the interim, or add a crown-visibility term now?
2. Metal undersides and shafts show haze where ground should be until R2a. Accept as the interim?
