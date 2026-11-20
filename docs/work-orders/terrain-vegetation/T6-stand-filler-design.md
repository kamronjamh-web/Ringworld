# T6 stand filler: design (near-only sub-canopy stems in dense forest)

Status: design written 2026-11-20 (claude-lead); **approved by the user 2026-11-20** with every recommendation of section 11: 150 fillers/ha in young closed stands (about 80 in old), no fir filler (fir stands wait for the T8/G3 tall conifer), small maples and small pines only (no medium maple 8), card-mesh shadows at High and up. Orders SF-1 to SF-6 started. User decision 2026-11-20: "design a near-only stand-filler class" after the forest variant survey (`.godot/audit_baseline/common/forest/variant_survey/`). No game code was changed; the cost prototype below was reverted.

Parent: [T6 work orders](T6.md) (design A7 understory, T6-4/T6-7 patterns, T6-11 cost gates) and the [forest density study](../../forest-density-study.md). The phase freezes with T6-13; this class lands before it (see section 7).

## 1. Problem and goal

Closed stands in `vegetation.forest_structure` worlds hold about 49 canopy stems per hectare. With the v2 size classes they cover 37 % of the ground with crown hulls (24 % with leaf opacity), against 80 to 95 % in a real closed forest. More canopy stems were measured (variant survey C): 1.46x stems cost +1.2 ms at the interior, 2.08x +1.65 ms, and the tree layer's draw caps then leave holes at 550 to 1,050 m; doubling the caps costs +2.3 ms on its own. The user chose a cheaper route: **extra sub-canopy stems near the player only**, where they are seen, closing sightlines and the sky overhead, with nothing added to the tree layer's far bands.

The filler is the intermediate and suppressed crown class of a real stand: young maples and pines of 4 to 7 m standing in the gaps between the dominant trees. Those exist in every closed temperate stand (typically 100 to 400 stems per hectare under the dominants), so the class is realism, not a boost.

## 2. Placement

**Pattern: a scatter class row drawn by the understory layer**, not a tree-layer near band.

| Option | Verdict |
|---|---|
| A scatter row (`RingScatterClassTable`, T5 framework), drawn by `RingUnderstoryLayer` like the saplings and shrubs | **Chosen.** Deterministic per-candidate records with a pure accept function (A8), habitat responses, spacing against canopy trunks (`AnyTreeWithin` through the tree mirror), reach and resident caps, its own GPU compute and budget slots. Nothing in the tree layer, its tiles, caches, caps, impostors or far bands changes. |
| A near band in the tree layer (`CachedTree` records with a near-only flag) | Rejected. It changes the vegetation tile format and cache identity, competes for the tree slot caps that already bind (survey C: the 20-900 m band draws 2,696 of 8,580 candidates), and every tree consumer (impostor, far canopy, colliders, fingerprint tile hashes) would have to skip the flag. |

**Row `veg.standfill`** (Id 15, `FeatureBit = 6`, new `RingScatterRenderGroup.StandFill = 7`, append-only enums):

| Field | Value | Why |
|---|---|---|
| ReachM | 150 | the sapling/shrub reach; the dither fade is 130-150 m |
| CellM / CandidatesPerCell | 8 m / 4 | up to 625 candidates per hectare before the spacing pass |
| BaseDensityPerHa | 150 | target 100-150 stems/ha in closed stands after spacing (section 6) |
| Terms | `1.0 x Dense x (1 - 0.45 x StandAge)`; `0.25 x OpenWoodland`; no constant | dense habitat only (OpenWoodland gets a quarter so the stand does not end at a hard line at the DenseForest threshold); young stands carry more sub-canopy stems than old self-thinned ones (A2's k(a) idea) |
| Multipliers | Treeline (0.3 to 1), Slope 28-34 deg (1 to 0), **NearestTreeM 2.5-6 m (0.3 to 1)** | fewer at the treeline and on steep ground, like the saplings; the NearestTreeM response puts fillers in canopy gaps rather than under crowns (gap regeneration), which also raises the cover gained per stem |
| TreeClearanceM | 2.5 m | clear of canopy trunks and their lowest branches |
| SpacingM | 3 m | a filler is a small tree, not a thicket |
| Avoid | snag 10 (2 m), sapling 12 (1 m) | the sapling layer stays the regeneration layer; fillers and saplings do not stand in one spot |
| Size | 0.85-1.15 (scale of the source height) | as the canopy's structured scale jitter (0.92-1.08) widened slightly |
| MeshCatalog / VariantSelector | Trees, BySpecies | species from A3's habitat weights at the point, as the canopy |
| Collision | Capsule for the stem (trunk radius from the source variant), same pool as the dead wood | a 6 m tree must not be walk-through |
| ResidentCap | 1,500 | 150/ha over the 7.07 ha reach disc is about 1,060 |

The class lives entirely in the scatter records of bit-6 worlds; generation 1 never sees the row (section 7). It interleaves with the existing layers as: canopy stems (tree layer, all distances) → stand fillers (gaps between canopy trunks, 0-150 m) → saplings and shrubs (below and around both) → dead wood.

## 3. Variants and size classes

The filler draws the **small classes of the canopy catalog** through a new vertex-fetch carrier pair (`UnderstoryStandFill`, built like `UnderstoryCatalog`'s sapling carriers from base LOD0 and LOD2, one carrier per LOD holding every variant padded to the largest, plus a caster carrier from LOD3):

| Species | Variants | Height | Crown radius | LOD0 / LOD2 triangles | Top fill (survey) |
|---|---|---|---|---|---|
| maple | 9, 10, 11 (Acer_small_1-3) | 6.0-6.9 m | 2.3-2.8 m | 1,732-3,442 / 262-531 | 0.56-0.58 |
| pine | 27, 28, 29 (Pine_small_1-3) | 4.0-4.7 m | 1.6-1.7 m | 1,912-2,333 / 768-884 | 0.43-0.45 |
| fir | none in v1 (see Q2) | | | | |

Padding: LOD0 to 3,442 triangles, LOD2 to 884. Fir 13 (6,813 / 2,278) would pad every instance to its size, so fir stands get no filler in v1; fir crowns are already the densest in the catalog (0.68-0.75) and fir is about 3 % of dense-forest stems in the survey worlds. A second carrier for fir 13 is a later option (Q2).

Medium maples (6-8) are not used: medium 6 is 14.6k triangles at LOD0, and medium maples are canopy trees in v2 already. Adding medium 8 (4.7k / 1.05k) as a quarter of the fillers would raise the cover gain by about 0.04 at 100/ha for about twice the per-instance vertex cost; not in v1 (Q3).

## 4. Draw range, fade and shadows

- **Bands** (understory pattern, `ResolveFrame`): L0 (LOD0 carrier) 0-35 m, handing over to L2 across 25-35 m; L2 25-150 m, dither fade-out across 130-150 m. Scaled by the understory distance scale (`PresetUnderstory`: Low 0.6, Medium 0.8, High and Very High 1.0, Ultra 1.2).
- **Why 150 m holds up:** in a closed stand the eye-level free path with fillers and the understory is about 30-60 m (section 6), so the fade band is mostly hidden behind stems; at an edge looking into the forest the band is 130-150 m away, where the canopy and its LOD2 band dominate. Look check: the T6-11 LOD-tail dolly at `forest_edge_eye` 60-200 m (order SF-4).
- **Shadows: casters at High and up** (a new caster slot drawing a third carrier made from the variants' LOD3 crossed cards, 36 triangles each, within min(sun near-map distance, 100 m)). Unlike 1-4 m saplings (casters at Very High only), a 6-7 m tree without a sun shadow is wrong in every sunlit view (lit trunk and floor under its crown). Medium and below: no casters. Cost is in section 8.
- **Budget slots:** two new understory slots, 5 (stand fill) and 6 (stand-fill caster), caps 1,500 and 1,000 (scaled by the preset cap scale like the others). The understory layer's draw count rises by 3 (two LOD outputs plus the caster), against the C1 plan gate of 16 draws (8 today).

## 5. Forest lighting and the canopy shade raster

- **Material:** the filler draws with `ring_understory.gdshader`, so leaf translucency, crown occlusion and canopy-shade receiving work as for the saplings: the carrier's parameter rows 8-10 carry each variant's `TreeCrownHullTable` row (crown model porosity 0.20 / 0.30 for kappa).
- **Raster: yes, fillers enter the blocked fraction, within the filler reach only.** `RingForestLighting.StartBuild` gains a second source, `RingUnderstoryLayer.CopyStandFillTrees(camera, 150 m, list)`, that appends filler records (position, root altitude, width = 2 x hull radius x scale, height) with the measured `PorosityOf(variant)` and the species factor. Weight: a record in the fade band (130-150 m from the raster anchor) gets porosity raised toward 1 by its fade factor, so the shade and the visible stems fade together.
  - Why: without it the floor under a filler is lit as open sky while the filler's crown is drawn over it, the same seam canopy shade was made to remove.
  - Self-shading: a filler fragment receives from its own raster entry like a canopy tree does today (the raster's base/top altitude test); the crown-occlusion term inside the crown is the per-fragment model. Same treatment as canopy trees, no special case.
  - Raster rebuild: the raster already rebuilds on a 16 m move or a tile signature change; add the understory set's epoch to the signature so a filler set change triggers a rebuild.
- **Far canopy (bit 8) and impostors:** unchanged. They represent the canopy layer only; fillers are below the canopy top and invisible from afar.

## 6. Cover gained near the eye

Boolean-model estimate on top of the measured v2 closed-stand state (49 canopy stems/ha, hull cover 0.37, leaf-opacity cover 0.24; probe of 80 dense tiles, seed 1337), small-maple fillers (crown area 20.5 m², top fill 0.57, crown width at 1.7 m eye height 3.6 m, side fill 0.41), placed at random relative to crowns (the NearestTreeM gap preference makes the real gain larger):

| Fillers per ha | Hull cover | Opacity cover (sky hidden from below) | Eye-level free path from the fillers alone |
|---|---|---|---|
| 0 (today) | 0.37 | 0.24 | none |
| +50 | 0.43 | 0.28 | 138 m |
| +100 | 0.49 | 0.32 | 69 m |
| +150 | 0.54 | 0.36 | 46 m |
| +200 | 0.58 | 0.40 | 34 m |

With the existing understory (shrubs and saplings) added, the 30-60 m eye-level target of the forest density study is reached at about 100-150 fillers per hectare. That is the design target: 150/ha at full Dense membership in young stands, about 80/ha in old stands (the StandAge term). Overhead, 150/ha raises the sky hidden near the eye from 24 % to about 36 % (0.54 hull); the canopy shade raster turns that into a darker, greener floor.

## 7. Generation 1, ledger and fingerprint

- **Under bit 6 (`vegetation.forest_structure`), no new bit**, provided it lands before T6-13 freezes the vegetation step. The row has `FeatureBit = 6`; `RingScatterClassTable.ForMask` drops it from every world without bit 6, so generation 1 and every other generation stay byte-identical (the generation 1 fingerprint has no scatter rows at all: `ScatterEnabled` is false for it).
- The bit-6 scatter class-table hash (`HashFor(mask)`, the scatter cache identity and the fingerprint's `class_table_hash`) changes, as it did for T6-4, T6-6 and T6-7. No frozen row records it yet; T6-13 captures it.
- **If T6-13 freezes first:** a new feature `vegetation.stand_fill` on bit 26 (next free after the reserved 0-25), `FeatureBit = 26` on the row, open until its own freeze step. Same code otherwise.
- The tree layer's tiles, the structured sampler and `ImportedTreeSourceTable` are untouched (fillers reuse base variants 9-11 and 27-29; no appended catalog rows, so `catalog_appended` is unchanged).

## 8. Cost

**Prototype (measured, reverted).** An environment-gated row in `RingScatterClassTable` (`veg.standfill_proto`: Dense habitat, reach 150 m, drawn through the existing sapling slot with the sapling meshes scaled 1.6-2.2x to 5-8 m) at 50 and 100 stems/ha. RTX 4070, 1920 x 1080, `--prefs=user`, review world seed 1337 (`ground.surface`, `scatter.rocks`, `vegetation.forest_structure`, `vegetation.ground_cover`, `vegetation.far_canopy`), canopy shade on, one boot per configuration with 2 rounds of 5 s windows. Root viewport GPU median, ms:

| Config | Pose | Whole frame (base) | Understory hide delta (same boot) | Understory primitives |
|---|---|---|---|---|
| High, no filler | interior_clear | 9.16 | 0.16 | 7.2 M |
| High, 50/ha | interior_clear | 9.49 | 0.23 | 10.0 M |
| High, 100/ha | interior_clear | 9.11 | 0.11 | 12.4 M |
| High, no filler | forest_edge_eye noon / sun 8 | 10.00 / 9.95 | 0.09 / 0.11 | 6.3 M / 8.1 M |
| High, 50/ha | forest_edge_eye noon / sun 8 | 10.03 / 10.20 | -0.02 / 0.18 | 8.5 M / 11.3 M |
| High, 100/ha | forest_edge_eye noon / sun 8 | 9.95 / 9.96 | 0.18 / 0.20 | 10.4 M / 14.0 M |
| Very High (sapling-slot casters), no filler | interior_clear / edge noon / edge sun 8 | 11.25 / 12.13 / 12.02 | 0.20 / 0.21 / 0.28 | 9.2 M / 8.1 M / 10.6 M |
| Very High, 100/ha (fillers cast through the LOD2 carrier) | interior_clear / edge noon / edge sun 8 | 11.48 / 12.53 / 12.32 | 0.40 / 0.54 / 0.47 | 17.9 M / 15.0 M / 20.4 M |

In-boot noise floor 0.00-0.07 ms (one window 0.18). Cross-boot differences at High scatter by about 0.3 ms (50/ha interior +0.33, 100/ha -0.05), so the same-boot understory hide delta is the better reading. **At High the 100/ha filler draws for about 0.0-0.1 ms** (hide delta -0.05 to +0.09 over no filler). **Casters through the LOD2 carrier add 0.2-0.33 ms** (Very High hide delta +0.20 to +0.33; cross-boot +0.23 to +0.40). The understory draw count stayed at 8 (10 with the sapling caster) because the prototype rode the sapling outputs. The prototype at 100/ha, interior: `.godot/audit_baseline/common/forest/stand_filler/interior_clear__noon_prototype_off_on.png` (a filler stands 3 m right of the eye; the mid-ground fills, the skyline is unchanged because fillers stay below the canopy).

The prototype's meshes are lighter than the real filler at LOD0 (sapling 465-646 triangles against small maple 1,732-3,442) and equal at LOD2 (157-280 against 262-884); triangle cost is small at these counts, and the alpha-tested crown area, which dominates, is matched by the scaling.

**Estimate for the real class** (from the prototype and survey C's per-band costs; est.):

| | interior_clear | forest_edge_eye |
|---|---|---|
| Fillers drawn (150/ha, small-maple and small-pine carriers; LOD0 about 5x and LOD2 about 3x the prototype's vertices) | 0.15-0.3 ms | 0.1-0.25 ms (the filler is mostly inside the forest, behind the edge) |
| Casters at High and up, **from a third carrier built from the variants' LOD3 crossed cards (36 triangles)** instead of the LOD2 carrier | 0.1-0.2 ms | 0.1-0.2 ms |
| Canopy raster with up to about 1,000 more trees | 0 ms GPU; build on a worker +0.3-1 ms (4,000 trees take 1.3-5 ms today) | same |
| **Total at High** | **about 0.25-0.5 ms** | **about 0.2-0.45 ms** |

Medium (no casters, density 0.75, reach 0.8): about 0.1-0.2 ms. The LOD3-card caster is a design change from the sapling caster (which draws the LOD2 carrier): shadows of 4-7 m trees at 20-100 m read the same from cards, and it is what keeps the caster inside the budget; SF-3 measures both and keeps the cards unless the shadow looks wrong in the stills. Against today's alternatives: 1.46x canopy stems cost +1.2 ms and 2.08x +1.65 ms at the interior with holes beyond 550 m (variant survey C).

## 9. Live tuning

One genuine tunable: **"Stand filler density"** (0-1, step 0.05, default 1.0), a rank gate of the stand-fill batches and its caster (no rebuild), next to "Understory density" in the Forest group of the grave-key drawer, persisted in `gameplay_vegetation_defaults.tres` like `UnderstoryDensity` (`GameplayVegetationDefaults`, `RingLiveTuningDrawer.Vegetation.cs`, `ApplyUnderstorySettings`). Presets set it like the understory: Low 0.5, Medium 0.75, High and up 1.0 (in `PresetUnderstory`). The reach follows the existing understory distance scale; shadows follow the preset (High and up); no switch for either.

## 10. Orders (implementer)

Each order: `tools/run_tests.ps1 -Label t6-sf -Since HEAD -DryRun`, then the listed tests; do not commit.

**SF-1. Carrier and catalog (S).** `UnderstoryCatalog.LoadStandFill()` builds `UnderstoryStandFill` (LOD0, LOD2 and LOD3-card caster carriers of base variants 9, 10, 11, 27, 28, 29; variant rows in that order; heights, unit radius, parameter rows 8-10 from `TreeCrownHullTable`), reusing the sapling carrier builder (`SaplingSources` generalised to a source list). Prepared textures: the sapling texture arrays already hold the maple and pine bark and foliage; confirm, else extend `tools/PrepareUnderstoryTextures.cs`. Test: extend `understory_catalog` checks in `understory_placement_test` (carrier triangle counts, row order, hull rows equal the table's).

**SF-2. Row and placement (M).** `RingScatterRenderGroup.StandFill = 7`; the `veg.standfill` row of section 2 (Id 15, FeatureBit 6); `UnderstoryCatalog.StandFillRowOf(mesh)`; BySpecies maps fir to no filler (the candidate is rejected). New test `stand_filler_placement_test` (headless, tier gate, tags vegetation, marker `STAND_FILLER_PLACEMENT`, expected_s 30, timeout_s 90): stems/ha in Dense tiles 100-160 for young stands and lower for old (StandAge response monotone); none outside Dense/OpenWoodland, in water, on site masks or on slopes over 34 deg; none within 2.5 m of a canopy trunk; mean NearestTreeM of fillers above that of random points; deterministic across workers; generation 1 table hash unchanged (`HashFor(0)` and the rocks-only mask). Manifest entry. Can it fail: drop the Dense term and confirm a FAIL.

**SF-3. Drawing, casters, density gate (M).** `RingUnderstoryLayer` slots 5 and 6 (caps 1,500 / 1,000), `SourceBuild` copies stand-fill records like saplings (nearest first), bands of section 4, the caster output (LOD3-card carrier) at High and up through a new `StandFillShadows` property set from the preset (`RingGraphicsQualityController.PresetStandFillShadows`: High and up), `StandFillDensity` rank gate. Colliders: capsules through the dead-wood collider pool (`RingUnderstoryLayer.Colliders.cs`), filtered by the density gate. Tests: extend `understory_render_match_gpu_test` (filler outputs drawn at the right bands, the caster present at High and absent at Medium) and `understory_collision_test` (a filler stem blocks).

**SF-4. Canopy raster source (S).** `RingUnderstoryLayer.CopyStandFillTrees`, `RingForestLighting.StartBuild` appends it (porosity by fade weight), the signature includes the understory set epoch. Test: extend `forest_canopy_raster_test` (a filler record raises the blocked fraction under it; the fade weight halves its effect at 140 m) and `forest_canopy_shade_gpu_test` if it needs a filler-present case.

**SF-5. Live tuning row (S).** "Stand filler density" per section 9 with the `live-tuning-setting` skill, including the settings smoke test.

**SF-6. Review (lead).** Cost at `interior_clear` (around 2,354,831, across 188,496), `forest_edge_eye` and `forest_clearing_eye`, High and Very High, `understory_hidden` same-boot A-B-A plus cross-boot against SF-0 (HEAD before SF-1); the C1 gate (understory 0.35 ms on the A1000 at Medium, 16 draws) re-read with the filler; the eye-level free path measured with the T6 visibility method at `interior_clear`; LOD-tail dolly 60-200 m at `forest_edge_eye`; stills (interior and edge, noon and 8 deg sun; filler off and on) to the user.

**Escalation:** the understory exceeds 16 draws; the 4070 filler cost exceeds 0.6 ms at any pose at High; the 130-150 m fade band is visible in the edge dolly; fir stands need a filler (Q2).

## 11. Questions for the user

1. Target density: 150 fillers per hectare in young closed stands (about 80 in old ones), giving about 46 m of eye-level free path from the fillers alone and 54 % hull cover near the eye. Higher densities cost more (section 8). Is that the right point?
2. Fir stands: no filler in v1 (fir crowns are already dense; fir 13 would triple the carrier cost). Add a fir filler carrier later, or leave fir stands as they are until the T8 tall conifer?
3. Medium maple 8 as a quarter of the fillers: about +0.04 cover at 100/ha for about twice the per-instance vertex cost. Include in v1?
4. Shadows at High and up (not only Very High as the saplings). Accept the extra caster cost shown in section 8?
