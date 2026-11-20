# Forest lighting: leaf translucency and under-canopy shade

Status: design, 2026-11-18 (lead). Source: `docs/forest-density-study.md` ranks 1 and 2 and its "User answers (2026-11-18)" item 2: both features, all worlds, behind a switch, before/after stills before switching on. Costs marked **est.** are estimates until FL-5 measures them.

Read with: `docs/work-orders/terrain-vegetation/TR.md` (F1 to F9, TR-7 card match, TR-10 canopy term, not built yet), `docs/reflections-plan.md` 10.6 Q1 (trees and grass keep `RADIANCE = 0` until a canopy-visibility term exists: section 6 supplies that term), `docs/sun-flare-shafts-near-fog-plan.md` (NF: the fog shader may read the same canopy cover).

## 0. Summary

| Feature | Physical basis | Technique | Est. 4070 cost |
|---|---|---|---|
| 1. Leaf translucency | A thin leaf transmits sunlight diffusely to its unlit side. Fresh broadleaf in the green band: R 0.10 to 0.15, T 0.10 to 0.20; red and blue lower, blue lowest. Needle shoots transmit much less (thick needles, clumping). | A second diffuse lobe in `light()` of `imported_tree` (foliage) and `imported_tree_impostor`: `tau(rgb) = k_species * s(rgb) * albedo`, gated by the same sun visibility as the reflected lobe and, past the tree sun-shadow range, by an analytic crown transmittance. The mesh uses the leaf's geometric facing; the cards use the expected value of the same quantity for a spherical leaf-angle distribution, so mesh and card agree on average at the hand-off. | 0.03 to 0.1 ms |
| 2a. Canopy sky occlusion | Under a canopy the floor sees a fraction of the sky (sky view factor 0.05 to 0.3 under closed canopy). | A camera-centred canopy raster (2 m texels, 512 x 512, mipmapped) built on the CPU from the resident tree records. One vertex-stage tap in every receiver under trees (terrain, grass, bark, POIs) scales `AO` by the sky visibility. | 0.05 to 0.15 ms GPU (all of 2), CPU on a worker, upload under 0.5 ms per rebuild |
| 2b. Green transmitted light | The sunlit canopy above re-emits a few per cent of the sun downward, green-yellow (multiple leaf scattering). | Same tap: an extra diffuse term for the primary sun in each receiver's `light()`, `E_top * blocked * tau_canopy(rgb)`, gated by the sun visibility above the canopy (cloud, height shadow, macro, storm) but not by the sun map. | included above |
| 2c. Crown self-occlusion | Leaves inside a crown see less sky (turbid-medium extinction). | Analytic: exit distance from the leaf to the crown hull (ellipsoid per variant) toward the sky, `exp(-kappa * d)`, multiplied into the trees' `AO`. Same hull and kappa on the mesh and the cards. | about 0 ms |

Nothing is boosted. Every constant below comes from leaf optics or canopy radiative transfer; none is a user tunable (section 8).

## 1. What exists today (verified 2026-11-18)

- `imported_tree.gdshader` `light()`: Lambert `n_dot_l` on the (authored, crown-like) shading normal plus a Blinn highlight, times `ring_shadow_sun_visibility` (sun map, cloud, height shadow, storm). No back lobe. `AO` = cloud and storm only. `RADIANCE = 0` (R1-1a).
- `imported_tree_impostor.gdshader`: the same `light()`; cards (LOD3, 300 m to 1,920 m) and the far quad (TR-6) are lit with the baked crown normal (TR-7). Variant data in `variant_parameters` (rows 0 to 7, built at run time in `ImportedTreeImpostorCatalog.CreateParameterTexture`).
- Grass cards: `BACKLIGHT = col * 0.28`, added in `light()` as `(LIGHT_COLOR / PI) * BACKLIGHT * max(dot(-N, L), 0) * visibility`. Godot multiplies `DIFFUSE_LIGHT` by `ALBEDO` afterwards (see the comment in `ring_terrain_direct_diffuse`), so the grass transmittance is `0.28 * albedo^2`, about 0.04 of the reflectance: 15 to 25 times weaker than real blades. Out of scope here; section 9 Q3.
- Every receiver writes `AO = min(1 - cloud * 0.72, ring_storm_ambient(...))` in `fragment()` with `AO_LIGHT_AFFECT = 0`: terrain (`terrain3d_ring_surface`, `ring_terrain_proxy`), grass cards, POIs (`ring_poi_architecture.gdshaderinc`), trees. Godot applies `AO` to the ambient diffuse and, through specular occlusion, to the ambient specular, so the engine sky reflection on the terrain and the POIs' `RADIANCE` follow it.
- Tree records: `_vegetationTiles` (`CachedTree`: canonical around/across metres, surface height, instance width and height, species, variant). Variant metadata has `HeightMeters` and `TrunkHeightMeters`. World placement through `RingLodGeometrySnapshot.CanonicalToWorld`.
- Camera-centred rasters in ring coordinates already exist: `HeightShadowFrameMath.BuildFrame` (`scripts/Shadows/RingHeightShadowSchedule.cs`) and the receiver mapping in `ring_height_shadow_sample` (world -> anchor tangent frame -> around/across metres). The canopy raster copies that mapping with its own globals.
- TR-10's canopy raster does not exist yet. This plan builds it so TR-10 can reuse it (section 7).
- GPU tree renderers already take per-renderer instance uniforms (`vegetation_haze_slot`, `RingBiomeDecorationLayer.Gpu.cs:1356`).
- Shader globals are declared in `project.godot [shader_globals]`. That file carries an unrelated editor hunk: main stages only the new globals hunk.

## 2. Constants (physical sources)

| Symbol | Value | Basis |
|---|---|---|
| `s(rgb)` spectral shape of T/R | (0.95, 1.10, 0.45) | PROSPECT-type fresh leaf: T/R about 0.4 in blue (450 nm), 1.0 to 1.2 in green (550 nm), 0.9 to 1.1 over the sRGB red band (570 to 700 nm, weighted near 600 nm). Green-yellow transmitted light. |
| `k_species` T/R ratio in green | maple 0.9; fir, pine 0.35 | Broadleaf transmits about as much as it reflects; needle shoots 0.2 to 0.4 (study section 4). With a leaf-texture green albedo near 0.15 this gives a green transmittance of 0.15 (maple) and 0.06 (needles), inside the 0.1 to 0.4 broadleaf range. FL-2 reports the measured mean foliage albedo per species. |
| `p` crown porosity (sky gap through one crown) | broadleaf 0.20; conifer 0.30 | Typical crown porosity 0.1 to 0.4. |
| `kappa` crown extinction (1/m) | `-ln(p) / (4/3 * r_mean)` per variant | Makes the analytic crown agree with `p`: the mean chord of a sphere of radius r is 4r/3. For a 3 m crown radius: 0.40 (broadleaf), 0.30 (conifer). |
| `lad` leaf inclination classes | per variant, 4 area weights | Area-weighted `acos(abs(n.y))` of the geometric triangle normals of the variant's cutout (foliage) surfaces of LOD2, classes 22.5 degrees wide (centres 11.25, 33.75, 56.25, 78.75 degrees from vertical); azimuth uniform. Measured planophile (area-weighted mean abs n.y 0.69 to 0.77 against 0.5 for spherical). FL-2c. |
| `tau_c(rgb)` canopy diffuse transmittance per unit blocked sky | broadleaf (0.025, 0.050, 0.010); conifer 0.6 times that | Goudriaan scattering for LAI 4, leaf scattering albedo 0.25 to 0.3 (green), 0.08 to 0.12 (red, blue): scattered transmission about 0.045 green and 0.01 to 0.02 red/blue of the incident flux, divided by the blocked fraction 0.86. |

## 3. Feature 1: leaf translucency

Per foliage fragment and per light (inside `if (ringworld_forest_lighting.x > 0.5)`):

```
DIFFUSE_LIGHT += (LIGHT_COLOR / PI) * (k_species * s) * facing * visibility * crown_sun
```
(Godot multiplies by `ALBEDO` afterwards, so the transmittance is `k * s * albedo`; haze, wetness, night tint and the cloud darkening of `ALBEDO` apply to it exactly as to the reflected lobe.)

- **visibility**: the existing `visibility` of the reflected lobe (sun map, cloud, height shadow, storm, the day/night direct factor). No new shadow terms.
- **facing, mesh and impostor (one model, FL-2c)**: the projected-area mean of the bi-Lambertian facing `max(-n.L, 0)` over the viewer-facing leaf side (weight `|n.V|`) for the variant's leaf inclination distribution (`lad`, section 2), azimuth uniform: 4 inclination classes x 8 azimuths evaluated per pixel (`ring_leaf_lad_facing`). It is validated against the exact facet sum of the LOD2 meshes (mean abs error 0.003 to 0.008, worst 0.015 to 0.028). The mesh no longer takes a per-leaf geometric normal (the `dFdx` code is gone): a per-leaf geometric facing cannot be reproduced by a card statistic, so both use the statistic. `L` and `V` are in model space (`V` towards the camera from the fragment's model position); `up` = +Y of the model.
- **crown_sun**: inside the tree sun-shadow range the sun map already shadows the inner leaves, so `crown_sun = 1`. Past it (and wherever the map has no tree casters: tree shadows suspended at speed, the cards), `crown_sun = exp(-kappa * chord_to_sun)`, the crown path length from the leaf towards the sun (`ring_crown_path_length`: an outside point whose ray crosses the crown is attenuated by the chord). The weight ramps from 0 to 1 over the last 20 % of the published tree shadow distance (`ringworld_forest_lighting.y`, 0 while tree shadows are suspended). Mesh and impostor evaluate it per pixel as an expectation over the first leaf on the view ray (`ring_crown_first_leaf_terms`): in a turbid crown the first-leaf depth is exponential with rate `kappa`, truncated to the chord (entry to exit, or to the fragment when the camera is inside the crown); three quantile midpoints `u` = 1/6, 1/2, 5/6 give depth `-log(1 - u q) / kappa`, `q = 1 - exp(-kappa chord)`, and the terms are averaged. Pixels outside the hull's outline use the surface point itself.
- **Crown hull**: one ellipsoid per variant (centre height, horizontal and vertical semi-axes) from the foliage surface's bounds of the authored LOD0, in the catalog-normalized tree space. One C# table feeds both shaders: instance uniforms `tree_crown_hull`, `tree_crown_hull_extra` and `tree_crown_lad` on each mesh renderer (one variant per renderer; also set for the streamed tree renderers in `InstallVegetationVariantUpload`, as in the tile path) and `variant_parameters` rows 8 to 10 for the impostor (row 9 z = `k_species`, row 10 = `lad`).
- Translucency is primary-sun only (`primary_sun` in `light()`): the point and spot lobes the first version gave it are physically negligible and are dropped. Bark has no lobe: the mesh applies it to cutout surfaces only; the impostor gates it to model heights at or above the crown base (`hull.x - hull.z`), where no leaf can be.
- Check against a physical bound (FL-2 test): a backlit crown's mean radiance never exceeds the same crown front-lit (k <= 1).

## 4. Feature 2c: crown self-occlusion

`AO *= sky` on foliage, with `sky` the expectation of `exp(-kappa * d_sky)` over the first leaf on the view ray (section 3; three quantile points), `d_sky` the crown path length from the leaf along `normalize(up + 0.7 * outward)` (`outward`: the horizontal direction from the crown axis to the leaf), the direction most of a side leaf's sky comes from. Mesh and cards use the same function per pixel (the per-vertex evaluation at real leaf positions gave a Jensen gap against the card's one-point evaluation). No floor. It applies to the ambient only (`AO_LIGHT_AFFECT` stays 0). The impostor gate of section 3 (no crown terms below the crown base) applies.
## 5. Feature 2a/2b: the canopy raster and its receivers

**Producer** (`RingForestCanopyRaster`, pure C#, plus a thin publisher node):
- Grid: 512 x 512 texels of 2 m (plus or minus 512 m), RGBA16F, full mip chain, anchored at the camera's canonical position snapped to 16 m. Rebuilt when the camera moves 64 m from the anchor or the tree tile set or edit version changes; at most once per 0.5 s.
- Built on a worker from a snapshot of the resident tree records within 600 m (copied on the main thread: position, surface height, width, height, species, variant crown base). Each tree splats its crown disk (radius width/2, area-weighted coverage so a sub-texel crown still counts).
- Channels, premultiplied by the blocked fraction `b = cover * (1 - p_species)` so mips average correctly: R = `b`, G = `b * base_altitude`, B = `b * top_altitude` (metres relative to the anchor's ground reference), A = `b * species_factor` (1 broadleaf, 0.6 conifer, scales `tau_c`). Coverage is clamped at 1 (overlapping crowns do not exceed full cover).
- Published as globals: `ringworld_canopy_cover` (sampler2D, linear mipmap), `ringworld_canopy_frame` (mat4, world -> anchor tangent frame, rebuilt every frame for the floating origin from `HeightShadowFrameMath.BuildFrame`), `ringworld_canopy_params` (radius, texel metres, half size, 1 = published), `ringworld_forest_lighting` (x translucency on, y tree shadow distance for section 3, z canopy shade on, w crown occlusion on). The texture update and the new frame land in the same frame.
- Off (switch off or not published): nothing is built; every consumer branch is skipped.

**Receiver function** (`materials/ring_canopy_shade.gdshaderinc`):
- `ring_canopy_sample(world)`: map to around/across metres and receiver altitude like `ring_height_shadow_sample`; one `textureLod` at the mip whose texel is about 12 m (the cos-weighted sky footprint for a receiver 10 to 15 m below the mean crown height; isolated trees then block 15 to 25 % under their crown, closed canopy 70 to 80 %); un-premultiply G, B, A by R.
- Height weight `w_h = 1 - smoothstep(base, top, receiver_altitude)`: full under the crowns, none above them (POI roofs, a ridge above the forest). Edge fade from 380 m to 500 m from the anchor.
- Returns `blocked = b * w_h * fade` and the species factor. `ring_canopy_sky_visibility = 1 - blocked`.
- `ring_canopy_transmitted(...)`, primary sun only, in `light()`: `(LIGHT_COLOR / PI) * max(dot(up, L), 0) * tau_c * species * blocked * (0.5 + 0.5 * dot(N, up)) * above`, with `above` the receiver's own sun visibility with the sun-map term set to 1 (cloud, height shadow, macro, storm; the contact term also 1). The sun-map term is left out because the receiver is under the canopy: that is where the transmitted light lives.
- Sampled per vertex (the field is smooth at 12 m) and passed as a varying.

**Receivers** (everything under trees): terrain (`terrain3d_ring_surface`, and `ring_terrain_proxy` where it is drawn inside 500 m), grass cards, POIs (`ring_poi_architecture.gdshaderinc`, so every POI shader gets it), tree bark (`imported_tree` without cutout: trunks under the canopy). Foliage uses 2c instead of the raster (its own crown is in the raster; using both would count it twice). Rocks (T5, `ring_scatter_rock.gdshader`) join once that lane's shader is committed (FL-4b, coordinated with main). Water is out of scope (reflections lane, section 7).

## 6. Interactions

| System | Behaviour |
|---|---|
| Haze | Both new diffuse terms go through `DIFFUSE_LIGHT * ALBEDO`, and every receiver already multiplies `ALBEDO` by the haze transmittance: same attenuation as the sun. In-scatter unchanged. |
| Sun shadows | The sun map keeps sun flecks under the canopy. Section 3 hands crown self-shadowing from the map to the analytic term at the tree shadow distance and while tree shadows are suspended. Floors past the tree shadow distance still get direct sun (today's gap; TR-10's job, which can read this raster). |
| Cloud, storm | Translucency uses the full `visibility`; the transmitted canopy light uses `above`, which keeps cloud and storm. `AO` keeps its cloud and storm factors. Under full overcast only 2a remains (grey, darker floor); the green from transmitted sky light is left out (about 1 % of the floor light, second order). |
| Wetness | Wet darkening is on `ALBEDO`, so it applies to both new terms. SSR unchanged. |
| Night | `LIGHT_COLOR` of the sun is 0 and the direct factor follows night: no translucency, no green. 2a darkens the night ambient under trees by `1 - blocked` (physical). Flagged for the user (section 9 Q2). |
| Eclipse | Both terms follow the primary light, its colour and the hub weights already used by each receiver (`ringworld_primary_light` on the terrain). |
| Impostor hand-off | One shared model (`ring_crown_first_leaf_terms`, `ring_leaf_lad_facing`): the same hull, `kappa`, `k_species`, leaf inclination weights and per-pixel evaluation in mesh and impostor, so they agree by construction; checked by FL-2's backlit match (10 %). |
| Reflections (10.6 Q1) | `ring_canopy_sky_visibility()` is the canopy-visibility term the reflections lane waits for. Terrain and POI ambient specular already follow `AO`. Trees and grass keep `RADIANCE = 0` here; lifting that stays with the reflections lane. |
| Near fog (NF) | The fog shader can read `ringworld_canopy_cover` for its ambient part. |
| Shadow debug view | Every new term is skipped while `ring_shadow_debug_enabled()`. |

## 7. Reuse

- TR-10 needs a canopy height and cover in ring coordinates: this raster has both (2 m, plus or minus 512 m). Its far levels (4 m and 16 m to 3 km) can be a coarser second raster from the same builder.
- The reflections lane can multiply the tree and grass sky sheen by `ring_canopy_sky_visibility()` once it decides to.

## 8. Switch and settings

- Debug switches, not menu settings: properties `TranslucencyEnabled`, `CanopyShadeEnabled`, `CrownOcclusionEnabled` on a `ForestLighting` node under the game root (reachable by `perf_bench -Variants` node ops and `PERF_BENCH_NODE_PROPS`), plus the start-up environment variable `RING_FOREST_LIGHTING=1` (all three on) or `=0` (all three off). Defaults since 2026-11-19 (user): translucency and crown occlusion ON, canopy shade OFF (see "Decisions (2026-11-19)"). Not in the grave-key menu: an on/off is a debug switch (AGENTS.md).
- No user tunables: every value is a physical constant (section 2). If the user wants one after the stills, the candidate is canopy shade as a whole (strength), not per receiver.
- Off is byte-identical to today: every new term sits inside a branch on the switch global, and nothing is published while off.

## 9. Questions for the user (with the stills)

1. Switch both on by default (all worlds)?
2. Nights under trees get darker by the sky visibility (closed canopy: ambient to 20 to 30 %). Physical. Keep, or hold the night ambient as today?
3. The grass cards' translucency is about 0.04 of their reflectance (real blades: 0.8 to 1). Align it with the tree model (same `s`, k about 0.8)? It would make backlit grass glow noticeably.

## 10. Cost plan

| Item | Est. | Where |
|---|---|---|
| Translucency, meshes | 0.02 to 0.06 ms (FL-2c: re-measure in FL-5; each foliage pixel now does the ray-ellipsoid test, up to 3 x (sky + sun) path lengths and 32 facing evaluations) | one more diffuse lobe per light in a triangle-bound shader (fragment work is 6 to 8 % of tree time) |
| Translucency and crown occlusion, cards | 0.01 to 0.04 ms | a ray-ellipsoid intersection and the shared first-leaf and facing terms per card pixel |
| Canopy taps (terrain, grass, bark, POIs) | 0.03 to 0.1 ms | one vertex-stage tap; grass has the most vertices |
| Transmitted term in `light()` | 0.01 to 0.03 ms | a few ALU per primary-sun light pass |
| CPU raster build | 1 to 5 ms on a worker per rebuild | about 10,000 trees within 600 m, 10 to 30 texels each |
| Main-thread upload | under 0.5 ms per rebuild (2.8 MB with mips); escalate above 2 ms (TR-10 limit) | |

Measured in FL-5 on the user's frozen world (`perf_bench -Variants`, `forest_interior`, `forest_edge`, `save_spot`).

## 11. Work orders (one implementer at a time)

| WO | Content | Tests |
|---|---|---|
| FL-1 | Canopy raster builder (pure), publisher node `ForestLighting` with the switches, globals in `project.godot`, the include `ring_canopy_shade.gdshaderinc` (no consumer yet), crown hull table | new headless `forest_canopy_raster_test` |
| FL-2 | Translucency and crown occlusion in `imported_tree` and `imported_tree_impostor` (hull instance uniform, `variant_parameters` row 8, species `k`) | new GPU `forest_lighting_tree_gpu_test` (off identity, backlit rim, mesh vs card backlit match within 10 %, backlit <= front-lit); TR F9 tree tests |
| FL-3 | Canopy receivers: terrain, proxy (inside 500 m), grass cards, POIs, bark | new GPU `forest_canopy_shade_gpu_test` (off identity; floor under a crown darker in ambient, green ratio rises, no change in the open or above the canopy); terrain, grass and POI receiver tests |
| FL-4 | Off identity across builds (HEAD against the change, switch off) at the forest poses, byte compare | `compare` SSIM 1, PSNR inf |
| FL-4b | Rocks (after T5's shader is committed) | |
| FL-5 | Cost: `perf_bench -Variants` (off, translucency, shade, both) at `forest_interior`, `forest_edge`, `save_spot`; stills in capture mode at noon, `sun_8_set` and low sun behind the trees (sun about 12 degrees, facing it), off against on, in `.godot/audit_baseline/common/forest-lighting/` | report table |

## User answers (2026-11-18)
- Grass translucency: align it with the tree leaf model, fixing the double albedo factor. Show before/after stills first.
- Night under a closed canopy: the physical drop to 20-30 % of open-ground ambient is accepted.
- Main: FL-3 may edit terrain3d_ring_surface.gdshader and ring_terrain_proxy.gdshader. Their current uncommitted diffs are line endings only (T4-7 changed no shaders). Stage only FL-3 hunks.


## FL-2c resolution (2026-11-18)
FL-2's check (c) showed the mesh against the cards off by x0.65 to 0.88 backlit and the cards 1.35 to 1.77x darker in ambient. Measured causes (evidence: `.godot/audit_baseline/common/forest-lighting/fl2c_root_cause/evidence.txt`):
1. Facing (main backlit gap): the card used the spherical-leaf mean `2 T(beta)`; the mesh leaves are planophile (area-weighted mean `|n_y|` 0.69 to 0.77 against 0.5) and the visible near-side leaves tilt outward towards the viewer, so the per-pixel mesh facing (0.41 to 0.55) matched neither the spherical mean (0.63) nor an azimuth-random model of the mesh's own distribution (0.50 to 0.54). A per-leaf geometric facing cannot be reproduced by a card statistic.
2. Bark (x0.80 to 1.0): the card applied translucency to trunk pixels, the mesh did not. The mesh foliage pixel fraction (0.87 maple, 0.99 fir, 0.75 pine) equals the residual ratio.
3. Crown AO: the mesh evaluated `exp(-kappa d)` per vertex at real leaf positions (a wide depth spread, about 10 % of leaves outside the inscribed hull) while the card evaluated it at one point: a Jensen gap. Mean visible depths were similar.

Fix: the mesh adopts the card's per-pixel statistical rule, and both evaluate it as an expectation with the same code (include `ring_forest_lighting.gdshaderinc`: `ring_crown_path_length`, `ring_crown_model_point`, `ring_crown_first_leaf_terms`, `ring_leaf_lad_facing`), with the variant's measured leaf inclination distribution (`lad`, `TreeCrownHull.LeafInclination`, instance uniform `tree_crown_lad` / `variant_parameters` row 10). The impostor's trunk lobe is removed by the crown-base gate; translucency is primary-sun only. The (c) backlit tolerance is the plan's 10 %.
Final state and numbers (FL-2c, 2026-11-18). Mesh and impostor are one model, with two engine constraints found on the way: (1) `MODEL_MATRIX` in `fragment()` lacks the MultiMesh instance transform, so the model-space sun direction (`tree_model_light`) and camera come from `vertex()` as flat varyings (FL-2's card crown_sun had this bug); (2) `imported_tree_impostor` is at the 32-varying limit, so k_species, facing and crown_sun reach `light()` as one varying `tree_leaf_transmit`. Crown sky occlusion applies to every mesh fragment at or above the crown base, bark included (branches inside the crown see the same sky; the card's gate); translucency stays on foliage.
Check (c), mesh against cards (gate 10 %), before FL-2c (FL-2) then after:
- backlit increase mesh/card: maple 0.677 -> 0.991, fir 0.689 -> 1.007, pine 0.725 -> 0.941.
- ambient on/off mesh/card: maple 1.766 -> 1.099, fir 1.348 -> 1.023, pine 1.424 -> 1.067.
(a) off identity 0 of 144 frames differ; identity against the pre-FL-2 shaders 0 of 144; (d) holds.

## FL-3a (2026-11-18): canopy receivers, terrain, proxy, POI, bark
Done: `materials/ring_canopy_shade.gdshaderinc` (`ring_canopy_sample`, `ring_canopy_sky_visibility`, `ring_canopy_transmitted`, `ring_canopy_above`, `ring_canopy_up_view`) and its receivers. Every new statement sits behind `ring_canopy_shade_on()` (`ringworld_forest_lighting.z`) and is skipped in the shadow debug view; switch off is byte-identical (checked against the pre-change shaders).
- Terrain3D (`terrain3d_ring_surface`) and proxy terrain (`ring_terrain_proxy`): one vertex tap into a `vec2` varying, `AO *= sky visibility` in `fragment()`, the transmitted term in `light()`. The proxy applies it wherever it is drawn; the include's 380 to 500 m edge fade zeroes it beyond the raster.
- Terrain lighting: `ring_terrain_lighting.gdshaderinc` gained additive overloads `ring_terrain_direct_visibility(..., instance_height, out canopy_above)` and `ring_terrain_direct_diffuse(..., contact_flags, out direct_visibility, out canopy_above)`. `canopy_above` is the sun visibility with the geometry and contact terms set to 1, times the hemisphere direct factor and the primary-sun weight (0 for other lights). The existing functions are untouched (grass cards and the T5 rock shader compile unchanged); the terrain's `light()` calls the new overload only while the switch is on.
- POI (`ring_poi_architecture.gdshaderinc`, both shaders): sampled per FRAGMENT, not per vertex (POI triangles span km; a vertex tap would interpolate across the 500 m raster). `above` carries the POI's own day direct factor and base direct factor.
- Bark (`imported_tree.gdshader`, no cutout): only below the crown base (`tree_model_position.y < tree_crown_hull.x - tree_crown_hull.z`, or no valid hull); at and above the base the crown occlusion of FL-2c applies and the raster would count that crown twice. Foliage never samples it. The shader is at the 32-varying limit, so the tap is in `fragment()` and `tree_leaf_transmit` carries `blocked * species factor` to `light()` for bark (it has no translucency); `above` = cloud and storm only (product path, geometry 1). `tree_model_position` is now also written while only the canopy switch is on.
- The up vector in every receiver is the canopy frame's third row (the ring zenith at the raster anchor), not each receiver's own radial.
- Test `forest_canopy_shade_gpu_test` (synthetic raster; POI quad, bark quads, proxy quad; Terrain3D needs a real world and is left to FL-4). Measured blocked fraction at the 12 m mip: closed stand (3.5 m crowns, 6 m grid) 0.731, isolated crown of radius 5 m 0.175 (radius 3.5 m: 0.084, below the plan's 15 to 25 % because the 12 m footprint is a trilinear blend of 8 m and 16 m texels, so a small crown straddling texel corners reads low), conifer stand 0.533 (species 0.597), fade 0.495 of the interior value at 440 m and 0 at 505 m. Added light under the stand has green/red 1.92 to 2.25 (tau_c 0.050/0.025) and zero in the open and above the crowns; conifer light per unit blocked is 0.58 to 0.60 of broadleaf.

## FL-3b (2026-11-18): grass cards as canopy receiver, leaf translucency aligned with the trees
Done in `materials/ring_grass_cards.gdshader` (the live grass; `ring_standing_grass.gdshader` is the blade render style, untouched). All new code is behind the switches; off is byte-identical.
- Canopy (switch z): `ring_canopy_sample` at the card's ground root (`gp`, one `vec2` varying `v_canopy`), `AO *= ring_canopy_sky_visibility` after the AO line, and in `light()` the new overload `ring_terrain_direct_diffuse(..., v_ground_hs, out direct_visibility, out canopy_above)` (additive, in `ring_terrain_lighting.gdshaderinc`; the grass passes its per-instance height term) plus `ring_canopy_transmitted`.
- Translucency (switch x): `BACKLIGHT = GRASS_LEAF_TRANSMISSION_K (0.8, plan 9 Q3: thin blades) * RING_LEAF_TRANSMISSION_SHAPE`, so the transmittance is `k * s * albedo` like the leaves (was `0.28 * albedo^2`, the double albedo factor). The lobe follows the blade face side toward the viewer (`max(-dot(face, L), 0)`, bi-Lambertian) instead of `-NORMAL` (which is the ground-up bent normal, so with the sun above the horizon the old lobe was about zero). `grass_card_backlight` stays the off-path value; no new menu setting.
- Test `forest_grass_shade_gpu_test` (shipped card material, FL-3a synthetic raster). Measured: off identity 0 of 20 (canopy) and 0 of 6 (translucency) off -> on -> off frames differ, and 0 of 20 against HEAD's grass shader and lighting include; ambient on/off 0.267 under the stand (1 - blocked 0.730 = 0.270), 0.827 isolated crown, 0.462 conifer stand, 1.000 in the open and above; added sun light under the stand has G/R 2.0 times the grass's own G/R (tau_c 0.050 / 0.025). Translucency, open grass, sun 45 deg behind the grass: green 0.0488 -> 0.0928 (gain 1.90); frontlit change 1.0000 (the lobe is zero when the sun is on the viewer's side); transmittance T/R 0.025 -> 0.88 (green), new/old 35 to 91 times.
- Open point for the primary: backlit-on exceeds frontlit-on (green 0.0928 vs 0.0691, ratio 1.34). The card model lights its reflection with the bent ground normal (n.L 0.7) and has no frontlit face-reflection lobe, so the per-leaf bound T <= R holds only as k * s <= 1 (0.76, 0.88, 0.36). The test reports this and asserts k * s <= 1; whether grass needs a frontlit face lobe is a design choice.
- Stills for the user (before/after, off left, on right; rows backlit, frontlit, stand under the canopy with ambient light only): `.godot/audit_baseline/common/forest-lighting/fl3b/contact_sheet.png` and the single PNGs beside it.

## FL-4 (2026-11-18): switch-off identity on the real world

- Tool `tools/forest_lighting_identity_world_gpu_test.gd` (`tools/tree_visibility_run.ps1 -Test forest_lighting_identity -Label fl4`): on the frozen slot 3 world it captures A (current shaders, all three switches off), swaps the pre-forest-lighting sources (commit 3ca696e) into the cached Shader resources at run time (Terrain3D: `shader_override` re-assigned) for B, restores, captures A2; plus BS (old shaders, switches on: positive control) and S (current, switches on). `--stills-only` writes still sets (FL-5); `--default-pair` writes off vs the shipped default.
- First run: 1 to 5 grass-card pixels at 1-2 LSB at noon. Bisect inside `ring_grass_cards.gdshader`: the cause was the old `ring_terrain_direct_diffuse` call moved into the `else` of an if/else (it compiles differently from a top-level call). Restructure: the old call stays verbatim and unconditional; with canopy shade on, a second call of the overload supplies `canopy_above` (its diffuse result discarded). Cost: with shade on, grass evaluates the terrain light terms twice (to be gated on blocked > 0 before shade can default on).
- Final: PASS, all 8 deterministic forest cases (forest_interior_eye, forest_edge_eye; noon and 8 degree sunset; 2 yaws) byte-identical to the pre-forest-lighting shaders. save_spot is not deterministic in this harness (A vs A2 1 to 22 px, the same as B vs A) and is not judged.

## FL-5 (2026-11-18): cost and stills

perf_bench `-Variants` on the frozen world, RTX 4070, 8 s windows, 3 rounds (report `.godot/perf_bench/fl5/20261118_235219/report.md`); dGPU p50 in ms, same_as_a 0.00 everywhere:

| Scenario (baseline GPU p50) | translucency | crown | shade | all |
|---|---|---|---|---|
| forest_interior (8.58) | +0.09 | +0.07 | +0.20 | +0.28 |
| forest_edge (9.41) | +0.08 | +0.04 | +0.21 | +0.30 |
| save_spot (9.52) | +0.10 | +0.07 | +0.16 | +0.25 |

- Render CPU within +-0.03 ms, draw calls unchanged. Canopy raster: build 1.3 to 5.0 ms on a worker (about 4000 trees at forest_interior), upload 0.49 to 0.69 ms on the main thread (target 0.5, limit 2), copy 0.06 to 1.5 ms.
- Stills: `.godot/audit_baseline/common/forest-lighting/fl5/` (off and all-on per pose at noon, 8 degree sunset, 12 degree low sun, night; translucency-only and shade-only at forest_interior noon and low sun; side-by-side pairs in `fl5/pairs/`). The biggest visible effect is backlit open grass at low sun (floor luminance 3 to 6 times); crowns lighter and yellower at noon. Canopy shade is not visible at the three bench poses (none stands under a closed canopy), so the night drop under a closed canopy still has no measurement. With all three on, grass at night was 1.5 times brighter: the grass translucency lobe applied to every light, including the night light.

## Decisions (2026-11-19, user)

- Default ON: leaf translucency and crown occlusion (`RingForestLighting` property defaults; cost about +0.12 to +0.17 ms on the 4070 from the FL-5 table). Canopy shade stays OFF by default until the grass duplicated-terms gate (skip the second terrain-terms call where blocked = 0) is in and stills at a closed-stand pose (including night) exist.
- The grass translucency lobe is primary sun only, as the trees (no translucency glow from the night light).
- Open: grass backlit-on exceeds frontlit-on by 1.34 (FL-3b) because the grass card's reflection uses a ground-blended normal; accepted with the stills, revisit with the grass reflection model.

## FL-6 (2026-11-20): canopy shade default readiness (grass gate, closed-stand stills, cost)

- Grass gate (`materials/ring_grass_cards.gdshader` `light()`): the second terrain-terms call (the overload that supplies `canopy_above`) and the transmitted term now run only where `ring_canopy_shade_on() && v_canopy.x > 0.0`. The verbatim first call stays unconditional (FL-4). Where blocked = 0 the transmitted term was exactly 0, so the gate changes no pixel; under the canopy the grass still evaluates the terms twice.
- Single evaluation under the canopy (the overload replaces the verbatim call inside an if/else on blocked > 0) was tried as two grass-only variants (g2a, g2b) in the real world (`--variants`): with every switch off they differ from the gated shader by 2 to 116 px (1 to 35 LSB) at deterministic forest poses, the same compile effect FL-4 found. Not adopted.
- Proof (`forest_grass_shade_gpu_test`, new checks): (e) canopy on vs off at blocked = 0 (open, above the crowns), 0 of 8 frames differ; (f) against HEAD's grass shader (`FOREST_GRASS_SHADE_REF_SHADER`), canopy off and on at every location and light, 0 of 40 frames differ.
- FL-4 world identity is stale: B (the 3ca696e sources) now differs from A by 100k to 330k px at every pose because later commits changed those shaders; it needs a new base (or the reference-shader approach of check (f)) before it is useful again.
- Closed stand: `--find-stand=forest_interior_eye` searches the published raster (`RingForestLighting.DensestCanopyOffset` / `CanopyBlockedAt`, debug probes). forest_interior_eye itself has blocked 0.009 (why FL-5 saw no shade); the densest stand within 400 m is at around 6,381,132 m, across 152,180 m (ground 671 m), blocked 0.90 (8 m mip) / 0.86 (16 m mip).
- Stills (`.godot/audit_baseline/common/forest/canopy_shade_default/`, shipped default left, default plus canopy shade right in `pairs/`; floor luminance shade / default): closed stand noon 0.89, 8 degree sun 0.42, night 0.00005 -> 0.00000 (the floor is already black at night without shade); forest edge 1.00 at all three (only the trunks below the crown base change). With shade off, bark below the crown base is visibly brighter than the crown-occluded bark above it (a seam at the crown base); shade on removes it.
- Cost (perf_bench `-Variants` on the frozen world `slot3_2026-11-16`, RTX 4070, 1600 x 900, VSync off, 8 s windows, 3 rounds A-X-A; baseline = the shipped default, translucency and crown occlusion on; report `.godot/perf_bench/canopy_shade_default/20261120_031740/report.md`): canopy shade adds GPU p50 +0.34 ms at the closed stand (A-A spread 0.57 ms there, range +0.07 to +0.35), +0.35 ms at forest_interior, +0.34 ms at forest_edge (A-A spread 0.03 to 0.06 ms); draws unchanged, render CPU within +-0.02 ms, main-thread `process` p50 +0.13 to +0.31 ms (hidden while the frame is GPU-bound). The cost is the same in the open and under the closed stand, so it is the receivers' fixed work (per-vertex raster taps, terrain overload, POI and bark taps) rather than the grass double evaluation; FL-5 measured +0.20 ms against an all-off baseline in an earlier tree state.
- Default flip: prepared, not applied (`.godot/audit_baseline/common/forest/canopy_shade_default/canopy_shade_default_on.patch`: `CanopyShadeEnabled = true` plus the summary comment and the `--default-pair` set). Default stays OFF until the user decides.
