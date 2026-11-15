# TR. Tree rendering: LOD triangles, far billboards, billboard match: work orders

Parent: no plan section yet. This phase comes from the 2026-11-13 conversation with the user (tree cost, the LOD2-to-billboard switch, and the "tree rendering in Godot" video). It sits beside T6 and touches the same renderer, so read T6 "Phase facts" first. Prerequisite reading: `docs/landscape-audit-t0-spec.md` lines 1115-1220 (T0 cost baseline), `docs/terrain-vegetation-revamp-plan.md` section 7.1 (tree pack audit).
Status: in progress. **TR-2 design written 2026-11-14** (section "Design (TR-2, 2026-11-14)"; it rewrites TR-4 to TR-7 and TR-9, adds TR-9a and decisions D4 to D6). **TR-3 done** (2026-11-14): leaf cutout uses ALPHA_SCISSOR_THRESHOLD in both tree shaders; after vs before SSIM 0.9896 against a before-vs-before floor of 0.98-0.99 at forest_edge_eye noon (identical heatmaps), the 0.9 fail check 0.823; the four TR-3 tests pass. The harness's go_pose now re-issues a drifted pose (user mouse look reached the captured window) and records `pose_reached`. TR-1 running.
Depends on: nothing. It can run alongside T1-T5. Coordinate with T6-8 (tint), T6-10 (far canopy) and T6-11 (LOD tail), which touch the same shaders.
Freeze: none. This is a renderer change, not world content (see decision D1).

**Goal (the user, 2026-11-13): draw as many trees as the game allows without tanking performance.** Triangle savings (decimation) and the camera-facing far billboard serve that goal. They free the budget that a higher tree count then spends. The camera-facing billboard is required, not optional: one quad per tree is what makes a large far tree count affordable. TR-2 sizes the tree count against a frame budget; TR-9 raises the count.

## Decisions for the user

| # | Blocks | Question | Recommendation |
|---|---|---|---|
| D1 | TR-5 to TR-7 | These change how every world looks, generation 1 included, the same way the grass Cards default did. Is that allowed, or must they be gated by generation? | Allow it. Trees are placed the same way; only their meshes and shading change. The world fingerprint must not move. **Answered 2026-11-14: all worlds.** |
| D2 | TR-5 | How far may decimation go? It is judged by the TR-4 contact sheets at the distance each LOD is used. | Choose from the contact sheets. Gates in TR-2. |
| D3 | TR-2 | Which fade distances are the reference for the tree budget and the TR-8 gates: the shipped defaults (LOD0 to 40 m, cards from 300-350 m) or the user's saved preferences (LOD0 to 200 m, cards from 550-900 m)? | **Answered 2026-11-14: the shipped defaults.** The user's preferences stay a heavier option on top. |
| D4 | TR-9 | (Added by TR-2, 2026-11-14.) If a preset cannot draw every placed tree inside its far distance within the budget, should it end the trees sooner (shorter far distance) or keep the distance and draw only a share of the far trees (thinner forest)? | Shorten the far distance. Thinning shows as a sparser forest; a shorter distance hands over to the far canopy tint (T6-10) in new worlds. Design B4, F3. **Answered 2026-11-14: end the trees sooner (shorten the far distance).** |
| D5 | none (deferred) | (Added by TR-2.) A lighter LOD0 mesh pays only with long near-detail fades such as the user's (LOD0 to 200 m: 1.8 to 2.5 ms for 256 trees). At the shipped fades LOD0 costs 0.1 to 0.5 ms. Build it? | Defer. Decide from TR-8's table at the user's preferences. Design C1. |
| D6 | TR-6 | (Added by TR-2.) Seen from above, a far tree is one quad that faces the camera and is sized to the tree's real outline from that angle (its side-view picture squeezed to a crown-sized patch). Nothing is enlarged. Is that look acceptable? | Yes, judged from TR-6's altitude captures. Design D3. **Answered 2026-11-14: yes, judged from TR-6's altitude captures.** |

## Phase facts

Verified 2026-11-13 against the `terrain-vegetation-revamp` working tree. TR-1 measured against these facts on 2026-11-14 and corrects F1 and F6 (with notes on F8 and TR-9): read "Corrections from TR-1 (2026-11-14)" under "TR-1 results" with them.

- F1. **LOD profile in production.** `UseCompactTreeLodProfile` is on by default (`RingBiomeDecorationLayer.RenderPolicy.cs:12`). Internal LOD slots map to authored LODs through `CreateImportedTemplates` (`RingBiomeDecorationLayer.cs:221-224`): slot 0 = authored LOD0, slot 1 = authored LOD1, slot 2 = authored LOD2, slot 3 = authored LOD3 (crossed cards, no shadows). The intervals are in `GpuTreeLodInterval` (`RenderPolicy.cs:56-68`) and the edges in `GpuTreeLodEdges` (`RingBiomeDecorationLayer.Gpu.cs:443-451`):
  - authored LOD0: 0 to 40 m, dither out from 25 to 40 m (`TreeNearDetailFadeStart/EndMeters`);
  - authored LOD1: not drawn (`IsGpuTreeLodEnabled`, `RenderPolicy.cs:47`);
  - authored LOD2: 25 to 350 m, dither in from 25 to 40 m and out from 300 to 350 m (`TreeBillboardFadeStart/EndMeters`, grave-key menu, `RingLiveTuningDrawer.Vegetation.cs:217-218`);
  - LOD3 crossed cards: 300 m to `MinimumLod5DistanceMeters` (1,920 m), dither in from 300 to 350 m;
  - slot 4 (the shared far impostor built from LOD3 cards, `ImportedTreeImpostorCatalog.cs`, shader `materials/imported_tree_impostor.gdshader`): 1,920 m to `MaximumLod5DistanceMeters` (5,500 m). *Unverified:* that slot 4 is exactly the shared impostor whenever `UseSharedFarTreeImpostors` is on. TR-1 confirms it.
- F2. **LOD selection is per tree, on the GPU.** `materials/vegetation_gpu/ring_gpu_vegetation_indirect.glsl` measures each tree's distance from its root (`tree_distance`, lines 80-84), accepts it into a LOD band (lines 116-118) and writes the dither coverage (lines 284-285, 349-350). The tree shaders dissolve on that value (`apply_tree_dither`). Godot visibility ranges, the self-fade mode and the "AABB centre vs fade origin" workaround from the video do not apply: there is no per-tree node, and the band and the dither already measure from the same point. Do not add visibility ranges.
- F3. **Custom AABBs** are already set per indirect MultiMesh (`RingGpuVegetationCompute.cs:1030, :1044`). They must enclose the geometry (tile, tree height, wind sway), never the LOD distance.
- F4. **Alpha cutout is a manual `discard`**: `materials/imported_tree.gdshader:104` (`use_alpha_cutout`, `alpha_cutoff` taken from the pack's alpha-scissor material, `ImportedTreeCatalog.cs:237-238`) and `materials/imported_tree_impostor.gdshader:108`. Both are `depth_draw_opaque`. The grass cards use `ALPHA_SCISSOR_THRESHOLD` (`ring_grass_cards.gdshader:178`).
- F5. **Triangle counts** (plan 7.1). LOD0: maple up to 22.5k, pine up to 11.8k, fir up to 13.0k. LOD2: 150 to 5.3k. LOD3: 16 to 36. LOD0 and LOD2 have two surfaces (bark, then foliage); LOD3 has one (`ImportedTreeImpostorCatalog.cs:55` throws on more).
- F6. **Cost (T0, RTX 4070, 1080p, Medium).** In forest, trees cost 8.6 to 10.7 ms of a 15 to 16 ms root GPU frame and add 8 to 16 M primitives across 150 to 750 draws. The opaque pass is 8 to 11 ms in forest against 2.5 to 4 ms in the open. GPU tree culling costs 0.02 to 0.04 ms. The GPU root count is exactly 65,536 (`GameplayVegetationDefaults.DefaultMaximumGpuTreeRoots`, `GameplayVegetationDefaults.cs:12`) at every forest pose, with about 260,000 tree records resident.
- F7. **The impostor shader lights the crossed cards with the card's own normal** (`imported_tree_impostor.gdshader:65`, normal map at :125), so each card shades as one flat plane.
- F8. **Existing harness.** `landscape_cost_baseline` (hide cases, viewport timing, opaque bracket) and `landscape_lod_switch_capture` are in `tools/test_manifest.json`. Reuse them; do not write new harnesses.
- F9. **Existing tests that cover this renderer:** `imported_tree_prepared_catalog_test`, `imported_tree_impostor_catalog_smoke`, `imported_tree_gpu_regression`, `tree_lod_dither_gpu_test`, `tree_lod_compute_regression`, `tree_render_policy_regression`, `tree_shared_impostor_integration`, `gpu_tree_integration_test`, `tree_lighting_shadow_gpu_test`, `vegetation_shadow_gpu_test`, `tree_atmosphere_gpu_test`, `vegetation_settings_smoke_test`.

## Order and parallelism

| WO | Role | Size | Depends on | Alongside |
|---|---|---|---|---|
| TR-1 measure: cost per LOD, overdraw, root cap, switch strip | sol_lead | M | none | TR-3 |
| TR-2 design: decimation targets, far billboard, billboard match | sol_lead (design) | S | TR-1 | TR-3 |
| TR-3 built-in alpha scissor in both tree shaders | sol_implementer | S | none | TR-1, TR-2 |
| TR-4 offline decimation and contact sheets | sol_implementer | M | TR-2 | TR-6, TR-7 |
| TR-5 wire the decimated LODs into the compact profile | sol_implementer | M | TR-4, user D1 and D2 | TR-6 |
| TR-6 camera-facing far billboard tier | sol_implementer | M | TR-2, user D1 | TR-4, TR-7 |
| TR-7 billboard match: crown normals, tint, alpha coverage | sol_implementer | M | TR-2, TR-3, user D1 | TR-4, TR-6 |
| TR-8 review pack: before/after cost and look | sol_implementer | S | TR-5, TR-6, TR-7 | none |
| TR-9a cap fall-through: budget-derived band edges (added by TR-2, 2026-11-14) | sol_implementer | M | TR-5, TR-6; merged after TR-8's cost run | TR-7 |
| TR-9 raise the tree count to the budget | sol_implementer | M | TR-8, TR-9a | none |
| TR-10 tree shadows past the near range: canopy in the height shadow | sol_lead, then sol_implementer | M | TR-1 for the design and prototype (alongside TR-4 to TR-7); TR-9 for the prototype pictures and the implementation (TR-2 design H) | TR-4 to TR-7 |

## Work orders

### TR-1. Measure: cost per LOD, overdraw, root cap, switch strip

ROLE: sol_lead
SIZE: M
DEPENDS ON: none

OBJECTIVE
A table of where tree GPU time goes in forest (per LOD slot, triangles against fill), how many in-range trees the 65,536 root cap drops, and a picture strip of the LOD2-to-cards switch.

CONTEXT / ROOT CAUSE
T0 measured trees only as one class (F6). Choosing what to decimate needs cost per LOD, and the overdraw hint (an 8 to 11 ms opaque pass in forest) needs confirming before decimation is credited with the savings.

FILES/SYMBOLS
`tools/landscape_baseline_common.gd`, the `landscape_cost_baseline` and `landscape_lod_switch_capture` runners, `RingBiomeDecorationLayer.Gpu.cs` (`GpuTreeLodEdges`, `GpuTreeLodCaps`), `GameplayVegetationDefaults.cs`.

REQUIRED CHANGES
1. Add hide cases per LOD slot (0, 2, 3, 4) to the cost harness: suppress one slot's indirect output at a time and keep the others. Report root GPU delta, draws and primitives per slot at `forest_interior_eye`, `forest_edge_eye`, `forest_clearing_eye` and `coast_eye` (noon, plus `forest_edge_eye` at sun_8_set).
2. Add a tree-shadow-off case (tree shadow casters only), so that shadow cost separates from main-pass cost.
3. Fill test at `forest_edge_eye`: the same frame at 50 % render scale. If tree cost falls by much more than the triangle count would explain, the forest is fill-bound. Also report the overdraw debug view (`Viewport.DEBUG_DRAW_OVERDRAW`) as a picture.
4. Root cap: at the three forest poses, count tree records inside the slot-4 far edge against the 65,536 roots drawn, and say which trees are dropped (nearest-first or not). Measure the same poses with `MaximumGpuTreeRoots` at 262,144.
5. Confirm or correct F1 (the slot 4 mapping).
6. Switch strip: `landscape_lod_switch_capture` at 250, 300, 325, 350 and 400 m from a single maple, pine and fir, at noon and sun 8 degrees. Make one `sheet` per species.

DO-NOT-CHANGE CONSTRAINTS
No production behaviour changes. Harness-only code goes in `tools/`. Use the 4070 numbers only on the 4070 (machine-specific).

VALIDATION STEPS
`tools/run_tests.ps1 -Tests landscape_cost_baseline,landscape_lod_switch_capture -Label tr1 -DryRun`, then the run. Noise floor as in T0 (baseline against baseline_repeat). Send the user the per-LOD table, the overdraw picture and the three switch sheets.

ESCALATION CONDITIONS
A slot cannot be hidden on its own without changing selection (report the reason). The cap test above 65,536 crashes or runs out of memory.

STOP RULE
2 watchdog periods, 3 build-plus-run attempts.

### TR-1 results (2026-11-14)

Measured on the RTX 4070 desktop only (machine-specific). 1920 x 1080, Medium preset, Forward+, VSync off, clocks held, clear weather, 2 rounds of 8 s windows per case (second round reversed), noise floor = baseline against baseline_repeat. The camera did not move in any window set (pose guard: 0.00 m, 0.000 degrees at every pose). All cited runs were made after TR-3 was committed (592c6cf, 16:52); only the T0 comparison straddles it.

| Run | What | Results |
|---|---|---|
| `tr1` 20261114-165524 | slot cases, shadows, fill test, overdraw, budget probe, with the user's preferences | `.godot/audit_baseline/rtx4070/tr1/tree_lod_slots/2026-11-14_16-56-02/tree_lod.md` (+ `.json`, PNGs) |
| `tr1` 20261114-171905 | root cap 65,536 / 262,144 / 262,144 with far caps raised | `.godot/audit_baseline/rtx4070/tr1/tree_lod_cap/2026-11-14_17-19-29/tree_lod.md` |
| `tr1` 20261114-174658 | slot cases with the shipped F1 fades forced, and the switch strip | `.godot/audit_baseline/rtx4070/tr1/tree_lod_slots/2026-11-14_17-47-22/tree_lod.md`, `.godot/audit_baseline/rtx4070/tr1/switch_strip/2026-11-14_18-04-41/` |

**Two configurations.** The harness loads the user's preference files, as T0 did. The user's `gameplay_vegetation_defaults.tres` overrides the fades, so T0 (F6) and the first two runs measured this, not F1:

| | LOD0 (slot 0) | LOD2 (slot 2) | cards (slot 3) | far (slot 4) | mid batch span | tree sun shadows |
|---|---|---|---|---|---|---|
| user preferences (what the user plays, what T0 measured) | 0 to 200 m, dither out 25 to 200 m | 25 to 900 m, dither in 25 to 200 m, out 550 to 900 m | 550 to 1,920 m | 1,920 to 5,500 m | 3 tiles (720 m) | 240 m (Medium) |
| shipped F1 fades (third run) | 0 to 40 m, dither out 25 to 40 m | 25 to 350 m, out 300 to 350 m | 300 to 1,920 m | 1,920 to 5,500 m | 2 tiles (480 m) | 240 m (Medium) |

Medium without any override would be 20/30 and 250/300 (`PresetTrees`); it was not measured.

#### 1. Cost per slot (user preferences)

Root viewport GPU ms, baseline minus case. A slot case hides that slot's main-pass renderers only; "shadows" removes the tree shadow casters only. The parts add up to the all-trees delta within 0.3 ms.

| pose | time | baseline | noise floor | all trees | slot 0 (LOD0) | slot 2 (LOD2) | slot 3 (cards) | slot 4 (far) | tree shadows | sum of parts |
|---|---|---|---|---|---|---|---|---|---|---|
| forest_interior_eye | noon | 15.87 | 0.01 | 9.66 | 2.50 | 3.96 | 0.32 | 0.29 | 2.30 | 9.37 |
| forest_edge_eye | noon | 15.02 | 0.18 | 8.48 | 1.77 | 4.80 | 0.10 | -0.04 | 1.53 | 8.16 |
| forest_clearing_eye | noon | 11.45 | 0.02 | 5.25 | 0.12 | 3.34 | 0.25 | 0.19 | 1.18 | 5.08 |
| coast_eye | noon | 8.38 | 0.01 | 1.65 | 0.55 | 0.64 | 0.03 | 0.00 | 0.44 | 1.66 |
| forest_edge_eye | sun_8_set | 16.11 | 0.01 | 10.43 | 1.70 | 4.74 | 0.14 | -0.01 | 3.66 | 10.24 |

Draw calls and primitives removed by each case (the primitive counter counts every pass, so a main-pass slot shows about twice its submitted triangles):

| pose | time | draws: all / s0 / s2 / s3 / s4 / shadows | primitives in millions: all / s0 / s2 / s3 / s4 / shadows |
|---|---|---|---|
| forest_interior_eye | noon | 677 / 48 / 30 / 9 / 12 / 366 | 13.01 / 5.01 / 4.02 / 0.40 / 1.20 / 2.38 |
| forest_edge_eye | noon | 296 / 66 / 46 / 5 / 1 / 178 | 15.73 / 4.96 / 8.07 / 0.22 / 0.02 / 2.47 |
| forest_clearing_eye | noon | 151 / 24 / 24 / 8 / 9 / 86 | 8.04 / 2.50 / 2.89 / 0.35 / 0.85 / 1.44 |
| coast_eye | noon | 656 / 38 / 42 / 0 / 0 / 364 | 1.64 / 0.68 / 0.61 / 0.00 / 0.00 / 0.35 |
| forest_edge_eye | sun_8_set | 748 / 66 / 46 / 5 / 1 / 418 | 15.73 / 4.96 / 8.07 / 0.22 / 0.02 / 2.47 |

What each slot draws for that time (GPU readback of the indirect counts):

| pose | slot | trees drawn | triangles per tree | submitted triangles | ms | microseconds per tree | ms per million triangles |
|---|---|---|---|---|---|---|---|
| forest_interior_eye | 0 | 256 | 465 to 22,490 (mean 8,300) | 2.12 M | 2.50 | 9.8 | 1.18 |
| forest_interior_eye | 2 | 2,696 | 157 to 5,321 (mean 1,400) | 3.78 M | 3.96 | 1.47 | 1.05 |
| forest_interior_eye | 3 | 5,200 | 36 | 0.19 M | 0.32 | 0.06 | 1.7 |
| forest_interior_eye | 4 | 3,715 | 36 | 0.13 M | 0.29 | 0.08 | 2.2 |
| forest_edge_eye | 0 | 256 | mean 6,500 | 1.66 M | 1.77 | 6.9 | 1.07 |
| forest_edge_eye | 2 | 2,696 | mean 1,650 | 4.45 M | 4.80 | 1.78 | 1.08 |
| forest_edge_eye | 3 | 5,200 | 36 | 0.19 M | 0.10 | 0.02 | 0.5 |
| forest_edge_eye | 4 | 3,327 | 36 | 0.12 M | -0.04 | at the noise floor | |

- LOD2 is the largest single cost in forest (3.3 to 4.8 ms), then LOD0 (1.7 to 2.5 ms for 256 trees), then shadows. Slots 3 and 4 together are under 0.6 ms.
- LOD0 and LOD2 both cost about 1.1 ms per million submitted triangles. One LOD2 tree costs 25 to 70 times one card tree; one LOD0 tree costs 4 to 7 times one LOD2 tree.
- Of the all-trees delta, about three quarters falls inside the opaque bracket (tree shadow maps and the colour pass: 7.23 of 9.66 ms at forest_interior_eye, 6.48 of 8.48 ms at forest_edge_eye) and the rest before it (depth prepass). The tree-shadow delta is entirely inside the bracket (1.52 of 1.53 ms).

#### 2. Tree shadows

Tree shadow casters cost 1.2 to 2.3 ms at noon in forest and 3.66 ms at forest_edge_eye with the sun at 8 degrees (0.44 ms at coast_eye). That is 18 to 24 % of the tree cost at noon and 35 % at low sun. The casters are the shadow proxies of slots 0 and 2 inside 240 m (24 to 34 LOD2 proxies with 0.36 to 0.47 M triangles, 15 to 23 card proxies with 0.10 M); at forest_edge_eye the same proxies draw 178 times at noon and 418 times at sun 8 degrees (more cascades see them). This is the number TR-10 starts from.

#### 3. Fill test and overdraw: the forest is triangle-bound

forest_edge_eye, noon, the same cases at 50 % 3D render scale (a quarter of the pixels). The whole frame falls from 15.02 to 12.91 ms; the tree cost barely moves.

| case | delta at 100 % | delta at 50 % | ratio | part that scales with pixels | part that does not |
|---|---|---|---|---|---|
| all trees | 8.48 | 7.98 | 0.94 | 0.67 | 7.81 |
| slot 0 | 1.77 | 1.67 | 0.95 | 0.13 | 1.64 |
| slot 2 | 4.80 | 4.63 | 0.97 | 0.22 | 4.58 |
| tree shadows | 1.53 | 1.53 | 1.00 | 0.00 | 1.53 |

Verdict: not fill-bound. A fill-bound pass would fall towards a quarter; trees keep 94 %. Decimation (fewer triangles) is the lever that pays, at about 1.1 ms per million triangles removed from slots 0 and 2. The 8 to 11 ms forest opaque pass of F6 is tree geometry, not overdraw.

Overdraw pictures (`Viewport.DEBUG_DRAW_OVERDRAW`), in the first run's folder:
- `forest_edge_eye__noon__overdraw.png`, `forest_interior_eye__noon__overdraw.png`: the whole scene. The grass saturates the lower half, so these say little about trees.
- `forest_edge_eye__noon__overdraw_trees_only.png`, `forest_interior_eye__noon__overdraw_trees_only.png`: terrain and grass hidden. The interior picture shows many stacked leaf-card layers over the whole upper half of the screen. The layers are real, but with the depth prepass they cost little shading time, which is what the fill test shows.
- `..._overdraw_trees_only_slot0_hidden.png`, `..._slot2_hidden.png`, `..._overdraw_trees_hidden.png`, `..._normal.png`: the same view without LOD0, without LOD2, without trees, and the normal frame.
- Limit of the engine view: it replaces every material, so the shared far impostor (slots 3 and 4, whose vertices come from a texture in its shader) does not appear, and leaf cut-outs and the LOD dither are ignored. The numbers above, not the pictures, carry the verdict.

#### 4. Budget chain: how many trees are drawn

The chain is: tree records in the streamed tiles, then the resident GPU roots (`MaximumGpuTreeRoots`), then each slot's distance band, rank gate and cap (`GpuTreeLodCaps`, `RingBiomeDecorationLayer.Gpu.cs:972`). Counts at the three forest poses and the coast (user preferences, 65,536 roots):

| pose | records inside 5.5 km | GPU roots | records inside 5.5 km that are not roots | reach of the roots | drawn: s0 / s2 / s3 / s4 | drawn in total | share of the records inside 5.5 km |
|---|---|---|---|---|---|---|---|
| forest_interior_eye | 220,313 | 65,536 | 154,777 | 2.8 to 3.0 km | 256 / 2,696 / 5,200 / 3,715 | 11,867 | 5.4 % |
| forest_edge_eye | 220,323 | 65,536 | 154,787 | 2.6 to 2.9 km | 256 / 2,696 / 5,200 / 3,327 | 11,479 | 5.2 % |
| forest_clearing_eye | 221,074 | 65,536 | 155,538 | 2.7 to 3.0 km | 51 / 2,696 / 5,200 / 3,204 | 11,151 | 5.0 % |
| coast_eye | 48,665 | 52,095 (all) | 0 | 5.9 km | 80 / 1,068 / 1,765 / 4,888 | 7,801 | 16 % |

Per slot at forest_edge_eye (the other forest poses are within a few percent):

| slot | band | rank gate | cap | records in band | GPU roots in band | after the rank gate | drawn | what limits it |
|---|---|---|---|---|---|---|---|---|
| 0 (LOD0) | 0 to 200 m | 1.0 | 256 | 395 | 395 | 395 | 256 | the cap |
| 2 (LOD2) | 25 to 900 m | 1.0 | 2,696 | 13,338 | 13,338 | 13,338 | 2,696 | the cap (20 % drawn) |
| 3 (cards) | 550 to 1,920 m | 0.35 | 5,200 | 37,888 | 37,888 | 16,893 | 5,200 | the rank gate, then the cap |
| 4 (far) | 1,920 to 5,500 m | 0.10 | 15,000 | 177,967 | 23,180 | 3,327 | 3,327 | the root cap, then the rank gate |

Drop order:
- **Root cap (65,536): nearest tiles first.** Source preparation takes whole tiles in order of tile-centre distance (`RingBiomeDecorationLayer.GpuPreparation.cs:146-179`). The nearest record that is not a root is at 2,646 to 2,786 m, the farthest root at 2,946 to 3,042 m, and 2,800 to 4,000 roots lie beyond the nearest dropped record (one tile of slack). Every tree from about 3 km to 5.5 km is absent: about 155,000 records, 70 % of those inside the far edge.
- **Slot caps: batch order, not tree order.** The compute shader gives each batch what is left of the slot's budget, in the order protected (collider) roots first, then batches by the nearest point of their bounds (`Gpu.cs:557-560`, `ring_gpu_vegetation_indirect.glsl:250-260`). A batch that straddles the limit keeps its first roots in buffer order. So the drops are patchy in space, not farthest-first.
- **Slot 0.** The collider trees come first (up to 192, within 110 m); the rest of the 256 goes to the nearest tile groups. At forest_interior_eye all but 7 trees inside 100 m are LOD0, but only 42 of 212 at 100 to 150 m and 19 of 274 at 150 to 200 m. These trees are inside the 25 to 200 m cross-dither, so a tree without its LOD0 part is drawn by LOD2 alone at partial dither coverage (see-through).
- **Slot 2.** Merged 720 m batches. At forest_edge_eye LOD2 draws 80 of 134 trees at 100 to 150 m, 202 of 690 at 200 to 300 m, 248 of 986 at 300 to 400 m, 795 of 2,397 at 400 to 550 m, 897 of 3,405 at 550 to 700 m and 306 of 5,469 at 700 to 900 m. The trees not drawn between 200 and 550 m have no other slot, so half to two thirds of the forest at that range is missing (drawn share of the trees at 200 to 550 m: 31 % at forest_edge_eye, 41 % at forest_interior_eye, 47 % at forest_clearing_eye).
- **Slot 3.** Nearest batches first; the 5,200 run out at about 1.2 km (forest_edge_eye: 1,593 of 4,185 at 900 to 1,200 m, 166 of 4,299 at 1,200 to 1,500 m, none of 4,422 at 1,500 to 1,920 m). There is an empty ring from about 1.3 km to 1.92 km before slot 4 starts.
- **Slot 4.** At 65,536 roots it draws every gated root (it is starved by the root cap, not its own cap). At 262,144 roots it hits 15,000 and drops the farthest batches (forest_interior_eye: 5,681 of 15,922 gated trees drawn between 4 and 5.5 km, 89 of 5,849 past 5 km).
- **Rank gates** (`Gpu.cs:497`): slot 3 keeps rank <= 0.35, slot 4 rank <= 0.10, by design. At the LOD2-to-cards hand-off 65 % of trees get no card; at 1,920 m another 71 % of the remaining trees go.

Full distance histograms (records, roots, gated and drawn per slot in 21 bins) are in each `tree_lod.md`.

#### 5. Root cap at 262,144

Same poses, same process, three configurations (user preferences). "far caps" also sets `MaximumLod4Trees` 16,384 and `MaximumTreeBillboards` 65,536, their property limits.

| pose | configuration | GPU roots | drawn: s0 / s2 / s3 / s4 | drawn in total | baseline ms | all trees ms | slot 3 ms | slot 4 ms | video memory MB | tree buffers MB | settle s |
|---|---|---|---|---|---|---|---|---|---|---|---|
| forest_interior_eye | 65,536 | 65,536 | 256 / 2,696 / 5,200 / 3,715 | 11,867 | 15.88 | 9.59 | 0.34 | 0.30 | 3,007 | 8.8 | 0.5 |
| forest_interior_eye | 262,144 | 241,730 (all) | 256 / 2,696 / 5,200 / 15,000 | 23,152 | 16.44 | 10.20 | 0.31 | 0.86 | 3,024 | 20.2 | 2.5 |
| forest_interior_eye | 262,144 + far caps | 241,730 | 256 / 2,696 / 15,401 / 25,270 | 43,623 | 17.42 | 11.17 | 0.85 | 1.32 | 3,048 | 37.8 | 0.9 |
| forest_edge_eye | 65,536 | 65,536 | 256 / 2,696 / 5,200 / 3,327 | 11,479 | 15.02 | 8.48 | 0.16 | 0.00 | 2,976 | 8.8 | 0.5 |
| forest_edge_eye | 262,144 | 242,493 (all) | 256 / 2,696 / 5,200 / 15,000 | 23,152 | 15.30 | 8.76 | 0.15 | 0.26 | 2,981 | 20.8 | 2.4 |
| forest_edge_eye | 262,144 + far caps | 242,493 | 256 / 2,696 / 16,384 / 24,929 | 44,265 | 15.90 | 9.38 | 0.46 | 0.57 | 2,997 | 37.5 | 0.8 |
| forest_clearing_eye | 65,536 | 65,536 | 51 / 2,696 / 5,200 / 3,204 | 11,151 | 11.49 | 5.24 | 0.27 | 0.24 | 2,780 | 7.6 | 0.4 |
| forest_clearing_eye | 262,144 | 243,230 (all) | 51 / 2,696 / 5,200 / 15,000 | 22,947 | 12.17 | 5.98 | 0.31 | 0.87 | 2,791 | 19.5 | 1.8 |
| forest_clearing_eye | 262,144 + far caps | 243,230 | 51 / 2,696 / 16,384 / 24,922 | 44,053 | 13.11 | 6.94 | 0.88 | 1.19 | 2,810 | 36.2 | 0.7 |

- No crash and no memory problem. All 242,000 streamed records fit under 262,144, so the root cap no longer drops anything. Noise floors 0.00 to 0.06 ms.
- 262,144 roots alone doubles the drawn trees (11,500 to 23,150) for 0.3 to 0.7 ms, and removes the empty band beyond 3 km. The drawn count then sits exactly on the sum of the slot caps (23,152).
- With the far caps raised the engine draws about 44,000 trees for 0.9 to 1.6 ms over today. Slot 3 then draws every gated tree; slot 4 is limited by its 0.10 rank gate (25,000 of 178,000 records in its band).
- A card tree costs 0.02 to 0.06 microseconds: slots 3 and 4 together cost 1.0 to 2.2 ms for about 41,000 trees, against 3.3 to 4.8 ms for 2,696 LOD2 trees.
- LOD0 and LOD2 are untouched by the root cap (their caps bind first).

#### 6. The shipped F1 fades

The same poses with the four fades of F1 and the default mid batch span forced on the tree layer (`--tree-set`); everything else as above (user preferences, Medium, 65,536 roots).

| pose | time | baseline | noise floor | all trees | slot 0 | slot 2 | slot 3 | slot 4 | tree shadows | sum of parts | drawn: s0 / s2 / s3 / s4 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| forest_interior_eye | noon | 13.96 | 0.05 | 7.75 | 0.52 | 3.73 | 0.31 | 0.20 | 2.31 | 7.08 | 35 / 2,152 / 5,200 / 3,715 |
| forest_edge_eye | noon | 10.79 | 0.01 | 4.22 | 0.09 | 2.32 | 0.24 | 0.00 | 1.48 | 4.12 | 12 / 1,547 / 5,200 / 3,327 |
| forest_clearing_eye | noon | 9.26 | 0.03 | 3.02 | 0.00 | 1.79 | 0.31 | 0.21 | 0.74 | 3.05 | 0 / 1,009 / 5,200 / 3,204 |
| coast_eye | noon | 7.48 | 0.05 | 0.74 | 0.02 | 0.41 | 0.04 | 0.01 | 0.11 | 0.60 | 0 / 230 / 1,823 / 4,888 |
| forest_edge_eye | sun_8_set | 10.12 | 0.01 | 4.41 | 0.10 | 2.34 | 0.22 | 0.03 | 1.90 | 4.60 | 12 / 1,547 / 5,200 / 3,327 |

- With these fades slots 0 and 2 are not cap-bound: every tree in the LOD0 and LOD2 bands is drawn (forest_interior_eye: 2,152 LOD2 trees, 3.63 M triangles, 3.73 ms). The patchy LOD2 drops of section 4 are an effect of the user's 900 m LOD2 band against the 2,696 cap.
- The frame is 0.9 to 6.0 ms cheaper than with the user's fades (forest_edge_eye: 15.02 to 10.79 ms at noon, 16.11 to 10.12 ms at sun 8 degrees). LOD0 falls from 1.8 to 2.5 ms to 0.1 to 0.5 ms, because 12 to 35 trees are inside 40 m instead of 256 inside 200 m. LOD2 is still the largest cost (1.8 to 3.7 ms in forest), then shadows.
- Slot 3 is still on its cap: 5,200 of 16,900 to 19,100 gated trees. The cards now start at 300 m, so they run out sooner, at about 1.0 to 1.1 km (forest_edge_eye: 600 of 4,185 drawn at 900 to 1,200 m, 1 of 4,299 at 1,200 to 1,500 m), and the empty ring reaches from there to 1.92 km.
- The root cap result is the same (nothing past about 3 km).
- Fill test at forest_edge_eye: 4.22 ms at 100 %, 3.93 ms at 50 % (ratio 0.93). Triangle-bound here too.
- At forest_interior_eye the parts add to 7.08 ms against 7.75 ms for all trees; the other poses agree within 0.2 ms.

#### 7. Switch strip

One isolated tree per species, watched at eye level (within 2.7 degrees of level) from five distances across the LOD2-to-cards fade, at noon and with the sun at 8 degrees, HUD hidden. Each sheet has noon on the top row (tiles 0 to 4) and sun 8 degrees on the bottom row (tiles 5 to 9); the columns are the five distances. "tight" is the centre 192 x 192 pixels enlarged 3 times without filtering; "wide" is the centre 576 x 324 pixels as rendered.

Sheets in `.godot/audit_baseline/rtx4070/tr1/switch_strip/2026-11-14_18-04-41/` (with `strip.json`: trees, ranks, every frame):

| fade | distances | sheets |
|---|---|---|
| shipped F1 fade, 300 to 350 m (the work order's strip) | 250, 300, 325, 350, 400 m | `maple_f1_tight.png`, `pine_f1_tight.png`, `fir_f1_tight.png` (and `*_f1_wide.png`) |
| the user's fade, 550 to 900 m | 500, 550, 725, 900, 950 m | `maple_user_tight.png`, `pine_user_tight.png`, `fir_user_tight.png` (and `*_user_wide.png`) |

- Trees: maple (variant 5, 20 m tall, rank 0.07) and pine (variant 17, 21 m, rank 0.27) near around 10,422,000 m / across 240,000 m; fir (variant 12, 10.5 m, rank 0.13) near around 18,759,000 m / across 160,000 m. All three have a rank under 0.33, so they keep a card. The fixture poses have no fir and no tree with a 950 m clear line, so the harness searches these scanned sites (`STRIP_SITES`).
- **Maple, noon.** LOD2 at 250 and 300 m is an open crown of sunlit yellow-green leaves with sky showing through. The card at 350 and 400 m is a solid dark green silhouette, wider than the tree and leaning to one side. At 325 m both are on screen, dithered.
- **Maple, sun 8 degrees.** The step has the opposite sign: LOD2 is dark with a little rim light, the card is evenly bright yellow-green (one flat plane lit by the low sun, F7).
- So the step is three things at once: brightness (darker at noon, brighter at low sun), crown openness (the cards lose the gaps between leaves) and silhouette.
- **Pine.** It stands in front of a pine wood. The change is small at these distances.
- **Fir.** The fir is the small dark tree at the centre of each tile. The taller pale tree about 20 m to its right is a maple, which shows the same step as the maple sheet.
- An earlier capture found the pine and fir on a 35 degree mountainside, so the camera looked down at them by 25 to 42 degrees: `.godot/audit_baseline/rtx4070/tr1/switch_strip/2026-11-14_17-13-42/pine_f1_tight.png`. From that angle the crossed cards read as flat T shapes on the ground at 350 and 400 m. It is kept as a picture of the steep-angle case of TR-2 item 4.

#### 8. F1 verdict

F1's slot-to-mesh mapping holds for slots 0 and 2 (authored LOD0, and authored LOD2 through `GetSimplifiedMesh`) and for slot 1 (not drawn). It is wrong for slot 3: with `MergeFarGpuTreeBatches` and `UseSharedFarTreeImpostors` on (both default, both on in every run), slots 3 and 4 both draw the shared far impostor (`Gpu.cs:260`, `:787-791`; probe: 36 triangles per tree and `shared_far_impostors = true` in both slots). The *unverified* item of F1 is confirmed: slot 4 is the shared impostor. Slots 3 and 4 differ only in batch size (2 and 4 tile spans), rank gate, cap and the dither data.

#### Corrections from TR-1 (2026-11-14)

The Phase facts above are left as written. Read them with these corrections:

1. **F1, intervals.** They are the code defaults (also the High and Very High preset values), not what the user plays or what T0 measured. The user's preferences give LOD0 0 to 200 m, LOD2 25 to 900 m and cards from 550 m, with 720 m mid batches; Medium without overrides gives 20/30 and 250/300 with a 240 m tree shadow distance (`RingGraphicsQualityController.PresetTrees`). Every cost in F6 was measured with the user's preferences.
2. **F1, slot 3.** Slot 3 is the shared far impostor, not the authored LOD3 mesh with the `imported_tree` shader. Authored LOD3 is used as the shadow proxy of slot 2 (`GpuShadows.cs:97`) and as the impostor's source. A change to the card look (TR-7) therefore lands in `imported_tree_impostor.gdshader` for the whole range from the LOD2 hand-off to 5.5 km, and TR-6's "dithered hand-off from the crossed-card tier" is a hand-off between two bands of the same mesh.
3. **F6, "the GPU root count is exactly 65,536".** That is the resident root cap, not the number of trees drawn. In forest the engine draws 11,200 to 11,900 trees of about 220,000 inside 5.5 km, and nothing beyond about 3 km.
4. **TR-9, limits.** The limits that bind are the per-slot caps `MaximumLod1Trees` (256), `MaximumLod2Trees` + `MaximumLod3Trees` (2,696 for slot 2 in the compact profile), `MaximumLod4Trees` (5,200) and `MaximumTreeBillboards` (15,000), plus the rank gates of `VegetationLodRankGate` (0.35 and 0.10). TR-9 lists only `MaximumTreeBillboards`. Raising `MaximumGpuTreeRoots` alone cannot draw more than 23,152 trees. None of the slot caps or gates is in the grave-key menu or in a preset.
5. **TR-1 and TR-7, the switch.** At the LOD2-to-cards hand-off 65 % of trees get no card (rank gate 0.35), and slot 2's cap already removes most LOD2 trees before the hand-off. The visible step is a change of tree count as much as a change of look. TR-2 item 7 ("fix that order first") applies: the slot caps do not drop nearest-first.
6. **TR-1, strip distances.** 250 to 400 m straddles the hand-off only with the 300/350 fade. With the user's fade the same five points are 500, 550, 725, 900 and 950 m. Both were captured.
7. **F8, harness.** `run_tests.ps1` cannot pass per-run arguments, so the TR-1 modes are selected with the `LANDSCAPE_HARNESS_ARGS` environment variable (see below). Output node names cannot be used to find a slot's renderers (Godot renames a recreated output to `@MultiMeshInstance3D@N`); the harness asks the layer.

#### Rerunning (TR-5, TR-8, TR-10)

```powershell
$env:LANDSCAPE_HARNESS_ARGS = '--tree-lod=slots --strip'      # slot cases, shadows, fill, overdraw, probe; then the strip
.\tools\run_tests.ps1 -Tests landscape_cost_baseline,landscape_lod_switch_capture -Label tr1
$env:LANDSCAPE_HARNESS_ARGS = '--tree-lod=cap'                # root-cap configurations
.\tools\run_tests.ps1 -Tests landscape_cost_baseline -Label tr1
```

Options: `--poses=`, `--rounds=`, `--seconds=`, `--rs50=<pose__time|none>`, `--overdraw=<pose__time|none>`, `--cap-configs=65536,262144,262144+far`, `--tree-set=Property:value,...` (sets tree-layer properties for the run), `--strip-configs=user,f1`. Without the variable both tests run their T0 behaviour unchanged. The probe is `tools/RingBiomeDecorationLayer.TreeBudgetProbe.cs` (read-only). Sheets: `tools/media/ffmpeg_tools.ps1 sheet -Path <strip>/<config>/<species>/tight/frame.png -Columns 5 -Rows 2 -TileWidth 576`.

### TR-2. Design: decimation targets, far billboard, billboard match

ROLE: sol_lead (design)
SIZE: S
DEPENDS ON: TR-1

OBJECTIVE
A short "Design" section added to this file that TR-4 to TR-7 implement without inventing anything.

REQUIRED CHANGES
Decide and write down:
1. **Decimation targets per LOD**, ordered by TR-1's cost per slot. LOD2 is the likely target because it covers 25 to 350 m and carries most trees. Options to rank:
   - deeper LOD2 meshes;
   - filling the unused authored-LOD1 slot with a decimated LOD2 for roughly 150 to 350 m, so that LOD2 proper covers only about 25 to 150 m;
   - a lighter LOD0.
   Give a triangle target per variant and size class.
2. **Decimation method.** Bark: meshoptimizer simplification (`ImporterMesh.GenerateLods`, or the same library offline) with the normal-split angles chosen. Foliage: prune whole leaf-cluster quads and scale the survivors up to keep the crown's coverage. Edge-collapse simplification does not work on alpha-cut quads. State the coverage rule: the projected alpha coverage at the LOD's switch distance stays within 5 % of the source.
3. **Storage and identity.** Decimated meshes go in a separate prepared resource beside `resources/trees/prepared/lolipop_catalog.res`. The 30-variant catalog stays byte-identical (`tree_catalog_append_test` rule from T6-5) unless D1 says otherwise. Say whether the vegetation tile cache (`lod_cache/vegetation`) or the world fingerprint keys on catalog identity, and how this avoids invalidating them.
4. **Far billboard.**
   - One quad per tree that turns about the tree's **surface-up axis**: the ring's radial direction, not world Y.
   - Use one of the two LOD3 card textures, so no new bake is needed.
   - Decide its band. Suggested: it replaces slot 4 (1,920 to 5,500 m), and the decision is whether it starts earlier.
   - Decide what happens at steep view angles (flying, looking down). A cylindrical billboard turns into a sliver there. Options: tilt toward the camera above some angle, or fade into the far canopy tint (T6-10).
   - Its shadows, haze and lighting follow the impostor shader's existing includes.
5. **Billboard match** (the 300 to 350 m step):
   - Crown normals: normal from the crown centre, blended with the card normal map by a stated weight.
   - Per-variant tint and brightness calibration: measured how, and stored where (the `variant_parameters` row 0 already multiplies albedo).
   - Coverage-preserving alpha mips for the card atlas.
   - Whether the switch distances move. TR-1 cost decides.
6. **Grave-key menu**, following AGENTS.md:
   - Genuine tunables only, such as the far billboard start distance and the LOD fade distances.
   - No per-LOD on/off toggles.
   - Debug and A/B switches go in debug tooling.
7. **Tree count budget (the phase goal).**
   - Set a tree GPU budget per preset at `forest_edge_eye`. Suggested starting point: Medium no higher than today's 8.6 ms on the 4070.
   - From TR-1's cost per slot, estimate how many trees each tier can hold within that budget once decimation and the far billboard are in.
   - Choose:
     - the new `MaximumGpuTreeRoots` default per preset;
     - how far the camera-facing tier extends, past 5.5 km if the budget allows;
     - how much earlier it can start than 1,920 m;
     - whether the placement density for far tiers can rise, or only the cap.
   - If TR-1 shows the cap drops trees in a non-nearest order, fix that order first.
8. **Gates for TR-8.**
   - Tree GPU time at `forest_edge_eye` down by a stated percentage.
   - Primitives down by a stated amount.
   - No new visible pop in the switch strips.
   - Shadow-casting coverage unchanged at 100 to 480 m.

VALIDATION STEPS
None to run. Each decision names the test or capture that checks it.

STOP RULE
1 watchdog period.

### Design (TR-2, 2026-11-14)

Written by the lead from TR-1's tables and the code; nothing was run. **Measured** means a TR-1 number. **Estimate** means arithmetic on TR-1 numbers that a later order must confirm; each estimate names that order. The reference configuration ("R") for every budget and gate is TR-1 section 6: RTX 4070, 1920 x 1080, Medium, the shipped fades 25/40 and 300/350 forced with `--tree-set` (user answer D3). Where it is cheap, the same table is also reported with the user's preferences; those rows are reports, not gates.

#### A. What TR-1 changes

1. The cost is triangles in LOD2 (measured 2.3 to 3.7 ms of a 4.2 to 7.75 ms tree cost at R), then tree shadows (1.5 to 2.3 ms), then LOD0 (0.1 to 0.5 ms). Cards cost 0.02 to 0.06 microseconds per tree.
2. The tree count is set by slot caps and rank gates, not by the root cap. At R the engine draws about 10,100 to 11,100 of 220,000 placed trees.
3. A tree that a cap or gate rejects is not drawn by any other tier. That is the cause of the patchy LOD2 forest (user preferences), the empty ring from 1.0 km to 1.92 km, and the 65 % loss of trees at the card hand-off.
4. Slots 3 and 4 are already one mesh and one shader (36 padded triangles per tree). So the far billboard is a second, lighter mesh on that shader, and the billboard match is one shader change for the whole range from the LOD2 hand-off to the far edge.

The design therefore has three parts, in order of yield: (B) no tree is lost, it only changes tier; (C) the LOD2 band draws a decimated mesh for most of its trees; (D) trees past 900 m are one quad each.

#### B. Cap and gate policy

**B1. A cap moves a hand-off inward; it never deletes a tree ("fall-through").** Each slot keeps a cap as an overload valve. When a slot's band holds more trees than its cap, the slot's far edge moves inward to the distance where the cap is reached, and the next slot's near edge and cross-dither move with it. Every tree in range is then drawn by exactly one tier (two inside a cross-dither). The last tier (the quad) has a cap equal to the root cap, so it never binds.

**B2. How the edge is found: on the CPU, in the loop that already exists.** `TryGpuTreeGroupPruneCounts` (`RingBiomeDecorationLayer.Gpu.cs:719`) already visits every root of every group that overlaps a band at each selection rebuild. It gains a 64-bin distance histogram per slot (slots 0 to 3). The effective far edge of a slot is the upper edge of the last bin at which the running count stays at or under 95 % of the cap; if the band never reaches that, the edge is the configured one. The effective edge goes into the existing `edges` array, so the band words `p[3]`, `p[4]` and the fade words `p[56..59]` carry it. **The fall-through needs no change to `ring_gpu_vegetation_indirect.glsl`, its bindings or its slot count.** (The only shader change in this design is the steep-angle rule of D3: two parameter words.)
- The fade width of a moved hand-off is the configured width of that hand-off, ending at the effective edge.
- Hysteresis: an effective edge moves only when the new value differs by more than one bin.
- The shader's `used[slot]` budget stays as a backstop. In a steady state it must not bind (the 5 % margin covers the reuse radius).
- Protected (collider) roots keep first claim on slot 0, as today.
- Without pruning or without a canonical focus (fixtures), the configured edges and the shader backstop apply, as today.
- Cost: CPU only, inside "Trees output selection". Estimate under 0.1 ms per rebuild (about 15,000 roots visited at R for slots 0 to 2, about 12,000 more for slot 3; one square root and one increment each). Gate: p95 of "Trees output selection" rises by at most 0.15 ms at `forest_interior_eye` while walking. **Checked by** TR-9a's test and telemetry.

**B3. Strict nearest-first inside the compute shader: not chosen.** The shader compacts each batch on its own and shares only a counter per slot, so a true nearest-first order needs either a global sort or a distance cut. A distance cut is what B2 computes. Doing it on the GPU instead (atomic histogram in phases 0 and 3, a one-group resolve dispatch, the cut applied on the next submission) would cost under 0.01 ms of GPU time, but it needs a new binding, new parameter words, a one-submission latency rule in the frame cache, and changes to `tree_lod_compute_regression`. Use it only if B2 misses its CPU gate.

**B4. Rank gates.** In the compact profile every slot's gate is 1.0. The static `VegetationLodRankGate` (0.85 / 0.60 / 0.35 / 0.10) stays for the non-compact profile and the CPU fallback. One preset value, `FarTreeDensity` (default 1.0 at every preset), is the gate of the quad slot; it is not in the menu and TR-9 lowers it only with the user's agreement (decision D4). No tree is scaled up to make up for a thinned tier (realism rule).
- Effect at R (estimate from TR-1's histograms): cards at 300 to 900 m rise from about 5,200 to about 12,300 trees; trees past 900 m rise from about 3,300 to about 207,000.
- **Checked by** TR-9's probe table (drawn against records per 100 m bin).

**B5. Root cap.** `MaximumGpuTreeRoots` is a memory guard, sized per preset to hold every record inside the far distance (measured: 242,000 records fit under 262,144 at 5.5 km; 65,536 reaches 2.6 to 3.0 km). If it still binds, the existing nearest-tile-first order applies. `MaximumTreeBillboards`' clamp and the output buffer capacity rise to the root range (1,048,576).

**B6. Caps per preset (compact profile).** Slot numbers are the new mapping of C1. "Roots" means the preset's root cap.

| Preset | slot 0 LOD0 | slot 1 LOD2 | slot 2 LOD2b | slot 3 crossed cards | slot 4 quad | roots | far distance |
|---|---|---|---|---|---|---|---|
| Low | 256 | 384 | 1,536 | 8,192 | roots | 98,304 | 3,000 m |
| Medium | 256 | 640 | 2,560 | 16,384 | roots | 262,144 | 5,500 m |
| High, Very High | 256 | 640 | 4,096 | 16,384 | roots | 262,144 | 5,500 m |
| Ultra Video, Cinematic | 256 | 1,024 | 8,192 | 32,768 | roots | 524,288 | 8,000 m if TR-9 confirms it, else 5,500 m |

- Slot 0 stays at 256 (it must hold the 192 colliders). At R it holds 12 to 35 trees and never binds.
- Worst case of a full slot at 1.1 ms per million triangles (measured rate): slot 1 at 640 x 1,690 = 1.2 ms; slot 2 at 4,096 x 600 = 2.7 ms.
- At R none of the caps binds (estimate: slot 1 holds 250 to 350 trees, slot 2 1,300 to 1,800, slot 3 about 12,300). They bind only with heavier fades, and then B1 applies.
- Low's values are provisional until the A1000 run of TR-9.

**B7. Menu and presets (AGENTS.md rules).**
- Preset values, not in the menu: the five caps, `FarTreeDensity`. They are overload valves, not tunables.
- Preset values with a menu row (Vegetation drawer, `live-tuning-setting` conventions):
  - "Reduced-detail fade start / end (m)" (`TreeMidDetailFadeStartMeters` / `EndMeters`), the LOD2 to LOD2b hand-off, beside the two existing fade pairs;
  - "Far billboard start (m)" (`TreeQuadStartMeters`);
  - "Far tree distance (m)" (`TreeFarDistanceMeters`, which drives `MaximumLod5DistanceMeters`).
- "Resident GPU tree roots" keeps its row; its default now comes from the preset.
- Not in the menu: the crown-normal weight, the calibration, the steep-angle thresholds, any per-tier switch. A/B is done with layer exports through the harness's `--tree-set` (for example `UseTreeRenderLods`, `UseFarTreeQuads`, both default on), like `UseSharedFarTreeImpostors` today.
- **Checked by** `vegetation_settings_smoke_test` (new keys, ranges, preset fallbacks) and `tree_render_policy_regression`.

#### C. Decimation (TR-2 items 1 to 3)

**C1. Ranking and slot mapping.** At R, 84 % of the LOD2 trees stand between 150 and 350 m (estimate from the `forest_edge_eye` histogram), where a 20 m tree is 94 to 40 pixels tall and the authored LOD2 spends more than one triangle per pixel.
1. **Chosen: a decimated LOD2 ("LOD2b") for the far part of the LOD2 band**, using the unused slot. The near part keeps the authored LOD2, so nothing changes where the mesh is largest on screen.
2. Not chosen now: a deeper LOD2 for the whole band. It would show at 25 to 60 m, where LOD2 is 230 to 560 pixels tall.
3. Deferred: a lighter LOD0. LOD0 costs 0.1 to 0.5 ms at R. It only pays at the user's preferences (1.8 to 2.5 ms for 256 trees). Decision D5.

Slot mapping in the compact profile (distance order kept, five slots as today):

| Slot | Mesh | Band at R | Batches | Shadow proxy |
|---|---|---|---|---|
| 0 | authored LOD0 | 0 to 40 m | per tile | authored LOD2 (unchanged) |
| 1 | authored LOD2 (simplified material, as slot 2 today) | 25 to 150 m | per tile | authored LOD3 cards (unchanged rule) |
| 2 | LOD2b | 130 to 350 m | merged mid batches | authored LOD3 cards (unchanged) |
| 3 | shared impostor, crossed cards | 300 to 1,050 m | 2-tile | none |
| 4 | shared far quad (D) | 900 m to the far distance | 4-tile | none |

- LOD2 to LOD2b hand-off: cross-dither 130 to 150 m at High and above; 110 to 130 m at Medium; 80 to 100 m at Low; 200 to 240 m at Ultra Video.
- All five slots carry dither data. `IsGpuTreeLodEnabled` enables slot 1.
- **Shadow proxies do not use decimated meshes.** Shadow coverage is unchanged by construction.

**C2. Triangle targets for LOD2b**, per size class (T6 fact P4). Target = bark plus foliage. TR-4 builds three candidate levels per variant for the user's choice (D2): A = 1.6 x the target, B = the target, C = 0.6 x the target.

| Class | Variants (index) | Authored LOD2 | LOD2b target (level B) |
|---|---|---|---|
| Large canopy: maple large, pine large | 3-5, 18-20 | up to 5.3k | 900 |
| Big: pine big | 15-17 | up to 3.2k | 700 |
| Medium: maple medium, pine medium, fir large | 6-8, 21-23, 12 | | 500 |
| Small: maple small, pine small, fir medium | 9-11, 27-29, 13 | | 250 |
| Sapling: maple sapling, pine sapling, fir small | 0-2, 24-26, 14 | 150 to 300 | 120 |

- A variant whose authored LOD2 is at or under 1.25 x its target is stored unchanged and marked "source".
- Bark takes at most 40 % of the target and at least 24 triangles.
- Phase gate on the result: the drawn-weighted mean of LOD2b at `forest_edge_eye` is at most 700 triangles (authored LOD2: 1,650 measured). If the class table cannot reach it within the coverage rule, TR-4 reports the best value.
- LOD0 and the authored LOD2 in slot 1 are not decimated.
- **Checked by** `tree_lod_decimation_test` (targets) and the TR-4 triangle table.

**C3. Method.**
- Bark: remove connected bark components whose mean diameter is under 0.2 m (one pixel at 150 m), then meshoptimizer simplification to the bark target, with normal merge 60 degrees and normal split 25 degrees (Godot's import defaults). TR-4 uses whichever deterministic binding the prepare tool can call (`SurfaceTool.GenerateLod` with a target index count, or `ImporterMesh.GenerateLods` taking the generated level nearest under the target) and reports which.
- Foliage: connected leaf-cluster components are removed whole, in a fixed order (a stable hash of the component's quantised centroid; components nearest the crown axis first). Survivors are scaled about their own attachment point by one uniform factor, at most 1.6. No edge collapse on alpha-cut quads.
- Coverage rule: the alpha-covered area of the decimated mesh, rasterised on the CPU at 0.21 m per pixel (one pixel at 150 m) from 8 azimuths at eye level and 4 azimuths from 30 degrees above, is within 5 % of the source on the mean and within 8 % in every view. The decimated AABB stays inside the source AABB grown by 2 %.
- The scale-up keeps the crown's measured coverage; it is not an enlargement of the tree.
- **Checked by** `tree_lod_decimation_test` (coverage, determinism, attachments, AABB) and the contact sheets at 150 m and 325 m, noon and sun 8 degrees.

**C4. Storage and identity.**
- New resource `resources/trees/prepared/lolipop_render_lods.res` with a manifest beside it, holding per variant index the chosen LOD2b mesh (two surfaces), the tool version and a hash of the source LOD2 it was made from. Loader: `ImportedTreeRenderLodCatalog`. If the resource is missing or its source hash is stale, slot 2 draws the authored LOD2 and logs one warning.
- The meshes are **not** added to `ImportedTreeVariant.Lods`. The fingerprint's catalog row hashes each variant's name, sizes and the surface lengths of `v.Lods` (`tools/WorldGenerationFingerprint.cs:167-181`); the vegetation tile cache keys on `VegetationDiskCacheVersion`, `ForestRegionSampler.AlgorithmVersion`, `ImportedTreeCatalog.CatalogVersion` and `VariantCount` (`RingBiomeDecorationLayer.TreeStreaming.cs:1512-1515`). None of these sees a separate render resource, so neither the fingerprint nor `lod_cache/vegetation` is invalidated.
- `lolipop_catalog.res` stays byte-identical.
- **Checked by** `world_generation_fingerprint_test`, `imported_tree_prepared_catalog_test` and `tree_streaming_contract_test`, all unchanged.

#### D. Far billboard (TR-2 item 4)

**D1. Mesh and band.** A second shared mesh of one quad (two triangles) per tree, drawn by slot 4 with the impostor shader in a quad mode (a duplicated material with `far_quad_mode` on, so tint, crown normal, haze, shadow, wetness and storm code is the same code as the cards).
- Texture: the UV rectangle of the largest plane of the variant's LOD3 mesh (largest area; the first on a tie). The quad's width, height and offset are that plane's position bounds, so texel density is unchanged. No new bake. These go in new rows of `variant_parameters`.
- Half the trees mirror U by a bit of their seed.
- No wind in quad mode (0.3 m of sway is under a quarter of a pixel at 900 m).
- Band: from `TreeQuadStartMeters` to the far distance. Start 900 m (Medium to Very High), 600 m (Low), 1,200 m (Ultra Video, Cinematic). Cross-dither with the cards from the start to start + 150 m. At 900 m a 20 m tree is 16 pixels tall.
- Far edge: the quad dithers out over the last 10 % of the far distance. T6-10's tint boundary stays at the far distance; because that distance is now a preset value with a menu row, T6-10 must read it and not assume 5,500 m.
- Cost (estimate): 36 padded triangles become 2, so per-tree vertex work falls by about 18 times; 207,000 quads are taken as 0.3 to 0.9 ms at `forest_edge_eye` and 0.6 to 1.8 ms at `forest_interior_eye`. **Checked by** TR-6's slot-4 case in the TR-1 harness, which reports microseconds per quad tree; TR-9 uses that number, not this estimate.

**D2. Orientation.** The quad turns about the tree's surface-up axis (the instance's Y axis, which is the ring's radial direction at the root) to face the **camera position**, not the camera's forward vector, so it does not swim when the view turns.

**D3. Steep view angles.** Let e be the elevation of the camera seen from the tree (sin e = up . direction to camera).
- The quad is centred on the tree's mid-height and stays perpendicular to the direction to the camera. Its height is scaled by sqrt(cos^2 e + (W/H)^2 sin^2 e), with W and H the variant's width and height. That is the outline of an ellipsoid of the tree's size seen from that angle: the full side view from the ground (identical to an upright quad at e = 0), a W x W patch from straight above, no sliver in between, and no enlargement.
- Directly overhead (up . direction above 0.999) the quad's horizontal axis falls back to the ring-axis direction.
- **Crossed cards at steep angles.** TR-1's mountainside strip shows the cards as flat T shapes at 25 to 42 degrees. A tree in the card band whose elevation is above 30 degrees is drawn by the quad instead, cross-dithered from 20 to 30 degrees. This is decided per tree in `accepted()` and in the coverage word of `ring_gpu_vegetation_indirect.glsl` (the root, its Y axis and the camera are already there; two new parameter words for the thresholds; no new binding). The CPU prune counts treat every group that overlaps the card band as a candidate for both slots 3 and 4.
- Fallback if that shader change is refused at TR-6's escalation: the card-to-quad start distance falls to the LOD2 hand-off as the camera's height above terrain rises from 75 m to 150 m (a CPU-only edge change). It does not cover the mountainside case.
- **Checked by** TR-6's captures (ground at 2 and 5 km; 300 m and 2 km altitude at 30 and 70 degrees down; the TR-1 mountainside pose) and by `tree_lod_dither_gpu_test` (coverage continuous across the distance hand-off and across 20 to 30 degrees). Decision D6 is the user's view of the overhead look.

**D4. Lighting, haze, shadows.** Unchanged includes. The quad casts no shadow, like the cards.

#### E. Billboard match (TR-2 item 5)

The step has three parts (TR-1 section 7): count, brightness with the sign changing between noon and low sun, and crown openness.

1. **Count.** Solved by B4: every LOD2b tree gets a card.
2. **Crown normal.** In the impostor shader (both modes) the shading normal is `normalize(mix(N_card, N_crown, w))` with w = 0.75 for the crown and falling to 0 below the crown base. `N_crown` is the normal of an ellipsoid crown facing the viewer: from the fragment's offset from the crown centre in the card plane, divided by the crown radii, plus the view direction times the remaining depth. Crown centre height and radii per variant come from the foliage surface bounds of the authored LOD2 and go in `variant_parameters`. The normal map keeps its detail on top. This is what makes a low sun light one side of the card crown, and noon light its top, as on the mesh.
3. **Tint and brightness calibration.** A calibration mode of `tree_billboard_match_gpu_test` renders each variant as LOD2b and as a card at 325 m under 3 sun elevations (noon, 30 degrees, 8 degrees) x 4 sun azimuths. Per variant it stores an RGB multiplier (the geometric mean of the mesh-to-card colour ratios over the 12 cases, clamped to 0.5 to 2.0) and an alpha-cutoff value that makes the card's covered area equal the mesh's. Stored in `resources/trees/prepared/lolipop_impostor_calibration.res`, made once on the 4070 and checked in; `CreateParameterTexture` multiplies row 0 by the RGB factor and uses the cutoff in row 1. T6-8's per-instance tint multiplies after it, once, in the same shader for cards and quads.
4. **Alpha mips.** The card atlas mips are rebuilt so that each level's coverage at the variant's cutoff equals the base level's. The atlas stays 256 x 256 (512 would add about 95 MB uncompressed over three arrays). Alpha anti-aliasing stays off: it needs MSAA, which no preset guarantees.
5. **Switch distances do not move.** With LOD2b the LOD2 band is cheap enough to keep 300 to 350 m; moving it out adds LOD2b trees, moving it in gives up mesh detail.
6. Tolerances for `tree_billboard_match_gpu_test`, per variant at 325 m: mean luminance of card against LOD2b within 10 % at noon and 15 % at sun 8 degrees; each mean colour channel within 12 %; covered area within 8 %. The test must fail with the calibration and the crown normal off. If no w in {0.5, 0.75, 1.0} meets both sun cases, that is TR-7's escalation.

#### F. Tree budget per preset (TR-2 item 7)

**F1. The budget is today's cost.** At R, measured: all trees 4.22 ms at `forest_edge_eye` noon and 7.75 ms at `forest_interior_eye` noon (of which tree shadows 1.48 and 2.31 ms), drawing 10,086 and 11,102 trees. The Medium budget is those two numbers: the new renderer draws every placed tree inside the far distance for no more tree GPU time than today. For each other preset the budget is the same rule at that preset's own values: no more tree GPU time than the old renderer in the same session (`--tree-set UseTreeRenderLods:false,UseFarTreeQuads:false` plus the old caps).

**F2. Estimate at R after TR-5 to TR-9** (to be replaced by TR-8's and TR-9's measurements):

| Part | `forest_edge_eye` today | after | `forest_interior_eye` today | after |
|---|---|---|---|---|
| LOD0 | 0.09 | 0.09 | 0.52 | 0.52 |
| LOD2 (slots 1 + 2) | 2.32 (1,547 trees) | 1.3 (250 authored + 1,300 LOD2b) | 3.73 (2,152 trees) | 1.85 |
| crossed cards | 0.24 (5,200) | 0.35 (12,300) | 0.31 (5,200) | 0.65 (12,300) |
| far tier | 0.00 (3,327 cards) | 0.3 to 0.9 (207,000 quads) | 0.20 (3,715 cards) | 0.6 to 1.8 (207,000 quads) |
| tree shadows | 1.48 | 1.48 | 2.31 | 2.31 |
| all trees | 4.22 (10,086 trees) | 3.5 to 4.1 (about 220,000) | 7.75 (11,102 trees) | 5.9 to 7.1 (about 220,000) |

**F3. Choices.**
- Root cap, far distance, quad start and caps per preset: the table in B6 and the starts in D1.
- The quad tier starts at 900 m instead of 1,920 m because a card tree costs about 18 times a quad tree and the difference is not visible at 16 pixels.
- Past 5.5 km a 20 m tree is under 2.6 pixels at 1080p; the far canopy tint (T6-10) is the right picture there. Only Ultra Video and Cinematic try 8 km, and only if TR-9's steps show streaming and memory hold.
- Placement density does not rise (D1: placement and fingerprint are fixed). With every gate at 1.0 the renderer draws every placed tree; that is the ceiling of this phase.
- If a preset cannot hold the budget, TR-9 shortens that preset's far distance first (decision D4).

**F4. At the user's preferences** (LOD0 to 200 m, LOD2 to 900 m; estimate, High caps): the LOD2 band holds about 13,300 trees against 640 + 4,096. B1 moves the card hand-off in to about 530 m, and every tree is drawn (today 31 to 47 % of the trees at 200 to 550 m are missing). LOD2 falls from 4.8 ms to about 3.0 ms; LOD0 stays at 1.8 to 2.5 ms (D5). TR-8 and TR-9 report this row.

#### G. Gates (TR-2 item 8)

**TR-8 (the renderer, tree counts as today: old caps and gates, `--tree-set`), at R:**
1. Slots 1 + 2 GPU time down by at least 35 % at `forest_edge_eye` and `forest_interior_eye` (from 2.32 and 3.73 ms).
2. All-trees delta down by at least 20 % at both poses (to 3.4 ms and 6.2 ms or less).
3. Submitted triangles in slots 1 + 2 (probe readback) down by at least 50 %.
4. No new visible pop:
   - `tree_billboard_match_gpu_test` passes (tolerances in E6);
   - the F1 switch strip (250 to 400 m) and a new strip across the LOD2 to LOD2b hand-off (110, 130, 140, 150, 170 m), noon and sun 8 degrees, signed off by the user;
   - a Movie Maker dolly from 100 to 400 m at `forest_edge_eye`: `flicker` shows no single-frame luma spike above 1.25 x the largest spike of the same dolly before the change.
5. Shadow coverage unchanged inside the preset's tree shadow distance (240 m at Medium; the "100 to 480 m" of item 8 was written before TR-1): `vegetation_shadow_gpu_test` and `tree_lighting_shadow_gpu_test` pass unchanged, and a `compare` of the shadow debug view at `forest_clearing_eye`, noon and sun 8 degrees, shows a noise-level heatmap.
6. Reported, not gated: the same table with the user's preferences.

**TR-9 (the count), at R and per preset:**
1. Drawn trees are at least 95 % of the records inside the far distance at the three forest poses (today about 5 %).
2. In every 100 m distance bin with at least 50 records, drawn / records is at least 0.95. This is the test for the empty ring and the patchy band.
3. All-trees delta at or under the budget of F1.
4. Tree buffers at most 64 MB; settle time at most 3 s; tree streaming inside its per-frame budget in the travel benchmark (L2-3c).
5. "Trees output selection" p95 up by at most 0.15 ms (B2).

#### H. TR-10 against TR-1's shadow numbers

- Tree shadows cost the same at R as with the user's fades at noon (2.31 against 2.30 ms at `forest_interior_eye`, 1.48 against 1.53 ms at `forest_edge_eye`) although the caster triangles fall about four times. At `forest_edge_eye` the cost follows the number of caster draws (178 draws 1.53 ms at noon, 418 draws 3.66 ms at sun 8 degrees). So this cost is per caster batch and cascade, not per triangle: **decimation will not lower it; TR-10 is the only order that does.** This is derived from TR-1's tables; TR-10's first step measures it.
- Trees past the tree shadow distance cast no shadow, and TR-9 only adds trees past 300 m. So TR-9 does not raise shadow cost, and TR-10 is not a precondition of TR-9.
- TR-10's canopy term comes from tree records. It matches the drawn forest only when every record is drawn, which is the state after TR-9.
- **Order: TR-10's design and prototype run now, alongside TR-4 to TR-7. Its prototype pictures and its implementation come after TR-9.** Its saving (0.7 to 2.3 ms at R) is then headroom on top of the F1 budget.
- Correction to TR-10's context: the tree sun-shadow distance is a preset value (160 / 240 / 360 / 480 / 700 m, `PresetTrees`), limited by the near sun map; TR-1 measured with 240 m (Medium). It is not 480 m at every preset.

### TR-3. Built-in alpha scissor in both tree shaders

ROLE: sol_implementer
SIZE: S
DEPENDS ON: none

OBJECTIVE
Both tree shaders cut leaves with `ALPHA_SCISSOR_THRESHOLD` instead of a manual `discard`, so Godot's alpha anti-aliasing can be used on them.

FILES/SYMBOLS
`materials/imported_tree.gdshader` (line 104, `use_alpha_cutout`, `alpha_cutoff`), `materials/imported_tree_impostor.gdshader` (line 108, flags.r and `tree_parameters.a`).

REQUIRED CHANGES
1. Set `ALPHA = albedo.a` and `ALPHA_SCISSOR_THRESHOLD = cutoff` where cutout is on. Leave bark (cutout off) opaque, with no ALPHA write.
2. Keep `apply_tree_dither`'s `discard` as it is: it is the LOD dissolve, not the leaf cutout.
3. Do not enable alpha anti-aliasing yet; TR-7 decides that.

DO-NOT-CHANGE CONSTRAINTS
The pixels must be identical at a fixed pose: run `compare` against a capture taken before editing. Both shaders stay in the opaque pipeline: no `blend_*`, no `depth_draw_always` change. The shadow and depth prepass must still cut out leaves.

VALIDATION STEPS
`tools/run_tests.ps1 -Tests imported_tree_gpu_regression,tree_lod_dither_gpu_test,tree_shared_impostor_integration,tree_lighting_shadow_gpu_test -Label tr3 -DryRun`, then the run. Before and after `compare` at `forest_edge_eye` noon. SSIM should sit at the cloud-noise level (about 0.997); judge the heatmap. Check that the check can fail: set the threshold to 0.9 temporarily and the heatmap must light up.

ESCALATION CONDITIONS
The pixels differ beyond noise, or shadows lose their leaf cutouts.

STOP RULE
1 watchdog period, 3 build-plus-test attempts.

### TR-4. Offline decimation and contact sheets

ROLE: sol_implementer
SIZE: M
DEPENDS ON: TR-2

OBJECTIVE
A prepare step that writes the decimated LOD meshes TR-2 specifies into their own resource, plus a contact sheet per variant comparing source and decimated meshes at their switch distances.

FILES/SYMBOLS
The tree prepare path (`prepare_tree_assets`, `PreparedImportedTreeCatalog.cs`, `PreparedImportedTreeCatalogValidation.cs`), and the new resource and loader named in TR-2.

REQUIRED CHANGES (rewritten 2026-11-14 after TR-1 and the TR-2 design; this replaces the original list)
1. Decimate the **authored LOD2** of each variant into LOD2b (design C1 to C3). LOD0 and the authored LOD2 itself are not changed. Implement C3's bark and foliage methods and coverage rule. The step must be deterministic: the same input gives byte-identical output. Report which simplification binding was used.
2. Build three candidate levels per variant: A (1.6 x the class target of C2), B (the target) and C (0.6 x the target). Variants at or under 1.25 x their target are stored unchanged and marked "source".
3. Keep the wind attachment data valid for the new vertices (`ImportedTreeWindAttachments.cs`), and keep the two-surface layout (bark, then foliage).
4. Write `resources/trees/prepared/lolipop_render_lods.res` and its manifest with level B, plus the tool version and the source hash (C4), and add the loader `ImportedTreeRenderLodCatalog`. Levels A and C are written under `.godot/` for the sheets only.
5. Write a triangle table: variant, size class, authored LOD2 triangles, triangles at A, B and C (bark and foliage), coverage error (mean and worst view), and the tree-weighted mean per level.
6. Contact sheets per variant: authored LOD2 beside A, B and C at 150 m and at 325 m, at noon and at sun 8 degrees (the distances where LOD2b is used).

DO-NOT-CHANGE CONSTRAINTS
`lolipop_catalog.res` must not change; `imported_tree_prepared_catalog_test` passes unchanged. Nothing in the runtime uses the new resource yet.

VALIDATION STEPS (rewritten 2026-11-14)
A new headless test `tree_lod_decimation_test`:
- every variant at level B is at or under its class target of C2, with bark at most 40 % and at least 24 triangles;
- the coverage error is within C3's rule (5 % mean, 8 % worst view) and the AABB within the source AABB grown by 2 %;
- the step is deterministic (run twice and compare the bytes);
- attachments are in range;
- the stored source hash matches the catalog, and the loader falls back to the authored LOD2 on a stale hash;
- it must fail when fed a target of 0 triangles.

Add a manifest entry for it, then run `tools/run_tests.ps1 -Tests tree_lod_decimation_test,imported_tree_prepared_catalog_test,world_generation_fingerprint_test -Label tr4`. Send the user the contact sheets and the triangle table (decision D2: the level, per class if the user wants).

ESCALATION CONDITIONS
Foliage cannot reach the target within the coverage rule. Report the best value reached per variant.

STOP RULE
2 watchdog periods, 4 build-plus-test attempts.

### TR-5. Wire the decimated LODs into the compact profile

ROLE: sol_implementer
SIZE: M
DEPENDS ON: TR-4, user D1 and D2

OBJECTIVE
The compact profile draws the decimated meshes in the bands TR-2 chose, with dithered hand-offs between all of them.

FILES/SYMBOLS
`RingBiomeDecorationLayer.RenderPolicy.cs` (`GpuTreeLodInterval`, `CompactTreeFadeBounds`, `UsesTreeDitherData`, `IsGpuTreeLodEnabled`), `RingBiomeDecorationLayer.Gpu.cs` (`GpuTreeLodEdges`), `RingBiomeDecorationLayer.cs:221-224`, the shadow LOD mapping in `RingBiomeDecorationLayer.GpuShadows.cs`, and `GameplayVegetationDefaults.cs` plus the drawer for any new distance.

REQUIRED CHANGES (rewritten 2026-11-14 after TR-1 and the TR-2 design; this replaces the original list)
1. Slot mapping of design C1 in the compact profile: slot 1 draws the authored LOD2 (with the simplified material slot 2 uses today), slot 2 draws LOD2b from `ImportedTreeRenderLodCatalog` at the level the user chose (D2). `IsGpuTreeLodEnabled` enables slot 1; `UsesTreeDitherData` covers slots 0 to 3 (slot 4 follows in TR-6).
2. Band edges and dither ranges: slot 1 from the near fade to the new mid fade end, with a cross-dither to slot 2 over `TreeMidDetailFadeStartMeters` to `TreeMidDetailFadeEndMeters` (preset values of C1). Slot 2 from there to the billboard fade, as slot 2 today. Every hand-off dithers; no hard cut is left inside the visible range.
3. Slot 1 uses per-tile batches; slot 2 keeps the merged mid batches. Caps for this order: slot 1 = `MaximumLod2Trees`, slot 2 = today's compact sum. The preset caps of B6 arrive in TR-9.
4. Shadow proxies do not change: slot 0 keeps the authored LOD2 proxy, slots 1 and 2 use the authored LOD3 card proxy (`GpuShadows.cs:97`). No shadow proxy uses a decimated mesh.
5. Add "Reduced-detail fade start / end (m)" to the grave-key menu and to `PresetTrees` with the `live-tuning-setting` skill (B7). Add the layer export `UseTreeRenderLods` (default on, no menu row) so the harness can draw the old mapping with `--tree-set`.
6. The rank gates and the root cap do not change in this order.

DO-NOT-CHANGE CONSTRAINTS
No visibility ranges and no fade modes on tree nodes (F2). Custom AABBs stay geometry-sized (F3). The CPU fallback path keeps its current behaviour.

VALIDATION STEPS (rewritten 2026-11-14)
- `tools/run_tests.ps1 -Tests tree_lod_compute_regression,tree_lod_dither_gpu_test,tree_render_policy_regression,imported_tree_gpu_regression,gpu_tree_integration_test,vegetation_shadow_gpu_test,tree_lighting_shadow_gpu_test,vegetation_settings_smoke_test -Label tr5 -DryRun`, then the run.
- Extend `tree_lod_dither_gpu_test` with slot 1 and the mid hand-off: coverage must be continuous across every hand-off. It must fail when the mid fade of one slot is shifted by 10 m.
- Rerun the TR-1 slot cases at R (`LANDSCAPE_HARNESS_ARGS = '--tree-lod=slots --tree-set=...'` with the F1 fades). Report slots 1 and 2 against TR-1's slot 2 (gates 1 and 3 of design G for TR-8: at least 35 % less time, at least 50 % fewer triangles), and the tree-shadow case (unchanged within the noise floor).
- Rerun the F1 switch strip, add a strip at 110, 130, 140, 150 and 170 m across the new hand-off, and send both to the user.

ESCALATION CONDITIONS
The new slot needs a change to the compute shader's slot count or ABI (the design expects none: five slots stay). Report it before editing the GLSL.

STOP RULE
2 watchdog periods, 4 build-plus-test attempts.

### TR-6. Camera-facing far billboard tier

ROLE: sol_implementer
SIZE: M
DEPENDS ON: TR-2, user D1

OBJECTIVE
The far tier draws one quad per tree that turns about the surface-up axis, in the band and with the steep-angle rule TR-2 set.

FILES/SYMBOLS
`ImportedTreeImpostorCatalog.cs` (the shared mesh), `materials/imported_tree_impostor.gdshader` (vertex), and the band edges in `RenderPolicy.cs` and `Gpu.cs`.

REQUIRED CHANGES (rewritten 2026-11-14 after TR-1 and the TR-2 design; this replaces the original list)
TR-1 showed that slots 3 and 4 already draw the same shared impostor (36 padded triangles per tree). This order gives slot 4 its own mesh.
1. A second shared mesh in `ImportedTreeImpostorCatalog`: one quad (two triangles) per tree, with a duplicated material that sets `far_quad_mode` on the same shader. Per variant, new `variant_parameters` rows hold the UV rectangle and the position bounds of the largest plane of the LOD3 mesh, and the variant's width and height (design D1). No new texture.
2. Vertex shader in quad mode (design D2, D3): corners from `VERTEX_ID`; the quad is centred on the tree's mid-height, perpendicular to the direction from the tree to the camera position, with its horizontal axis `cross(up, direction)` and its height scaled by sqrt(cos^2 e + (W/H)^2 sin^2 e); the ring-axis fallback directly overhead; U mirrored by a seed bit; no wind. Keep the haze, cloud shadow, height shadow, wetness and storm includes exactly as they are.
3. Band: slot 4 runs from `TreeQuadStartMeters` (preset: 600 / 900 / 900 / 900 / 1,200 m) to the far distance; slot 3 ends at the start + 150 m; cross-dither over those 150 m; slot 4 carries dither data. The quad dithers out over the last 10 % of the far distance. `MinimumLod5DistanceMeters` no longer sets this edge in the compact profile.
4. Steep-angle rule for the card band (design D3): in `ring_gpu_vegetation_indirect.glsl`, a tree whose camera elevation is above 30 degrees is accepted by slot 4 instead of slot 3, with the coverage word cross-dithering from 20 to 30 degrees; two new parameter words; the CPU prune counts keep both outputs for groups in the card band. **Report before editing the GLSL**; if the change is refused, implement the altitude fallback of D3 and say so.
5. Add "Far billboard start (m)" to the grave-key menu and to the presets (B7). Add the layer export `UseFarTreeQuads` (default on, no menu row) for the harness.
6. Caps and gates do not change in this order (TR-9).

DO-NOT-CHANGE CONSTRAINTS
Root positions, variant selection, the buffer bindings and the slot count do not change. The far canopy tint boundary (T6-10) still lines up with the far distance.

VALIDATION STEPS (rewritten 2026-11-14)
- `tools/run_tests.ps1 -Tests tree_shared_impostor_integration,imported_tree_impostor_catalog_smoke,tree_atmosphere_gpu_test,tree_lod_dither_gpu_test,tree_lod_compute_regression -Label tr6 -DryRun`, then the run.
- Extend `tree_lod_dither_gpu_test`: coverage is continuous across the card-to-quad hand-off in distance and across 20 to 30 degrees of elevation. Extend `imported_tree_impostor_catalog_smoke`: the quad's bounds and UV rectangle match the chosen LOD3 plane for all 30 variants.
- Cost: the TR-1 cap run (`--tree-lod=cap`, configuration `262144+far`) at R, before and after. Report slot 4 in microseconds per tree for cards and for quads. TR-9 uses this number in place of the design's estimate (F2).
- Captures: ground at 2 km and 5 km; 300 m and 2 km altitude looking down at 30 and 70 degrees; the TR-1 mountainside pose (`switch_strip/2026-11-14_17-13-42`, pine at 350 and 400 m). Send them to the user (decision D6).

ESCALATION CONDITIONS
The steep-angle rule shows a visible pop or sliver that TR-2 did not foresee.

STOP RULE
2 watchdog periods, 4 build-plus-test attempts.

### TR-7. Billboard match: crown normals, tint, alpha coverage

ROLE: sol_implementer
SIZE: M
DEPENDS ON: TR-2, TR-3, user D1

OBJECTIVE
The 300 to 350 m LOD2-to-cards step is hard to see in the switch strips at noon and at low sun.

FILES/SYMBOLS
`materials/imported_tree_impostor.gdshader`, `ImportedTreeImpostorCatalog.cs` (`variant_parameters`, atlas build), and the card atlas mip generation.

REQUIRED CHANGES (rewritten 2026-11-14 after TR-1 and the TR-2 design; this replaces the original list)
TR-1 showed that the card look is one shader for everything from the LOD2 hand-off to the far distance (slot 3, and slot 4 in either mode), and that the step is count, brightness and crown openness together. The count part is TR-9's (gates at 1.0); this order does the other two. The mesh side of the match is LOD2b when TR-5 has landed, else the authored LOD2.
1. Crown normal (design E2) in `imported_tree_impostor.gdshader`, for the cards and the quad mode: `normalize(mix(N_card, N_crown, w))`, w = 0.75 in the crown and 0 below the crown base; crown centre height and radii per variant from the authored LOD2 foliage bounds, in `variant_parameters`.
2. Calibration (design E3): a calibration mode of the new test renders each variant as mesh and as card at 325 m under 3 sun elevations x 4 azimuths and writes `resources/trees/prepared/lolipop_impostor_calibration.res` (RGB multiplier clamped to 0.5 to 2.0, and the alpha cutoff that equalises covered area). `CreateParameterTexture` applies both. The resource is made on the 4070 and checked in.
3. Coverage-preserving alpha mips for the card atlas (design E4). The atlas stays 256 x 256. Alpha anti-aliasing stays off.
4. Coordinate with T6-8 (per-instance tint): the calibration multiplies row 0; T6-8's tint multiplies after it, once, for cards and quads.
5. The switch distances do not move (design E5).

DO-NOT-CHANGE CONSTRAINTS
LOD0, LOD2 and LOD2b shading does not change.

VALIDATION STEPS (rewritten 2026-11-14)
A new GPU test `tree_billboard_match_gpu_test`, per variant at 325 m, at noon and at sun 8 degrees (design E6):
- mean luminance of the card within 10 % of the mesh at noon and 15 % at sun 8 degrees;
- each mean colour channel within 12 %;
- covered area within 8 %;
- it must fail with the calibration and the crown normal off.

Add a manifest entry. Then run `tools/run_tests.ps1 -Tests tree_billboard_match_gpu_test,imported_tree_impostor_catalog_smoke,tree_shared_impostor_integration,tree_lighting_shadow_gpu_test,tree_atmosphere_gpu_test -Label tr7`. Rerun the TR-1 switch strip in both configurations (`--strip-configs=user,f1`); send the before and after sheets. Note in the report that isolated strip trees keep their card today (rank under 0.33), so the strip shows the look, not the count.

ESCALATION CONDITIONS
Matching at noon breaks the low-sun match, or the reverse.

STOP RULE
2 watchdog periods, 4 build-plus-test attempts.

### TR-8. Review pack: before and after cost and look

ROLE: sol_implementer
SIZE: S
DEPENDS ON: TR-5, TR-6, TR-7

OBJECTIVE
The T0 cost table rerun with the new renderer, judged against TR-2's gates, and the user's sign-off on the look.

Note (2026-11-14, TR-2 design G): the gates are judged at the reference configuration R (TR-1 section 6: the shipped fades forced with `--tree-set`), with the tree counts as today (old caps and gates), against TR-1's third run. The count is raised afterwards in TR-9. Use the TR-1 slot cases (`--tree-lod=slots`), and report the same table once with the user's preferences.

REQUIRED CHANGES
1. `landscape_cost_baseline` at the T0 poses and machine. Report a before and after table: root GPU, tree hide delta, primitives and draws.
2. The switch strips and the far and altitude captures from TR-5 to TR-7, as one contact sheet.

VALIDATION STEPS
`tools/run_tests.ps1 -Tests landscape_cost_baseline,landscape_lod_switch_capture -Label tr8`, then `-Suite gate` at the commit point. The primary agent runs that.

STOP RULE
1 watchdog period.

### TR-9a. Cap fall-through: budget-derived band edges

Added 2026-11-14 by the TR-2 design (B1 to B3). It is TR-2 item 7's "fix that order first".

ROLE: sol_implementer
SIZE: M
DEPENDS ON: TR-5, TR-6. It can run alongside TR-7. Merge it after TR-8's cost run, so that TR-8 measures the renderer with the hand-offs where TR-1 had them.

OBJECTIVE
A tree that a slot's cap cannot hold is drawn by the next cheaper slot instead of not at all. The hand-off distance moves inward; no tree goes missing.

CONTEXT / ROOT CAUSE
TR-1 section 4: the slot caps hand their budget out batch by batch, so drops are patchy, and a dropped tree has no other slot. With the user's 900 m LOD2 band, half to two thirds of the forest at 200 to 550 m is missing.

FILES/SYMBOLS
`RingBiomeDecorationLayer.Gpu.cs` (`TryGpuTreeGroupPruneCounts` :719, `PruneGpuTreeBudgetGuaranteedPrefix` :751, `GpuTreeLodEdges`, `ServiceGpuTreeOutputs`, `GpuTreeSelectionCanReuse`), `RingBiomeDecorationLayer.RenderPolicy.cs` (`GpuTreeLodInterval`), `tools/RingBiomeDecorationLayer.TreeBudgetProbe.cs` (read-only probe).

REQUIRED CHANGES
1. In the prune-count loop, fill a 64-bin distance histogram per slot for slots 0 to 3 (ordinary roots that pass the slot's gate).
2. Per slot, the effective far edge is the upper edge of the last bin at which the running count is at or under 95 % of the cap; otherwise the configured edge. Apply it to the slot's far edge and fade-out, and to the next slot's near edge and fade-in, keeping the configured fade width. An edge moves only when it changes by more than one bin.
3. The effective edges are part of the selection signature, so a change rebuilds the selection. The shader's `used[slot]` backstop stays.
4. Protected roots keep first claim on slot 0. Fixtures without a canonical focus, and `PruneEmptyGpuTreeBatches` off, keep today's behaviour.
5. No change to `ring_gpu_vegetation_indirect.glsl`.

DO-NOT-CHANGE CONSTRAINTS
At R with today's caps the drawn set of slots 0 to 2 is unchanged (no cap binds there). Slot 3's cap of 5,200 does bind at R until TR-9 raises it, so the card-to-quad hand-off moves inward there; that is expected. The non-compact profile and the CPU fallback are unchanged. No menu row.

VALIDATION STEPS
- Extend `tree_lod_compute_regression` (or `tree_render_policy_regression`): a fixture with more roots in a band than the cap. Every root inside the far distance is drawn by exactly one slot outside the cross-dithers; the edge moves inward; the test must fail with the effective edge disabled.
- `tools/run_tests.ps1 -Tests tree_lod_compute_regression,tree_render_policy_regression,tree_lod_dither_gpu_test,gpu_tree_integration_test -Label tr9a -DryRun`, then the run.
- The TR-1 probe with the user's preferences at `forest_edge_eye`: in every 100 m bin from 0 to 900 m, drawn / records is at least 0.95 (TR-1: 31 % at 200 to 550 m). Report the effective edges.
- CPU telemetry "Trees output selection" while walking at `forest_interior_eye`: p95 up by at most 0.15 ms. If it is more, report; the GPU histogram of design B3 is the fallback and needs the lead.
- A Movie Maker walk of 200 m at the user's preferences with `flicker`: the moving hand-off shows no single-frame spike above 1.25 x the same walk before the change.

ESCALATION CONDITIONS
The effective edge oscillates while the camera stands still, or the CPU gate is missed.

STOP RULE
2 watchdog periods, 4 build-plus-test attempts.

### TR-9. Raise the tree count to the budget

ROLE: sol_implementer
SIZE: M
DEPENDS ON: TR-8, TR-9a

OBJECTIVE
Each preset draws as many trees as TR-2's tree budget allows, using the savings from TR-5 to TR-7.

FILES/SYMBOLS
- `GameplayVegetationDefaults.cs` (`DefaultMaximumGpuTreeRoots`, its 256 to 1,048,576 sanitize range at :122-126);
- `RingGraphicsQualityController.cs` (`PresetTrees`);
- `MaximumLod5DistanceMeters` and the far billboard band;
- `MaximumTreeBillboards` (`RingBiomeDecorationLayer.TreeStreaming.cs:856`, clamped to 65,536);
- the GPU buffer capacities in `RingGpuVegetationCompute.cs`.

REQUIRED CHANGES (rewritten 2026-11-14 after TR-1 and the TR-2 design; this replaces the original list)
TR-1 showed that the slot caps and rank gates bind, not the root cap (correction 4). The files above therefore also include `RingBiomeDecorationLayer.Gpu.cs` (`GpuTreeLodCaps` :972, the `gates` array in `ServiceGpuTreeOutputs` and `GpuFrameCache.cs:133`), `MaximumLod1Trees` to `MaximumLod4Trees` (`RingBiomeDecorationLayer.cs:96-99`) and `VegetationLodRankGate` (`TreeStreaming.cs:1478`, which stays as it is for the non-compact profile and the CPU fallback).
1. Rank gates (design B4): in the compact profile every slot's gate is 1.0; the quad slot's gate is the preset value `FarTreeDensity`, 1.0 at every preset, not in the menu.
2. Caps and roots per preset (design B6): the five slot caps, `MaximumGpuTreeRoots` and the far distance become preset values next to `PresetTrees`. The quad slot's cap is the root cap. Raise the clamps that sit below these numbers (`MaximumTreeBillboards` 65,536, `MaximumLod4Trees` 16,384, `MaximumLod3Trees` 8,192) and the matching buffer capacities.
3. Add "Far tree distance (m)" to the grave-key menu and the presets (B7). "Resident GPU tree roots" takes its default from the preset. Saved user values still win (sparse overrides).
4. Step up at `forest_edge_eye`, `forest_interior_eye`, `forest_clearing_eye` and `coast_eye`, at R, and record all-trees ms, each slot's ms, drawn trees, tree buffers, video memory, settle time and streaming CPU at each step:
   - a: gates 1.0 for slot 3 only (cards for every LOD2b tree);
   - b: plus roots 262,144 and gate 1.0 for slot 4 (every tree to 5.5 km);
   - c: (Ultra Video and Cinematic only) far distance 8,000 m with roots 524,288.
   Stop at the budget of design F1. If step b is over budget at a preset, shorten that preset's far distance in 500 m steps (decision D4); do not lower `FarTreeDensity` without the user.
5. Repeat step b at Low and High with their own values against the old renderer in the same session (`--tree-set UseTreeRenderLods:false,UseFarTreeQuads:false` and the old caps and gates).
6. Run the travel benchmark at the chosen settings: tree streaming must stay within its per-frame budget (`docs/load-time-plan.md` L2-3c).
7. Report the same table once with the user's preferences (design F4).

DO-NOT-CHANGE CONSTRAINTS
- Tree placement does not change. Only how many placed trees are drawn changes. Far density does not rise above the placed trees, and no tree is scaled up.
- The world fingerprint does not change.
- The non-compact profile and the CPU fallback keep their gates and caps.

VALIDATION STEPS (rewritten 2026-11-14)
- Before running, check the selection with `tools/run_tests.ps1 -Tests tree_streaming_contract_test,tree_gpu_preparation_smoke,gpu_tree_integration_test,tree_gpu_benchmark,tree_render_policy_regression,tree_lod_compute_regression,vegetation_settings_smoke_test,world_generation_fingerprint_test -Label tr9 -DryRun`, then run them. `TreeRenderingRegressionSmoke` still checks the static gates for the legacy path; add the compact gates to it.
- Gates (design G, TR-9), from the TR-1 probe and cap run at R:
  - drawn trees at least 95 % of the records inside the far distance at the three forest poses;
  - drawn / records at least 0.95 in every 100 m bin with at least 50 records (no empty ring, no patchy band);
  - all-trees delta at most 4.22 ms at `forest_edge_eye` noon and 7.75 ms at `forest_interior_eye` noon (Medium); at Low and High, at most the old renderer's delta in the same session;
  - tree buffers at most 64 MB, settle time at most 3 s, "Trees output selection" p95 up by at most 0.15 ms.
- Make a table of the steps.
- Send the user ground captures (`forest_edge_eye`, and a pose that looks across 1 to 5 km of forest) and altitude captures (300 m and 2 km) at the old and new counts, side by side.

ESCALATION CONDITIONS
Memory or streaming, not GPU time, sets the limit. Report which, and at what count.

STOP RULE
2 watchdog periods, 4 build-plus-test attempts.

### TR-10. Cheaper tree shadows past the near range: canopy in the height shadow

ROLE: sol_lead (design and prototype), then sol_implementer
SIZE: M
DEPENDS ON: TR-1 (the tree-shadow-off case). It can run alongside TR-4 to TR-7.

**Premise check against TR-1 (TR-2 design H, 2026-11-14). Read this with the text below, which is kept as written.**
- The premise holds, for a different reason than the objective gives. TR-9 only adds trees past 300 m, which cast no sun-map shadow, so a higher count does not raise shadow cost either way. What TR-10 buys is the shadow cost itself: 0.7 to 2.3 ms at noon and 1.9 ms at sun 8 degrees with the shipped fades (1.2 to 2.3 ms and 3.7 ms with the user's fades), the second largest tree cost. Decimation does not lower it: the cost follows the number of caster batches and cascades, not caster triangles (derived from TR-1 sections 1, 2 and 6; step 0 below measures it).
- Order: the design and the first prototype run now. **The prototype pictures for the user and the implementation come after TR-9**, because the canopy term is built from tree records and only matches the drawn forest once every record is drawn.
- Step 0 (added): with the TR-1 harness at R, measure the tree-shadow case at tree shadow distances 120, 240 and 360 m and with the LOD0 proxies and the card proxies hidden in turn, at `forest_edge_eye` noon and sun 8 degrees. This sets the hand-off distance of step 2 and says how much of the cost is caster batches.
- Correction to the context below: the tree sun-shadow distance is a preset value (160 / 240 / 360 / 480 / 700 m, `RingGraphicsQualityController.PresetTrees`) limited by the near sun map; it is not 480 m at every preset. TR-1 measured with 240 m (Medium).
- The canopy must be built from all tree records inside the far distance (gate 1.0, design B4), not from the roots a slot accepted.

OBJECTIVE
Trees cast real sun-map shadows only close to the camera. Beyond that, forests darken the ground through the existing shader-based shadow path, so a higher tree count (TR-9) does not raise shadow cost.

CONTEXT / ROOT CAUSE
Added 2026-11-13 after the user's second video ("Better performance and 8 km cloud shadows - Godot Open World", Gierki Dev). It moves shadows from the directional light into shaders through global uniforms. The shadow revamp already did this for terrain and clouds (`docs/shadow-revamp-plan.md` S2, S4, S5, S7):
- the near sun map covers 400 m with 2 cascades;
- the GPU heightfield shadow (`materials/ring_height_shadow.gdshaderinc`) and the cloud shadow maps are applied in the shared compose include, through `light()` and global uniforms.

Trees are the gap. They still render into the sun map out to the tree sun-shadow distance (480 m at every preset, per `docs/quality-presets-plan.md` line 450). Past it, forests cast nothing onto the terrain, while the trees themselves still receive shadows (per-instance terrain and cloud visibility, S7).

REQUIRED CHANGES
1. Design: add a canopy term to the S4 heightfield shadow producer. For each heightfield texel, add the forest canopy height times canopy cover, from the tree records or the forest sampler, with partial transmittance for gaps. The design states:
   - whether the term goes into the march height or into a separate canopy-occlusion channel;
   - the cost of a rebuild when tree tiles stream;
   - that the canopy matches the trees drawn after TR-9.
2. Choose the hand-off distance where real tree shadows stop and the canopy term takes over, with a crossfade like S5's macro take-over. Make it a preset value; the existing tree shadow distance setting becomes that hand-off.
3. Prototype at `forest_edge_eye` and `forest_clearing_eye`, at noon and with the sun at 8 degrees.
4. Implement after the user approves the prototype pictures.

DO-NOT-CHANGE CONSTRAINTS
- Inside the hand-off distance, near shadows stay as they are.
- No new per-subsystem toggles: the canopy term follows the height shadow setting.
- Points of interest and terrain receive it automatically through the compose include.

VALIDATION STEPS
- Run the TR-1 tree-shadow-off case before and after.
- Run `tools/run_tests.ps1 -Tests vegetation_shadow_gpu_test,tree_lighting_shadow_gpu_test,terrain_shadow_lod_gameplay_test -Label tr10 -DryRun`, then the run.
- Send the user the prototype pictures, and a strip across the hand-off distance.

ESCALATION CONDITIONS
The canopy term makes forests read as solid blocks at low sun, or a streaming rebuild hitches by more than 2 ms.

STOP RULE
2 watchdog periods for the design and prototype.

## Phase acceptance

1. TR-1 to TR-10 (with TR-9a) are done, and TR-2's gates (design G) are met on the 4070. The A1000 run follows, as for the other phases (README decision 11).
2. Every test in F9 passes, plus `tree_lod_decimation_test` and `tree_billboard_match_gpu_test`. `world_generation_fingerprint_test` passes unchanged.
3. The user has signed off the TR-8 sheets.
