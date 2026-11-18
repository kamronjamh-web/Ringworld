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
| `tau_c(rgb)` canopy diffuse transmittance per unit blocked sky | broadleaf (0.025, 0.050, 0.010); conifer 0.6 times that | Goudriaan scattering for LAI 4, leaf scattering albedo 0.25 to 0.3 (green), 0.08 to 0.12 (red, blue): scattered transmission about 0.045 green and 0.01 to 0.02 red/blue of the incident flux, divided by the blocked fraction 0.86. |

## 3. Feature 1: leaf translucency

Per foliage fragment and per light (inside `if (ringworld_forest_lighting.x > 0.5)`):

```
DIFFUSE_LIGHT += (LIGHT_COLOR / PI) * (k_species * s) * facing * visibility * crown_sun
```
(Godot multiplies by `ALBEDO` afterwards, so the transmittance is `k * s * albedo`; haze, wetness, night tint and the cloud darkening of `ALBEDO` apply to it exactly as to the reflected lobe.)

- **visibility**: the existing `visibility` of the reflected lobe (sun map, cloud, height shadow, storm, the day/night direct factor). No new shadow terms.
- **facing, mesh** (`imported_tree`, foliage surfaces only, `use_alpha_cutout`): `max(-dot(Ng, L), 0)` with `Ng` the geometric leaf normal facing the viewer, from `cross(dFdx(VERTEX), dFdy(VERTEX))` in `fragment()` passed to `light()` by a varying. The authored up-pointing shading normal is not used: transmission depends on which side of the leaf the light is.
- **facing, cards and far quad** (`imported_tree_impostor`): the projected-area mean of the mesh quantity for a spherical leaf distribution, `2 * T(beta)`, `T(beta) = (sin(beta) + (pi - beta) cos(beta)) / (3 pi)`, `cos(beta) = -dot(L, V)` (Ross 1981, bi-Lambertian spherical leaves). Backlit (looking at the sun through the crown) gives 2/3, side light 0.21, front light 0.
- **crown_sun**: inside the tree sun-shadow range the sun map already shadows the inner leaves, so `crown_sun = 1`. Past it (and wherever the map has no tree casters: tree shadows suspended at speed, the cards), `crown_sun = exp(-kappa * chord_to_sun)`, the distance from the leaf to the crown hull toward the sun. The weight ramps from 0 to 1 over the last 20 % of the published tree shadow distance (`ringworld_forest_lighting.y`, 0 while tree shadows are suspended). The mesh evaluates it at the vertex (varying); the cards at the expected first-leaf point of the view ray: the front intersection of the view ray with the hull plus `min(1/kappa, chord/2)` along it (pixels outside the hull's outline use the card point).
- **Crown hull**: one ellipsoid per variant (centre height, horizontal and vertical semi-axes) from the foliage surface's bounds of the authored LOD0, in the catalog-normalized tree space. One C# table feeds both shaders: an instance uniform `tree_crown_hull` on each mesh renderer (one variant per renderer) and a new `variant_parameters` row 8 for the impostor (w = `k_species`).
- Point and spot lights get the same lobe (physical, negligible in practice). Bark has no lobe.
- Check against a physical bound (FL-2 test): a backlit crown's mean radiance never exceeds the same crown front-lit (k <= 1).

## 4. Feature 2c: crown self-occlusion

`AO *= exp(-kappa * d_sky)` on foliage, with `d_sky` the exit distance from the leaf to the hull along `normalize(up + 0.7 * outward)` (`outward`: the horizontal direction from the crown axis to the leaf), the direction most of a side leaf's sky comes from. Mesh: per vertex. Cards: at the same expected first-leaf point as section 3. No floor. It applies to the ambient only (`AO_LIGHT_AFFECT` stays 0).

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
| Impostor hand-off | The same hull, `kappa`, `k_species` and the projected-area mean of the same facing term; checked by FL-2's backlit match. |
| Reflections (10.6 Q1) | `ring_canopy_sky_visibility()` is the canopy-visibility term the reflections lane waits for. Terrain and POI ambient specular already follow `AO`. Trees and grass keep `RADIANCE = 0` here; lifting that stays with the reflections lane. |
| Near fog (NF) | The fog shader can read `ringworld_canopy_cover` for its ambient part. |
| Shadow debug view | Every new term is skipped while `ring_shadow_debug_enabled()`. |

## 7. Reuse

- TR-10 needs a canopy height and cover in ring coordinates: this raster has both (2 m, plus or minus 512 m). Its far levels (4 m and 16 m to 3 km) can be a coarser second raster from the same builder.
- The reflections lane can multiply the tree and grass sky sheen by `ring_canopy_sky_visibility()` once it decides to.

## 8. Switch and settings

- Debug switch only, default off: properties `TranslucencyEnabled`, `CanopyShadeEnabled`, `CrownOcclusionEnabled` on a `ForestLighting` node under the game root (reachable by `perf_bench -Variants` node ops and `PERF_BENCH_NODE_PROPS`), plus the start-up environment variable `RING_FOREST_LIGHTING=1` (all three on). Not in the grave-key menu: an on/off is a debug switch (AGENTS.md).
- No user tunables: every value is a physical constant (section 2). If the user wants one after the stills, the candidate is canopy shade as a whole (strength), not per receiver.
- Off is byte-identical to today: every new term sits inside a branch on the switch global, and nothing is published while off.

## 9. Questions for the user (with the stills)

1. Switch both on by default (all worlds)?
2. Nights under trees get darker by the sky visibility (closed canopy: ambient to 20 to 30 %). Physical. Keep, or hold the night ambient as today?
3. The grass cards' translucency is about 0.04 of their reflectance (real blades: 0.8 to 1). Align it with the tree model (same `s`, k about 0.8)? It would make backlit grass glow noticeably.

## 10. Cost plan

| Item | Est. | Where |
|---|---|---|
| Translucency, meshes | 0.02 to 0.06 ms | one more diffuse lobe per light in a triangle-bound shader (fragment work is 6 to 8 % of tree time) |
| Translucency and crown occlusion, cards | 0.01 to 0.04 ms | a ray-ellipsoid intersection per card pixel |
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
