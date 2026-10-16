# Grass "variant D" integration plan (cards + hero blades) — implementation-ready

Status: read-only plan. Repo C:\Projects\RingworldTechDemo. Baseline = working tree on 2026-10-15 with the
streaming-hysteresis change partly applied (uncommitted diff in RingStandingGrassLayer.cs, RingGrassTileBuilder.cs,
RingStandingGrassLayer.Diagnostics.cs, GameplayGrassDefaults.cs, RingLiveTuningDrawer.Grass.cs). All line numbers
below are from that tree and MUST be re-verified after the hysteresis work lands (marked [RV]).

## 0. Dependency on the hysteresis change (gate S0)

Plan relies on these landed pieces (current locations, all [RV]):
- `RingGrassTileData.TierKeys` + `RingGrassTileBuilder.FilterToTier` (RingGrassTileBuilder.cs:30-50, 262-285); tier key
  computed before sampling (:128-133) and stored per candidate (:208-210).
- `Plan()` add-fast/drop-slow + cap head start `SortKey = d - H` (RingStandingGrassLayer.cs:457-536, esp. 496-531).
- `ResolveCoverage` / `CoverageForTier` (≈1096-1123) — currently a hard 3-tier switch (0/1/2). Cards need an N-tier
  ladder, so this is the main hook to generalise.
- `Queue()` downgrade-filter branch + `FilterResidentToTier` (537-586), per-reason counters
  (`GrassBuildNewTileCount`, `...TierUpgradeCount`, `...TierOtherCount`, `GrassTierFilterCount`, `GrassTilesEvictedCount`,
  567-586, 668-673, Diagnostics.cs:176-178), `StreamingHysteresis` H (≈1092-1094).
- tools/GrassStreamingHysteresisTest.cs + grass_streaming_hysteresis_test.gd (untracked) — will be extended for Cards.
- Tree-side keep band / translation-only rebase (RingBiomeDecorationLayer.TreeStreaming.cs, GpuRebase.cs) — not touched
  by this plan, but S6 changes `RingGpuVegetationCompute` which trees share, so tree GPU tests must be re-run.

Exit criteria for S0: hysteresis tests green, counters exposed, no open edits in the four grass files.

## 1. Architecture

Decision: confirm the doc's `GrassRenderStyle {Blades, Cards}` INSIDE `RingStandingGrassLayer` (not a second layer).
Reason: the just-landed hysteresis (plan, cap bias, tier hold, downgrade filter, counters), invalidation (edits
880-909, exclusions 910-953, field identity 407-445, modifiers), underground hold (229, 255-270), suspend/reset, GPU
registration and adaptive grouping all live there; a second layer would duplicate ~2.5k lines or force a
scheduler extraction first. Cost: style branches. Contain them with one captured record per frame:

- New `RingGrassStyleProfile` (record, new file) built from the layer's properties each step:
  TileMeters (Blades: `SpatialTileMeters`; Cards: `GrassCardTileMeters`=32), coverage radius (Cards: reach),
  resident-tile cap and instance budget (Cards: derived from reach, never from blade keys), tier ladder
  (fraction per tier), placement options, material, mesh. `TileSize` (cs:241), `GrassStreamingCoverageRadius`
  (≈1083), `EffectiveGrassDrawDistance` (Appearance.cs:29-42, Cards returns reach — no resident-window clamp because the
  cap is sized from reach), `CoverageForDistance/CoverageForTier`, `Start()` worker args (587-675), `EnsureResources`
  (301-327), `Install` (736-829, Cards: near renderer only, never intermediate), `NearMeshFor` (1266),
  `ConfigureLodMaterials` (1652-1691), `AddGpuFrame` (1416-1422) and `CreateGrassMaterial` (1746) read it.
- Style is part of `Fingerprint()` (1202-1243): a switch = `Reset()` + full rebuild (explicit user action, allowed).
  In Blades style the fingerprint, placement and buffers must be byte-identical to today (golden test).
- Cards tier ladder: tier t has fraction f_t = 2^(-t/2) (t = 0..T, f_T ≥ floorDensity/latticeDensity), evaluated at
  `d_nearest - L` (L = lookahead, default H) so upgrades are requested before the camera arrives; downgrade still needs
  +H (existing ResolveCoverage logic, generalised to the ladder). Cap = max(setting, desired + 2*perimeter) so it never
  saturates (invariant 7).

Hero layer: owned by the grass layer (new partial `RingStandingGrassLayer.HeroBlades.cs` + worker builder
`RingGrassHeroCellBuilder.cs`), child renderers of the layer (so UndergroundHold / Visible / suspend cover it).
- Keyed on canonical 4 m cells (own key type `RingGrassHeroKey`, NOT RingGrassTileKey — modifier slots and work caches
  are keyed by tile key). Ring of (2n+1)^2 cells around an anchor cell; anchor moves only when the focus leaves the
  anchor cell by > cell/2 + hysteresis (spike `update_hero`, grass_cards_spike.gd:204-246), with ring wrap via
  `RingGrassCoordinates.WrapTileIndex`.
- Driven by the same revisions: edit Invalidate (drop cells overlapping dirty columns), exclusions, modifier snapshot,
  field identity/epoch, settings fingerprint (incl. hero keys), Reset/ClearTiles/SuspendStreaming clear it.
- Rebase = transform only: each cell carries a `RingGrassTileData` + `TilePresentationCache`, presented through the
  existing `RefreshTilePresentation` (PresentationCache.cs:168-221), which only rewrites transform/origin uniforms.
- CPU MultiMesh only (≤25 cells × ~800 tufts); no compute. ≤2 cell builds in flight; budgets share the layer's
  upload/ms limits. Counters: `HeroCellBuildCount`, `HeroReanchorCount`, `HeroCellsEvicted`.

## 2. Shared shader includes and colour pipeline

S1 (pure refactor, terrain pixel-identical):
- `materials/ring_terrain_surface_albedo.gdshaderinc`: the six `biome_*_albedo` sampler uniforms + `biome_texture_scale`
  (moved verbatim from terrain3d_ring_surface.gdshader:45-51), `vec3 ring_terrain_grass_tint(vec3)` (the
  `(.72,1.30,.62)@.58` line, :259) and `vec3 ring_terrain_biome_mix(vec3 grass, desert, forest, tundra, alpine, vec4 packed)`
  (weights/normalisation, :244-266). Sampling stays caller-side: terrain keeps `texture()` triplanar (implicit
  derivatives, anisotropic) so its pixels cannot change; grass uses `textureLod` y-projection.
- `materials/ring_terrain_lighting.gdshaderinc`: uniforms :71-77 (sun_direction, day_start/full_dot, night_tint,
  night/day_direct, shadow_floor) + a function `ring_terrain_direct_diffuse(normal, light, attenuation, is_directional,
  light_color, albedo, view_matrix, day_factor, cloud_occ, shadow_uv, world, viewer)` holding light() :337-370 diffuse
  part, plus `ring_terrain_night_tint(albedo, day)` and the atmosphere composite (:288-295). Terrain light() calls it.
- `RingTerrain3DSurface`: single choke point `SetMaterialParameter` (RingTerrain3DSurface.cs:427-432) gains a follower
  fan-out: `RegisterSurfaceFollower(ShaderMaterial, IReadOnlySet<StringName> names)`; replays last values on register;
  dedupes per name (so stationary frames write 0 params). Route `UpdateFrameMaterialParameters`' direct
  `set_shader_param` calls (:316-318) through it. Follower names: lighting set, atmosphere set (incl.
  `ring_atmosphere_camera_world`, center, day factor), `ring_terrain_shadow_anchor_uv`, texture anchors, biome textures.
  Macro/cloud shadow: `RingTerrainMacroShadowProjector.Register` / `RingCloudShadowProjector.Register` on card + hero
  materials.
- Regression: tools/terrain_surface_include_regression.gd — golden PNGs captured from the PRE-change commit at 4 poses
  × {day, night, haze}, fixed time, clouds/wind/TAA frozen; post-change must be 0-diff.

Card/hero colour (S4), per instance tile renderer:
- New instance uniform `grass_terrain_origin_uv` (vec4: xy = canonical texture uv of the tile's gust origin =
  (Modulo(aroundCenter,5), Modulo(acrossCenter,5))*0.2 in double on CPU; zw = macro-shadow uv origin =
  (around/C, across/width)) written in `RefreshTilePresentation` next to the gust mapping (canonical → unchanged by
  rebase; only origin_world changes). Reuse the existing gust covectors (`grass_gust_around/across_world.xyz`) and
  `grass_gust_origin_world`: `uv(p) = origin_uv.xy + vec2(dot(p-o, around.xyz), dot(p-o, across.xyz)) * biome_texture_scale`.
  This equals the terrain's y-projection uv (terrain3d_ring_surface.gdshader:250-253 reduces to canonical
  (around, across)*0.2 mod 200 repeats; triplanar x/z ≤0.3% and dirt=0 for slopes ≤25°).
- Per vertex (far path, cards only): ground-projected vertex position `root + offset_h`; `textureLod` at
  lod = log2(footprint texels) - lod_bias(3) for the dominant two biome weights; root darkening / tip gain converge to 1
  over converge_start..end of reach (grass_cards.gdshader:136-142).
- Per fragment (< `frag_color_end` 70 m, both cards and hero): `textureLod` at the ground position with lod from
  ground-plane derivatives + bias -0.5 (grass_cards.gdshader:149-160; grass_hero.gdshader:75-80), all non-zero biomes
  (instance-uniform branch on weight > 1e-3), through `ring_terrain_grass_tint`/`ring_terrain_biome_mix`, then
  `spike_near_blade_color` (spike_grass_color.gdshaderinc) promoted to `materials/ring_grass_card_color.gdshaderinc`,
  shared by card and hero shaders (same uniform names, fed from one C# config).
- Biome weights: per root in COLOR (unchanged layout). Builder computes them bilinearly from the four Terrain3D
  vertex-grid samples around the root (terrain interpolates `_color_maps`, :218-225) to avoid a seam at biome borders.
- Lighting: card/hero light() = `ring_terrain_direct_diffuse` with ground normal at the root blended to the bent
  normal toward the tip (normal_out/face), + BACKLIGHT; fragment applies night tint and per-vertex atmosphere
  (ALBEDO *= 1-a, EMISSION = rgb*a*energy). Cloud AO term identical to terrain (:303).
- MultiMesh COLOR multiplies mesh vertex COLOR → hero mesh root offsets move from COLOR.rg
  (GrassCardsSpikeAssets.cs:582,589) to CUSTOM0.

## 3. Asset generation (S2, new files only)

- `scripts/Voxel/LOD/RingGrassCardAtlasGenerator.cs`: port `BuildAtlasFull` + `DrawTuft` + `Down`
  (GrassCardsSpikeAssets.cs:58-220) unchanged math; D parameters (56 blades, seed 1337, rootSpread .16, minTall .24,
  widthMul 1.2). Coverage-preserving mips (quantile scale per variant per level) kept. Output RGBA8 2048x512 + full chain.
- `RingGrassCardMeshFactory.cs`: `BuildMesh` (:244-280): 3 cards × 3 rows, 18 v / 12 t, UV2=(lx, cardIndex) for the 3→1
  collapse; `CustomAabb` widened by comp cap 2.5 × single-card width gain 1.6.
- `RingGrassHeroTuftMeshFactory.cs`: `BuildHeroTuftMesh(8, 4242, .15, .013)` (:541-606), root offset in CUSTOM0.
- Cache: `RingGrassCardAssets` static, built once off the main thread (Task) keyed by
  hash(params, GeneratorVersion); optional `user://grass_cache/atlas_<hash>.res`. Layer shows nothing in Cards style
  until assets are ready (no fallback flicker). Determinism: integer RNG + fixed loop order; golden FNV hash of mip0 per
  machine; test asserts two in-process builds identical and coverage per mip within ±3% of mip0.

## 4. Placement (S3, RingGrassTileBuilder.cs — after S0)

Spike → production adaptation: spike bakes a camera-centred field (BuildFieldV2 :417-528). Production bakes a
camera-independent lattice per tile; everything distance-dependent moves to runtime (compute + vertex shader) via a
per-instance rank.
- Card placement mode (new `CardPlacement` options record passed to `Build`): lattice density = `GrassCardNearDensity`
  (14/m², D) on the global canonical lattice (existing jittered-lattice loop :214-231, no clump 1-in-4 rejection).
- Rank/tier key = Bayer-stratified: rank = (bayer16(i&15, j&15) + Hash01(i,j,seed,salt+97)) / 256 on the LATTICE CELL
  (not the rounded 1 m canonical cell used at :131 — at 14/m² ~14 candidates share one 1 m key). Keeps every tier
  subset a jittered grid (replaces the spike's per-band lattices) and nested.
- Card mode uses rank as `Candidate.Priority`, so the per-tile cap truncation is itself a rank prefix (nesting holds
  under caps) and the buffer is rank-sorted (FilterToTier = prefix copy; CPU fallback can use VisibleInstanceCount).
- Low upright layer (D: 18/m², full to 8 m, gone by 22 m, h .11-.20, w .30-.48): separate lattice salt, kind flag,
  included only for tiers whose nearest distance ≤ lowEnd + L; card-aware `FilterCardTier` drops it on downgrade.
- TryPlace filters unchanged (:116-211): BlocksVegetation (:139), biome chance (:142-148), slope/soil/resolver
  (:155-186), water (:187), modifiers (:190-194), exclusions with clumpRadius = card footprint (0.3-0.7 m) (:195-196).
- Height by biome: tall/short from seam-periodic value noise (70 m/23 m, like `MacroPatch` :415-426) with
  P(tall) = smooth(.42,.58,n) × (grassWeight + .3·forest); h = (tall ? CardTall .6 : CardShort .3) × U(.85,1.15) ×
  `appearance.HeightFor(...)`; width .45 + h·1.1; lean ±.12 baked into basis Y; yaw from hash.
- Payload (20 floats, `Pack` :366-378 contract kept): transform 12; COLOR = biome weights (bilinear, §2);
  CUSTOM = (kind·4 + atlasVariant + rank·0.999, tuftRandom, windAround, windAcross).
- Runtime (identical C# reference `RingGrassCardCurve` feeds both compute params and material uniforms):
  dens(d) = base(d) + (14 - base(d))·(1 - smooth(8,45,d)), base(d)=clamp(2.5(20/max(d,20))^1.3, .15, 2.5);
  keep = dens/14; accept rank<keep && growIn ≥ .08 (growIn = smooth(0,1,(keep-rank)/max(.02,.2keep)));
  scale = min(1/sqrt(min(1,dens/2.5)), 2.5)·growIn·lerp(nearW .7,1,smooth(8,45,d))·fade·heroThin.
- GPU compute (S6): `ParameterBytes` 256→512 (RingGpuVegetationCompute.cs:14, :44 ParameterWords 64→128), card branch
  in `accepted()` (ring_gpu_vegetation_indirect.glsl:31-87) keyed p[62], curve in p[64..79]; new
  `BatchFrameParameters` fields added to `FrameEquals` (:907-925); frustum guard via p[61] = card radius × 2.5 × 1.6.
  Output rows copied unchanged (shader recomputes scale). Single pass (pass 0); capacity = full count initially.
- Tile grouping: Cards keep adaptive 2x2/4x4 grouping for far tiers (rank travels in CUSTOM, no metadata needed);
  grouping start ≥ 120 m.
- Hero lattice: `RingGrassHeroCellBuilder` runs the same TryPlace core on a 4 m cell at 50 tufts/m² (spike
  BuildHeroCell :608-641), same height rule × .9, no tier key.

## 5. Settings (S8)

- Layer [Export] properties (names = keys, applied via `_grass.Set` in RingGraphicsQualityController.cs:525-572):
  `GrassRenderStyle` (0 Blades,1 Cards); cards coverage: `GrassCardReachMeters` (100-600, 400), `GrassCardTileMeters`
  (16-64, 32), `GrassCardNearDensity` (2-24, 14), `GrassCardBaseDensity` (.5-6, 2.5), `GrassCardFloorDensity`
  (.02-1, .15), `GrassCardDensityFalloff` (.5-2.5, 1.3), `GrassCardNearBoostStart/EndMeters` (8/45),
  `GrassCardLowLayerDensity` (0-30, 18), `GrassCardLowLayerEndMeters` (22), `GrassCardScaleCompensationMax` (2.5),
  `GrassCardSingleCardStart/EndMeters` (90/160), `GrassCardFadeBandPercent` (15); form: `GrassCardTallHeight` (.6),
  `GrassCardShortHeight` (.3), `GrassCardNearWidthScale` (.7), `GrassCardLean` (.12), `GrassCardTilt` (.08),
  `GrassCardNormalOut` (.45), `GrassCardNormalFace` (.25), `GrassCardBacklight` (.28), `GrassCardAlphaCutoff` (.5),
  `GrassCardAtlasBlades` (56, regenerates); colour: `GrassCardGreenPull` (.15), `GrassCardRootAo` (.5),
  `GrassCardTipLight` (.16), `GrassCardBladeValueJitter` (.22), `GrassCardBladeHueJitter` (.10),
  `GrassCardDryFraction` (.07), `GrassCardTuftValueJitter` (.14), `GrassCardFragmentColorMeters` (70),
  `GrassCardConvergeStart/EndPercent` (60/90), `GrassCardTerrainSheen`; hero: `EnableGrassHeroBlades` (true),
  `GrassHeroDensity` (50), `GrassHeroFadeStart/EndMeters` (4/6), `GrassHeroDitherMeters` (1.4),
  `GrassHeroCellMeters` (4), `GrassHeroHysteresisMeters` (.75), `GrassHeroHeightScale` (.9), `GrassHeroBladesPerTuft`
  (8), `GrassCardHeroThinMinimum` (.4), `GrassCardHeroThinStart/EndMeters` (3/7.5).
  Rebuild class: lattice density, low layer density, tile size, heights, lean, atlas blades, hero density/cell →
  fingerprint; everything else material/compute only (curve changes go through the coverage fingerprint → tier
  filter/rebuild).
- GameplayGrassDefaults: append keys to `Keys`, `Limits`, bool list in `TryValidate` (GameplayGrassDefaults.cs:96-108,
  [RV] after S0 which edits these lines), companion clamps in `SetOverride` (start<end, short<tall).
- Drawer: new partial `scripts/ui/RingLiveTuningDrawer.GrassCards.cs`, style selector at the top of the Grass tab,
  groups "Grass cards — coverage / form / colour" and "Hero blades"; the H slider (S0) stays shared.
- Migration: blade keys (BladesPerSquareMeter, MaximumResidentTiles, MaximumInstances*, VisibilityRadius, Lod*,
  GrassMin/MaxHeight, camera facing, ribbons, tints) are read only in Blades style, so the user's maxed blade prefs never
  reach Cards. Shared: Enabled, wind/gust, interaction, modifiers, biome density/height multipliers, hysteresis.
  Profile default stays Blades (ResetGrassTuningToCurrentProfile) until the user accepts; flipping the default later is
  a one-line profile change; an explicit saved `GrassRenderStyle` always wins. Reset preserves wind keys (existing).
- Quality profiles (RingGrassQualityProfile.cs:58-79): add card budgets (reach, near density, hero on/off) per preset;
  laptop tier = D defaults, Low = variant E (hero 3 m) / reach 300.

## 6. Tests

- grass_card_atlas_test / grass_card_mesh_test (S2): determinism, coverage ±3% per mip, chain dims; 18v/12t, pinned
  roots, UV2 card index, AABB; hero 8×(9v,7t), CUSTOM0 offsets, no vertex COLOR.
- grass_card_placement_test (S3): PlacementHash determinism; nesting `FilterCardTier(build(f1),f2) == build(f2)` byte-
  identical (incl. capped tiles); seam tile; BlocksVegetation; water; slope; exclusions with footprint; modifiers;
  grassland mean height > forest/sparse; stratification (nearest-neighbour CV at f=1/64 ≤ jittered-grid bound).
  Blade golden: Blades-style PlacementHash unchanged vs pre-refactor.
- terrain_surface_include_regression (S1): 0-diff.
- grass_card_color_match_gpu_test (S5/S9): ΔE2000 card-root band vs hidden-grass ground ≤ 3 (day), ≤ 4 (night, haze);
  biome-border pose; reach-edge convergence; hero vs card at the 4-6 m band ≤ 3.
- GpuGrassCardCullingTest (S6): compute accept set == C# reference curve on a synthetic batch; skip-unchanged honours
  card params; existing tree GPU tests green.
- Streaming invariants (extend GrassStreamingHysteresisTest for Cards + hero): idle 10 s → 0 builds/uploads/GPU
  registrations/`PresentationParameterWrites`/material sets/hero builds; ±0.75 m and ±H/2 oscillation 60 s → 0 builds
  after settle; rebase → 0 builds, 0 replans; straight crossing → builds ≤ tiles in swept band + 1, hero cells per
  re-anchor ≤ 2n+1 (≤ 4n+1 diagonal).
- Underground hold (grass_lifecycle / underground participant): Cards + hero hidden, 0 builds, instant resume, retained
  instance ids.
- grass_live_tuning_smoke_test extension: style switch both ways, every new key set/persist/reload, explicit style
  beats profile default, blade-key edits don't change the Cards fingerprint.
- In-game A/B (S11, A1000, 1600x900, measured only after `PendingGrassTileBuildCount == 0` — see perf-ranking
  Correction): extend grass_render_profile.gd / grass_cost_gameplay_capture.gd: Blades(user maxed), Blades(default),
  Cards-D, Cards-E, Hidden × poses spawn, meadow ground 1.7 m, crouch 0.8 m, 5 m, 20 m; GPU ms (delta vs Hidden),
  CPU "Grass update (inclusive)", resident instances, VRAM, paired screenshots.

## 7. Phasing (implementer-sized; ‖ = can run in parallel)

| Step | Content | Depends | Gate |
|---|---|---|---|
| S0 | hysteresis lands; re-verify all [RV] refs | – | its tests green |
| S1 ‖ | terrain includes + follower fan-out + pixel regression | – (not in S0 files) | 0-diff |
| S2 ‖ | atlas / card mesh / hero mesh factories + cache + tests | – | atlas+mesh tests |
| S3 | card placement mode, Bayer rank, low layer, FilterCardTier, bilinear weights | S0 | placement tests, blade golden |
| S4 ‖S3 | ring_grass_cards.gdshader, ring_grass_hero.gdshader, ring_grass_card_color.gdshaderinc; synthetic GPU scene | S1,S2 | shader compiles, colour test on flat Terrain3D fixture |
| S5 | GrassRenderStyle + RingGrassStyleProfile in layer, ladder tiers, budgets, CPU MultiMesh path, origin-uv uniform, follower registration | S0,S3,S4 | idle 0, no seam, Blades unchanged |
| S6 ‖S7 | compute card mode (ParameterBytes, accept branch, FrameEquals) | S5 | parity test, tree tests |
| S7 ‖S6 | hero partial + builder + anchor hysteresis + invalidation hooks + counters | S5 | hero invariant tests, underground |
| S8 | settings keys, drawer, profiles, migration | S5,S7 | smoke test |
| S9 | Cards-mode terrain coverage/sheen: `CoordinatedGrassHandoffBand` (Appearance.cs:44-58) and `ApplyTerrainCoverage` publish card fade band; sheen term guarded by a global so Blades terrain stays 0-diff | S5,S1 | colour-match test |
| S10 | full invariant suite Cards+hero | S6–S9 | all green |
| S11 | in-game A/B + perf, user acceptance; separate default flip | S10 | Cards-D grass ≤ 3.6 ms at spawn, hero ≤ 0.6 ms, no pose worse than Blades, CPU grass update ≤ Blades, 0 idle builds, no colour seam |

## 8. Risks

1. 32 m tiles tiered by nearest point over-provision near density (≈17 tier-0 tiles × ~14k) vs spike's ~160k total;
   √2 ladder + lookahead bound it — measure resident instances/VRAM in S5; fallback 16 m card tiles near.
2. Bayer-rank thinning ≠ spike's per-band lattices (look may differ) — judge in S11 A/B.
3. Curve duplicated in GLSL (compute) and gdshader — parity test mandatory.
4. ParameterBytes change touches tree compute — tree regressions.
5. Texture seam if circumference mod 5 m ≠ 0 or Terrain3D frame anchor unwrapping differs from canonical wrap — seam
   pose in colour test.
6. Godot instance-uniform cap (16/shader): cards use ~10 — keep count in review.
7. Alpha-scissor depth prepass behaviour in-game (SSR/SSAO on) was only proven in the spike.
8. Hero ring centred on the explorer focus, fade on camera distance — breaks if the camera sits far from the player.
9. Legacy voxel terrain (no Terrain3D followers): Cards falls back to world-xz uv + grass-own lighting; not colour-exact.

## 9. Open questions for the user

1. Camera: always first-person (hero ring on the player) or can it orbit far from the player?
2. Is D's default green_pull .15 acceptable as "match the ground", or default 0?
3. Must Cards support the legacy voxel-terrain path, or is Terrain3D-only acceptable (Blades remains there)?

---

## USER DECISIONS (2026-10-15) — binding

- Camera is always first-person (the hero ring centred on the player is fine).
- Keep D's green_pull = 0.15 as the default; the user will tune it later (expose it in the grave-key menu).
- Grass colour targets Terrain3D (runtime-surface worlds). The legacy/smooth voxel terrain falls back to the existing approximate colour; revisit when the cave direction-B backend lands.
- Gate S0 (streaming hysteresis) has landed in the working tree (uncommitted) — re-verify [RV] line references against it before each step.

## USER DECISIONS (2026-10-15, later) — binding

- **Hero blades are scrapped.** Variant D becomes cards-only: S7 is dropped, and hero-specific code, shaders, assets and tests are removed. Card hero-thinning is permanently off (cards keep full density at the camera). The S11 hero ≤ 0.6 ms gate is dropped.
- **Card placement moves to the GPU compute path (S6).**
- **Adaptive tile grouping is re-enabled for Cards** (S5 had disabled it).

- 2026-10-16: user confirmed the per-preset card reach values stay as implemented (Low 300, Laptop/Medium/High 400, VeryHigh 450, UltraVideo 500, Cinematic5090 600 m); 400 m remains the default.
