# Grass redesign: clump cards with terrain-sampled colour

Read-only design notes. No project files were changed. Repo: C:\Projects\RingworldTechDemo. Target GPU: RTX A1000 6 GB laptop.

## 0. Current system (evidence)

### Where cost and reach come from
- User prefs (`%APPDATA%/Godot/app_userdata/RingworldTechDemo/gameplay_grass_defaults.tres`): BladesPerSquareMeter 96, MaximumResidentTiles 1018, MaximumInstancesPerTile 1700, LodNear 250 / Mid 632 / Far 1156 m, VisibilityRadius 295, camera-facing on (51 to 2000 m), UseGrassClumps, reduced near geometry, single-ribbon intermediate.
- Tiles are 16 m (`RingGrassQualityProfile.cs:58-79`). Each root is a meadow tuft of up to 8 blades. Near tufts are capped at 32 triangles and other tiers at 16 (`RingMeadowGrassMeshLibrary.cs:47-49`). Near and intermediate are separate passes, each with its own instance buffer (`RingStandingGrassLayer.cs:28-33`). Today that is 392k roots, or roughly 5–10M submitted triangles.
- Reach is not what the sliders say. `EffectiveGrassDrawDistance` (`RingStandingGrassLayer.Appearance.cs:29-42`) clamps the coordinated draw distance to the guaranteed resident window, `tile*(sqrt(tiles/pi)-sqrt(2))`. That gives 16*(18.0-1.41), about **265 m**, even though LodFar is 1156 m.
- Measured on the A1000 at 1280x720 in isolation (`artifacts/grass-compute-benchmark/report.json`): with 5.07M blade primitives, the viewport GPU takes 2.91 ms against 0.97 ms with grass hidden. At 2.56M primitives it takes 2.18 ms. That works out to about 0.4 ms per million blade triangles in isolation. At spawn in the full game (1600x900, both passes, 1018 tiles) the cost is 9.6 ms (`docs/perf-ranking-2026-10-15.md:39`).

### Why colours do not match
1. Grass albedo goes to **constant per-biome texture averages**. In `ring_grass_appearance.gdshaderinc:33-40` the `grass_terrain_average_*` uniforms come from `RingTerrainVisuals.CaptureTexturePalette()` (`RingTerrainVisuals.cs:48-73`, `RingStandingGrassLayer.Appearance.cs:129-151`). The ground under the grass is a 5 m-period textured triplanar surface (`terrain3d_ring_surface.gdshader:249-269`, `biome_texture_scale=.2`). The result is flat-coloured grass on top of textured ground, plus relief of 0.8–1.2 (`ring_grass_appearance.gdshaderinc:54-58`).
2. The grass shader has **no atmosphere or night-tint terms**. The terrain applies `ALBEDO = c*mix(night_tint,1,day)`, `ALBEDO *= 1-atmosphere.a`, and `EMISSION = atmosphere` (`terrain3d_ring_surface.gdshader:288-295`). Grass has none of these (`ring_standing_grass.gdshader:273-323`), so the mismatch grows with distance and at night.
3. **Lighting is different.** The terrain's custom `light()` combines terrain macro shadow, planet light, cloud shadow, shadow_floor, and `ALBEDO*n.l` (`terrain3d_ring_surface.gdshader:337-371`). Grass uses Lambert/pi with a primary-sun gate (`ring_standing_grass.gdshader:325-343`, `ring_grass_lighting.gdshaderinc`). It only converges toward the terrain in the 70–90% distance band (`ring_standing_grass.gdshader:337-342`).
4. The terrain's grass-coverage tint (`ring_terrain_grass_coverage.gdshaderinc`) is a separate approximation with its own parameters.

### Which ground is actually under the grass in gameplay
Certified surface worlds use **Terrain3D** (`world_size_launcher.gd:1843-1866`: `UseTerrain3DSurface=true` and SmoothTerrainGrid freed). Terrain3D texture coordinates are canonical. With `ring_texture_anchor_*` taken mod 1000 (`RingTerrain3DSurface.cs:279-283`), the y-projection uv is (canonical around, canonical across) * 0.2. Grass is limited to slopes of 25° or less. There, triplanar x/z weights are below 0.3% and `ground_facing` is 1, so the dirt blend and the side projections never appear under grass. **The y-projection alone reproduces the terrain albedo under grass exactly.** The legacy voxel path (`voxel_ring_terrain.gdshader:69`) uses `world_position*scale` directly, and grass can do the same in that mode.

### Invariants that must survive
- Placement filters in `RingGrassTileBuilder.TryPlace` (`RingGrassTileBuilder.cs:110-199`): site mask `SiteMask.BlocksVegetation` (128), biome chance (131-137), soil and slope (146-175), water (176), modifier density (179-183), exclusions with footprint radius (184-185), canonical lattice hashing and seam wrap (201-219), and priority-sorted rank prefix (237-240).
- Underground: `GrassParticipant` binds `RingStandingGrassLayer` "StandingGrass" and toggles `UndergroundHold` (`scripts/Underground/Participants/UndergroundParticipants.cs:23-31`, `RingStandingGrassLayer.cs:224-229`).
- Shared wind: `ring_wind_field.gdshaderinc` (`ring_wind_gust_at_world`, mean vector), `ring_local_wind.gdshaderinc`, the grass gust/interaction/modifier includes, and per-tile canonical covectors (`RingStandingGrassLayer.Gusts.cs:171-173`).
- GPU compaction is already in place: `RingGpuVegetationCompute` (20-float stride, `ring_gpu_vegetation_indirect.glsl`) with skip-unchanged, plus adaptive 2x2/4x4 grouping.
- Settings: sparse overrides in `GameplayGrassDefaults` (`Keys` and `Limits`), drawer `RingLiveTuningDrawer.Grass*.cs`, controller `RingGraphicsQualityController.SetGrassTuningOverrides`.
- Shadows: grass casts none (`RingStandingGrassLayer.cs:729,1545`). Keep it that way.

## 1. Candidate approaches

| | A. Alpha-tested crossed clump cards (recommended) | B. Solid multi-blade clump meshes | C. Hybrid: solid near (<15–20 m) + cards + terrain fur |
|---|---|---|---|
| Instances | 10–20x fewer (0.15–3 per m² by distance) | Same order as today | Like A plus a small near set |
| Triangles | 12 per instance near, 4–6 effective far → ~0.5–1.2M | 16–32 per instance → several million | A + ~0.5M |
| Reach | 300–400 m affordable | Bounded by triangle count (~150–250 m) | 300–400 m |
| Overdraw | Main risk, mitigated below | None (early-Z) | Cards only beyond 15 m |
| Close-up look | Visibly "cardy" at boot height; weak top-down | Best | Best |
| Est. GPU at spawn (A1000, 1600x900) | **~1.5–3 ms** (to verify in spike) | ~5–9 ms at comparable reach | ~2.5–4 ms |
| Complexity | One mesh, one material | Existing system | Two representations whose colours must agree |

Far band, for every option: no shells. Shells would mean re-rendering the Terrain3D clipmap 2–4 times, which costs too much. Instead, the existing terrain coverage term gets a grazing-angle "tip colour sheen": `mix(terrain, tipColour, coverage*(1-|N.V|)^p)`. That is what a card field looks like at grazing angles, it is cheap, and it continues past the card reach.

MultiMesh vs compute-indirect: keep the existing `RingGpuVegetationCompute` compaction. It is in production, skips work when unchanged, and has a CPU vertex-LOD fallback. With roughly 90k resident instances, the plain MultiMesh fallback is also affordable (18 vertices per instance).

### Overdraw and alpha-test mitigation (approach A)
1. **Trimmed card silhouettes**: a 6–8 vertex outline that hugs the tuft mask instead of a full quad (about 30–45% fewer empty fragments).
2. **The card count per instance falls with distance, in one mesh**: 3 crossed quads near, then quads 1 and 2 shrink to zero width over a band while quad 0 turns to face the camera (the Rodrigues code already exists at `ring_standing_grass.gdshader:187-218`). Far overdraw is about 1 layer instead of 3.
3. **Rank thinning with scale compensation**: survivors scale by `1/sqrt(keep)` (capped near 2.5x), so instances per screen pixel stay roughly constant with distance.
4. **Coverage-preserving alpha mips** (Castaño): distant cards keep their coverage at the alpha cutoff, so no extra cards are needed to compensate for thinning.
5. **A cheap depth-pass fragment**: compute colour in `vertex()` and pass it as varyings, so the alpha-scissor depth prepass only samples the mask. Forward+ prepasses alpha-scissor materials. The spike must confirm that the colour pass stays at about 1x shading.
6. Collapse cards that are edge-on to the view (and fade them toward top-down) rather than rasterising slivers.
7. Keep cards short: 0.2–0.6 m tall, with footprint radius 0.3–0.7 m.

LOD and popping: there are no mesh swaps. Instances grow in through a rank window (`scale = smoothstep(rank-w, rank, density(d))`) rather than popping. The final fade collapses height over the last ~15% of reach after colour has converged. Tiers use the deterministic prefix, so instances never move.

Wind: reuse the shared field (mean vector times `ring_wind_gust_at_world`), the texture gust, interaction, and the t² tip bend with pinned root and arc shortening (`ring_standing_grass.gdshader:221-250`). Phase comes from the canonical custom.zw. Cards need 2–3 vertical segments for the bend.

## 2. Colour matching (concrete method)
- New `materials/ring_terrain_surface_albedo.gdshaderinc` holds one function, `ring_terrain_biome_albedo(vec2 uv5m, vec4 weights, float lod)`. It is extracted from `terrain3d_ring_surface.gdshader:249-269` (y-projection, grass multiplier `(.72,1.30,.62)` at .58, normalised biome weights). The Terrain3D shader uses it too, so there is one source of truth, with a pixel-identical regression capture.
- In the card vertex shader, the canonical uv comes from per-tile instance uniforms in the same pattern as the gust covectors: `uv = origin_uv + (dot(world-origin, around_cov), dot(world-origin, across_cov))*0.2`, where origin_uv is the tile's canonical around/across mod 1000. Biome weights stay in COLOR, as now. Each vertex samples at its own ground-projected position (root plus horizontal offset) with `textureLod` at a mip that matches the card footprint (about 0.5 m per texel). Only the 2 dominant biomes are sampled. Light and dark patches in the ground texture therefore carry on into the grass.
- Gradient: `base = A*rootDark` (0.75–1.0) and `tip = A*tipGain` (value x1.15–1.3, slight yellow shift), with ±6–8% per-blade variation from the atlas B channel. With distance, rootDark and tipGain go to 1, and the field's average converges to the terrain-with-coverage colour between 60% and 90% of reach. The terrain coverage tint reads the same `tipGain` and coverage parameters as globals, so both sides agree by construction.
- Lighting: normal = instance up (terrain normal). The terrain's light body moves into a shared `ring_terrain_lighting.gdshaderinc` used by both shaders (macro shadow, cloud shadow, planet, shadow_floor), along with night tint and atmosphere (atmosphere per vertex on cards). The card base is then identical to the terrain under it in every light condition.
- A CPU-baked fallback (per-instance colour from mip-reduced `Image` copies on workers) is possible but duplicates shader logic. Not recommended.

## 3. Data and streaming
- Reuse the scheduler, jobs, and invalidation in `RingStandingGrassLayer` as a new **render style** (`GrassRenderStyle {Blades, Cards}`). Card code lives in new partials and files.
- Tiles are 32 m. Density tiers by distance use the existing `sourceFraction`/tier mechanism: about 2.5 per m² at 0–40 m, 0.6 at 40–120 m, and 0.15 from 120 m to reach. That is about 90k resident and about 35k visible instances, roughly 7 MB of instance data. 400 m reach comes to about 600 tiles, and adaptive grouping merges the far ones.
- `TryPlace` is unchanged apart from lattice spacing taken from instance density and clumpRadius set to the card footprint. The site mask, biomes, water, slope, exclusions, and modifiers are kept. Worker cost falls about 10x because there are fewer surface resolves.
- Payload: keep the 20-float layout (compute stride). COLOR holds biome weights. Custom holds (curve | atlasVariant packed, tint, windAround, windAcross).
- Compute: add a card-mode continuous density curve `density(d)=clamp((d0/d)^k,floor,1)` with grow-in to `accepted()`, using a new `p[]` slot.
- Idle invariant: the plan is recomputed only when the focus tile changes or an input revision changes (edits, exclusions, modifiers, settings fingerprint). Material parameters are set only when their fingerprint changes. Compute runs only when the camera or membership changes. A test asserts zero builds, uploads, dispatches, and parameter sets during a 10 s stationary window.

## 4. Assets (all procedural)
- **Tuft atlas**, 2048x512, 4 variants. R = coverage mask, G = height gradient, B = per-blade random value, A = blade-cross "normal x". Colour comes from the terrain, so the texture holds no albedo. Generated in C# (`RingGrassCardAtlasGenerator`): N tapered, curved blades per variant, drawn as polygons with analytic AA, using a deterministic seed. Mips are built by hand with coverage preservation (binary-search an alpha scale per mip so coverage at threshold 0.5 equals mip 0), then `Image.CreateFromData(..., mipmaps: true)` produces an `ImageTexture`. It can be baked to `.res` or generated at startup (tens of ms, off the main thread).
- **Card mesh** (`RingGrassCardMeshFactory`): 3 trimmed cards at 0/60/120°, 2–3 segments, outward tilt of 10–15°, about 18–24 vertices and 12 triangles. CUSTOM0 holds card index and pivot for the far single-card collapse.
- Optional later: an authored atlas rendered from `resources/grass/meadow/source/meadow_grass.blend` into the same channel contract.

## 5. Grave-key settings (new "Grass cards" section; sparse persistence)
`GrassRenderStyle` (Blades/Cards), `GrassCardReachMeters` (100–600), `GrassCardNearDensity` (per m²), `GrassCardFarDensity`, `GrassCardDensityFalloff`, `GrassCardMinHeight/MaxHeight`, `GrassCardMinWidth/MaxWidth`, `GrassCardScaleCompensationMax`, `GrassCardSingleCardDistance`, `GrassCardFadeBandPercent`, `GrassCardAlphaCutoff`, `GrassCardAtlasBlades` (regenerates), `GrassCardTerrainColorMatch` (0–1), `GrassCardTipGain`, `GrassCardTipHueShift`, `GrassCardRootDarkening`, `GrassCardColorVariation`, `GrassCardConvergeStart/EndPercent`, `GrassCardTerrainSheen`. Shared keys reused: Enabled, wind response keys, gust, interaction, modifiers, biome density and height, terrain coverage. Quality presets gain card budgets.
Migration: the old keys stay untouched and only apply to Blades. Card keys are new, so the user's maxed blade prefs (96/m², 1018 tiles) do not carry over. Default is `Blades` until accepted, then the default flips to `Cards`. An explicit saved style keeps winning.

## 6. Tests to add
`grass_card_atlas_test` (determinism hash, coverage per mip ±3%), `grass_card_mesh_test` (pinned roots, winding, bounds, triangle budget), `grass_card_placement_test` (determinism, prefix subset across tiers, seam, BlocksVegetation, water, slope, exclusions with footprint, modifiers), `terrain_albedo_include_regression` (Terrain3D capture before and after extraction must be pixel-equal), `grass_card_color_match_gpu_test` (ΔE between card-root pixels and the hidden-grass ground below a threshold in day, night, and haze; convergence at the reach edge), `grass_card_idle_test`, `grass_card_lod_continuity_test`, underground hold for the card style, live-tuning smoke for the new keys and the style switch, and an extended `grass_render_profile.gd` covering Blades(maxed) / Blades(default) / Cards / Hidden at the spawn pose.

## 7. Phased plan
- P0 Spike: an isolated 400 m card field on Terrain3D-shaded flat ground, with the procedural atlas, on the A1000. Measure the GPU delta, check the behaviour of the alpha-scissor prepass, and pick density and segment count. Exit: under 3 ms, plus screenshots.
- P1: Extract the shared terrain albedo and lighting includes. Terrain must stay pixel-identical.
- P2: Atlas generator and card mesh factory, with tests.
- P3: Card placement mode in the tile builder (tiers, footprint, payload), with tests.
- P4: `ring_grass_cards.gdshader` and the layer integration behind `GrassRenderStyle`, CPU fallback first.
- P5: GPU compaction card mode (density curve and grow-in), grouping, and the idle test.
- P6: Quality profiles, drawer section, persistence, smoke test.
- P7: Terrain sheen harmonisation and the colour-match GPU test.
- P8: A/B screenshots (spawn, meadow, forest edge, slope, low sun, night, storm) and the perf profile on the A1000, then user acceptance and the default flip.

## 8. User decisions
1. Style: realistic meadow or soft, stylised tufts (this sets the atlas parameters).
2. Height: lawn-short (0.15–0.35 m) or knee-high (0.4–0.8 m).
3. Up close: cards everywhere (A), or keep a sparse solid-blade hero layer within about 15 m (C).
4. Reach and budget target: for example 300 m at about 2 ms, or 400 m at about 3 ms.
5. Colour: exact terrain-derived colour (tips lightened) or a deliberately greener tint on top of the terrain-derived base.
6. Once cards are accepted: retire the blade system or keep it as an option.

---

## USER DECISIONS (2026-10-15) — binding

- Approach: option A, alpha-tested crossed clump cards coloured from the terrain albedo at the root.
- Style: realistic tufts.
- Height: mix by biome (taller in meadow/grassland, shorter elsewhere).
- Near grass: cards all the way to the camera (no solid-blade near layer).
- Reach vs budget: decide after the P0 spike; expose reach as a setting.
- Still open: exact terrain colour vs a greener tint on top (decide from P0/P7 screenshots); retiring the blade system after cards are accepted.
- Sequencing: build on the shared scheduler only after the stationary-rebuild (churn) fix lands; cards must satisfy the idle invariant (zero builds/uploads/culls/material updates when stationary).

## Streaming invariants (from the 2026-10-15 churn investigation) — binding for the card system

1. Idle means zero builds, uploads, GPU re-registrations and material updates.
2. Placement depends only on the quantised focus cell (with hysteresis) and on the terrain, edit, exclusion, modifier and settings revisions. It never depends on the raw float pose, time, wind, lighting or clouds.
3. A floating-origin rebase is a transform only: no replan, no rebuild.
4. Add fast, drop slow, for both tile membership and LOD tier, with hysteresis H = clamp(tile/2, 2, 8) m.
5. Downgrades filter existing data (tiers are nested subsets) and never rebuild.
6. The work done per cell crossing is bounded by the swept area.
7. A saturated cap must not flap.
8. Build counts are exposed per reason for tests and diagnostics.

## USER DECISIONS after P0b (2026-10-15) — binding, supersede earlier where they conflict

- Base look: variant C (fuller tufts, per-fragment terrain colour, bent normals, backlight, low upright layer).
- Colour: match the ground (keep the terrain-sampled colour even where it reads straw/stubble; do not push extra green).
- Near layer: TRY a short solid-blade ("hero") layer within roughly 3–5 m on top of the cards (supersedes "cards all the way to the camera"); keep it only if the A/B shows a clear improvement for the cost.
- Reach: start with 400 m (cost is near-identical to 300 m).

## USER DECISION after P0c (2026-10-15) — binding

- Go with variant **D**: variant C cards + the short solid-blade hero layer (8-blade tufts, ~50 tufts/m², fade 4–6 m with ±1.4 m dither, cards thinned to 40% at the camera back to full by 7.5 m), same colour pipeline for blades and cards, 400 m reach. Measured on the A1000: grass total ≈3.0–3.6 ms GPU vs 9.6 ms for the current blades; hero layer +0.47–0.60 ms. Variant E (3 m hero radius) is the fallback if budget tightens.
- Integration starts after the grass/tree streaming hysteresis fix lands; old blade system stays behind a switch until the user accepts the in-game A/B.
