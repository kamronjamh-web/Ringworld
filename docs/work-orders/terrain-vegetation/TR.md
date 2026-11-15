# TR. Tree rendering: LOD triangles, far billboards, billboard match: work orders

Parent: no plan section yet. This phase comes from the 2026-11-13 conversation with the user (tree cost, the LOD2-to-billboard switch, and the "tree rendering in Godot" video). It sits beside T6 and touches the same renderer, so read T6 "Phase facts" first. Prerequisite reading: `docs/landscape-audit-t0-spec.md` lines 1115-1220 (T0 cost baseline), `docs/terrain-vegetation-revamp-plan.md` section 7.1 (tree pack audit).
Status: in progress. **TR-3 done** (2026-11-14): leaf cutout uses ALPHA_SCISSOR_THRESHOLD in both tree shaders; after vs before SSIM 0.9896 against a before-vs-before floor of 0.98-0.99 at forest_edge_eye noon (identical heatmaps), the 0.9 fail check 0.823; the four TR-3 tests pass. The harness's go_pose now re-issues a drifted pose (user mouse look reached the captured window) and records `pose_reached`. TR-1 running.
Depends on: nothing. It can run alongside T1-T5. Coordinate with T6-8 (tint), T6-10 (far canopy) and T6-11 (LOD tail), which touch the same shaders.
Freeze: none. This is a renderer change, not world content (see decision D1).

**Goal (the user, 2026-11-13): draw as many trees as the game allows without tanking performance.** Triangle savings (decimation) and the camera-facing far billboard serve that goal. They free the budget that a higher tree count then spends. The camera-facing billboard is required, not optional: one quad per tree is what makes a large far tree count affordable. TR-2 sizes the tree count against a frame budget; TR-9 raises the count.

## Decisions for the user

| # | Blocks | Question | Recommendation |
|---|---|---|---|
| D1 | TR-5 to TR-7 | These change how every world looks, generation 1 included, the same way the grass Cards default did. Is that allowed, or must they be gated by generation? | Allow it. Trees are placed the same way; only their meshes and shading change. The world fingerprint must not move. |
| D2 | TR-5 | How far may decimation go? It is judged by the TR-4 contact sheets at the distance each LOD is used. | Choose from the contact sheets. Gates in TR-2. |

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
| TR-9 raise the tree count to the budget | sol_implementer | M | TR-8 | none |
| TR-10 tree shadows past the near range: canopy in the height shadow | sol_lead, then sol_implementer | M | TR-1 | TR-4 to TR-7 |

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

REQUIRED CHANGES
1. Implement TR-2's bark and foliage methods and coverage rule. The step must be deterministic: the same input gives byte-identical output.
2. Keep the wind attachment data valid for the new vertices (`ImportedTreeWindAttachments.cs`), and keep the two-surface layout (bark, then foliage).
3. Write a triangle table: variant, size class, source triangles, decimated triangles, coverage error.
4. Contact sheets: each variant rendered source next to decimated at the distance where it is used, at noon and at sun 8 degrees.

DO-NOT-CHANGE CONSTRAINTS
`lolipop_catalog.res` must not change; `imported_tree_prepared_catalog_test` passes unchanged. Nothing in the runtime uses the new resource yet.

VALIDATION STEPS
A new headless test `tree_lod_decimation_test`:
- triangle targets are met;
- the coverage error is within the rule;
- the step is deterministic (run twice and compare the bytes);
- attachments are in range;
- it must fail when fed a target of 0 triangles.

Add a manifest entry for it, then run `tools/run_tests.ps1 -Tests tree_lod_decimation_test,imported_tree_prepared_catalog_test -Label tr4`. Send the user the contact sheets and the triangle table (decision D2).

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

REQUIRED CHANGES
1. Load the decimated resource and feed it to the slots TR-2 named.
2. Add the new band edges and dither ranges. Every hand-off dithers; no hard cut is left inside the visible range.
3. Point the shadow proxies at the decimated meshes only where TR-2 says so.
4. Add the new distances to the grave-key menu with the live-tuning-setting conventions and presets.

DO-NOT-CHANGE CONSTRAINTS
No visibility ranges and no fade modes on tree nodes (F2). Custom AABBs stay geometry-sized (F3). The CPU fallback path keeps its current behaviour.

VALIDATION STEPS
`tools/run_tests.ps1 -Tests tree_lod_compute_regression,tree_lod_dither_gpu_test,tree_render_policy_regression,imported_tree_gpu_regression,gpu_tree_integration_test,vegetation_shadow_gpu_test,vegetation_settings_smoke_test -Label tr5 -DryRun`, then the run. Extend `tree_lod_dither_gpu_test` with the new band: coverage must be continuous across every hand-off. Rerun the TR-1 switch strip and send it to the user.

ESCALATION CONDITIONS
The new slot needs a change to the compute shader's slot count or ABI. Report it before editing the GLSL.

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

REQUIRED CHANGES
1. A single-quad far mesh per variant, using the UVs of one LOD3 card.
2. Vertex orientation: turn about the instance's surface-up axis toward the camera, applying TR-2's steep-angle rule. Keep the haze, cloud shadow, height shadow, wetness and storm includes exactly as they are.
3. A dithered hand-off from the crossed-card tier.
4. Add the start distance to the grave-key menu if TR-2 made it a tunable.

DO-NOT-CHANGE CONSTRAINTS
Root positions, variant selection and the culling ABI do not change. The far canopy tint boundary (T6-10) still lines up with the last billboard.

VALIDATION STEPS
`tools/run_tests.ps1 -Tests tree_shared_impostor_integration,imported_tree_impostor_catalog_smoke,tree_atmosphere_gpu_test,tree_lod_dither_gpu_test -Label tr6 -DryRun`, then the run. Captures at 2 km and 5 km from the ground, and from 300 m and 2 km altitude looking down at 30 and 70 degrees. Send them to the user.

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

REQUIRED CHANGES
1. Crown normals as TR-2 specified.
2. Per-variant tint and brightness calibration: a measuring step plus stored factors.
3. Coverage-preserving alpha mips for the card atlas. Turn on alpha anti-aliasing only if TR-2 chose it.
4. Coordinate with T6-8 (per-instance tint) so the tint is applied once.

DO-NOT-CHANGE CONSTRAINTS
LOD0 and LOD2 shading does not change.

VALIDATION STEPS
A new GPU test `tree_billboard_match_gpu_test`: the mean luminance and coverage of each variant's LOD2 and card renders match within TR-2's tolerance at the switch distance, at noon and at sun 8 degrees. It must fail with the calibration off. Add a manifest entry. Then run `tools/run_tests.ps1 -Tests tree_billboard_match_gpu_test,imported_tree_impostor_catalog_smoke,tree_lighting_shadow_gpu_test -Label tr7`. Rerun the TR-1 switch strip; send the before and after sheets.

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

REQUIRED CHANGES
1. `landscape_cost_baseline` at the T0 poses and machine. Report a before and after table: root GPU, tree hide delta, primitives and draws.
2. The switch strips and the far and altitude captures from TR-5 to TR-7, as one contact sheet.

VALIDATION STEPS
`tools/run_tests.ps1 -Tests landscape_cost_baseline,landscape_lod_switch_capture -Label tr8`, then `-Suite gate` at the commit point. The primary agent runs that.

STOP RULE
1 watchdog period.

### TR-9. Raise the tree count to the budget

ROLE: sol_implementer
SIZE: M
DEPENDS ON: TR-8

OBJECTIVE
Each preset draws as many trees as TR-2's tree budget allows, using the savings from TR-5 to TR-7.

FILES/SYMBOLS
- `GameplayVegetationDefaults.cs` (`DefaultMaximumGpuTreeRoots`, its 256 to 1,048,576 sanitize range at :122-126);
- `RingGraphicsQualityController.cs` (`PresetTrees`);
- `MaximumLod5DistanceMeters` and the far billboard band;
- `MaximumTreeBillboards` (`RingBiomeDecorationLayer.TreeStreaming.cs:856`, clamped to 65,536);
- the GPU buffer capacities in `RingGpuVegetationCompute.cs`.

REQUIRED CHANGES
1. Apply TR-2's per-preset root cap and far-tier extent.
2. Lift any other hard limit that sits at 65,536 (`MaximumTreeBillboards`, buffer capacities), within TR-2's numbers.
3. Step the cap up at `forest_edge_eye`, `forest_interior_eye` and `coast_eye` (for example 65k, 131k, 262k and 524k roots). Record tree GPU ms, frame ms, VRAM and streaming CPU at each step. Stop at the budget.
4. Run the travel benchmark at the chosen setting: tree streaming must stay within its per-frame budget (`docs/load-time-plan.md` L2-3c).

DO-NOT-CHANGE CONSTRAINTS
- Tree placement does not change. Only how many placed trees are drawn changes, unless TR-2 and the user agreed to raise far density.
- The world fingerprint does not change.

VALIDATION STEPS
- Before running, check the selection with `tools/run_tests.ps1 -Tests tree_streaming_contract_test,tree_gpu_preparation_smoke,gpu_tree_integration_test,tree_gpu_benchmark,vegetation_settings_smoke_test -Label tr9 -DryRun`, then run them.
- Make a table of the cap steps.
- Send the user ground and altitude captures at the old and new counts, side by side.

ESCALATION CONDITIONS
Memory or streaming, not GPU time, sets the limit. Report which, and at what count.

STOP RULE
2 watchdog periods, 4 build-plus-test attempts.

### TR-10. Cheaper tree shadows past the near range: canopy in the height shadow

ROLE: sol_lead (design and prototype), then sol_implementer
SIZE: M
DEPENDS ON: TR-1 (the tree-shadow-off case). It can run alongside TR-4 to TR-7.

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

1. TR-1 to TR-10 are done, and TR-2's gates are met on the 4070. The A1000 run follows, as for the other phases (README decision 11).
2. Every test in F9 passes, plus `tree_lod_decimation_test` and `tree_billboard_match_gpu_test`. `world_generation_fingerprint_test` passes unchanged.
3. The user has signed off the TR-8 sheets.
