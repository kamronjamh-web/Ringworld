# TR. Tree rendering: LOD triangles, far billboards, billboard match: work orders

Parent: no plan section yet. This phase comes from the 2026-11-13 conversation with the user (tree cost, the LOD2-to-billboard switch, and the "tree rendering in Godot" video). It sits beside T6 and touches the same renderer, so read T6 "Phase facts" first. Prerequisite reading: `docs/landscape-audit-t0-spec.md` lines 1115-1220 (T0 cost baseline), `docs/terrain-vegetation-revamp-plan.md` section 7.1 (tree pack audit).
Status: in progress. **TR-2 design written 2026-11-14** (section "Design (TR-2, 2026-11-14)"; it rewrites TR-4 to TR-7 and TR-9, adds TR-9a and decisions D4 to D6). **TR-6 done** (2026-11-14): camera-facing far quad (slot 4, from 900 m at Medium) with the steep-angle hand-off; 5 compute words p[78..82]; partition test in tree_lod_compute_regression; about 0.002 us per quad (vs 0.03-0.06 us per far card tree), all-trees 14-21 % lower at the cap run; `UseFarTreeQuads` default **off** until TR-7 fixes its brightness. No 5 km ground capture (no clear site). **TR-3 done** (2026-11-14): leaf cutout uses ALPHA_SCISSOR_THRESHOLD in both tree shaders; after vs before SSIM 0.9896 against a before-vs-before floor of 0.98-0.99 at forest_edge_eye noon (identical heatmaps), the 0.9 fail check 0.823; the four TR-3 tests pass. The harness's go_pose now re-issues a drifted pose (user mouse look reached the captured window) and records `pose_reached`. TR-1 running. **TR-7 escalated** (2026-11-14): design E cannot match the cards (cause: the cards are lit with their flat plane normal, the mesh with up-pointing leaf normals; pine card pictures are 0.65 times the mesh albedo). Decision in "TR-7 escalation (2026-11-14)": re-bake the card atlas (albedo and normals, top view included) from the authored LOD2; TR-7 is now TR-7a and TR-7b, TR-6b needs no bake of its own.
Depends on: nothing. It can run alongside T1-T5. Coordinate with T6-8 (tint), T6-10 (far canopy) and T6-11 (LOD tail), which touch the same shaders.
Freeze: none. This is a renderer change, not world content (see decision D1).

**Goal (the user, 2026-11-13): draw as many trees as the game allows without tanking performance.** Triangle savings (decimation) and the camera-facing far billboard serve that goal. They free the budget that a higher tree count then spends. The camera-facing billboard is required, not optional: one quad per tree is what makes a large far tree count affordable. TR-2 sizes the tree count against a frame budget; TR-9 raises the count.

## Decisions for the user

| # | Blocks | Question | Recommendation |
|---|---|---|---|
| D1 | TR-5 to TR-7 | These change how every world looks, generation 1 included, the same way the grass Cards default did. Is that allowed, or must they be gated by generation? | Allow it. Trees are placed the same way; only their meshes and shading change. The world fingerprint must not move. **Answered 2026-11-14: all worlds.** |
| D2 | TR-5 | How far may decimation go? It is judged by the TR-4 contact sheets at the distance each LOD is used. | Choose from the contact sheets. Gates in TR-2.  **Answered 2026-11-14 on the TR-4 sheets: level A (1.6x target) for every tree.** A variant that fails the coverage rule even at A (v13 and v14, the small firs) keeps its authored LOD2. |
| D3 | TR-2 | Which fade distances are the reference for the tree budget and the TR-8 gates: the shipped defaults (LOD0 to 40 m, cards from 300-350 m) or the user's saved preferences (LOD0 to 200 m, cards from 550-900 m)? | **Answered 2026-11-14: the shipped defaults.** The user's preferences stay a heavier option on top. |
| D4 | TR-9 | (Added by TR-2, 2026-11-14.) If a preset cannot draw every placed tree inside its far distance within the budget, should it end the trees sooner (shorter far distance) or keep the distance and draw only a share of the far trees (thinner forest)? | Shorten the far distance. Thinning shows as a sparser forest; a shorter distance hands over to the far canopy tint (T6-10) in new worlds. Design B4, F3. **Answered 2026-11-14: end the trees sooner (shorten the far distance).** |
| D5 | none (deferred) | (Added by TR-2.) A lighter LOD0 mesh pays only with long near-detail fades such as the user's (LOD0 to 200 m: 1.8 to 2.5 ms for 256 trees). At the shipped fades LOD0 costs 0.1 to 0.5 ms. Build it? | Defer. Decide from TR-8's table at the user's preferences. Design C1. |
| D6 | TR-6 | (Added by TR-2.) Seen from above, a far tree is one quad that faces the camera and is sized to the tree's real outline from that angle (its side-view picture squeezed to a crown-sized patch). Nothing is enlarged. Is that look acceptable? | Yes, judged from TR-6's altitude captures. Design D3. **Answered 2026-11-14: yes, judged from TR-6's altitude captures.** **Confirmed 2026-11-14 on the TR-6 captures: the upright shape is accepted (brightness is TR-7's).** |
| D7 | none (TR-7b closes either way); a new order TR-7c if yes | (Added by TR-7b escalation 2, 2026-11-15.) The tree models change with distance because of their leaf-texture mips: at 325 m a maple crown covers 28 % more area and is 19 % brighter in direct sun than the same model close up, a pine covers 17 % more and is 14 % darker and less green, a fir covers 15 % less; a fir at 650 m keeps 36 % of the area it has at 325 m. The amount depends on distance and screen resolution. The far cards do not do this (they keep the close-up look), which leaves a step of about 10 % in brightness where the model hands over to the cards, and a larger step in crown fullness when the hand-off is moved out (the user's 550 to 900 m preference). Should the models get distance-stable mips (alpha that keeps the covered area, colour that ignores the cut-out texels), as the cards have? It changes the look of every tree from a few tens of metres outward: maples 12 to 22 % thinner and darker at noon, pines thinner and greener, firs fuller. Nothing is boosted or enlarged. | **User answer 2026-11-15: Yes, TR-7c** (after the `--dump` sheet). Was: Yes, but decide from pictures: TR-7b-2's `--dump` sheet shows three trees as drawn today, with stable mips, and as cards. If yes, TR-7c (size M, lead design first): build the mip chains in the tree prepare step, then recalibrate the cards. If no, the step stays as measured and TR-7b's switch strip is the judge. |
| D8 | TR-7c-1, TR-7c-2 | (Added 2026-11-15; the user: "Hold off on this change until I see it first myself.") TR-7c-PV builds the stable mips into `.godot/tr7c/preview/` only and draws a before/after sheet (maple, fir, pine at 20 m, 80 m, 325 m, 650 m and 900 m, in ambient light and at noon, today beside stable mips) and a dolly video. Nothing shipped changes until the user approves. Note that the change is visible from about 10 m outward, not only far away, and that leaf outlines seen edge-on get softer close up. Go ahead with the shipped change? | Decide from the sheet and the video. **Answered yes 2026-11-15** (the user: "Make it a real look"): ship the distance-stable leaf mips with the isotropic alpha sampler of design E (aniso against iso differed by more than 5 % in the preview, so the shader change is kept and is on by default whenever the loader swaps the texture). TR-7c-1 and TR-7c-2 authorised. |
| D9 | none (decided at TR-8); a new order TR-7d if yes | (Added by the TR-7c-2 lead decision, 2026-11-15.) The far cards are thinned to the model's true crown area by an alpha threshold, which keeps the densest parts of each card picture. Those are not a fair sample of the crown: maple cards are 10 % brighter at noon than the close-up model (single variants up to 21 %), pine cards up to 21 % darker at a 30 degree sun for one variant, and the maple saplings lose 9 % of their red. Against the model they replace at the hand-off the step is 2 to 6 %. Should the cards be thinned without that bias (each texel drawn with a probability equal to its leaf coverage: crowns beyond 325 m made of scattered texels instead of the densest clumps)? Nothing is boosted either way. | Decide from TR-8's switch strips. If the hand-off is not visible, no. If yes: TR-7d (size M, lead prototype and a preview sheet first). |

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
| TR-6b top-down crown card for the far tier (user request 2026-11-14; rewritten by the TR-7 escalation) | implementer | S | TR-6, TR-7b | TR-9a |
| TR-7 billboard match, as TR-7a (card atlas bake from LOD2) then TR-7b (shader, catalog, test); see "TR-7 escalation (2026-11-14)" | sol_implementer, lead checks the TR-7a contact sheet | M | TR-2, TR-3, user D1 | TR-4, TR-6 |
| TR-7c distance-stable leaf mips for the tree models (user D7 yes, 2026-11-15): TR-7c-PV preview only, then after user D8 TR-7c-1 (resource, loader, shader) and TR-7c-2 (match test, recalibration); see "TR-7c. Distance-stable leaf mips for the tree models (design and order, 2026-11-15)" | implementer; lead design done | M (preview), then M and S | D7; TR-7c-1 and TR-7c-2 also need D8 and TR-7b-3 | TR-9a |
| TR-8 review pack: before/after cost and look | sol_implementer | S | TR-5, TR-6, TR-7 (and TR-7c if D8 is yes) | none |
| TR-9a cap fall-through: budget-derived band edges (added by TR-2, 2026-11-14) | sol_implementer | M | TR-5, TR-6; merged after TR-8's cost run | TR-7 |
| TR-9 raise the tree count to the budget | sol_implementer | M | TR-8, TR-9a | none |
| TR-10 tree shadows past the near range: canopy in the height shadow (user asked 2026-11-15 for cheap tree shadows to 1 to 3 km) | sol_lead, then sol_implementer | M | TR-1 for the design and prototype (alongside TR-4 to TR-7); TR-9 for the prototype pictures and the implementation (TR-2 design H) | TR-4 to TR-7 |

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

**Items 2, 3 and 6 are replaced by "TR-7 escalation (2026-11-14)" under TR-7 (measured: the ellipsoid normal and one multiplier cannot match). Items 1, 4 and 5 stand.**

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

### TR-6b. Top-down crown card for the far tier (requested by the user, 2026-11-14)

ROLE: implementer (bake and shader), after the lead checks the bake method
SIZE: M
DEPENDS ON: TR-6, TR-7 (the card calibration also covers this card)

OBJECTIVE
Seen from above, a far tree also shows a horizontal card through the middle of its crown with a real top-down picture of that tree type, so a forest seen from the air reads as treetops rather than as tilted side views.

CONTEXT
The user asked for it after judging the TR-6 captures (D6 confirmed): "have the trees that face you have an intersecting texture running through the middle of them for when the player is above them". Reusing a side picture of the tree as the horizontal card would look like a tree lying on the ground, so the card gets its own top-down bake.

**Superseded in part by "TR-7 escalation (2026-11-14)", section "TR-6b rewritten": items 1 and 4 are replaced there (no separate bake; the LOD3 top card, re-baked by TR-7a).**

REQUIRED CHANGES
1. An offline bake: render each variant's authored LOD2 from straight above (orthographic, the crown's bounds, the same atlas size and alpha rules as the LOD3 cards) into a new atlas layer per variant, stored beside the impostor atlas; deterministic, and the 30-variant catalog stays byte-identical.
2. The far quad mesh gains a second, horizontal card at mid-crown height, sized to the crown's horizontal bounds (no enlargement).
3. Visibility: the horizontal card fades in with the same 20-30 degree steepness the TR-6 rule uses, and the upright card keeps its D3 behaviour; at low angles the horizontal card is edge-on and is dropped entirely (no cost from the side).
4. Lighting: the horizontal card's normal is surface-up, blended with TR-7's crown normal; TR-7's per-variant calibration applies to it as well.
5. The UseFarTreeQuads A/B export covers it; no new menu row.

VALIDATION STEPS
- Measure the added cost per far tree at the TR-6 poses (expected a few tenths of a millisecond at 200,000 far trees).
- The TR-6 altitude and mountainside captures again (300 m and 2 km, 30 and 70 degrees), before and after, for the user.
- A dolly through 20-30 degrees: `flicker` shows no new spike.
- `tree_lod_dither_gpu_test`, `tree_shared_impostor_integration`, `imported_tree_impostor_catalog_smoke`, `tree_atmosphere_gpu_test`.

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

**Superseded in part by "TR-7 escalation (2026-11-14)" below: items 1 and 2 and the validation steps are replaced by TR-7a and TR-7b there; items 3 to 5 stand.**

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

### TR-7 escalation (2026-11-14)

The implementer reported that design E (ellipsoid crown normal plus one RGB multiplier per variant) cannot match the cards to the LOD2 mesh: card/mesh luminance 0.33 at noon, 0.80 at sun 30 degrees, 0.92 at sun 8 degrees. This section gives the cause, replaces design E and TR-7's steps, and rewrites TR-6b. Evidence is in `.godot/tree_billboard_match/escalation/` (`probe.txt`, `probe_nmap.txt`, the two probe scripts, `billboard_textures_sheet.png`), measured on the RTX 4070 with the test's scene.

#### Diagnosis

The two shaders have the same `light()` (`imported_tree.gdshader:149-176`, `imported_tree_impostor.gdshader:230-257`): plain `max(dot(NORMAL, LIGHT), 0)`, no wrap, no transmission, the same AO and day terms. Both are `cull_disabled`, so Godot flips the normal of back faces towards the viewer. The difference is in the inputs.

**1. Normals (the main cause, and the reason the sign changes with the sun).** Lit by the sun alone (ambient 0), mean N.L over 4 azimuths:

| | noon | sun 30 | sun 8 (includes the day factor) |
|---|---|---|---|
| LOD2 mesh, all 30 variants | 0.309 | 0.261 | 0.132 |
| LOD2 mesh, maple / fir / pine | 0.478 / 0.181 / 0.198 | 0.312 / 0.257 / 0.222 | 0.125 / 0.151 / 0.134 |
| crossed cards, card normal (w = 0) | **0.000** | 0.269 | 0.169 |
| crossed cards, ellipsoid crown normal (w = 1) | 0.115 | 0.296 | 0.184 |
| crossed cards, the pack's baked normal map switched on | 0.169 (maple 0.282, fir 0.039, pine 0.105) | 0.266 | 0.159 |

- The LOD2 foliage normals are authored pointing up: area-weighted mean n.y 0.70 to 0.83 for maple and 0.49 to 0.76 for pine (fir 0.09 to 0.12; fir variant 14 has its normals against its winding). So at noon the mesh crown is sunlit.
- The cards are vertical planes shaded with their geometric normal (phase fact F7), so at noon they receive no sun at all and are lit by the ambient only. At low sun a card that faces the sun is lit across its whole face, which the up-pointing mesh normals are not. That is the dark card at noon and the bright card at sun 8 degrees in TR-1's strip.
- The card's normal map is skipped because `simplified_tree_shading` is on (`imported_tree_impostor.gdshader:202-211`; `SimplifyDistantTreeMaterials` defaults to true, `RingBiomeDecorationLayer.RenderPolicy.cs:29`, applied to the cards at `RingBiomeDecorationLayer.cs:535-538`).
- The ellipsoid cannot supply the missing light: a tall crown seen from the side has few up-facing normals (0.115 against the mesh's 0.309).

**2. The card picture is a separate bake, and for pine it is darker.** The card albedo is the pack's `*_Billboard_Mat_baseColor.png`, not the leaf atlas (`ImportedTreeImpostorCatalog.cs:229-230`). Lit by white ambient alone (so: mean albedo of the covered pixels), card/mesh in green is 0.97 for maple, 0.75 to 0.97 for fir and **0.61 to 0.71 for pine** (red 0.61 to 0.65). There is no lighting baked into the pictures; pine is simply a darker picture. This factor does not depend on the sun.

**3. These two numbers reproduce the implementer's table.** With the test's ambient (linear luminance 0.378 times the energy) the ratio is albedo ratio x (ambient + card N.L) / (ambient + mesh N.L):

| w = 0 | noon | sun 30 | sun 8 |
|---|---|---|---|
| predicted, maple / fir / pine | 0.29 / 0.48 / 0.33 | 0.84 / 0.83 / 0.77 | 1.11 / 0.86 / 0.80 |
| measured (control_off rows of `table.csv`) | 0.28 / 0.48 / 0.34 | 0.82 / 0.82 / 0.78 | 1.09 / 0.86 / 0.81 |

A single multiplier would have to be 3.0 at noon and 1.1 at sun 8 degrees, so no clamp range makes design E3 work. The "paler and bluer at noon" card is the same thing: the card shows only the blue ambient, the mesh shows white sun plus ambient.

**4. The test's lighting understates the problem.** The test uses sun energy 1 with a flat ambient of energy 0.55; the game uses sun energy 4.0 with sky ambient of energy 0.35 (`scenes/Main.tscn:22-24, :51`). The ambient is what hides the missing sun on the card, so the game's noon step is larger than the test's 0.33 (TR-1 saw a "solid dark green silhouette"). Tree shadows do not enter: the casters end at 240 m (TR-1 section 2). A test that mixes sun and ambient into one luminance cannot be trusted at another ambient level; the two terms have to be gated separately.

**5. The pack already ships an impostor bake with a top view.** Each billboard atlas holds 8 (maple), 4 (fir) or 3 (pine) side views plus one top-down view, each with a baked normal map (`billboard_textures_sheet.png`), and every LOD3 mesh has one horizontal card that shows the top view (one card in 9 / 5 / 4 has a vertical normal). Switching the pack's normal maps on is not enough, for three reasons:
- They were not baked from our LOD2: noon N.L reaches 0.28 of the mesh's 0.48 for maple and 0.04 of 0.18 for fir.
- The green channel favours "down" for fir and pine and "up" for maple, so the convention is not consistent between species.
- Averaging loses the light: the maple map gives a noon response of 0.412 at full size, 0.326 after the 4x resize to the 256 atlas and 0.283 at the next mip (the measured 0.282). Neighbouring leaves have opposite normals and the average points at the viewer.

**6. A TR-6 bug.** The far quad's normal points away from the camera. `FAR_QUAD_CORNERS` (`imported_tree_impostor.gdshader:30`) is counter-clockwise seen from the camera, Godot's front face is clockwise, and the back-face flip reverses the camera-facing normal set at `:110`. Sun alone at 30 degrees, w = 0: N.L 0.000 with the sun behind the camera and 0.750 with the sun behind the tree.

#### Decision

Replace the card pictures with our own bake of the authored LOD2, albedo and shading normal per card, in the pack's existing card layout. Rejected:
- An elevation-dependent multiplier or an extra lighting term: it would be a fitted boost, and it cannot give the lit top and dark underside that the mesh shows at about 45 pixels.
- Wider tolerances: the error is a factor of 3 at noon in the test and more in the game.
- Switching on the pack's normal maps: item 5.
- Keeping the ellipsoid: item 1.

What the bake fixes, by construction: the card's albedo is the LOD2's (pine included), the card's normals are the LOD2's shading normals, the silhouette and crown openness are the LOD2's, and the top card is a top view of the same LOD2, which is the picture TR-6b needs. Cost:
- Offline: a CPU tool that reuses the rasteriser of TR-4 (`tools/TreeLodDecimator.cs`, `Coverage.Raster`) with a depth buffer; no GPU, deterministic, seconds to run.
- Disk: one checked-in resource with 30 layers of 256 x 256 albedo and normal.
- Memory: none added. `albedo_layers` and `normal_layers` already exist at this size; only their content changes.
- Frame time: one more texture read per card pixel while simplified shading is on, less the ellipsoid code that is removed. Not measured (performance runs are paused); TR-8 measures it with the slot 3 and slot 4 cases. TR-1 section 3 found the forest triangle-bound, so this is expected to be small.

No brightness multiplier remains anywhere in the card path.

#### TR-7 rewritten (this replaces design E items 2, 3 and 6 and TR-7's REQUIRED CHANGES 1 and 2; E4 and E5 stand)

**TR-7a. Bake (implementer; the lead checks the contact sheet before TR-7b).**
1. New offline step in the tree-LOD prepare tool (`tools/PrepareTreeRenderLods.cs` or a sibling) writing `resources/trees/prepared/lolipop_impostor_atlas.res` and a manifest (tool version, source LOD2 hash per variant), by the rules of design C4. `lolipop_catalog.res` stays byte-identical; a missing or stale resource falls back to the pack's pictures with one warning.
2. Per variant and per LOD3 card (the horizontal one included): fit the card's UV-to-position map (as `LargestPlane` does), and rasterise the authored LOD2 (bark and foliage, the foliage alpha-tested at its authored cutoff) orthographically along the card's normal into the card's UV rectangle of a 256 x 256 layer, nearest surface wins. The LOD3 mesh, its UVs and its vertex count do not change.
3. Albedo layer: the mesh's albedo texture times its tint, averaged in linear light over at least 3 x 3 sub-samples; alpha is the covered fraction. Dilate colour into uncovered texels. The alpha mips stay coverage-preserving (`PreserveAlphaCoverage`, design E4).
4. Normal layer: the normal the mesh shader would shade with (interpolated vertex normal, reversed where the triangle is back-facing to the bake direction, Godot's clockwise front face), written in the card's own tangent frame from `tangent_data` and `normal_data`, so the existing `NORMAL_MAP` path reproduces it. One surface sample per texel, never an average. Dilated like the colour.
5. Normal mips are selected, not averaged: each mip texel takes the normal of one covered child of its most-covered child's card, chosen by per-card error diffusion on the six-axis clamped light response (fixed order on ties; see the TR-7a lead check, fix 1). The tool prints, per variant and mip level 0 to 3, the mean of max(n.L, 0) over covered texels for noon, 30 and 8 degrees against the full-resolution value; levels 0 to 3 must be within 3 %.
6. Contact sheet: per species, the old and new albedo and normal layers side by side.

**TR-7b. Shader, catalog, test (implementer).**
1. `imported_tree_impostor.gdshader`: sample `normal_layers` for every card pixel, outside the `simplified_tree_shading` branch, with `filter_nearest_mipmap` on that sampler; metallic, roughness and the specular term stay inside the branch. Remove `crown_normal_weight`, `tree_crown_view_normal`, `tree_model_position` and the fragment block that uses them.
2. Far quad: reorder `FAR_QUAD_CORNERS` to clockwise seen from the camera (item 6). Its normal-map frame is the bake's frame (across, tree up, horizontal direction to the camera), not the tilted quad's, so top light stays top light from above.
3. `ImportedTreeImpostorCatalog`: build both arrays from the bake resource; remove `Crown`, `CrownOf` and the crown rows; remove the RGB multiplier from `CreateParameterTexture`. The calibration resource keeps the alpha cutoffs only (version 2); the fingerprint hashes the baked atlas.
4. `tree_billboard_match_gpu_test`, per variant at 325 m, cards and quad against the mesh, 4 azimuths:
   - white ambient only: each mean colour channel within 8 %; covered area within 8 %;
   - sun only at noon, 30 and 8 degrees: the azimuth mean within 10 % (noon, 30) and 15 % (8); report each azimuth;
   - the far quad lit from the camera side: N.L with the sun behind the camera above N.L with the sun behind the tree;
   - control: with the bake resource absent (pack pictures) the test must fail.
5. Rerun the TR-1 switch strip in both configurations and send the before and after sheets. A dolly from 250 to 400 m at noon through `flicker`: no new spike from the nearest-sampled normals.
6. `UseFarTreeQuads` goes on by default when this passes.

Escalate if: the sun-only gate fails at one elevation for a whole species; the mip rule of TR-7a item 5 cannot be met; or the dolly shows shimmer on the cards that the LOD2 mesh does not have.

**The uncommitted TR-7 work.**
- Keep: the test file and its manifest entry, the driver bridge (`ImportedTreeCatalogDriver.cs`), `MeasureForeground`, the cutoff calibration and `ApplyCalibration`, `DefaultCutoffs`, `PreserveAlphaCoverage`, the `--sweep` and `--dump` aids.
- Revise: the test's cases and tolerances (item 4); the calibration resource (cutoffs only).
- Revert: the crown-normal code in the shader, `Crown` / `CrownOf` / rows 5 and 6 in the catalog, the RGB multiplier, and `resources/trees/prepared/lolipop_impostor_calibration.res` (it is stale once the atlas changes; regenerate it at the end of TR-7b).

#### TR-6b rewritten (this replaces TR-6b's REQUIRED CHANGES 1 and 4)

No separate top-down bake. The top view is the LOD3 mesh's horizontal card, which the crossed cards already draw and which TR-7a re-bakes from the LOD2.
1. The far quad mesh gains a second quad: the variant's horizontal LOD3 card (its UV rectangle, bounds and height from that card, in the `variant_parameters` rows the crown ellipsoid used). 12 vertices per tree.
2. Visibility and the A/B export as in the original items 3 and 5.
3. Lighting: the baked normal layer, like every card. No calibration.
4. The catalog smoke asserts that every variant has exactly one horizontal LOD3 card.
- Note: the card sits at the height the pack gave it, the same as in the crossed cards, so the hand-off at 900 m does not move it. If the user wants it at mid-crown instead, that is a one-line offset.

**TR-6b implementation notes (untested).** The far quad mesh is 12 vertices; vertices 6 to 11 are the horizontal card, drawn by `imported_tree_impostor.gdshader` from `variant_parameters` rows 5 to 7 (UV rectangle; XZ corners of the parallelogram that covers it, found by inverting the card's affine UV map; height; frame code), which `ImportedTreeImpostorCatalog.FarQuadTop` / `Geometry.HorizontalCard()` fill. Visibility: smoothstep of sin(elevation) from sin 20 to sin 30 degrees (constants in the shader, matching the GLSL rule); zero collapses all six vertices to one point, otherwise the card fades in by screen-door through `tree_dither_window.y`. Lit by the baked normal layer through a frame built from the frame code, as the upright quad is. Smoke: `AssertHorizontalCard` (exactly one horizontal card, quad reproduces the UVs, height). To run when tests resume: `imported_tree_impostor_catalog_smoke`, `tree_billboard_match_gpu_test`, `tree_lod_dither_gpu_test`, `tree_shared_impostor_integration`, `tree_atmosphere_gpu_test`, plus the 20-30 degree dolly through `flicker`.

Order from here: TR-7a, the lead's contact-sheet check, TR-7b, then TR-6b. TR-4 and TR-5 are unaffected. TR-8 adds the card-pixel cost to its table.

**Not a decision, but the user should know:** matching the mesh changes the far forest. Cards at noon become sunlit instead of dark, cards at low sun become darker, and pine cards become about 1.5 times brighter in every light (all of it out to the far distance). That is the LOD2 look carried outward, with nothing added.

#### TR-7a lead check (2026-11-15)

Verdict: the bake is right at full resolution; two fixes are needed before TR-7b (one in the baker's mips, one in the test). Checked on the uncommitted TR-7a tree, headless only. Sheets: `.godot/tr7a/sheets/`; evidence: `.godot/tr7a/lead_check/`.

**What is right.**
- Albedo. Fully covered texels against the pack pictures, linear green: pine 1.52 to 1.65 times brighter (pack/bake 0.60 to 0.66, the 0.61 to 0.71 of the diagnosis), maple 1.05 to 1.12, fir 1.00 to 1.03. The pack's dark patches in the pine crowns are gone; no lighting is baked in.
- Silhouettes and top cards. 183 cards, exactly one top card per variant, every card in the pack's rectangle. Coverage sum against the independent 5 x 5 silhouette: worst 1.11 %, mean signed 0.04 %.
- Normals. Side-card binormal is world up in every variant, so green is up. Maple side cards (v0, v3, v6, v10 sampled): 47 to 61 % of the coverage points up, 28 to 35 % down (leaves seen from behind, flipped as the mesh shader flips them); their top cards have mean n.y 0.74 to 0.81. Side cards alone, all variants, give a noon response of 0.41 / 0.20 / 0.22 (maple / fir / pine) against the mesh probe's 0.478 / 0.181 / 0.198.
- Determinism. Four runs, the same artifact hash (B2F1F1F0B4848F96).

**The fit residual (v19) is real, not a baker bug.** The pack's side cards have three vertex rows. In four pine variants (v19, v22, v23, v27) the middle row's v is not midway between the outer rows (v19: 0.00487, 0.31772, 0.63937 at heights 0, 0.495, 0.99), so the UVs are piecewise affine. One affine map misses by 0.38 to 0.93 texel (v22 the worst); every other card fits to 1e-5. That is at most 0.5 % of the tree height, under half a pixel at 325 m. Done: the test's limit is now one atlas texel (`TreeImpostorAtlasTest.cs`), with the reason in a comment. A per-triangle bake would be exact and is not worth its code.

**Fix 1 (baker): the mip rule fails, and it matters.** Worst mip 1 to 3 against level 0 is 14.53 % (limit 3 %); at noon the worst of level 1 / 2 / 3 is 3.9 / 8.0 / 14.5 %. The hand-off at 325 m draws about levels 1 to 2 (a card is 82 to 162 texels tall and the tree a few tens of pixels), so the drift would use most of TR-7b's 10 % gate. Cause: "most-covered child" prefers solid surfaces. Maple drifts up (11 of 12 variants), fir and pine drift down (bark wins over sparse needles). An unbiased hashed pick still leaves 10 % at level 3 from sample noise. Replace the selection in `BuildNormalMips`, still one surface normal per texel:
- Parent texels in scan order. Per child an integer weight w = round(coverage x Sub x Sub x 4^(level - 1)).
- The block's card is the card of the most-covered child, as now. Candidates are the covered children of that card.
- Per candidate a six-vector f from its stored bytes, with (x, y, z) = 2 x byte - 255: max(x, 0), max(-x, 0), max(y, 0), max(-y, 0), max(z, 0), max(-z, 0). That is the clamped response to light along the six frame axes.
- target = sum of w f over the candidates, S = sum of w. Pick the candidate that minimises |E + target - S f|^2 (integer arithmetic, first candidate on a tie), then E += target - S f. E is one running six-vector per card, zero at the start of each level.
- Coverage and the no-covered-child case stay as they are. Update the doc comments, the manifest's `method` text and item 5 above.
- Measured with a Python prototype on this bake's level-0 data (`lead_check/mip2.py`, `down()`, kept as the reference for the rule; the level-0 dump it reads was a temporary tool output and is not kept): worst over all variants, suns and levels 1.42 % (noon level 1 / 2 / 3: 0.16 / 0.48 / 1.26 %). `lead_check/v10_l2_max_vs_diffuse.png` shows level 2 before and after: no streaks. The 3 % limit stays.

**Fix 2 (test): the fixed IoU limit of 0.9 measures sampling, not the bake.** 63 of 183 cards are below it (lowest 0.826, v19 card 1; pine side cards 0.83 to 0.89, top cards 0.89 to 0.96), while the coverage sums agree within 1.11 %. Half-coverage flips between 3 x 3 and 5 x 5 samples in sparse foliage. Replace it per card: the IoU against the silhouette must be above the highest IoU of the baked half-coverage mask against itself moved one texel (plus and minus x and y, inside the card's owned texels). Measured: all 183 cards pass, smallest margin 0.068, median 0.24. Keep the 3 % sum limit and the bake-without-foliage control (it still rejects).

**After the two fixes.** Run `prepare_tree_impostor_atlas`, then `tree_impostor_atlas_test`, in separate runs (in one run they start together and the test reads the resource while the tool writes it). When the test passes TR-7a is done; no second lead check. The checks after the mip gate (repeat bake, saved bytes, manifest, stale detection) have not run yet.

**To watch in TR-7b.**
- `imported_tree_impostor.gdshader:214-219` assumes Godot negates TANGENT, BINORMAL and NORMAL on back faces and un-negates x and y there. If the engine negates only NORMAL, that line turns up into down on every card seen from behind. Not verified here. The noon sun-only gate shows it; check this first if noon fails.
- Maple side cards read 0.41 at noon against the mesh probe's 0.478. If the noon gate misses by that amount for the whole species after fix 1, the likely cause is the mesh's leaf alpha at 325 m (read from a mip, so the crown is fuller than the full-resolution cutout the bake uses). Escalate with the per-azimuth rows.

**For the user.** Christmas tree_3 (v14) has its shading normals against its winding (known from the diagnosis). The bake copies that faithfully: its top card points down (mean n.y -0.54, noon response 0.01) and its 30 degree response is 0.19 against 0.27 for the other two firs. The mesh is lit the same wrong way at every distance. Repairing v14's normals in the tree prepare step and re-baking is a separate small order; it changes how that variant looks up close. Sheets worth opening: `v19_Pine_large_2.png` (pine brightness), `v03_Acer_large_1.png`, `v13_Christmas tree_2.png`, `v14_Christmas tree_3.png` (the inverted top card).

#### TR-7b escalation 2 (2026-11-15)

The implementer's TR-7b run left the cards failing 21 / 14 / 4 variants (noon / sun 30 / sun 8), 28 variants on the ambient colour gate, and the far quad failing coverage (29) and the camera-side check (16). Measured on the RTX 4070 with the uncommitted TR-7b tree, through a temporary `--lead-diag` mode of the test (removed again; the test file is byte-identical to the implementer's). Evidence in `.godot/tree_billboard_match/escalation2/`: `table.csv` and `report.json` (the implementer's run), `diag1.csv` and `diag2.csv` (the lead's two passes), `analyse_diag.py`, `analyse_quad.py`, `analyse_table_per_azimuth.py`, and `lead_diag_mode.gd.txt` (the diagnostic mode, to rerun).

**Verdicts.**
1. Maple low, pine high: not a card error. The bake and the shader reproduce the tree model within 5 % (species mean) in every light. The step is in the reference: the model's own leaf-texture mips change its look with distance and resolution. No multiplier is needed or allowed; the gate's reference changes (fix order C), and whether the model is corrected is the user's decision D7.
2. Blue channel: the same cause for pine (the model at 325 m has lost 30 % of its blue and 6 % of its green to its texture mips; the card has the model's close-up colour within 3 %). For maple the channel is too small to gate (0.005 linear, 0.6 % of the luminance). The implementer's "blue only" is not quite right: maple red is 9 to 13 % off as well, for the same reason. The gate changes to errors relative to the luminance.
3. Far quad: one sign bug (found, fix verified: 13 to 29 of 30 lit from the camera side; the 30th is v14, whose mesh fails the same check). Coverage is not an azimuth problem: the gate already uses the azimuth mean. The quad covers too little because it uses the cutoff calibrated for the crossed cards; it needs its own.
4. A new defect found on the way, in the uncommitted calibration: the calibrated cutoffs break the coverage-preserving alpha mips, so card forests would thin to 56 to 67 % of their area by 900 m. It must be fixed before TR-7b is committed (fix order B3).
5. No fix here boosts, widens or enlarges anything.

**Measurements.** All ratios are means over the variants of a species (maple v0-11, fir v12-14, pine v15-29), 4 azimuths, at the test's pixel scale unless a viewport size is given (128 px = 325 m at 1080p; 64 px = 650 m; 46 px = 900 m; 256 px = 325 m at 4K).

"Reference model" below is the authored LOD2 with its albedo texture read at mip 0, 8 times supersampled (1024 px): the model as it looks close up, seen from 325 m. "Shipped model" is the LOD2 as the game draws it at 325 m.

| | maple | fir | pine |
|---|---|---|---|
| Shipped model / reference model, covered area | 1.28 (1.14 to 1.44) | 0.85 (0.68 to 1.11) | 1.17 (1.02 to 1.30) |
| Shipped / reference, ambient r, g, b | 1.10, 0.98, 0.85 | 0.97, 1.06, 1.02 | 1.02, 0.94, 0.70 |
| Shipped / reference, sun-only luminance, noon / 30 / 8 | 1.19 / 1.14 / 1.04 | 1.04 / 1.07 / 1.05 | 0.86 / 0.92 / 0.95 |
| Shipped model's covered area at 64 px, 256 px, 512 px against 128 px | 0.84, 0.99, 0.95 | 0.36, 1.28, 1.36 | 0.65, 1.04, 1.04 |
| Cards / reference model, ambient r, g, b, luminance | 0.98, 1.00, 0.76, 0.99 | 0.99, 0.99, 0.99, 0.99 | 0.99, 1.00, 0.97, 0.99 |
| Cards / reference model, sun-only luminance, noon / 30 / 8 | 1.04 / 1.01 / 0.98 | 0.99 / 1.00 / 1.02 | 0.95 / 1.01 / 1.01 |
| Cards / shipped model, sun-only luminance (the implementer's table) | 0.87 / 0.88 / 0.94 | 0.95 / 0.93 / 0.97 | 1.12 / 1.09 / 1.07 |
| Cards / shipped model at the game's light mix (sun 4.0, ambient 0.35) | 0.89 / 0.91 / 0.96 | 0.94 / 0.94 / 0.96 | 1.09 / 1.08 / 1.06 |
| Card covered area at 256, 64, 46, 32 px against 128 px, calibrated cutoffs | 1.27, 0.69, 0.56, 0.42 | 1.20, 0.90, 0.63, 0.55 | 1.17, 0.74, 0.67, 0.54 |
| The same with the authored cutoffs (what the alpha mips were built for) | 1.04, 0.95, 0.91, 0.87 | 1.03, 0.97, 0.93, 0.88 | 1.04, 0.89, 0.86, 0.81 |
| Quad / shipped model, covered area: card-calibrated cutoff, authored cutoff | 0.65, 1.06 | 1.07, 1.61 | 0.80, 1.14 |
| Quad / cards, covered area at 46 px, authored cutoffs | 0.85 | 1.15 | 0.89 |
| Quad with the sign fix / reference model, sun-only luminance, noon / 30 / 8 | 1.01 / 1.02 / 1.03 | 1.02 / 1.00 / 1.03 | 1.00 / 1.04 / 1.02 |

Per variant against the reference model: the cards' worst are v20 0.84 and v29 0.87 at noon and v25 0.86 at sun 30 (3 / 2 / 0 variants outside 10 / 10 / 15 %); the quad's are v24 1.36, v26 0.78 and v27 0.81 at noon.

**Findings.**

1. *The tree model changes with distance.* The leaf textures use the importer's plain mip chain. At 325 m the model reads mips where the alpha of a whole leaf cluster is one average, tested against a cutoff of about 0.2 to 0.3, and the colour includes the texels under the cut-out. With the albedo forced to mip 0 (same geometry, same pixels) the maple crown has 0.80 of its covered area, pine 0.88, fir 1.23, and the colours of row 2 appear. The effect moves with resolution and distance (row 4): a fir at 650 m keeps 36 % of the area it has at 325 m. So "the mesh at 325 m at 1080p" is one point on a curve, and a 10 % gate against it cannot hold at 4K or at another hand-off distance. The user's own fade preference puts the hand-off at 550 to 900 m, where the curve is steepest.
2. *The cards are right.* Against the reference model the cards are within 1 % in ambient luminance, 3 % in red and green, and 5 % in sun-only luminance for every species and elevation. The guess of the TR-7a lead check (maple side cards 0.41 against the mesh probe's 0.478 because the mesh crown is fuller at a mip) is confirmed: 0.478 was the shipped model, and the reference model gives 0.39 (cards 0.41).
3. *The decode fix is right.* Godot negates only `NORMAL` on a back face, so removing the in-plane negation was correct. All 183 cards have frame sign +1, so the baker's `* s` on all three components is harmless and stays. `tree_card_back` and `card_bake_side` are dead.
4. *Calibration against preservation.* `PreserveAlphaCoverage` scales each alpha mip so the count at the authored cutoff (0.25) stays that of the base level. The calibration then moved the cutoff to 0.30 to 0.51, where that no longer holds (rows 9 and 10). The mips have to be built for the cutoff in use.
5. *The quad's normal.* `imported_tree_impostor.gdshader:162` maps the baked card normal N0 to `toward_camera_flat * (frame_sign * hand * u_sign * v_sign)`. That product is the sign of (canonical plane normal . card vertex normal), which `LargestPlane` fixes by its x >= 0 rule, not by the card. For the cards whose vertex normal is the other way (16 variants) the quad shows the picture mirrored and, because the mapping is kept a rotation, turns the baked side away from the camera. The baked side must always face the camera; a mirrored picture mirrors only the across component, which `TANGENT`'s `u_sign` already does. With `toward_camera_flat * frame_sign` the quad is lit from the camera side in 29 of 30 variants and its sun-only means are within 4 % of the reference model. The horizontal card's frame (`:107-117`) is right as written (it is drawn in place, so its `top_hand` is needed).
6. *The quad's coverage.* Crossed cards cover more than one picture (several pictures overlap on screen), so the cutoff that brings the cards down to the model's area erodes a single picture to 0.65 (maple) and 0.80 (pine) of it. At the authored cutoff the quad is at 1.06 / 1.61 / 1.14. It needs its own calibrated cutoff, and its own alpha mips built for that cutoff (finding 4).
7. *One picture per tree.* The quad's per-variant brightness follows the one card it copies (0.78 to 1.36 at noon). The species means are within 4 %. This is accepted at 900 m and beyond (a tree is 17 pixels tall, the hand-off dithers over 150 m); the test gates the species mean and bounds the variant.
8. *Not needed, noted for TR-8.* Maple and fir LOD3 have each side plane twice, back to back (8 and 4 side cards on 4 and 2 planes), meant to be drawn one-sided. With `cull_disabled` both are drawn on top of each other. Dropping the back-facing twin in the vertex stage changes the match by under 2 % for maple and +6 % for fir, lowers the maple cards' area by 6.5 % (recalibrate) and halves the card fill for 15 variants. Pine has single cards and needs both sides.

**Decision.**
- TR-7b's gates measure the cards and the quad against the reference model for colour and light, and against the shipped model for covered area (the hand-off silhouette). The step against the shipped model is reported and bounded, not gated at 10 %.
- The model's own drift goes to the user as D7. Rejected: baking the model's 325 m look into the cards (it would freeze one resolution's artefact into every far tree and needs an emulation of the GPU's mip choice in the baker); any multiplier; a mip bias on the model (partial, and it shimmers).
- Fix the quad sign, give the quad its own cutoff and alpha mips, and build both tiers' alpha mips for the cutoff in use.

**Fix order TR-7b-2 (implementer, size M).** It continues the uncommitted TR-7b tree. Nothing in it waits for D7.

A. `materials/imported_tree_impostor.gdshader`
1. Line 162: `NORMAL = far_quad_to_model_direction(MODEL_MATRIX, model_scale, toward_camera_flat * frame_sign);`. Remove `hand` from the upright-quad block and rewrite its comment (lines 148 to 152): the baked side always faces the camera; mirroring flips the across component only. Do not touch the horizontal-card block.
2. Remove `tree_card_back` and `card_bake_side` (lines 52, 84, 172, 191). Keep the decode and its comment.
3. `ALPHA_SCISSOR_THRESHOLD = far_quad_mode ? flags.a : tree_parameters.a;` (row 2's alpha becomes the quad's cutoff).

B. `ImportedTreeImpostorCatalog.cs`, `ImportedTreeImpostorCalibration.cs`, `ImportedTreeCatalogDriver.cs`
1. Calibration version 3: `Cutoffs` (cards) and `QuadCutoffs`, both per variant; version 2 is stale.
2. The fingerprint hashes the base level only (the bake's `AlbedoBase`, or the resized base level of the pack's pictures) and the variant count. It must not depend on mips or cutoffs.
3. One function builds an albedo array for a given cutoff set: per layer the base level, `GenerateMipmaps`, then `PreserveAlphaCoverage(image, the cutoff in use for that variant)`. The card material gets the array for the card cutoffs, the quad material the array for the quad cutoffs; when the two sets are equal (no calibration) both share one array. `ApplyCalibration` and `UseBake` rebuild the arrays and republish the parameters. Cost: 10.5 MB more texture memory when calibrated, and 30 more 256 x 256 mip builds on the workers at load; report both for TR-8.
4. `CreateParameterTexture`: row 1 alpha = the card cutoff in use, row 2 alpha = the quad cutoff in use (it was the constant 1).
5. Driver: `ApplyImpostorCalibration(cards, quads)`, `GetImpostorCalibrationQuadCutoffs`, `SaveImpostorCalibration(cards, quads)`. Update `ImportedTreeImpostorCatalogRegressionSmoke` for version 3 and the second array.

C. `tools/tree_billboard_match_gpu_test.gd`
1. Two models. The shipped model as now (128 px, 325 m). The reference model: the same meshes with each material's shader swapped for a test-local copy (`Shader.new()`, `code = shader.code.replace("texture(albedo_texture, UV)", "textureLod(albedo_texture, UV, 0.0)")`; fail if the text is not found; put the shaders back afterwards), drawn in a 512 px viewport (512 and 1024 agree within 0.2 %). Say in the header why the reference is not the shipped material.
2. Calibration: for each scan cutoff apply it to both tiers, measure the cards (325 m) and the quad (900 m, narrowed lens) over the azimuths, and take each tier's crossing with the shipped model's covered area. Save version 3.
3. Cards, gated:
   - covered area against the shipped model within 8 % per variant;
   - ambient against the reference model: luminance within 5 %, and each channel's difference within 8 % of the reference luminance (the per-channel ratio is reported only);
   - sun only against the reference model over 12 azimuths (15 + 30 k degrees): the species mean of the variant ratios within 10 / 10 / 15 %, each variant within 20 / 20 / 25 %;
   - scale: the cards' covered fraction in a 64 px viewport within 15 % and in a 46 px viewport within 20 % of the 128 px value (species mean; same lens).
4. Quad, gated (900 m, narrowed lens, 128 px unless stated):
   - covered area against the shipped model within 8 % per variant; ambient as for the cards;
   - sun only against the reference model: species mean within 10 / 10 / 15 %, each variant within 40 % (one picture per tree, finding 7);
   - the camera-side check as now, for seed 0 and for a mirrored seed (`fract(z * 7919) > 0.5`); and the mirror identity: seed 0 with the sun 90 degrees to the right of the camera equals the mirrored seed with the sun 90 degrees to the left, within 3 %;
   - scale: the quad's covered fraction at 46 px within 20 % of its 128 px value, and quad against cards at 46 px within 15 % (species means).
5. Reported and bounded: cards against the shipped model, sun only, species mean within 25 % (today 0.87 / 0.88 / 0.94, 0.95 / 0.93 / 0.97, 1.12 / 1.09 / 1.07). This is the hand-off step D7 is about; put it in `report.json` as `handoff_step`.
6. Control as now. Report only, for TR-6b: camera 60 degrees above the horizon, noon and sun 30, reference model, cards and quad.
7. `--dump` also saves, for v3, v13 and v19 at 325 m in ambient and at noon, the shipped model, the reference model and the cards, as one contact sheet for D7 (`tools/media/ffmpeg_tools.ps1 sheet`). Each mode writes its own `table_<mode>.csv` and `report_<mode>.json` so a later run does not overwrite the evidence. Update `expected_s` in the manifest.

Validation.
- Build, then `tools/run_tests.ps1 -Tests tree_billboard_match_gpu_test -Label tr7b` with `TREE_BILLBOARD_MATCH_ARGS=--calibrate`, then again without it.
- Then `-Tests imported_tree_impostor_catalog_smoke,tree_shared_impostor_integration,tree_impostor_atlas_test,tree_lod_dither_gpu_test,tree_atmosphere_gpu_test,tree_lighting_shadow_gpu_test`.
- Then TR-7b items 5 and 6 as written (switch strip in both configurations, the 250 to 400 m dolly through `flicker`, `UseFarTreeQuads` on).

Escalate if: a species mean misses a sun or ambient gate against the reference model; more than three variants miss the per-variant bound; the 64 px or 46 px scale gate misses after B3 (first try counting coverage in `PreserveAlphaCoverage` on a bilinear 4 x 4 sub-sample grid in place of whole texels; the authored-cutoff row above shows pine at 0.86 with texel counting); or the quad's covered area cannot be brought within 8 % by a cutoff between 0.05 and 0.95.

Stop rule: 2 watchdog periods, 4 build-plus-test attempts.

**For the user.**
- D7 (new, in the decisions table): whether the tree models get distance-stable leaf-texture mips.
- The cards now carry the model's close-up look outward. Against the model as it is drawn today at 325 m they are 11 % darker (maple) and 9 % brighter and greener (pine) at noon in the game's light; fir is within 6 %. TR-7b's switch strip will show whether that is visible.
- Five pine models (v23 to v27) are 2.1 to 3.8 times brighter at noon from one side than from the other (v26 the most). The model shader reverses the up-pointing needle normals on faces seen from behind, and these models' needle planes mostly face one way. The cards show the average of the two sides, so single pines can change tone at the hand-off. It is a property of the models at every distance, like v14's inverted normals; changing it means changing how the near trees are lit, which is a separate question.
- Texture memory: +10.5 MB for the quad's own alpha mips.

#### 46 px gate decision (2026-11-15)

Fix order TR-7b-2 was implemented (uncommitted). Every gate passes except quad against cards at 46 px: maple 1.150 / fir 1.117 / pine 1.097 against 15 %. Measured on the RTX 4070 through a temporary `--lead-diag` mode of the test (removed; the test file is byte-identical to the implementer's, sha1 465df89f). Ambient only, 12 azimuths (15 + 30 k) per variant. Evidence in `.godot/tree_billboard_match/lead46/`: `diag.csv`, `analyse.py`, `lead_diag_mode_patch.py.txt` (the mode, to rerun).

**Decision: the quad comes down to the cards; the cards are not changed.** The quad's cutoff is calibrated against the cards' covered area at 46 px (the pixel size at which the cards hand over to it), not against the shipped model at 128 px. The gate stays and is tightened to 10 %. A change to the test's calibration and gates only: no shader or C# change, calibration version stays 3 (the resource is regenerated).

**Measurements** (species means of the per-variant ratios, maple / fir / pine).

| | maple | fir | pine |
|---|---|---|---|
| Cards, 46 px against 128 px | 0.894 | 0.937 | 0.890 |
| Quad, 46 px against 128 px | 1.018 | 1.013 | 0.972 |
| Quad / cards at 46 px (4 azimuths gave 1.150 / 1.117 / 1.097) | 1.148 | 1.055 | 1.102 |
| Shipped model, 46 px against 128 px (64 px: 0.846 / 0.360 / 0.656) | 0.720 | 0.239 | 0.481 |
| Reference model drawn at 46 px against its supersampled area | 0.937 | 0.999 | 0.925 |
| Cards at 46 px / reference model (all 30: 1.050) | 1.136 | 0.820 | 1.028 |
| Quad at 46 px / reference model (all 30: 1.173) | 1.304 | 0.870 | 1.129 |
| Cards at 46 px / shipped model at 46 px (variants with any pixels) | 1.42 | 1.39 | 3.04 |
| Quad at 46 px / shipped model at 46 px | 1.62 | 1.51 | 3.37 |
| Cards, the 46 px mips sampled densely (128 px, mip bias 1.476) against plain 128 px | 0.891 | 0.927 | 0.910 |
| Cards without anisotropic filtering, 46 px against 128 px | 0.918 | 0.921 | 0.891 |
| Cards with the quad's cutoffs, 46 px against 128 px | 0.933 | 0.898 | 0.905 |
| Quad with the cards' cutoffs, 46 px against 128 px | 0.967 | 0.990 | 0.952 |
| One front-facing side card alone, 46 px against 128 px | 0.878 | 0.867 | 0.935 |

One side card alone by the angle between the view and the card's normal, all variants: 15 degrees 0.961, 45 degrees 0.937, 75 degrees 0.783 (0.874 without anisotropic filtering).

**Findings.**
1. *The cards are the closer tier at 46 px.* Both tiers are calibrated at 128 px to the shipped model, which there covers 1.28 / 0.83 / 1.17 of the reference model. At 46 px the cards are closer than the quad to the reference model in 25 of 30 variants and to the shipped model in 25 of 30 (the quad is closer to the reference for v13, v16, v18, v19 and v20). The shipped model loses area towards 46 px for every species, and the reference model drawn at 46 px loses 6 to 8 % for maple and pine; only the quad keeps its 128 px area.
2. *The cards' loss is not a defect of the calibrated mips, and not overlap.* A single card loses as much as all the cards together, so the crossed planes are not the cause. The loss is in the texture content (the dense sampling of the 46 px mips reproduces it), it remains at the quad's low cutoffs, and anisotropic filtering changes it by under 2.5 %. It grows with the card's angle to the view: 4 % nearly face-on, 22 % at 75 degrees. A card seen at an angle is filtered over a wider footprint than the bilinear tap `PreserveAlphaCoverage` builds for (anisotropic taps, or about two more mip levels without them, where a picture is 3 to 10 texels in a layer that holds 4 to 9 pictures), and averaged alpha at the crown edge falls under the cutoff. The quad shows one picture face-on and keeps its area. *Not verified:* which part of that footprint (taps, level blend, neighbouring pictures) carries most of it.
3. *The gate was at its noise edge.* Maple is 1.150 with 4 azimuths and 1.148 with 12; fir is 1.117 and 1.055 (3 variants). The quad's picture is the same from every azimuth (spread 1.5 %), so at 46 px it is one sample of 40 to 100 pixels per variant.
4. *Predicted after the change* (linear between each variant's two measured cutoffs; fir is an extrapolation): quad cutoffs about 0.44 / 0.69 / 0.47 (now 0.342 / 0.665 / 0.391), quad / cards at 46 px 1.01 / 1.01 / 0.97, quad at 128 px against the shipped model 0.89 / 0.98 / 0.88.

**Rejected.**
- Keeping the cards' area to 46 px (per-card coverage preservation, or a cutoff that follows the view angle): not small, and it moves the cards from 1.05 to about 1.17 of the reference model.
- Leaving the gate, or widening it: the step has the same sign for every species (10 to 15 % more crown area beyond 900 m), and maple passes or fails on 0.0002.
- Gating each tier against the model at 46 px: the shipped model at 46 px keeps 24 to 72 % of its area (v12 and v13 have no pixels at all), so it cannot be a target, and against the reference model both tiers carry the 128 px calibration (1.28 / 0.83 / 1.17).

**Fix order TR-7b-3 (implementer, size S).** `tools/tree_billboard_match_gpu_test.gd` only; keep out of the `--dump` code (`capture_dump`, `dump_images`, `_write_dump`), which another order is changing. This replaces "the shipped model" as the quad's coverage target in C2 and C4 above; the cards' target and every other gate stand.
1. Constants: `HANDOFF_SIZE := 46` (the viewport edge at which a 325 m tree has its 900 m pixel size); `QUAD_AGAINST_CARDS_TOLERANCE := 0.10`.
2. New `_cards_at_handoff() -> Array[float]`: per variant, the mean over 12 azimuths (15 + 30 k) of `_measure("card", variant, CARD_DISTANCE, AMBIENT, azimuth, HANDOFF_SIZE)[0]`.
3. `_run_calibration`: keep the joint scan. Then bracket and bisect the cards alone against `ship_cover` (7 steps, `ApplyImpostorCalibration(cutoffs, cutoffs)`), set the final card cutoffs, apply them, and take `handoff := await _cards_at_handoff()`. Then bracket the quad from `quad_cover` against `handoff[variant]` and bisect it (7 steps, `ApplyImpostorCalibration(cutoffs, quad_cutoffs)`, the quad measured at 128 px and 900 m as now). Add `quad_target_cards_at_46` to each `cutoff_note`. The quad is calibrated at 128 px because its 46 px image is too few pixels to bisect on (finding 3) and it holds its area between the two sizes.
4. `_evaluate` takes the coverage target per variant and its name in place of reading the shipped table: the cards and the control get the shipped model's ambient covered fraction (as now), the quad gets `_cards_at_handoff()`, measured in `_run` after `capture_dump = false`. The 8 % per-variant limit stays for both.
5. `_scale_stability` takes the same array and uses it for `quad_over_cards_46`; `cards_64`, `cards_46` and `quad_46` stay as they are.
6. Report only: the quad at 128 px against the shipped model at 128 px, species means and per variant (`quad_over_shipped_model_128`), and the target array. Update the header's gate list, the `--calibrate` line and the summary print.
7. Run `--calibrate`, then verify, then the other tests of the TR-7b-2 validation list. The card cutoffs must come out unchanged.

Escalate if: quad against cards at 46 px misses 10 % for a species after the recalibration; a quad variant has no crossing inside the scan (0.05 to 0.88; v12 and v13 are the nearest, at 0.78 and 0.74 today); or a quad sun or ambient gate that passes today fails.

**For the user.**
- Nothing is boosted or widened. Beyond 900 m a far tree covers what its cards cover at 900 m, which is about 11 % less crown area than the uncommitted state for maple and pine and 2 to 6 % less for fir. `UseFarTreeQuads` has been off by default, so no shipped picture changes.
- For D7: at the pixel size of 900 m the tree models as drawn today keep 72 % (maple), 24 % (fir) and 48 % (pine) of the area they have at 325 m, and the two small firs (v12, v13) draw no pixels at all. With the user's 550 to 900 m fade preference those are the models on screen at that distance.

### TR-7c. Distance-stable leaf mips for the tree models (design and order, 2026-11-15)

ROLE: lead design (this section), then implementer
SIZE: M for the preview (TR-7c-PV); M plus S for the two orders behind the gate
DEPENDS ON: user D7 (answered yes). TR-7c-PV depends on nothing else. TR-7c-1 and TR-7c-2 depend on **user D8** (approval of the preview) and on TR-7b-3 being handed back.

**Gate (the user, 2026-11-15: "Hold off on this change until I see it first myself").** Only TR-7c-PV below is authorised. It writes under `.godot/tr7c/` and adds new files under `tools/`; it writes nothing to `resources/`, does not touch the prepared catalog, a shader, a runtime script or `tools/tree_billboard_match_gpu_test.gd`, and changes no shipped picture. TR-7c-1 (the shipped resource, loader and shader) and TR-7c-2 (test gates and recalibration) start only after the user has seen the preview sheet and said yes (D8).

Checked headless on the uncommitted tree (TR-6b, TR-7a, TR-7b, TR-7b-2, TR-9a and the v14 repair in the working tree, TR-7b-3 in progress). No GPU run. Evidence in `.godot/tr7c_lead/`: `probe_uv.gd` (read-only probe of the prepared catalog), `lod2_tris.csv` (its output), `leaf_mips_reference.py` (the rule, kept as the reference for the C# builder) and `leaf_mips_reference_output.txt`.

#### Facts

1. *Three atlases, three cutoffs.* Every variant of a species reads one leaf atlas with one cutoff, in LOD0, the authored LOD2 and LOD2b alike (LOD2b has no materials of its own; the loader gives it the LOD2's):

| | atlas | size | `alpha_cutoff` | variants | covered share of the area the LOD2 cards show |
|---|---|---|---|---|---|
| maple | `maple/textures/Cluster_Mat_baseColor.png` | 2048 | 0.25 | v0-11 | 0.371 |
| fir | `fir/textures/Brunches_Mat_baseColor.png` | 4096 | 0.376 | v12-14 | 0.385 |
| pine | `pine/textures/Clusters_Mat_baseColor.png` | 2048 | 0.25 | v15-29 | 0.313 |

   So "textures shared between variants" needs no per-variant copies: one mip chain per atlas, three in all. The cutoff is the material's `alpha_cutoff`, which TR-3 passes to `ALPHA_SCISSOR_THRESHOLD`. The LOD3 billboard pictures are not involved (the cards have their own bake).
2. *The atlases are far finer than the screen.* LOD2 foliage has 500 to 700 texels per metre (maple), 800 to 1,450 (pine; saplings 2,500) and 2,300 to 4,100 (fir). At 1080p (0.37 m per pixel at 325 m, the match test's lens) level 0 is on screen only closer than about 1.5 m (maple) and 0.3 m (fir). At 10 m a maple reads level 2.8, at 40 m level 4.8. At 325 m the whole atlas is 8 to 11 texels across for maple (level 7.5 to 8.0), 2 to 7 for pine (8.2 to 9.9) and 3 to 5 for fir (9.7 to 10.6); at 900 m add 1.5 levels. A leaf cluster is a quarter of an atlas, so at the distances D7 is about one cluster is one to three texels.
3. *Why the model drifts.* At those levels the importer's alpha is close to the atlas mean (0.22, 0.27, 0.21) tested against 0.25, 0.376, 0.25: maple and pine sit near the cutoff and fill in, fir is under it and vanishes. Prototype, leaf-card surface area against the close-up cutout, isotropic sampling (before cards overlap on screen, so the on-screen change is smaller; the GPU measurements of escalation 2 are the on-screen ones):

| distance | maple today / stable | fir today / stable | pine today / stable |
|---|---|---|---|
| 10 m | 1.12 / 1.00 | 1.24 / 1.00 | 1.33 / 1.00 |
| 20 m | 1.24 / 1.00 | 1.33 / 1.00 | 1.41 / 1.00 |
| 40 m | 1.41 / 1.00 | 1.43 / 0.98 | 1.52 / 1.00 |
| 80 m | 1.64 / 1.01 | 1.39 / 1.02 | 1.57 / 1.00 |
| 160 m | 1.77 / 1.00 | 1.05 / 1.03 | 1.50 / 1.00 |
| 325 m | 1.70 / 1.01 | 0.31 / 1.05 | 1.09 / 0.92 |
| 650 m | 1.14 / 0.98 | 0.00 / 0.91 | 0.74 / 0.68 |
| 900 m | 0.57 / 0.89 | 0.00 / 0.43 | 0.13 / 0.56 |

   The colour drift is reproduced as well: with the importer's mips the visible colour is 0.63 to 0.73 in blue for pine and 1.08 to 1.14 in red for maple at levels 6 to 9 (escalation 2 measured 0.70 and 1.10); with covered-only linear-light colour all three species are within 3 % in red and green and 2 % in luminance, except fir (limit 3 below).
4. *The drift starts near the camera, not at a few tens of metres.* D7's text said "from a few tens of metres outward". The table shows the model already draws 12 to 33 % more leaf surface at 10 m than close up. The preview therefore shows near tiles, and the user should expect the near and middle forest to change too (maple and pine crowns more open, fir slightly more open up to about 80 m and fuller beyond 160 m).
5. *Leaf albedo is an ordinary import, not a streamed one* (`docs/godot-48-native-texture-streaming.md`: alpha foliage is excluded because the importer has no alpha-coverage option). The three atlases are fully resident today: 5.6 + 22.4 + 5.6 MB as DXT5 with mips.
6. *The world fingerprint and the tile cache do not see textures* (`tools/WorldGenerationFingerprint.cs:167-182` hashes names, sizes and array lengths; the tile cache mixes `CatalogVersion`). Decision D1 holds whatever is chosen here.

#### Design

**A. Where: offline, in a sibling prepare tool, as its own checked-in resource.** `tools/prepare_tree_leaf_mips.gd` (headless) writes `resources/trees/prepared/lolipop_leaf_mips.res` and a manifest, by the rules of design C4, exactly as TR-4 and TR-7a do. A loader swaps the leaf materials' `albedo_texture` when the catalog is hydrated. Missing or stale: the import's own mips are drawn, with one warning.
- Not in `lolipop_catalog.res` or `tools/prepare_tree_assets.ps1`: that pipeline forces an editor reimport and stages and promotes the catalog, and every file in `tools/PreparedTreeAssets.targets` changes the implementation identity. The catalog already carries the uncommitted v14 repair; it stays byte-identical here. The hook goes in `ImportedTreeCatalog.Prepared.cs` (`HydratePrepared`), which is not in the identity list.
- Not import settings: the importer has no coverage option, cannot ignore cut-out texels in the colour, knows nothing about the cutoff or the meshes, and any `.png.import` change stales the catalog's source fingerprint.
- Not at load: a 4096 atlas would have to be decompressed, rebuilt and recompressed on every start (an estimated second or more, not measured). The cards can do it at load only because their layers are 256 px and uncompressed.
- Texture type: an `ImageTexture` whose `Image` has the import's format and size (DXT5, full chain). Level 0 is the import's level 0, copied byte for byte, so nothing changes where level 0 is drawn and every tool that reads the base level (`TreeLodDecimator.Coverage`, the TR-7a atlas bake) reads the same bytes as today.
- Memory: unchanged once loaded (the same format and size replace the import, which should be released when no material holds it; *unverified*, the preview reports texture memory). During load both exist (33.6 MB for a moment) and the import is still read from disk; a later catalog re-prepare could drop that dependency. Disk: about 34 MB more in the repository (less if the resource is saved compressed). Load time: one more threaded resource load; not measured (performance runs are paused), TR-8 and the load-time agenda measure it.
- Determinism: CPU only, fixed iteration order, bisection on doubles, sRGB through a 256-entry table. The gate is "two builds on one machine give the same hash" plus properties of the stored resource (below); byte equality across the two machines is not gated (block compression may differ by CPU).

**B. Alpha mips: the covered fraction, scaled per leaf cluster so the passing area is the close-up area.** The rule is `leaf_mips_reference.py`; the C# builder is a port of it.
1. `mask` = level-0 alpha >= the cutoff in use. `cover_L` = box average of `mask` (the covered fraction of each mip texel). The mask, not the raw alpha, so half-transparent texels count the way the near shader cuts them.
2. `usage` = a 512 x 512 map of foliage surface area per UV cell, from the LOD0 and LOD2 foliage triangles of every variant of the species, each (variant, LOD) normalised to 1. The cards show only part of the atlas (48 / 70 / 78 % of it); passing area outside the cards is never drawn and must not count. Without this weighting the 4 x 4 level of maple keeps 0.72 of the area.
3. Islands = 4-connected components of the 64 x 64 `cover` grid (cells with any covered texel, no wrap); components under 1/256 of the covered mass join the nearest; every cell belongs to the nearest island (ties to the lower index). Maple has 5, fir 4, pine 4.
4. Per level with at least 4 x 4 texels and per island: one scale s, found by bisection (26 steps on 0 to 64), so that the usage-weighted area where the bilinearly filtered (repeat) `clamp(round(255 cover_L s))` reaches the cutoff equals the usage-weighted covered area at level 0 inside that island. Three passes over the islands in index order (neighbours share texels at coarse levels). The 2 x 2 level gets one scale for the whole atlas. The 1 x 1 level is the largest byte below the cutoff: it never passes by itself (see limits).
5. Why per island: one scale per level, as `PreserveAlphaCoverage` uses for the cards, keeps the species total but not each tree. The small pines (v27-29) use other clusters than the large ones and would keep 0.58 to 0.90 of their needles from 80 to 330 m, and two maple saplings would draw 1.2 times theirs beyond 600 m. Per island every variant is within 1 % down to 16 x 16 texels, 2 % at 8 x 8 and 7 % at 4 x 4.

**C. Colour mips: linear-light mean of the covered level-0 texels.** Per mip texel, sum(linear colour x mask) / sum(mask), both sums smoothed with two passes of [1 2 1] in each axis before dividing; texels with no covered texel take the next coarser level (pull-push), so filtering across a cut edge never pulls in the dilated background colour. No multiplier anywhere. The importer's mips average gamma-encoded bytes over all texels, cut-out ones included; that is the source of the maple red and the lost pine blue.

**D. Compression.** Each built level is compressed alone (`Image.Compress` to the import's format) and the bytes are concatenated behind the import's level 0 with `Image.CreateFromData(w, h, true, format, data)`; the total length must equal the import's. The tool then decompresses its own output and repeats the coverage table on it. This does not depend on whether `Image.Compress` keeps or regenerates mips. *Unverified:* that DXT5 keeps the scaled alpha within 2 % of coverage at every level; the tool gates it.

**E. Shader: the leaf alpha is read isotropically.** `imported_tree.gdshader` samples with `filter_linear_mipmap_anisotropic` (`project.godot` does not set the level, so the engine default applies; 4x assumed, *unverified*). An anisotropic tap set reads a finer level averaged along one axis, and a coverage-preserving level is only right when read as built. Prototype, a footprint of 2 and 4 texels along one axis: maple keeps 0.90 and 0.67 at level 7 and 0.80 and 0.50 at level 8; fir 0.83 and 0.00 at level 10; pine 0.89 and 0.71 at level 7. For a crown with leaf cards at all angles that is roughly 9 % (maple, 325 m) to 18 % (fir, 325 m) less leaf surface, and it depends on the view angle (cards seen from above are face-on, from the side edge-on), which is the step the stable mips are meant to remove. It is the same erosion the far cards show at 46 px (46 px gate decision, finding 2).
- Change (TR-7c-1): a second sampler on the same texture, `uniform sampler2D leaf_alpha_texture : filter_linear_mipmap, repeat_enable;` and `uniform bool leaf_alpha_isotropic = false;`. Where cutout is on and the flag is set, `ALPHA = texture(leaf_alpha_texture, UV).a * albedo_tint.a`; the colour keeps the anisotropic read. The loader sets both with the texture. The default keeps every other user of the shader (the dither test's solid texture, the source fallback) as it is. One more texture read per leaf pixel, in the colour and shadow passes; TR-8 measures it.
- Near look: on leaf cards seen at a grazing angle the outline comes from a level one or two coarser than today, so it is softer close up. The preview shows it at 20 m. If the user dislikes it, the fallback is to fade the isotropic read in between levels 2 and 4 (`textureQueryLod`), not to drop it.
- The preview measures the stable mips both ways. If the anisotropic read is within 5 % of the isotropic one for every species at 128, 64 and 46 px from the side and from 60 degrees above, the shader change is dropped.

**F. Limits (nothing is added to hide them).**
1. Below 4 x 4 texels the atlas cannot carry the coverage: alpha test on a 2 x 2 or 1 x 1 level is all or nothing per cluster. The 1 x 1 level is set to fail, so a tree whose clusters are under a pixel loses its leaves gradually and never gains area. That is trees 2 to 5 px tall: the saplings beyond about 300 m, the small pines and the two small firs (v13, v14) beyond 650 m (table above: fir 0.43 and pine 0.56 at 900 m, against 0.00 and 0.13 today; the large trees of both keep 0.9 or more). Setting it to pass would draw every card solid (2.6 to 3.2 times the leaf surface): a boost, rejected.
2. Halfway between two levels the species area moves by up to 5 % down to 8 x 8 texels and 3 % between 8 x 8 and 4 x 4 (single variants 8 %). Between 4 x 4 and 2 x 2 it falls by up to 22 %, and below that further (limit 1).
3. Fir is 3 to 6 % darker than close up between 20 and 200 m in the prototype (today 3 to 4 %): the texels that pass are the dense twig centres. The smoothing in C recovers 1 to 5 points at the two coarsest of those levels. If the preview shows more, widen that neighbourhood; never multiply.
4. Level 0 does not change, so the magnified near look (closer than 0.3 to 1.5 m) is identical.

**G. What it does to the neighbours.**
- *TR-4 coverage rule and LOD2b.* The rule already measures the close-up cutout (level-0 alpha at the authored cutoff, `TreeLodDecimator.Coverage`). Today the drawn LOD2b departs from it because scaled survivor clusters read other mip levels than the authored ones; with stable mips the drawn model is what the rule measured. Rule, constants and `lolipop_render_lods.res` do not change (its hash is over the LOD2 arrays). v13 and v14 keep their authored LOD2. D2 was judged on sheets drawn with today's mips, so TR-7c-2 redraws three of them for the user.
- *TR-5 dither test.* `tree_lod_dither_gpu_test` uses a solid opaque texture and the flag defaults to off: no change, it must pass as it is. In the game the 25 to 40 m and mid hand-offs get both sides at the same crown area by construction.
- *TR-7a atlas bake.* Reads level 0: not regenerated.
- *Billboard match test.* The shipped model comes to within the gates of the reference model, so: the shipped model gets the gates the cards have against the reference (area, ambient, sun, 64 and 46 px scale); `handoff_step` is tightened from 25 % to the sun tolerances; the reference shader copy must also replace the new alpha read. Calibration: `--calibrate` is rerun (version 3 stays); the cards' target, the shipped model's area at 128 px, moves by about -22 % (maple), +18 % (fir), -15 % (pine), so the card cutoffs move towards the authored ones, and the quad follows the cards at 46 px as TR-7b-3 defines.
- *Tree shadows.* The shadow meshes duplicate the materials after the swap, so shadows cut with the same mips.
- No grave-key row: this is not a tunable. The A/B is the loader's diagnostics switch.

**Rejected.**
- A mip bias or a mip clamp on the model: partial, and it shimmers.
- Hashed (stochastic) alpha in the shader: it keeps area at every level without special mips, but it turns every leaf edge into per-pixel noise from a few metres on (there is no temporal anti-aliasing to hide it) and changes the near look far more than D7 describes.
- One scale per level for the whole atlas: B5.
- Building the far look into the cards instead: rejected in escalation 2.

#### TR-7c-PV. Preview only (implementer, size M). The only step authorised.

Objective: the stable mips in a scratch folder and a before/after contact sheet for the user. No shipped file changes.

Files, all new: `tools/TreeLeafMipBuilder.cs` (the rule, B to D), `tools/PrepareTreeLeafMips.cs` and `tools/prepare_tree_leaf_mips.gd` (headless tool), `tools/TreeLeafMipPreviewDriver.cs` and `tools/tree_leaf_mip_preview.gd` (GPU tool), and two `tool` entries (tier manual) in `tools/test_manifest.json`, added after TR-7b-3 is handed back. Do not edit anything under `resources/`, `materials/`, `scripts/`, or `tools/tree_billboard_match_gpu_test.gd` (copy what is needed from it).

1. Builder: a port of `leaf_mips_reference.py`. Input: level 0 of the imported texture decompressed to RGBA8, the cutoff, the usage map. Output: the mip chain and a table per level (per-variant passing area against level 0: mean, minimum, maximum; the same halfway to the next level; the same on the decompressed output).
2. Tool: group the cutout foliage materials of `ImportedTreeCatalog.Load()` by albedo texture; fail if one atlas has two cutoffs. Build, compress (D), and save one `ImageTexture` per atlas plus `manifest.json` and `leaf_mips_table.md` under `res://.godot/tr7c/preview/` (the default output of this step; it refuses any path outside `res://.godot/`). It must fail when: level 0 differs from the import by a byte; the data length differs; a level with at least 8 x 8 texels is off by more than 2 % for the species mean or 5 % for a variant (decompressed output); the 4 x 4 level is off by more than 10 % for a variant; a second build in the same run gives another hash. Compare the table with `leaf_mips_reference_output.txt`: within 2 points at every level down to 8 x 8 (the reference used the PNG and LOD2 only).
3. Preview (GPU): the match test's scene (its lens, haze off, white ambient and noon as in the D7 sheet), materials duplicated in the tool, never the catalog's own. Three looks: today (as shipped); stable (the preview texture, alpha through a tool-local shader copy with the isotropic sampler of E); stable with the shipped shader. The model is what the shipped defaults draw at each distance: LOD0 at 20 m, then the authored LOD2 or LOD2b (`ImportedTreeRenderLodCatalog.GetLod2b`) by TR-5's mid fade (`CompactTreeMidFadeBounds`).
   - User sheet `.godot/tr7c/preview/tr7c_preview_sheet.png`: v3 (maple), v13 (fir), v19 (pine); per species two rows (ambient, noon) and ten columns: today and stable at 20 m (crown, 256 px), 80 m, 325 m (128 px), 650 m (64 px) and 900 m (46 px). Far tiles are drawn at their real pixel size and enlarged with nearest filtering; labelled.
   - Detail sheet `tr7c_preview_detail.png` for the lead: at 325 m and 900 m, today, stable, stable with the shipped shader, and the reference model (albedo at level 0, 512 px).
   - Numbers, all 30 variants, 12 azimuths: covered area, ambient colour and noon sun-only luminance of the three looks at 128, 64 and 46 px against the reference model; the same from 60 degrees above at 128 px; LOD2b against the authored LOD2 with stable mips at 128 px. `preview_table.csv`, `preview_report.json`.
   - Dolly: v3 and v13, 15 m to 420 m in 300 frames, today beside stable; `mp4`, and `flicker` on each half. The stable half must not have larger spikes than today's.
4. Expected (report, do not tune to reach them): stable against the reference model, species mean: covered area within 10 % at 128 px, 15 % at 64 px, 20 % at 46 px; ambient luminance within 5 %, each channel within 8 % of the luminance; noon within 10 %.
5. Run: `tools/run_tests.ps1 -Tests prepare_tree_leaf_mips -Label tr7c`, then `-Tests tree_leaf_mip_preview -Label tr7c` (separate runs; the GPU queue decides when). Clean up as AGENTS.md says: keep the two sheets, the mp4, the tables and the scratch textures; delete the frames. `tools/prune_test_output.ps1 -Apply`.
6. Hand the sheet, the mp4 and the species table to the primary agent for the user. Stop there.

Escalate if: the concatenated chain does not load or does not keep the built levels; the stable look's level-0 colour differs from today's (the `ImageTexture` is not read as sRGB the way the import is); DXT5 breaks the 2 % gate; an expectation of item 4 is missed for a species; the anisotropic and isotropic looks differ by less than 5 % everywhere (the shader change is then dropped); or the dolly shows shimmer or steps the importer's mips do not have.

Stop rule: 2 watchdog periods, 4 build-plus-test attempts.

**TR-7c-PV DXT5 note (2026-11-15).** First run of `prepare_tree_leaf_mips`: the rule as specified (open loop: build the levels, then compress each alone) misses the DXT5 gates 13 times (species mean beyond 2 % at 16 x 16 and 8 x 8 for fir, maple and pine, e.g. fir 1.037, maple 0.976 and 1.031, pine 0.964 and 0.950; 4 x 4 variants beyond 10 % for fir 0.847..0.884 and pine 1.080..1.196). Cause: a DXT5 alpha block stores two endpoints and 8 interpolated values per 4 x 4 block, so values near the cutoff move by up to about a seventh of the block range, and an 8 x 8 level is only 4 blocks. Decision (primary): the scale bisection of design B is measured on the DXT5 round trip (compress, decode, measure) for levels up to 256 x 256, so the stored levels are the ones that were measured (closed loop; the preview set is `atlas_*_cl.res`, the open-loop set is kept for comparison). Closed loop leaves 3 misses: maple 8 x 8 species mean 0.978 (variants 0.967..0.986) and pine 4 x 4 one variant at 0.845 (0.845..1.017); fir passes everything. Those two gates are set to report-plus-bound at the measured DXT5 floor: maple 8 x 8 species mean within 3 % (also against the reference table, 2.4 points), pine 4 x 4 per variant within 20 %. Every other gate is as specified. TR-7c-1's builder and test inherit the closed loop and these two bounds.

#### TR-7c-1. Shipped resource, loader, shader (implementer, size M). Authorised (D8 answered yes, 2026-11-15).

1. `scripts/Vegetation/PreparedTreeLeafMips.cs` (schema and tool version, the catalog's `SourceFingerprint` it was built against, per atlas: source texture path, cutoff, size, texture) and `ImportedTreeLeafMipCatalog.cs`: `Apply(variants, resource)`, `ConfigureForDiagnostics(path, enabled)`, `FallbackReason`, one warning per cause. Stale means: fingerprint differs from the catalog resource's, a material's cutoff differs from the recorded one, or the size differs. Startup reads metadata only, never pixels.
2. Hook in `ImportedTreeCatalog.Prepared.cs` only: `HydratePrepared` applies it to every cutout foliage material (all four LODs) before anything duplicates materials; `PreparePreparedCooperatively` requests the resource through `RingThreadedResourceClaims` beside the catalog. Do not edit a file listed in `tools/PreparedTreeAssets.targets`; `lolipop_catalog.res` must stay byte-identical. The source fallback keeps the import's mips and its warning says so.
3. `materials/imported_tree.gdshader`: design E, unless the preview dropped it.
4. The tool's default output becomes `resources/trees/prepared/lolipop_leaf_mips.res` with a manifest (tool version, fingerprint, cutoffs, the table, artifact hash), staged and promoted as `prepare_tree_render_lods` does.
5. New headless `tree_leaf_mips_test` (tier gate, manifest entry): the stored resource against a fresh build (table within 0.5 points; hash equality reported, not gated); level 0 equals the import; stale and missing fall back; with the importer's mips the table must fail (the test can see the feature missing).
6. Driver: `UseLeafMips(bool)` and `LeafMipsLoaded()` on `ImportedTreeCatalogDriver` for the match test's control.
7. Validation: `tree_leaf_mips_test`, `imported_tree_prepared_catalog_test`, `imported_tree_gpu_regression`, `tree_lod_dither_gpu_test`, `tree_lod_decimation_test`, `tree_impostor_atlas_test`, `tree_lighting_shadow_gpu_test`, `vegetation_shadow_gpu_test`, `tree_atmosphere_gpu_test`, `gpu_tree_integration_test`, `native_texture_streaming_test`. `compare` at `forest_edge_eye` noon before and after: a visible change is expected and must be confined to tree crowns and their shadows; send the heatmap.

Escalate if: the catalog's identity or bytes move; a shadow test fails; the heatmap shows change outside the trees.

#### TR-7c-2. Match test and recalibration (implementer, size S). Authorised (D8 answered yes, 2026-11-15); after TR-7c-1 and after TR-7b-3 is committed.

1. `tools/tree_billboard_match_gpu_test.gd`: the reference shader copy also replaces `texture(leaf_alpha_texture, UV)` (fail if not found). New gates for the shipped model against the reference model: covered area within 10 % per species at 128 px, its 64 and 46 px area within 15 and 20 % of its 128 px value, ambient luminance within 5 % and channels within 8 % of the luminance, sun only within 10 / 10 / 15 %. `handoff_step` tolerance 25 % becomes 10 / 10 / 15 %. Control: with `UseLeafMips(false)` the model gates must fail for maple and pine.
2. `--calibrate`, then verify; the calibration resource is regenerated (version 3). Report the old and new card and quad cutoffs. Rerun `--dump`.
3. For the user: `tree_lod_contact_sheets` for v3, v13 and v19 (D2 was judged with the old mips); the TR-1 switch strip in both configurations; the 250 to 400 m dolly through `flicker`.
4. Tests: the TR-7b-2 validation list.

Escalate if: a model gate misses for a species; the card cutoffs leave 0.05 to 0.95; or a card or quad gate that passes today fails after recalibration.

**TR-7c-1/2 status (2026-11-15, implementer).** TR-7c-1 code, resource (lolipop_leaf_mips.res 33.6 MB), tool gates, tree_leaf_mips_test and the headless tests pass. TR-7c-2 ESCALATED: first `--calibrate` run (verify part) misses the model gates: fir and pine ambient luminance 0.919 and 0.947 (limit 5 %), channels 0.094 and 0.087 (limit 8 %), fir noon 1.137, and the 64 and 46 px scale of the model: fir 0.761 and 0.694, pine 0.762 and 0.649 (limits 15 and 20 %; the F1 limit: clusters under 4 x 4 texels thin out). Card gates that passed before: maple noon 1.105, v0 noon 1.214, v25 sun30 0.793, v2 channel 0.088. Control passes (importer mips fail all three species). Needs a lead decision (gates or rule).

**For the user.**
- Nothing is boosted, widened or multiplied. Where the atlas cannot carry the area (trees 2 to 5 px tall) the leaves thin out; they are never filled in.
- The change is not only far away. The models already draw more leaf surface than close up from about 10 m (fact 4). The preview's 20 m and 80 m tiles show what the near and middle forest will look like.
- Leaf outlines on cards seen edge-on become softer close up if the isotropic alpha read is kept (design E); the preview shows it.
- About 34 MB more in the repository; an 8 MB variant (levels 1 and up only, joined to the import at load) costs a texture read-back at every start and is not recommended.

#### TR-7c-2 lead decision (2026-11-15)

The first `--calibrate` run of TR-7c-2 missed gates on the model (fir and pine: area at 64 and 46 px, ambient colour, fir noon) and on the cards (maple noon, three variant bounds). Measured on the RTX 4070 on the uncommitted TR-7c-1 tree, through three temporary modes of the match test (removed; the test file is byte-identical to the implementer's, sha1 6bebd146, and the calibration resource was not written) and two read-only headless probes. No shader or C# file was edited: the shader variants were made the way the test makes its reference model (a test-local copy of the shader text). Evidence in `.godot/tree_billboard_match/lead7c2/` (`diag.csv`, `diag_floor.csv`, `diag_iso.csv`, `analyse_diag.py`, `analyse_floor.py`, `predict_cards.py`, the three `lead_diag*_mode_patch.py.txt` to rerun) and `.godot/tr7c_lead2/` (`colour_rule_reference.py` and its output, the reference for the new colour rule; `normal_bias.py`; `probe_levels.gd` and `levels.csv`; `probe_normals.gd`).

**Verdicts.**
1. *Model area at 64 and 46 px: not a floor. One cause, with a remedy that adds nothing.* The isotropic alpha read takes its level from the longer axis of the pixel footprint, so a leaf card seen at an angle reads one to three levels coarser than a card seen face-on, and reaches the 2 x 2 and 1 x 1 levels (which cannot carry the area, limit F1) long before the tree is small. With the alpha read limited to the 4 x 4 level the model keeps its area: 64 px 0.970 / 0.915 / 0.915 and 46 px 0.987 / 0.945 / 0.931 of its 128 px area (now 0.941 / 0.761 / 0.762 and 0.890 / 0.694 / 0.649), which is what the reference model itself keeps when it is drawn at those sizes (0.947 / 0.973 / 0.912 and 0.936 / 0.953 / 0.924). The gates stay at 15 and 20 %. F1's sentence "the large trees of both keep 0.9 or more" was wrong: the large pines (v18 to v20) kept 0.38 at 64 px and 0.20 to 0.31 at 46 px.
2. *Fir noon 1.137: the same cause.* The cards that vanish are the ones seen at an angle, so the visible set is not a fair sample of the crown's normals. With the 4 x 4 limit fir noon is 1.009 (v12 1.22 to 0.96, v13 1.22 to 1.05).
3. *Fir and pine ambient colour: a real defect in the colour rule, two parts, plus the block compression.* (a) The colour sums are not usage weighted, so at the coarse levels the smoothing pulls in clusters no tree draws: pine's 2 x 2 and 1 x 1 levels hold red 1.08 and green 0.96 of the close-up colour (the brown cluster of island 1, 1 % of the usage). (b) Limit F3 as predicted: fir's passing texels are the dense twig centres, 4 to 5.5 % dark at the 16 and 8 texel levels. (c) The DXT5 colour endpoints are 5:6:5 bits: at these dark colours one step is about 30 % of a red or blue value and 11 % of a green one in linear light, and the stored fir levels of 8 and 4 texels are 8 to 11 % low in red, 14 to 17 % low in blue and 2 % low in green (3 to 4 % in luminance). The remedy is the one F3 named (a wider colour neighbourhood, here the leaf-cluster island at the coarse levels), usage weights, and a snap of the flat colours on the DXT5 round trip. Every stored colour stays a weighted mean of covered level-0 texels: no multiplier, no offset to the rule's colour.
4. *Cards and quad: not a card defect and not the choice of target. It is the alpha threshold.* A card layer texel is about 15 cm, most texels are partly covered, and at any cutoff the cards draw the texels above it. Thinned to the true crown area (which the stable model now has) that is the densest half, which is not a fair sample: the cards' light against the reference model moves with the cutoff, for every variant. Calibrated to the reference model's area instead of the shipped model's, maple noon is 1.097 instead of 1.103, so the target stays the shipped model (the hand-off silhouette). The hand-off step, the thing the eye can compare, is inside 10 % and stays gated. The four misses become bounds at the measured floor (below), and an unbiased thinning is proposed as TR-7d for the user to decide at TR-8 (D9).
5. *`HashSources` hashing level 0 only is right.* The bake reads level 0 only (`TreeImpostorAtlasBaker.cs:72-77` and `TexelIndex`), and the manifest shows only the source hashes and the artifact hash moved. Two tidy-ups in the order (it calls `GetData()` twice, and `GetMipmapOffset(1)` fails on a texture without mips).
6. *Nothing here is a boost.* The level limit holds the close-up area and never exceeds it (64 px 0.94 / 0.88 / 0.91 and 46 px 0.96 / 0.91 / 0.93 of the reference model's area). It does change a picture the user has seen: see "For the user".

**Measurements.** Species means of the variant ratios, maple / fir / pine; 128 px unless a size is given; sun cases of the diagnostic runs over 6 azimuths (15 + 60 k).

The shipped model against the reference model, by alpha read:

| | as built (TR-7c-1) | alpha limited to the 4 x 4 level | limited to the 8 x 8 level | reference model drawn at the same size |
|---|---|---|---|---|
| Covered area | 0.964 / 0.944 / 0.948 | 0.969 / 0.965 / 0.995 | 0.952 / 0.937 / 0.987 | 1.011 / 1.010 / 1.014 |
| 64 px against own 128 px | 0.941 / 0.761 / 0.762 | 0.970 / 0.915 / 0.915 | 0.927 / 0.877 / 0.910 | 0.947 / 0.973 / 0.912 |
| 46 px against own 128 px | 0.890 / 0.694 / 0.649 | 0.987 / 0.945 / 0.931 | 0.921 / 0.896 / 0.924 | 0.936 / 0.953 / 0.924 |
| Noon | 1.075 / 1.153 / 0.922 | 1.078 / 1.009 / 0.907 | 1.096 / 1.237 / 0.920 | 0.997 / 1.004 / 0.997 |
| Sun 30 | 1.057 / 0.973 / 0.968 | 1.058 / 0.961 / 0.958 | 1.071 / 1.006 / 0.958 | 1.004 / 0.984 / 1.000 |
| Sun 8 | 1.009 / 0.930 / 0.963 | 1.007 / 0.939 / 0.961 | 1.010 / 0.942 / 0.959 | 1.005 / 0.984 / 0.999 |
| Noon at 64 px, 46 px | 1.116, 1.100 / 1.192, 1.238 / 1.022, 1.001 | 1.131, 1.112 / 1.058, 1.170 / 0.982, 1.002 | 1.192, 1.209 / 1.224, 1.382 / 1.023, 0.985 | 1.050, 1.057 / 1.050, 1.191 / 1.079, 1.076 |
| Ambient luminance | 0.999 / 0.919 / 0.947 | 0.999 / 0.920 / 0.951 | 1.000 / 0.918 / 0.950 | 1.004 / 0.987 / 0.999 |
| Ambient r, g | 0.957, 1.011 / 0.881, 0.928 / 1.048, 0.922 | unchanged | unchanged | 1.012, 1.002 / 0.984, 0.988 / 1.008, 0.997 |
| Covered pixels that change when the camera turns a quarter pixel, 128 px | 0.224 / 0.217 / 0.301 | 0.223 / 0.214 / 0.274 | 0.220 / 0.253 / 0.292 | 0.433 / 0.437 / 0.534 |
| The same at 46 px | 0.452 / 0.583 / 0.800 | 0.441 / 0.399 / 0.593 | 0.444 / 0.411 / 0.558 | 0.681 / 0.422 / 0.775 |

The cards along the cutoff (one cutoff for all variants; area and light against the reference model):

| cutoff | maple area, noon, red | fir area, noon | pine area, noon, sun 30 |
|---|---|---|---|
| 0.35 | 1.67, 0.993, 0.991 | 1.11, 1.022 | 1.46, 1.013, 1.015 |
| 0.60 | 1.34, 1.034, 0.981 | 0.89, 1.034 | 1.03, 0.960, 1.000 |
| 0.74 | 1.08, 1.082, 0.970 | 0.70, 1.023 | 0.72, 0.914, 0.982 |
| 0.80 | 0.92, 1.109, 0.965 | 0.61, 1.053 | 0.57, 0.873, 0.969 |
| 0.86 | 0.75, 1.137, 0.957 | 0.53, 1.029 | 0.41, 0.847, 0.959 |

The cards recalibrated, from that scan (cards / reference at noon, sun 30, sun 8; the worst variant; the worst channel error):

| target of the cards' area | maple | fir | pine |
|---|---|---|---|
| Shipped model, as built | 1.103 / 1.023 / 0.975; 0.196; 0.086 (v2) | 1.038 / 0.965 / 0.960; 0.110; 0.028 | 0.953 / 0.995 / 1.000; 0.213 (v25 sun 30); 0.035 |
| Reference model | 1.097 / 1.023 / 0.979; 0.186; 0.086 | 1.036 / 0.974 / 0.972; 0.111; 0.019 | 0.956 / 0.998 / 1.000; 0.206; 0.031 |
| Shipped model with the 4 x 4 limit (what the order gives) | 1.102 / 1.023 / 0.976; 0.194; 0.086 | 1.041 / 0.969 / 0.964; 0.109; 0.028 | 0.953 / 0.994 / 0.997; 0.212; 0.031 |
| Hand-off step with the 4 x 4 limit (cards / shipped model; before the colour fix) | 1.023 / 0.967 / 0.969 | 1.033 / 1.008 / 1.026 | 1.056 / 1.040 / 1.039 |

Old and new cutoffs (TR-7b-3 against this run), species means: cards 0.63 to 0.79, 0.62 to 0.54, 0.53 to 0.64; the shipped model's area at 128 px moved by 0.68 to 0.85 (maple), 1.24 to 1.39 (v12, v13) and 0.72 to 0.87 (pine). Design G's sentence that the cutoffs would move towards the authored ones was wrong for maple and pine: a thinner target needs a higher cutoff.

The colour of the passing area against the close-up colour, prototype (`colour_rule_reference.py`, before compression): luminance with the rule as shipped, then with the new rule; levels by their size in texels:

| level | maple | fir | pine |
|---|---|---|---|
| 64 | 0.991 / 0.991 | 0.973 / 0.973 | 0.999 / 0.999 |
| 32 | 0.993 / 0.995 | 0.967 / 0.967 | 1.002 / 1.001 |
| 16 | 0.998 / 1.000 | 0.945 / 1.000 | 1.009 / 1.000 |
| 8 | 0.999 / 1.000 | 0.958 / 1.000 | 1.007 / 1.000 |
| 4 | 0.998 / 1.000 | 1.006 / 1.000 | 0.992 (r 1.05, g 0.98) / 0.996 |
| 2 | 0.999 / 1.000 | 1.010 / 1.000 | 0.979 (r 1.08, g 0.96) / 1.000 |

**Findings.**
1. *The test draws every variant at its species' typical placed height* (18, 11, 20 m). The authored heights are 1.5 to 32 m, so the sapling variants are drawn 5 to 13 times their authored size (fine levels: v24 to v26 hold 0.97 to 1.01 at 64 and 46 px as built) and the large pines at 0.63 of theirs. At 128 px the firs v12 and v13 read the 8 and 4 texel levels face-on and the 2 and 1 texel levels at an angle; that is why fir was the species that failed.
2. *The 4 x 4 limit is the better of the two.* The 8 x 8 level sampled at a pixel spacing wider than its texels aliases (fir noon 1.24); the 4 x 4 level is smooth enough to be sampled that sparsely. The limit does not add flicker by the quarter-pixel measure, and removes some at 46 px (cards no longer blink out as their level crosses 2 x 2). *Not verified:* motion with wind, and the look. The order measures a far dolly with `flicker` and redraws the preview sheet.
3. *Reading the colour isotropically as well does not help* (`diag_iso.csv`: fir 0.933, pine 0.945). The anisotropic colour read stays.
4. *Stored coarse levels* (`levels.csv`, decoded sRGB bytes): fir 2 x 2 and 1 x 1 are (66, 85, 41) for an intended (64, 86, 44); fir 4 x 4 mean (60.6, 85.0, 41.0) for (63.7, 85.9, 44.3); pine 2 x 2 (115, 125, 66) for an intended (112, 126, 67), where the usage-weighted colour would be (108, 129, 65).
5. *The cards.* Even at cutoff 0.05 the crossed cards cover twice the model (v3: 0.078 of the viewport against 0.040). At cutoff 0.35 they match the reference model's light within 2 % for every species; the drift grows with the cutoff and with the area removed (v0, v1, v9, v11: target area times 0.69 to 0.70, noon +10 to +16 points; v3, v4: times 0.84 to 0.85, no change). The maple saplings' red is the same effect (v2: channel error 0.021 at cutoff 0.35, 0.088 at 0.80).
6. *Not explained.* The stable maple model is 7.5 % brighter at noon and 5.7 % at sun 30 than the reference model, with or without the level limit (the reference model drawn at 128 px is at 0.997). An estimate from the LOD2 shading normals and the passing area (`normal_bias.py`) gives +1 % for maple and -4 to -7 % for the large pines (measured -8 to -15 % before the colour fix). Inside the gates. Also: the GPU is 2 to 4 points darker than the prototype at the 32 and 64 texel levels for pine (v24 to v26: 0.978 against 1.00).

**Decision.**
- The shader gets a coarsest alpha level (the 4 x 4 level), set by the loader with the texture. This replaces limit F1.
- The colour rule changes at the levels of 512 texels and smaller (usage weights) and of 32 texels and smaller (neighbourhood); flat colours are snapped on the DXT5 round trip. This replaces design C for those levels. The alpha levels must come out byte-identical.
- The model's gates stay exactly as TR-7c-2 item 1 wrote them (area 10 %, 64 and 46 px 15 and 20 %, ambient 5 % and 8 %, sun 10 / 10 / 15 %), and `handoff_step` stays at 10 / 10 / 15 %.
- The cards' and the quad's gates against the reference model become bounds at the measured floor of thresholded cards, named as such in the test: cards species mean 12 / 10 / 15 % (measured 1.103), card variants 25 / 25 / 25 % (measured 0.214 and 0.213), channel 10 % for cards and quad (measured 0.088 and 0.091). The quad's sun gates do not change. These bounds go back to 10 %, 20 / 20 / 25 % and 8 % if TR-7d is built.
- Rejected: bounding the model at its measured values (the cause is found and the remedy is small); calibrating the cards to a larger area to buy back light (1.10 times the reference area gives maple noon 1.076: it trades a wrong area for a smaller light error); a colour multiplier or offset per level; the 8 x 8 limit; an isotropic colour read.

**Fix order TR-7c-2b (implementer, size M).** It continues the uncommitted TR-7c-1 and TR-7c-2 tree. Do A to C first (headless), then D and E, then F.

A. `tools/TreeLeafMipBuilder.cs`: colour only. First copy `.godot/tr7c/ship/leaf_mips_table.md` to `leaf_mips_table_tr7c1.md`.
1. Usage weight per 512-grid cell: `u = usage / (mean of the cells with usage > 0) + 0.001`.
2. At the level with 512 texels multiply `num` and `den` by `u` per texel; the coarser levels are `Box` of those. Levels above 512 texels keep today's sums and two passes.
3. Colour per level of size s (texels): s from 512 to 64: two passes of `Smooth` (as now); s = 32: four passes (`Smooth` takes the count); s = 16, 8, 4: every texel takes the colour of its island (`Owners`, as the alpha uses), which is sum of `num` over sum of `den` (weighted, on the 512 grid) over the cells with `ownG == k`, or the atlas colour when that island's `den` sum is 0; s = 2, 1: every texel takes the atlas colour (the same sums over all cells). Pull-push as now where a texel has no `den`.
4. `Build` takes a colour codec beside the alpha codec. With it, every flat colour (one per island and the atlas colour) is snapped once before it is written: among the sRGB byte triples (r + i, g + j, b + k), i, j, k from -4 to 4, clamped, compress a 4 x 4 block of each, decode it, and take the triple whose decoded colour has the smallest E against the target, E = |Yd - Yt| / Yt + 0.5 max over channels of |cd - ct| / Yt in linear light (Y = 0.2126 r + 0.7152 g + 0.0722 b); ties to the smallest |i| + |j| + |k|, then the first in loop order. The alpha code is not touched.
5. New measure beside `AreaOf` and `AreaHalfway`: the usage-weighted mean linear colour over the 512-grid cells where the bilinear alpha passes (colour sampled bilinearly, repeat), for a level and for the half-way blend of two levels, against the usage-weighted mean of the covered level-0 texels.

B. `tools/PrepareTreeLeafMips.cs`
1. The colour codec is `CompressLevel` then a decode of the colour, the same call the final chain uses. Tool version `tr7c-2`; update the manifest's `note`.
2. The table gets, per level of 512 texels and smaller, on the decoded chain: luminance and r, g, b of the passing area against the close-up colour, at the level and half-way. Half-way means the blend with the next coarser level; skip a row where nothing passes. Gates (species usage): levels of 16 texels and smaller, luminance within 4 % and each channel within 8 % of the luminance (a flat colour can only sit on the 5:6:5 grid: for these six colours the best grid point is 0.96 to 1.03 in luminance and up to 8 % of the luminance off in one channel); levels of 32 to 512 texels, luminance within 5 % and each channel within 6 % of the luminance (fir is 2.7 to 3.4 % dark at the 64 and 32 texel levels: limit F3, kept as a bound). Compare the undecoded colours with `colour_rule_reference_output.txt` (column `hyb`): within 2 points at every level.
3. The alpha columns of the new table must be identical to `leaf_mips_table_tr7c1.md`. Re-prepare `lolipop_leaf_mips.res`.

C. `tools/TreeLeafMipsTest.cs`: the stored resource's colour table against a fresh build within 0.5 points; the colour gates of B2 on the stored resource; every swapped material has `leaf_alpha_max_lod` equal to log2(size) - 2; `ImportedTreeImpostorAtlasBake.HashSources` of every variant is the same with `SetActive(false)` and `SetActive(true)`.

D. Shader, loader, hash
1. `materials/imported_tree.gdshader`: `uniform float leaf_alpha_max_lod = 16.0;` with a comment (the coarsest level the isotropic cutout read may use; the loader sets the 4 x 4 level; coarser levels cannot carry the covered area, and a card seen at an angle reaches them first). In `fragment()`:
   `if (leaf_alpha_isotropic) { float leaf_alpha_lod = min(textureQueryLod(leaf_alpha_texture, UV).y, leaf_alpha_max_lod); ALPHA = textureLod(leaf_alpha_texture, UV, leaf_alpha_lod).a * albedo_tint.a; } else ALPHA = albedo.a;`
   The colour read does not change.
2. `ImportedTreeLeafMipCatalog.SetMaterial`: also set `leaf_alpha_max_lod` to log2(texture width) - 2 when the stable mips go on and 16 when they come off. `tools/TreeLeafMipPreviewDriver.cs:169-170`: the same for its "stable" look.
3. `ImportedTreeImpostorAtlasBake.HashSources`: one `GetData()` call; `int length = image.HasMipmaps() ? (int)image.GetMipmapOffset(1) : data.Length;` and `sha.AppendData(data, 0, Math.Min(length, data.Length))`. The hashes must not move (`tree_impostor_atlas_test` passes without re-preparing the atlas).

E. `tools/tree_billboard_match_gpu_test.gd`
1. `ALPHA_MIP_ZERO_FROM := "textureLod(leaf_alpha_texture, UV, leaf_alpha_lod)"` (the reference model still reads mip 0; fail if the text is not found).
2. `CARD_SUN_TOLERANCE := [0.12, 0.10, 0.15]`, `CARD_VARIANT_SUN_TOLERANCE := [0.25, 0.25, 0.25]`, `CARD_CHANNEL_TOLERANCE := 0.10`, with one comment: the measured floor of cards thinned by an alpha threshold (this section), to return to 10 %, 20 / 20 / 25 % and 8 % with TR-7d. `_evaluate` takes the species sun tolerance and the channel tolerance: the cards get the three new values; the quad gets `SUN_TOLERANCE`, its 40 % and `CARD_CHANNEL_TOLERANCE`; the control keeps `SUN_TOLERANCE` and `CHANNEL_TOLERANCE`. The model's constants and `handoff_step` do not change.
3. Update the header's gate list.
4. `--calibrate`, then verify. Report the old and new card and quad cutoffs.

F. Validation and pictures
1. Build; `tools/run_tests.ps1 -Tests prepare_tree_leaf_mips -Label tr7c`, then `-Tests tree_leaf_mips_test,imported_tree_prepared_catalog_test,tree_impostor_atlas_test,imported_tree_impostor_catalog_smoke,tree_lod_decimation_test`.
2. The match test with `TREE_BILLBOARD_MATCH_ARGS=--calibrate`, then without it, then with `--dump`.
3. `-Tests imported_tree_gpu_regression,tree_lod_dither_gpu_test,tree_shared_impostor_integration,tree_lighting_shadow_gpu_test,vegetation_shadow_gpu_test,tree_atmosphere_gpu_test,gpu_tree_integration_test,native_texture_streaming_test`.
4. `compare` at `forest_edge_eye` noon against the TR-7c-1 capture: the change must be confined to tree crowns and their shadows; send the heatmap.
5. `tree_leaf_mip_preview` again: the user sheet (it now shows the level limit and the new colours), and a second dolly, v13 and v19 from 300 m to 1,000 m in 300 frames with wind on, stable mips with `leaf_alpha_max_lod` 16 beside the 4 x 4 limit; `mp4`, and `flicker` on each half. The limited half must not have larger spikes than the other.
6. TR-7c-2 item 3 as written (the three D2 contact sheets, the TR-1 switch strip in both configurations, the 250 to 400 m dolly through `flicker`).
7. Clean up as AGENTS.md says; `tools/prune_test_output.ps1 -Apply`.

Expected (report, do not tune to reach them): model area 0.97 / 0.97 / 1.00; 64 px 0.97 / 0.92 / 0.92; 46 px 0.99 / 0.95 / 0.93; model noon about 1.08 / 1.05 / 0.94; ambient luminance 1.00, 0.95 to 0.98, 0.96 to 0.99; cards as the third row of the recalibration table; hand-off step within 6 %; pine card cutoffs lower than this run's (the pine model is 5 % fuller at 128 px), maple unchanged.

Escalate if: an alpha level changes by a byte; a colour gate of B2 fails; a model gate misses for a species (send the model rows and the tool's colour table); a card or quad value leaves the new bounds or `handoff_step` leaves 10 / 10 / 15 %; the far dolly's limited half has larger `flicker` spikes; the heatmap shows change outside the trees; or `textureQueryLod` fails to compile in the depth or shadow pass.

Stop rule: 2 watchdog periods, 4 build-plus-test attempts.

**TR-7c-2b colour note (2026-11-15).** First TR-7c-2b build (colour rule as ordered, snap on a flat opaque block) missed ten B2 colour gates after DXT5: fir 64 and 32 (luminance 0.947 and 0.940, channel g 0.063), maple 256 to 64 (red -0.067, -0.063, -0.075 of the close-up luminance), pine 8 x 8 (luminance 0.955) and pine 4 x 4 (luminance 0.829, g -0.277). The undecoded colours were on the lead's reference (hyb) within 2 points everywhere, so the rule was right and the loss was the block compression, present from the 512-texel level down (fir 0.976, maple 0.965 at 512).
- Baseline: the gates' close-up colour is level 0 as decoded from the import (what the player sees). The uncompressed source PNG gives nearly the same colour (usage-weighted linear luminance: fir 0.0783 decoded / 0.0782 PNG, maple 0.0579 / 0.0574, pine 0.1920 / 0.1921), so the baseline was not the cause.
- Decision (primary): colour is closed on the DXT5 round trip like the alpha. For each level of 4 to 512 texels `TreeLeafMipBuilder.Build` iterates a per-level linear RGB offset (up to 5 trials, trial 0 = no offset, best of the trials by E; deterministic) on compress -> decode until the decoded usage-weighted colour of the passing area equals the intended one. The alpha bytes are not touched: the alpha columns of the table are identical to `.godot/tr7c/ship/leaf_mips_table_tr7c1.md` (diffed). No multiplier; every stored colour is still a re-encoded estimate of the weighted mean of covered level-0 texels.
- Result: every B2 colour gate passes as written, including pine 8 x 8 and 4 x 4, so the proposed bound for those two levels was not needed and none was added. Decoded luminance against the close-up (level / half-way): fir 512 to 32 texels 0.999 to 0.963, flat levels 0.998 to 1.007; maple 0.999 to 1.003, flat 1.025; pine 0.990 to 1.006, 8 x 8 1.003, 4 x 4 1.001. Remaining channel offsets: maple red -0.060 at 32 texels and +0.055 on its flat levels, fir red +0.049 on its flat levels (all inside their 6 and 8 % limits; the 5:6:5 grid). Table: `.godot/tr7c/ship/leaf_mips_table.md` (copy `leaf_mips_table_tr7c2.md`).

- Validation (2026-11-15, RTX 4070): `prepare_tree_leaf_mips` pass; `tree_leaf_mips_test`, `imported_tree_prepared_catalog_test`, `tree_impostor_atlas_test`, `imported_tree_impostor_catalog_smoke`, `tree_lod_decimation_test` 5/5 pass; the eight GPU tree tests 8/8 pass; `tree_billboard_match_gpu_test` calibrated (version 3) and verified, pass, with control; smoke 12/12 pass. Card cutoffs (species means, maple/fir/pine): TR-7c-2 first run 0.786/0.544/0.639, now 0.784/0.524/0.619; quad 0.652/0.636/0.583, now 0.647/0.630/0.564; no variant without a crossing. Model: area 0.969/0.965/0.995, ambient 1.018/0.964/0.986, noon 1.095/1.063/0.942; hand-off step within 4.3 % everywhere. The calibrate run took 2,784 s (verify and the earlier calibrations 60 to 220 s): its log lacked `TestRunMouseRelease: focus returned to window`, the process sat idle (GPU 3 %), and a rerun with timing prints did 150 ms per calibration apply and 48 frames in 200 ms, so it was the window not being foreground, not the shader.
- Far dolly (v13, v19, 300 to 1,000 m, wind on): `flicker` top spikes limited / unlimited: v13 9.60, 9.43, 9.26 / 9.09, 8.57, 8.57; v19 9.95, 9.77, 9.60 / 9.95, 8.92, 8.92; mean change 1.134 / 1.204 (v13), 0.500 / 0.476 (v19). The limited half has more spikes above 9 (v13 3 against 1, v19 5 against 1): reported for the lead (escalation condition of F5).

**TR-7d (proposed, not authorised; D9).** Unbiased thinning of the far cards. Each mip level of the card alpha stores `1 - noise / coverage` (clamped; a fixed blue-noise value per texel and level, coverage the box-averaged baked coverage), so a texel passes a cutoff c with the probability coverage times (1 - c): the drawn texels are a fair sample of the crown at every cutoff and level, the covered area is linear in the cutoff, `PreserveAlphaCoverage` and the second (quad) array are no longer needed (10.5 MB and 30 mip builds at load saved). The crown beyond 325 m is then made of scattered texels instead of the densest clumps, which is a look the user has not seen. Size M, lead prototype first (CPU, on the baked level-0 data: light against the cutoff), then a preview sheet, then the catalog. Decide at TR-8 from the switch strips.

**For the user.**
- Nothing is boosted, widened or multiplied. The far models keep the crown area they have close up; before this order the conifers lost it with distance.
- This changes what the D8 preview showed at 650 m and 900 m: firs keep about 92 and 95 % of their 325 m crown area there (as first built: 76 and 69 %) and pines 92 and 93 % (76 and 65 %; the large pines had kept 38 % and 20 to 31 %). The redrawn sheet and a 300 to 1,000 m video come with the order.
- Firs were 8 % too dark and pines 5 % too dark and slightly brown at 325 m with the first build of the stable mips; the colour fix removes most of it. Firs stay 2 to 3 % darker than close up between roughly 15 and 150 m (limit F3).
- The far cards of maples are 10 % brighter at noon than the close-up model, because cards thinned to the true crown area are drawn from their densest parts. Against the model they replace at the hand-off the step is 2 to 6 %. D9 asks whether to change how the cards are thinned.

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

#### TR-8 results (2026-11-15, implementer)

Machine: RTX 4070 (reference configuration R), 1920 x 1080, Medium, Forward+, VSync off, clear weather, 2 rounds of 8 s windows per case (second reversed), noise floor = baseline against baseline_repeat. Tree layer forced with `--tree-set`: F1 fades (25/40, 300/350, mid 130/150, mid batch span 2), old caps and gates (nothing changed), `DisableGpuTreeEffectiveEdges:1` (TR-9a edges off so the hand-offs sit where TR-1 had them). BEFORE = `UseTreeRenderLods:0,UseFarTreeQuads:0` in the same session and build. HEAD ba1ab88 plus the harness edits below. All runs "pass"; the camera did not move in any window set. Harness switches via `LANDSCAPE_HARNESS_ARGS` (`--tree-lod=slots --poses=forest_edge_eye,forest_interior_eye --rs50=none --overdraw=none --tree-set=...`); the 1 / 0 spelling is needed for booleans (`_parse_tree_settings` reads `true` as 0).

**Cost at R (noon unless stated; ms of root GPU; slot deltas are baseline minus case, slots 1 + 2 is the sum of the two cases).**

| pose | renderer | root GPU | noise floor | all trees | slot 0 | slots 1 + 2 | tree shadows | slots 1 + 2 triangles (M) | draws | primitives (M) | trees drawn |
|---|---|---|---|---|---|---|---|---|---|---|---|
| forest_edge_eye | BEFORE (this session) | 10.69 | 0.03 | 4.25 | 0.10 | 2.31 (s1 0.03, s2 2.28) | 1.47 | 2.709 | 1,124 | 13.84 | 10,086 |
| forest_edge_eye | AFTER (edges off) | 9.59 | 0.01 | 3.14 | 0.06 | 1.18 (s1 0.36, s2 0.81) | 1.48 | 1.299 | 1,440 | 12.32 | 14,252 |
| forest_edge_eye | AFTER, effective edges on (reported) | 9.62 | 0.00 | 3.19 | 0.10 | 1.26 | 1.49 | 1.299 | 1,440 | 12.32 | 14,585 |
| forest_edge_eye | TR-1 third run | 10.79 | 0.01 | 4.22 | 0.09 | 2.32 | 1.48 | 2.709 | 671 | 13.84 | 10,086 |
| forest_interior_eye | BEFORE | 14.04 | 0.02 | 7.81 | 0.64 | 3.64 (s1 -0.03, s2 3.67) | 2.33 | 3.628 | 1,114 | 20.10 | 11,102 |
| forest_interior_eye | AFTER (edges off) | 12.19 | 0.01 | 6.01 | 0.73 | 2.05 (s1 0.65, s2 1.39) | 2.47 | 1.919 | 1,395 | 16.45 | 15,246 |
| forest_interior_eye | AFTER, effective edges on (reported) | 12.12 | 0.03 | 5.88 | 0.73 | 2.01 | 2.39 | 1.919 | 1,392 | 16.34 | 15,035 |
| forest_interior_eye | TR-1 third run | 13.96 | 0.05 | 7.75 | 0.52 | 3.73 | 2.31 | 3.628 | 1,233 | 20.10 | 11,102 |
| forest_edge_eye, sun 8 deg | BEFORE | 9.92 | 0.01 | 4.50 | 0.07 | 2.29 | 1.86 | 2.709 | 1,165 | 14.00 | 10,086 |
| forest_edge_eye, sun 8 deg | AFTER (edges off) | 8.86 | 0.02 | 3.43 | 0.08 | 1.20 | 1.79 | 1.299 | 1,482 | 12.48 | 14,252 |
| forest_edge_eye, sun 8 deg | TR-1 third run | 10.12 | 0.01 | 4.41 | 0.10 | 2.34 | 1.90 | 2.709 | 1,166 | 14.02 | 10,086 |

Notes: BEFORE reproduces TR-1's third run within 0.1 ms (all trees 4.25 / 7.81 against 4.22 / 7.75). "Draws" is the baseline frame's draw-call count; TR-1's table of draws removed per case is a different quantity, so the TR-1 draws cell is the baseline draw count from its tree_lod.md (`.godot/audit_baseline/rtx4070/tr1/tree_lod_slots/2026-11-14_17-47-22/`). Triangles are the probe's `submitted_triangles` per slot (slot 2 before the change is the only LOD2 slot; after, slot 1 is authored LOD2 and slot 2 is LOD2b). Trees drawn is the sum of the probe's drawn counts over the five slots (the quad tier adds about 4,000 trees: slot 4 draws 7,443 against 3,327 at edge, 7,787 against 3,715 at interior; with effective edges on, slot 3 falls from 5,200 to 4,712 / 4,724 and slot 4 rises to 8,264 / 8,052).

**Gates (design G, TR-8 items 1 to 3), at R, against BEFORE in the same session.**

| gate | forest_edge_eye | forest_interior_eye | verdict |
|---|---|---|---|
| G1 slots 1 + 2 GPU down at least 35 % | 2.31 to 1.18 (-48.9 %) | 3.64 to 2.05 (-43.7 %) | PASS (floor 0.01 to 0.03 ms; against TR-1's 2.32 / 3.73: -49.1 % / -45.0 %); sun 8 deg edge -47.6 % |
| G2 all-trees delta down at least 20 % | 4.25 to 3.14 (-26.1 %) | 7.81 to 6.01 (-23.0 %) | PASS (against TR-1's 4.22 / 7.75: -25.6 % / -22.5 %); sun 8 deg edge -23.8 % |
| G3 slots 1 + 2 triangles down at least 50 % | 2.709 M to 1.299 M (-52.0 %) | 3.628 M to 1.919 M (-47.1 %) | PASS at edge, **MISS at interior by 2.9 points** |

Where the G3 miss comes from (probe, interior): slot 1 (authored LOD2, 25 to 150 m) draws 383 trees and 0.635 M triangles (1,659 per tree); slot 2 (LOD2b) draws 1,841 trees and 1.284 M (697 per tree). BEFORE drew 2,152 LOD2 trees at 1,686 per tree. The interior pose has twice the trees inside 150 m that the edge has (383 against 189), and every one of them still draws the full authored LOD2; the LOD2b saving (2.4 times fewer triangles per tree) applies only beyond 150 m. Edge: slot 1 189 trees 0.281 M, slot 2 1,408 trees 1.018 M. Not fixed (stop rule).

Other observations, not gates:
- **Draw calls rise** (edge 1,124 to 1,440, interior 1,114 to 1,395) although primitives fall (-11 % and -18 %): slot 1 uses per-tile batches and the quad tier adds outputs. Trees drawn rise from 10,086 / 11,102 to 14,252 / 15,246 (the far quads).
- **Tree shadows** edge 1.47 to 1.48 (noise), interior 2.33 to 2.47 ms (+0.14). The noise floor is 0.01 to 0.03 ms, but the same AFTER configuration measured 2.39 ms in the effective-edges-on run, so run-to-run is about 0.08 ms and the +0.14 is only one to two times that. Shadow casters (probe): slots 3 and 4 have no shadow proxies in either renderer. The new casters are slot 1's 48 to 53 proxy outputs; shadow draws go from 360 to 570 (edge) and 278 to 444 (interior), total caster triangles stay about 70,000 (edge) and 135,000 (interior). This fits TR-2 H: the shadow cost follows batches, not triangles. Far quads and cards do not cast.
- Slot 4 (far tier) costs 0.0 to 0.3 ms in the main pass in both renderers (interior 0.28 before, 0.05 after).

**Reported, not gated: the user's preferences** (LOD0 to 200 m, LOD2 to 900 m, cards 550 to 900 m; `--tree-set=DisableGpuTreeEffectiveEdges:1` only; old caps). BEFORE adds `UseTreeRenderLods:0,UseFarTreeQuads:0`. Noon, ms.

| pose | renderer | root GPU | all trees | slot 0 | slots 1 + 2 | tree shadows | slots 1 + 2 triangles (M) | draws | trees drawn |
|---|---|---|---|---|---|---|---|---|---|
| forest_edge_eye | BEFORE | 15.03 | 8.56 | 1.86 | 4.81 | 1.65 | 4.451 | 1,197 | 11,479 |
| forest_edge_eye | AFTER | 13.01 | 6.56 | 1.81 | 2.61 (s1 0.70, s2 1.91) | 1.83 | 2.223 | 1,494 | 15,952 |
| forest_interior_eye | BEFORE | 16.08 | 9.87 | 2.57 | 3.93 | 2.51 | 3.777 | 1,026 | 11,867 |
| forest_interior_eye | AFTER | 15.48 | 9.28 | 2.62 | 3.12 (s1 1.17, s2 1.95) | 2.90 | 2.964 | 1,386 | 15,802 |
| forest_edge_eye, sun 8 deg | BEFORE | 15.98 | 10.51 | 1.80 | 4.69 | 3.75 | 4.451 | 1,217 | 11,479 |
| forest_edge_eye, sun 8 deg | AFTER | 14.14 | 8.72 | 1.85 | 2.64 | 4.03 | 2.223 | 1,514 | 15,952 |

With the user's bands the saving is smaller at the interior (all trees -6 %, slots 1 + 2 -21 %, triangles -21 %): slot 1 now holds 640 trees at 1,728 triangles each and slot 0 still draws 256 LOD0 trees out to 200 m (2.6 ms). Tree shadows rise 0.2 to 0.4 ms (more slot 1 proxies). F4's estimate (LOD2 4.8 to about 3.0 ms) is met at the edge (4.81 to 2.61) and not at the interior.

**Look gates (G4, G5).**
- `tree_billboard_match_gpu_test`, `tree_lighting_shadow_gpu_test`, `vegetation_shadow_gpu_test`: pass (`.godot/test_runs/tr8_look/20261115-152231/results.md`, 3 of 3).
- Switch strips, quads on, shipped fades, noon (top row) and sun 8 deg (bottom row), five distances left to right: 250 / 300 / 325 / 350 / 400 m (`.godot/tr8/strips/f1_250-400_{maple,pine,fir}.png`) and 110 / 130 / 140 / 150 / 170 m across the LOD2 to LOD2b hand-off (`.godot/tr8/strips/mid_110-170_{maple,pine,fir}.png`; `strip.json` beside them). Reading the maple sheets: no pop at the LOD2 to LOD2b hand-off (leaf mass and silhouette continuous across the dither); at 250 to 400 m the maple changes smoothly into a smaller, brighter card/quad silhouette as in TR-7. **User sign-off pending.**
- Dolly 100 to 400 m behind the `forest_edge_eye` pose (the harness backs the camera away along the pose heading, 1 m per frame, 300 frames; `--tr8-dolly`), old renderer against new, `flicker`:
  - eye height 1.7 m (`.godot/tr8/dolly/`): largest spike before 33.087 (frame 1), after 33.151 (frame 1), ratio 1.002; the five largest spikes sit on the same frames in both. This path passes through tree crowns (frames 0 and 148 are inside leaves), so the mean change is 16.6 and the metric is dominated by the camera, not by the tree tiers.
  - eye height 40 m above ground (`--dolly-lift=40`, `.godot/tr8/dolly_hi/`, mean change 5.4): largest spike before 8.295 (frame 267), after 8.259 (frame 271), ratio 0.996.
  - Gate (no spike above 1.25 x the before maximum): PASS in both. The first run has low sensitivity (see above); the second is the cleaner evidence. mp4s: `dolly/dolly_{before,after}.mp4`, `dolly_hi/dolly_hi_{before,after}.mp4`, sheets beside them.
- Shadow debug view (`ShadowDebugMode` 1) and normal frame at `forest_clearing_eye`, old renderer against new, `compare` (after against before): noon debug SSIM 0.9949 / PSNR 38.6 dB, noon normal 0.9930 / 36.9, sun 8 deg debug 0.9943 / 38.1, sun 8 deg normal 0.9939 / 39.0. Heatmaps (`.godot/tr8/shadow/compare_*.png`, x8): black everywhere in the near field and on the ground shadows; the only differences are the silhouettes of far trees (quads and cards against old cards) and the sky/cloud noise. PASS (noise-level, none in the 240 m shadow range).
- Contact sheet for sign-off: `.godot/tr8/contact_sheet.png` (top to bottom: F1 strip maple, LOD2b strip maple, F1 pine, LOD2b pine, F1 fir, LOD2b fir, then the far and altitude sheet: forest ground, altitude 300 m, 2,000 m down 30 and 70 degrees, a 2 km ground view, mountain pine at 350 m; each pair is cards then quads, order listed in `.godot/tr8/far/contact_order.txt`; individual files in `.godot/tr8/far/`).

**Verdict.** G1 and G2 pass at both poses; G3 passes at `forest_edge_eye` (-52.0 %) and misses at `forest_interior_eye` (-47.1 %). G4 and G5 evidence is in and clean by the numbers; the user's visual sign-off on the strips and contact sheet is outstanding. Nothing was changed in the renderer.

**Evidence.** Cost runs (JSON and md, copied): `.godot/tr8/cost/tr8_{after,before,after_edges,user_after,user_before}.{json,md}` (originals under `.godot/audit_baseline/rtx4070/tr1/tree_lod_slots/2026-11-15_14-12-44, 14-23-03, 14-33-26, 14-43-44, 14-54-16`), table extractor `.godot/tr8/cost/tab.ps1`. Test runs: `.godot/test_runs/tr8_{after,before,after_edges,user_after,user_before,strip_f1,strip_mid,dolly,dolly_hi,shadow,far,look}/`.

**Harness edits** (`tools/landscape_baseline_common.gd`, `tools/landscape_lod_switch_capture.gd`): `tree_slot1_hidden` added to `TREE_LOD_CASES` (its "no effect" check is skipped when `UseTreeRenderLods` is forced off); `--tr8-dolly` (with `--dolly-lift=<m>`) and `--tr8-shadow` modes (old renderer then new, F1 fades, `--tree-set` merged on top). Run: `$env:LANDSCAPE_HARNESS_ARGS = '--tr8-dolly --dolly-lift=40'; .\tools\run_tests.ps1 -Tests landscape_lod_switch_capture -Label tr8_dolly_hi`. Strips: `--strip --strip-configs=f1` and `--strip --strip-configs=f1 --strip-distances=110,130,140,150,170`.

**User decisions (2026-11-15).** Strips, far sheet and 40 m dolly signed off. G3 interior miss accepted as is (option A): G1 and G2 pass, the triangle gate is a proxy for time; the LOD2b start stays at 130/150 m. D9 = yes: TR-7d goes ahead, lead prototype and preview first.

**Odd things.** (1) `--tr6` reports FAIL for `ground_5km` (no isolated rank <= 0.1 tree with a clear 5,000 m line among `STRIP_SITES`); all other far and altitude frames were written, so the sheet lacks the 5 km ground view. (2) The first dolly is camera-dominated (see above), hence the second. (3) Draw calls rise by about 300 per frame. (4) `NaN` JSON warning from `_save_json` at the partial save is harmless.

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

TR-9a implementation notes (untested; build passes, no test has run, tests paused 2026-11-15). `UpdateGpuTreeEffectiveEdges` (Gpu.cs) runs at the top of a selection rebuild (compact profile, `PruneEmptyGpuTreeBatches`, canonical focus, `DisableGpuTreeEffectiveEdges` off) and stores the effective hand-off ends of slots 0 to 3. They travel in `edges[1..4]` (edges[2] is now the LOD2 to LOD2b end, the near fade end when the render LODs are off), so `GpuTreeLodEdges()` returns them to every consumer, `GpuTreeSelectionCanReuse` and `_gpuLastEdges` compare them (the selection signature), and `GpuTreeLodInterval` derives each band and fade from them with the configured widths (`HandOffStart`). Per slot a 64-bin histogram runs over [previous minimum end less its width, configured end); `ResolveGpuTreeEffectiveEnd` takes the upper edge of the last bin at 95 % of (cap minus protected roots in the band), with a one-bin hysteresis against the stored end. Slot 0's end never goes inside the farthest protected root. `RunEffectiveEdgeResolver` and `RunEffectiveEdgeCase` in `TreeRenderPolicyRegression` check it; `RunCapPruningCase` sets `DisableGpuTreeEffectiveEdges` because it tests the configured bands.

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

**User request (2026-11-15).** The user asked for simple, cheap tree shadows out to 1 to 3 km; this order is the chosen route (option A against forest-density darkening and an extra far-tree shadow layer). Range: the height-shadow L0 (4 m texels, about ±2 km) carries crown-scale shadows to about 2 km, L1 (16 m, about ±8 km) carries forest-mass shadows to 3 km and beyond. At 1 to 3 km a tree is 5 to 20 px tall at 1080p, so 4 m and 16 m texels are adequate. It is also a performance lever for the 90 fps pass: tree sun-map shadows cost 1.48 to 2.47 ms at R (TR-8), and step 2's hand-off can pull the sun-map tree shadow distance in once the canopy term covers the ground beyond it. Known limit: a heightfield has no light through gaps between crowns (the partial-transmittance term of step 1 is the answer); the strip across the hand-off is a user sign-off item.

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
