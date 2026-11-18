# Forest density study: why the Godot forest demo reads as dense, and what Ringworld lacks

Status: read-only study, 2026-11-18. No code, settings or assets changed. No GPU run was made: every Ringworld cost below is either a measured number cited from an existing doc, or an estimate marked **est.** and derived from those measurements.

Sources:
- The demo is `GamesNotDeveloped/godot-forest-demo`, branch `master`. Files read: `levels/test_biomes.tscn`, `addons/gnd_biomes/{Biomes,BiomeScatterEntry,AutoBiomesFog}.gd`, `materials/{environment.tres,tree_wind.gdshader,Foliage_Master_Shader.tres}`, `scenery/SunShafts{CompositorEffect,Controller}.gd`, the grass material, the tree and plant materials, the glTF bounds of every tree model, `levels/biomes_mask.png`, `project.godot` and the player prefab.
- Ringworld: `RingBiomeDecorationLayer.cs` and `.TreeStreaming.cs`, `ForestRegionSampler.cs`, `materials/imported_tree.gdshader`, `ring_grass_cards.gdshader`, `RingGrassQualityProfile.cs`, `RingAmbientOcclusionController.cs`, `docs/terrain-vegetation-revamp-plan.md` (sections 6 to 8, 11, 12), `docs/work-orders/terrain-vegetation/T6.md` and `TR.md` (TR-1, TR-8 to TR-10), `docs/perf-pass-2026-11.md` (0.2, 0.4, 0.7, 0.14) and `docs/occlusion-culling-plan.md`.

## 0. Answer in short

- **The demo is a hectare-sized thicket.** Its whole world is 100 m x 100 m, with about 2,500 trees per hectare. The trees are 2 to 7 m tall, and their crowns reach close to the ground. The player is scaled to 0.65, which lowers the eye to roughly 1.1 to 1.3 m, and the field of view is 64.5 degrees. Eye-level sightlines close within about 4 m. The view never reaches the edge of the world, so the demo can thin its trees to 10 % beyond 50 m and swap them for billboards at 20 to 40 m without anyone seeing it.
- **Ringworld's "dense forest" is open woodland by real-world standards.** Placement caps the canopy at 133 stems per hectare (12 candidates per 30 m cell), then applies cover, a grove patch and a spacing pass. At `forest_interior_eye` on the user's slot-3 world there are 6 trees within 30 m (21 per hectare locally, perf 0.1). On the TR-1 fixture world, about 200 trees stand inside 100 m (about 64 per hectare). Eye-level occluders are trunks and the odd sapling. **Mean eye-level sightline is about 120 m in dense habitat and 450 m at the interior pose, 30 to 100 times the demo's.** There is no shrub, fern or understory layer.
- **The demo's fog is not what makes it dense.** Its depth fog peaks at 8.85 % opacity at 570 m. Its volumetric fog is only 6.23 m long, so everything beyond about 6 m gets the same 9 % veil, nudged between 3 and 9 % by a forest mask. What the fog does do is warm and soften the view, and its froxels show tree-shadowed sun streaks in the first few metres in front of the camera (sun volumetric energy 6.2).
- **The look comes from five things, in this order:** sightline closure (stem density plus crowns down to eye level), undergrowth, the lighting under the canopy (SDFGI, SSIL and SSAO darken the floor and tint it green), strong leaf translucency, and the grade (a LUT, desaturation, AgX contrast 1.57, flat warm ambient). The first four are physical. **The grade, the constant veil and the 2.5-degree sun are stylistic**, and this study does not recommend copying them.
- **What we lack, ranked by visual gain per ms** (section 5):
  1. leaf translucency on tree foliage;
  2. canopy sky occlusion for everything under the trees, plus crown self-occlusion;
  3. an understory that closes eye-level sightlines to 30 to 60 m in dense habitat;
  4. weather-driven near fog with shafts (study 2);
  5. denser young stands.

  Items 1 and 2 are not in any phase today. Items 3 and 5 are T6 (T6-3, T6-4, T6-7, T6-9). Item 4 is in `docs/sun-flare-shafts-near-fog-plan.md`.
- **Cost reality.** Forest poses already run at 15.5 to 20 ms on the 4070 against the 11.1 ms target, and trees are triangle-bound: about 1.1 ms per million triangles, with fill barely mattering. More canopy stems is the expensive way to get density (est. +2 to 3 ms to double them). Near-only cards and lighting terms are the cheap way.

## 1. The demo, measured from its files

### 1.1 Scale and placement (`levels/test_biomes.tscn`, `Biomes.gd`)

| Layer | Rule | Count (derived) |
|---|---|---|
| Trees | Jittered grid, one candidate per 1.9 m cell (`average_spacing` 1.9), area 100 x 100 m. The mask (`mask_inverse`) keeps 92 % of the area. Entry weights: pine-1 0.1, pine-3 0.2, pine-2 0.1, birch 0.05. Scale 0.4 to 1.4. | about 2,550 trees, **about 2,500 per hectare** |
| Plants | 2.4 m cells, capped at 7,000. Fern "paproc" 0.6, plant99 0.5 (scaled up to 2x), flower-2.1 0.2, flower-2.2 0.25. No shadows. | about 1,600, about 1,600 per hectare |
| Flowers | 1.0 m cells on a flower mask, capped at 500, no shadows | at most 500 |
| Grass | A 15 m radius around the camera, 0.2 m spacing, capped at 10,000 (about 0.27 m effective). Backlight 0.71. | 10,000 |

- Tree sizes, from the glTF bounds:
  - pine-1 is 5.3 m tall at scale 1, so 2.1 to 6.4 m with its scale range, with a crown about 2 m wide starting at 2.0 m (0.8 to 2.4 m after scaling);
  - pine-2 is about 7.3 m and birch about 7.4 m (crown about 5 m wide).
  - pine-3's node transforms could not be resolved from its file.
- Instancing: one `MultiMeshInstance3D` per 16 m chunk and species, with chunk colliders.
- The far form is a camera-facing (fixed-Y) billboard with a baked texture, switched by `visibility_range` at 40 m for trees and 20 m for the birch and the plants.
- **Density LOD**: each chunk keeps a hash-ranked prefix of its instances. Trees fall from 100 % at 30 m to 10 % at 50 m; plants fall from 100 % at 10 m to 10 % at 20 m.
- Camera: the player is scaled to 0.65 (eye about 1.1 to 1.3 m) and the field of view is 64.5 degrees. Ours is 1.7 m and 80 degrees.

### 1.2 Lighting and atmosphere (`materials/environment.tres`, the light node)

- **GI**: SDFGI, SSIL and SSAO are on (SSAO light affect 0.11). Ambient is a flat warm colour at energy 2.35 with no sky contribution.
- **Tonemap and grade**: AgX (exposure 1.32, contrast 1.57), glow (intensity 0.96, bloom 0.23), saturation 0.88 and a Krita-made 3D LUT.
- **Depth fog**: density 0.0885, begin 3.4 m, end 570 m, curve 0.33. In Godot depth mode, fog = pow(smoothstep(begin, end, d), curve) x density, so it is **2.4 % at 50 m and 8.85 % at most**. Sun scatter is 0.34.
- **Volumetric fog**: density 0.015, length **6.23 m**, anisotropy 0.35, GI inject 1.1, sky affect 0.77, temporal reprojection 0.95. Beyond the froxel length the last slice applies, so everything past about 6 m carries a fixed 1 - exp(-0.015 x 6.23) = **9 % veil**. `AutoBiomesFog` tweens the density between 0.005 (clearing, 3 %) and 0.015 (forest) from the mask under the player.
- **Sun**: energy 1.41, indirect energy 0.93, **volumetric fog energy 6.18**, angular size 2.5 degrees (the real sun is 0.53), PSSM shadows to 200 m. Trees cast shadows; plants do not.
- **Screen-space sun shafts** (`SunShaftsCompositorEffect.gd`, post-transparent): a 40-tap radial blur toward the sun of pixels brighter than a threshold, added in a warm gold (0.72, 0.64, 0.38). It is the classic GPU Gems 3 method: light is added whether or not there is air to scatter it.
- **Foliage** (`tree_wind.gdshader`):
  - `BACKLIGHT` is 0.6 to 0.74 grey on the tree leaves and 0.28 to 0.34 on the fern, with SSS 0.52 on the birch;
  - wind is vertex sway;
  - the pine needles use alpha scissor 0.0, so the cards are effectively opaque.
  - `Foliage_Master_Shader.tres` is a small visual shader (albedo times colour, saturation, emission, double-sided); it adds nothing to the look.

### 1.3 Why it reads as dense: eye-level sightline closure

A horizontal ray through randomly placed vertical occluders of density n (per m²) and width w has a mean free path of λ = 1/(n w). The chance of seeing past a distance d is exp(-d/λ).

| | n (per m²) | effective w at eye height | λ | chance of seeing past 20 m |
|---|---|---|---|---|
| Demo trees (crowns from 0.8 to 2.4 m, eye about 1.2 m) | 0.255 | about 1 m (trunks and low crowns) | **about 4 m** | under 1 % |
| Ringworld dense habitat (60 to 130 stems/ha, a quarter of maples and a fifth of pines are saplings) | 0.006 to 0.013 | about 1 m (0.4 m trunks, 3 m sapling crowns) | **about 75 to 160 m** | 77 to 88 % |
| Ringworld `forest_interior_eye` (6 trees within 30 m) | 0.0021 | about 1 m | **about 450 m** | 96 % |
| Real mature temperate forest with a shrub layer (300 to 600 stems/ha, shrub cover 20 to 60 %) | | | **about 20 to 60 m** (typical summer visibility) | |

The demo is a real stand type, a young conifer plantation or thicket, so the look is physical. It is still the extreme end. **A realistic target for our dense habitat is 30 to 60 m of eye-level visibility, not 4 m.** Our canopy is also open overhead. A hundred trees per hectare with 10 m crowns, randomly placed, cover about 55 % of the sky, where a closed forest covers 80 to 95 %. Looking up, the player sees mostly sky.

## 2. Ringworld's forests today

| Topic | Today | Source |
|---|---|---|
| Species and layers | Canopy only: maple, fir and pine from the LOLIPOP packs (30 variants). Saplings compete as canopy trees (25 % of maples, 21 % of pines). No shrubs, ferns, dead wood or litter. Grass cards under the trees at forest weight 0.42. | T6 P4, P10, P15 |
| Placement | 30 m cells, 12 candidates each (a **cap of 133/ha**). Chance = regional density (0 to 1; dense forest ≥ 0.53) times a grove patch (0.55 to 1.46 at GroveDensity 0.7). A Matérn spacing pass of 2.5 to 9 m by moisture and cover. No trees at 32 degrees or steeper. | `TreeStreaming.cs` 1172-1290, `ForestRegionSampler.cs` |
| Density by habitat | Uniform variant choice inside a species, scale 0.85 to 1.15. No stand age, so young and old trees mix everywhere. | T6 P10 |
| LOD bands (shipped, F1) | LOD0 0-40 m (dither 25-40), LOD2 25-150 m, LOD2b 130-350 m, crossed cards 300-1,920 m, far quads 1,920-5,500 m | TR F1, TR-8 |
| LOD bands (user profile) | Near detail 10/20 m (memory note); LOD2 out to 900 m, cards 550-900 m | TR-1, memory |
| Budgets that cap density | 65,536 GPU roots (about 3 km of forest; TR-9 plans 262,144). Slot caps 256 / 2,696 / 5,200 / 15,000. Rank gates 0.35 for cards and 0.10 for far quads (TR-9 sets them to 1.0). | TR-1 sections 4 and 5 |
| Grass | Cards at 14 per m² near, reach 400 m; grass draw 0.25 to 0.67 ms | `RingGrassQualityProfile.cs`, perf 0.14 |
| Culling | GPU per-tree LOD bands. No per-instance frustum culling yet (OC phase 1 planned, 0.7 to 1.0 ms in forest). Occlusion "adds nothing measurable" today because the forest is too sparse for trees to hide trees. | occlusion plan |
| Lighting | SSAO only (radius 1.2 m, intensity 2.15). No SSIL or SDFGI. Godot volumetric fog is forced off by `RingVolumetricCloudLayer`. Tree foliage is Lambert with no translucency (`imported_tree.gdshader` `light()`). The tree `AO` carries only cloud and storm occlusion. Tree `RADIANCE` is held at black (R1-1a). Grass cards have backlight 0.28. Nothing under the canopy is darkened except by direct sun shadows. | shaders, controllers |
| Haze | Physical haze at Rayleigh-only half density (vastness 1.1 item 3): invisible inside a forest. The static and weather mist layers were removed on 2026-11-16 because they were anchored at sea level. | haze plan, MIST-1/2 |

**Cost wall (4070, 1080p).**
- Trees are 4.2 to 7.8 ms at the shipped fades before TR-5 and 3.1 to 6.0 ms after it (TR-8), out of a 15.5 to 20 ms forest frame. The target is 11.1 ms (perf 0.2).
- They are not fill-bound: at a quarter of the pixels the tree cost keeps 94 %.
- Per tree: LOD0 7 to 10 µs, LOD2 0.75 to 1.8 µs, a card tree 0.02 to 0.06 µs.
- Tree sun shadows cost 1.2 to 2.5 ms and follow the number of caster batches.

## 3. Factor by factor: what makes the demo dense, and whether we have it

| Factor | Demo | Ringworld | Physical or stylistic | Belongs to |
|---|---|---|---|---|
| Stem density at eye level | 2,500/ha, crowns to the ground | 60 to 130/ha in dense habitat, crowns high | physical (a young stand) | T6-3 (stand age: young stands dense with small classes) |
| Understory (shrubs, saplings, ferns) | ferns and broadleaf plants at about 1,600/ha, grass within 15 m | none; saplings scattered in the canopy lottery | physical | T6-4 saplings, T6-7 shrubs, T6-9 ferns and ground-cover cards |
| Dead wood | none | none | physical | T6-5, T6-6 |
| Forest floor | grass under the trees | grass cards at forest weight 0.42 | under a closed canopy, real floors are litter, moss and ferns with little grass | T6-9 (grass reads canopy), T4 ground material |
| Short sightlines | about 4 m | 75 to 450 m | follows from the rows above | as above |
| Bounce light and AO | SDFGI, SSIL, SSAO | SSAO only | physical | **not planned**; section 5 item 2 is the cheap substitute |
| Canopy darkening of ambient | implicit in SDFGI and SSIL | none: the floor gets open-sky ambient | physical (sky view factor about 0.05 to 0.3 under a closed canopy) | **not planned** (TR-10 handles direct sun only) |
| Leaf translucency | BACKLIGHT 0.6 to 0.74 on trees, SSS on the birch | none on trees; 0.28 on grass | physical. A green leaf transmits about as much visible light as it reflects (both about 0.05 to 0.15). The demo's value is roughly 1.5 to 2 times albedo, so it is boosted. | **not planned** |
| Near fog and shafts | a 6 m froxel veil (9 %), sun volumetric energy 6.2, radial screen shafts | none | the veil is constant whatever the weather; the screen shafts add light without air: **stylistic** | study 2 (weather-driven, physical) |
| Grade | LUT, saturation 0.88, AgX contrast 1.57, exposure 1.32, flat warm ambient | AgX, model-derived sky ambient | **stylistic** | none: do not adopt |
| Soft sun | 2.5 degrees | real sun size | **stylistic** (5x the sun's diameter) | none |
| Camera | eye at about 1.2 m, field of view 64.5 degrees | 1.7 m, 80 degrees | camera choices; a narrow field of view compresses depth | none (mention only) |
| Scene scale | 1 ha, fog-closed | km-scale, 5.5 km of trees | the demo's LOD tricks only work because nothing beyond 50 m is ever seen | none |

## 4. What each missing item costs at our scale (4070, 1080p)

Measured anchors: trees are about 1.1 ms per million triangles and fill-insensitive (TR-1 3). Grass draw is 0.25 to 0.67 ms. Near terrain is 4.5 to 5.1 ms. A card tree costs 0.02 to 0.06 µs. All of the following are **est.**

| Item | What it needs | Est. 4070 cost | Basis |
|---|---|---|---|
| Leaf translucency (trees) | a back-side diffuse term in `imported_tree.gdshader` `light()` and the impostor, gated by the same shadow visibility; leaf transmittance about 0.7 to 1.0 times albedo for maple, 0.2 to 0.4 for needles | **0.03 to 0.1 ms** | trees keep 94 % of their cost at a quarter of the pixels (TR-1 3), so fragment work is about 6 to 8 % of tree time; one extra term is a fraction of that |
| Canopy sky occlusion (all receivers) and canopy-shade tint | a canopy-cover raster near the camera (2 to 4 m texels, from tree records; it can share TR-10's canopy raster). One fetch in the terrain, grass, tree, understory and POI shaders scales ambient by the sky view factor and tints it toward the transmitted green. | **0.05 to 0.15 ms** GPU; CPU raster on tile streaming (TR-10 escalation limit: no rebuild hitch over 2 ms) | one texture tap in shaders that cost 0.5 to 5 ms |
| Crown self-occlusion | ambient darkening by depth inside the crown, from the vertex position against the crown hull (instance height and width are known) | **about 0 ms** | vertex math in a triangle-bound shader |
| Understory: shrubs, saplings, ferns, near-only (to about 150 m), sized to give λ of 30 to 60 m in dense habitat (about 300 to 500 shrubs/ha, so 2,000 to 3,500 resident) | T6-4, T6-7, T6-9; crossed cards textured from the leaf clusters; no sun-map shadows below Very High | **0.15 to 0.4 ms** (alpha-cut overdraw near the camera; the depth prepass limits shading) | T6 gate (a): 0.3 ms and 16 draws on the A1000. Cards are cheap per instance; the cost is pixels near the camera. |
| Dead wood (fallen trees, snags, stumps) | T6-5, T6-6, about 400 resident | 0.05 to 0.1 ms | LOD0 to 2 bark only, near-only |
| Grass thinned under the canopy | T6-9 | **-0.05 to -0.2 ms** (fewer cards) | grass draw 0.25 to 0.67 ms |
| Young stands denser (more small trees per ha) | T6-3, plus raising the 12-candidate cell cap for young stands (or a separate sapling class) | ± 0.5 ms at a fixed triangle budget (small variants are 0.5 to 3.4k triangles at LOD0) | T6 gate (b): the canopy remix within +5 % of the generation-1 `trees_hidden` delta |
| Doubling canopy stems in dense habitat | more full trees | **+2 to 3 ms** | 1,500 to 2,200 LOD2 trees at 0.75 to 0.9 µs after TR-8, plus LOD0, plus shadow batches |
| Weather-driven near fog with shafts | study 2 (Godot volumetric fog with a terrain-relative fog shader) | 0.4 to 0.8 ms while active, 0 otherwise | study 2 section C |
| SSIL | engine SSIL, half resolution | 0.6 to 1.5 ms | Godot SSIL at 1080p; low gain per ms once item 2 exists |
| SDFGI | engine SDFGI | 1.5 to 3+ ms, and probably unusable | it voxelises static meshes in camera cascades; our terrain streams and our trees are GPU-indirect multimeshes; ring scale |

Interaction worth knowing: once understory closes sightlines at 30 to 60 m, trees beyond about 100 m in forest interiors become hidden. The occlusion study's "trees hiding trees adds nothing" was measured on today's sparse forest. With an understory, its colour-pass occlusion variant (OC phase 2, a user decision) would save more than the 0.1 ms it nets today. This is an expectation, not a measurement.

## 5. Ranked recommendations (visual gain per ms)

| Rank | Item | Gain | Est. ms | Phase | Realism note |
|---|---|---|---|---|---|
| 1 | **Leaf translucency on tree foliage** (trees and impostor, identical at the hand-off) | high in every sunward or backlit view, which is most forest shots; crowns glow instead of going flat and dark | 0.03 to 0.1 | **new** (rendering only; could apply to all worlds, as TR D1 did) | physical if transmittance stays near albedo (maple) and lower for needles; the demo's 0.6 to 0.74 grey is a boost |
| 2 | **Canopy sky occlusion plus canopy-shade tint, and crown self-occlusion** | high: a dark, green-shaded floor makes sun flecks and shafts read; the enclosed feel SDFGI gives the demo | 0.05 to 0.15 | **new**; shares TR-10's canopy raster; could apply to all worlds | physical (sky view factor, transmitted green); no floor or boost needed |
| 3 | **Understory sized to close sightlines** (saplings as their own class, shrubs, ferns; densest at edges and in young stands) | highest structural gain; the main reason the demo reads dense | 0.15 to 0.4 | T6-4, T6-7, T6-9 (new worlds) | target 30 to 60 m of eye-level visibility in dense habitat, more in open woodland; not the demo's 4 m |
| 4 | **Weather-driven near fog with near shafts** | very high when present (dawn, humid, after rain), none otherwise | 0.4 to 0.8 while active | study 2 | physical only if driven by conditions and anchored to the terrain; the demo's constant veil is stylistic |
| 5 | **Stand age: dense young stands of small trees, sparse old stands** (raise the cell cap for young stands) | medium-high; gives real thickets in places | ± 0.5 at a fixed budget | T6-3 (new worlds) | physical |
| 6 | Forest floor: grass thinned under canopy, litter and moss ground, dead wood | medium | about 0 (grass savings pay for dead wood) | T6-5, T6-6, T6-9, T4 | physical |
| 7 | Tree count to the budget, so the canopy is continuous from outside | medium, mostly from outside the forest | 0.3 to 0.7 (TR-1 at 262,144 roots) | TR-9 | physical (no scaling up) |
| 8 | Far canopy tint | medium at range | about 0.05 | T6-10 | physical |
| 9 | SSIL | low once item 2 exists | 0.6 to 1.5 | none | physical; poor value |
| - | Not recommended: the demo's grade (LUT, desaturation, contrast, warm flat ambient), the constant fog veil, the 2.5-degree sun, screen-space radial sun shafts, doubling canopy stems, SDFGI | | | | stylistic, or too costly at ring scale |

Notes for the planner:
- Items 1 and 2 are small, change no placement and need no generation step if the user allows all worlds (TR D1 precedent). They also make items 3 and 4 look right: understory and shafts read weakly over a bright, open-sky-lit floor.
- Item 3 is the core of T6. This study gives T6-1 a measurable target: an eye-level visibility of about 30 to 60 m at `forest_interior_eye` in dense habitat. The λ = 1/(n w) rule converts that target into a shrub density, and a headless test can check it (count occluder crossings along sample rays).
- **Budget.** Forest poses are already 4 to 9 ms over the 11.1 ms target. Items 1, 2 and 6 are nearly free. Item 3 should be paid for by OC phase 1 (0.7 to 1.0 ms in forest) and the T6-9 grass reduction. Item 4 is conditional.

## 6. Questions for the user

1. Leaf translucency and canopy sky occlusion change how every forest looks (darker floor, glowing backlit leaves). Should they apply to all worlds, as the tree LOD work did (TR D1), or only to new worlds?
2. Is 30 to 60 m of eye-level visibility in dense forest the right target? That is a mature forest with a shrub layer. The demo's 4 m is a young plantation; T6-3's stand age would give both kinds in places.
3. The understory costs an estimated 0.15 to 0.4 ms in a frame already over budget. Should it be funded by OC phase 1 (per-instance tree frustum culling) before T6-4 and T6-7 land, or ship with presets scaling its reach?

## User answers (2026-11-18)
1. Target for dense forest: 30-60 m of eye-level visibility, as real dense temperate forest. It is reached mainly with the understory and young dense stands.
2. Leaf translucency and the under-canopy shade (canopy sky occlusion plus a green tint): do both, in all worlds. Build them behind a switch and show before/after stills before turning them on.
3. Understory keeps the T6 order. It is not pulled ahead of occlusion culling.
