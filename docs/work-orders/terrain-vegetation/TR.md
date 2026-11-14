# TR. Tree rendering: LOD triangles, far billboards, billboard match: work orders

Parent: no plan section yet. This phase comes from the 2026-11-13 conversation with the user (tree cost, the LOD2-to-billboard switch, and the "tree rendering in Godot" video). It sits beside T6 and touches the same renderer, so read T6 "Phase facts" first. Prerequisite reading: `docs/landscape-audit-t0-spec.md` lines 1115-1220 (T0 cost baseline), `docs/terrain-vegetation-revamp-plan.md` section 7.1 (tree pack audit).
Status: not started
Depends on: nothing. It can run alongside T1-T5. Coordinate with T6-8 (tint), T6-10 (far canopy) and T6-11 (LOD tail), which touch the same shaders.
Freeze: none. This is a renderer change, not world content (see decision D1).

**Goal (the user, 2026-11-13): draw as many trees as the game allows without tanking performance.** Triangle savings (decimation) and the camera-facing far billboard serve that goal. They free the budget that a higher tree count then spends. The camera-facing billboard is required, not optional: one quad per tree is what makes a large far tree count affordable. TR-2 sizes the tree count against a frame budget; TR-9 raises the count.

## Decisions for the user

| # | Blocks | Question | Recommendation |
|---|---|---|---|
| D1 | TR-5 to TR-7 | These change how every world looks, generation 1 included, the same way the grass Cards default did. Is that allowed, or must they be gated by generation? | Allow it. Trees are placed the same way; only their meshes and shading change. The world fingerprint must not move. |
| D2 | TR-5 | How far may decimation go? It is judged by the TR-4 contact sheets at the distance each LOD is used. | Choose from the contact sheets. Gates in TR-2. |

## Phase facts

Verified 2026-11-13 against the `terrain-vegetation-revamp` working tree.

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
