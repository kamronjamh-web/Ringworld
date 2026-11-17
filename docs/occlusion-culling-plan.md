# Occlusion culling: study and plan (2026-11-16)

Status: study only. Nothing in the game changed. The measurement probe lives outside the tracked tree (`.godot/perf_pass_real/occl/`). Nothing is committed. Linked from `docs/perf-pass-2026-11.md` section 0.11.

Question from the user: does Ringworld need occlusion culling, where would it pay, and how should it be built so that it is invisible (no change to draw distance, density or pixels)?

## 1. Answer in short

- **Little is hidden where the cost is.** About 90 % of the tree triangles the camera draws are in the nearest trees (LOD0 to LOD2: 4.4 to 4.9 of 4.9 to 5.3 million triangles in forest). Those trees are the occluders, so they are almost never hidden. LOD0 trees were never hidden at any pose. Most hidden instances are far cards and quads of a few dozen triangles.
- **Terrain does almost all of the occluding.** Testing against a depth buffer with trees and grass removed finds nearly the same hidden set as testing against the full depth buffer: within 1 % of tree triangles, and within 0.08 ms in the oracle. Trees hiding trees adds nothing measurable, even with no wind margin.
- **The larger, simpler and safer win is per-instance frustum culling of trees.** Trees have none today. The GPU tree compaction keeps every instance in the LOD distance band, and Godot only culls whole renderers (240 m tiles). 10 to 22 % of the tree instances submitted to the GPU are off screen.
- **Hidden instances must be removed from the draw, not collapsed in the vertex shader.** Collapsing them in the shader (an early-out in `vertex()`) keeps only 3 to 12 % of the frustum saving and 35 to 40 % of the frustum-plus-occlusion saving (section 3.4). The cost of an instance is mostly its vertex invocations, whatever the shader does with them.
- **Measured oracle savings.** The tree camera draws were replaced by copies holding only the kept instances; shadows were untouched. A-X-A, 1600 x 900, RTX 4070:

  | pose (at pose yaw) | frame p50 | tree camera draws cost | frustum only saves | frustum + occlusion saves | of which occlusion |
  |---|---|---|---|---|---|
  | forest interior | 17.9 | 6.4 | 0.86 to 1.04 | 1.41 to 1.66 | 0.5 to 0.6 |
  | forest edge | 17.2 | 6.6 | 0.88 to 1.06 | 1.85 to 2.35 | 1.0 to 1.3 |
  | plains | 13.8 | 2.6 | 0.41 | 0.48 | 0.07 |
  | mountain foot (save spot, ground) | 10.5 | 0.27 | 0.02 | 0.06 | 0.04 |

  - Each range runs from the conservative wind envelope (0.6 x tree height) to no wind margin. A live wind bound sits between the two.
  - The pose yaws happen to be the best views for occlusion. Over four yaws, the hidden share of in-frustum tree triangles is 1 to 25 % in forest. The yaw-averaged occlusion saving is therefore about a third of that column: about 0.2 ms in the interior and 0.4 ms at the edge.
- **Grass, terrain chunks and POIs are not worth it.**
  - Grass: 0 to 28 % of clumps hidden, in a system that costs 0.2 to 0.8 ms. That is at most 0.1 to 0.2 ms.
  - Proxy terrain sectors: 5 to 18 % of in-frustum sectors hidden. Terrain3D's camera-centred clipmap cannot be culled piece by piece.
  - POIs: none was near any pose (0 of 10 POI meshes hidden).
- **Recommendation.**
  - **Phase 1 (do it):** per-instance frustum culling of the tree camera lists. A second compaction inside the existing tree cull feeds a camera-only multimesh per batch, while shadows keep the full list. It is exact in both the depth prepass and the colour pass, and byte-identical by construction.
  - **Expected saving:** about 0.7 to 1.0 ms in forest, 0.3 to 0.4 ms on the plains, nothing at altitude. That is 4 to 6 % of a forest frame, after the padding the latched frustum needs (section 4.1).
  - It does not reach 90 fps on its own. Perf doc sections 0.7 and 0.10 list the rest.
- **Occlusion proper is a user decision, and I recommend against doing it now.**
  - The exact variant culls in the colour pass only, using this frame's prepass depth. It saves 0.4 to 0.7 ms at slope-facing forest views and about 0.1 to 0.2 ms averaged. It needs merged per-mesh camera lists and about 90 dispatches every frame. The net gain is about 0.1 ms.
  - A terrain-horizon test in the Phase 1 compaction would save both passes, about 0.2 to 0.5 ms averaged in forest. It is exact only if its margin covers the level-of-detail (LOD) error of three terrain systems; a wrong margin leaves a hole in the image.
  - Previous-frame Hi-Z (a depth pyramid from last frame's depth) is not invisible: disoccluded trees miss the prepass for one frame.
- **Not recommended:**
  - Godot `OccluderInstance3D` built from terrain. It culls whole renderers, and no GPU tree renderer was ever fully hidden.
  - CPU culling of terrain chunks and POIs: there is little to hide.
  - Any shadow-pass culling: Godot keeps no per-cascade instance lists.

## 2. What is drawn today

- **Trees.**
  - `RingBiomeDecorationLayer.Gpu.cs` creates one `MultiMeshInstance3D` per (tile group, variant, LOD), named `GpuTree_<around>_<across>_<variant>_L<lod>` (`CreateGpuTreeOutput`, `Gpu.cs:1300`).
  - Tiles are `CellSizeMeters` x `SpatialTileCells` = 30 m x 8 = 240 m. Mid and far LODs merge 2 to 4 tiles. About 450 such renderers are live at the forest poses.
  - `RingGpuVegetationCompute` compacts each group's roots into its renderer's indirect buffer (`ring_gpu_vegetation_indirect.glsl`, `accepted()`). For trees, the test is the LOD distance band, the steep-view rule and a **per-LOD instance budget**: `used[slot]`, with caps `MaximumLod1Trees` 256, `MaximumLod2Trees` 640, then 1,800 and 5,200, claimed nearest-first in request order.
  - There is **no frustum test for trees**; grass has one (`p[13]` planes). Godot frustum-culls each renderer by its custom AABB, so a 240 m tile around the camera also draws the trees behind the camera.
- The cull re-runs only when the camera moves or its inputs change (`SkipUnchangedGpuCulling`). Turning the camera does not re-run it, because nothing in it depends on view direction today.
- **The budget means culling must come after compaction.** At LOD0 the 256-tree cap binds in forest. If hidden instances were removed before the budget claim, trees farther out would take their places, which is a visible change.
- **Tree shadows.**
  - They come from separate shadow-only proxies (`GpuShadows.cs`): a GPU mirror (`AttachOutputMirror`) of each LOD0 to LOD2 output, drawn with the authored low-detail mesh and set to `ShadowsOnly`.
  - The camera renderer's `CastShadow` is off once its proxy is ready.
  - Camera culling therefore leaves shadows untouched, as long as the mirror keeps copying the full list.
- **Per-instance terms.**
  - Each batch has a slot range in the vegetation haze atlas (`AssignHaze`, `RingGpuVegetationCompute.cs:337`). It is allocated whether or not haze is on.
  - The tree shaders find their haze and shadow terms at `vegetation_haze_slot.x + INSTANCE_ID`. Each texel carries a slot tag that is checked against the renderer's (`ring_vegetation_haze.gdshaderinc:30`).
  - Any compaction that changes `INSTANCE_ID` must therefore move the terms with the rows.
  - The atlas allows 16,384 rows x 1,024 slots (`RingVegetationHaze.MaximumAtlasRows`), far more than trees use.
- **Grass** already has per-instance GPU frustum culling with a latched, padded frustum (`RingStandingGrassLayer.CullingCamera.cs`, `GrassCullingFrustum.Pad`). The latch re-runs the cull after 3 degrees of rotation, with 5 degrees of padding. Phase 1 reuses this pattern.
- **Terrain.**
  - Terrain3D draws a camera-centred clipmap (addon internals, not nodes).
  - The proxy rings are `MeshInstance3D` sector nodes, about 3,000 of them live.
  - Godot occlusion culling is already on (`project.godot:69`) with 512 bedrock occluders that sit under the ground (`RingTerrainGlobalLod.cs:1451`). The CPU raster therefore runs every frame and hides nothing a ground camera can see.
- **Frame structure.**
  - Godot 4.8 Forward+ draws opaque geometry twice: a depth prepass (which also writes normals and roughness for SSAO and SSR), then the colour pass.
  - `PRE_OPAQUE` compositor effects run between the two (`docs/shadow-revamp-plan.md`, design limits). `scripts/render/RingContactShadows.gd` already uses that slot.
  - Alpha-scissor and `discard` materials write prepass depth.

## 3. How much is hidden (measured)

### 3.1 Method

- **Setup.** Slot 3 copy (`.godot/perf_pass_real/appdata_slot3`), the real start menu, then Continue. Optimized C#, 1600 x 900, VSync off, noon with clocks running, RTX 4070 at P0.
- **Tools.** Driver `.godot/perf_pass_real/occl/occlusion_probe.gd`. Runner `run_occl.ps1`, which takes the GPU queue, checks that the user is not playing and restores the slot.
- **Measurement.** At each pose and four yaws (pose yaw, +90, +180, +270), after streaming went idle:
  1. **Depth.** The main camera's opaque depth is read back after the opaque pass, in metres from the camera, with the existing `RingFarDepthCapture.gd` at scale 1. It is read twice: once with everything drawn, and once with trees and grass hidden ("terrain only"). A max-depth pyramid (Hi-Z) is built from each.
  2. **Instances.** Every instance in every tree and grass multimesh the camera draws is read back: the indirect count from the command buffer and the rows from the instance buffer.
  3. **Bounds.** Each tree instance gets a bounding sphere: the mesh AABB half-diagonal times the instance scale, plus a wind envelope of 0.6 x height (the shader's cap, `ring_local_wind.gdshaderinc:137`), plus 0.25 m. Grass clumps use 2.25 x their height, as the grass compute does.
  4. **Classification (conservative).**
     - A sphere that reaches the near plane counts as visible.
     - A sphere outside any frustum plane counts as "outside".
     - Otherwise the sphere counts as "occluded" when its nearest point is farther than the farthest depth in its screen rectangle. The depth is read from the pyramid level where the rectangle spans at most 4 texels.
     - "Occluded by terrain" repeats the test on the terrain-only depth.
  5. **Other geometry.** Proxy sectors, POI meshes and the global ring are tested per node against their world AABB, respecting cull masks and visibility ranges.
- **Runs.** Run 2 repeats the forest poses with no wind envelope, a lower bound for what a live wind bound gives. Runs 3 and 4 repeat the oracle and add the vertex-kill cases (section 3.4).

### 3.2 Results

Instances and triangles are counted at the full-detail LOD of each mesh. Trees means every instance the camera draws: the LOD0 to LOD4 GPU outputs plus the CPU-uploaded far tiles. "Occluded, of in-frustum" is the occluded share of what frustum culling would leave.

| pose | yaw | tree instances / triangles | outside the frustum | occluded (full depth) | occluded by terrain only | occluded, of in-frustum | grass clumps: outside / occluded | proxy sectors: occluded of in-frustum (nodes / tris) |
|---|---|---|---|---|---|---|---|---|
| forest interior | 0 | 16,728 / 5.34 M | 20 % / 13 % | 34 % / 7 % | 34 % / 6 % | 43 % / 8 % | 27,778: 16 % / 19 % | 9 % / 8 % |
| forest interior | 90 | 16,728 / 5.34 M | 17 % / 10 % | 25 % / 2 % | 23 % / 1 % | 31 % / 2 % | 26,424: 16 % / 9 % | 11 % / 9 % |
| forest interior | 180 | 16,728 / 5.34 M | 13 % / 12 % | 20 % / 1 % | 15 % / 0 % | 23 % / 1 % | 23,008: 18 % / 3 % | 9 % / 8 % |
| forest interior | 270 | 16,728 / 5.34 M | 22 % / 10 % | 19 % / 2 % | 17 % / 1 % | 25 % / 2 % | 26,820: 16 % / 5 % | 8 % / 7 % |
| forest edge | 225 | 16,514 / 4.86 M | 16 % / 15 % | 33 % / 15 % | 33 % / 15 % | 39 % / 18 % | 26,267: 16 % / 24 % | 14 % / 13 % |
| forest edge | 315 | 16,366 / 4.86 M | 15 % / 8 % | 18 % / 5 % | 17 % / 4 % | 21 % / 5 % | 25,558: 16 % / 14 % | 7 % / 6 % |
| forest edge | 45 | 16,567 / 4.86 M | 17 % / 10 % | 25 % / 1 % | 24 % / 1 % | 30 % / 1 % | 25,203: 16 % / 5 % | 8 % / 7 % |
| forest edge | 135 | 16,567 / 4.86 M | 22 % / 11 % | 33 % / 8 % | 32 % / 7 % | 42 % / 9 % | 25,874: 16 % / 16 % | 10 % / 8 % |
| plains | 0 | 10,501 / 2.00 M | 10 % / 18 % | 38 % / 3 % | 34 % / 2 % | 42 % / 3 % | 10,080: 14 % / 0 % | 6 % / 5 % |
| plains | 90 | 10,180 / 2.00 M | 19 % / 6 % | 5 % / 0 % | 5 % / 0 % | 7 % / 0 % | 9,131: 10 % / 2 % | 6 % / 5 % |
| plains | 180 | 10,338 / 2.00 M | 9 % / 6 % | 27 % / 1 % | 27 % / 1 % | 29 % / 1 % | 4,177: 14 % / 6 % | 6 % / 5 % |
| plains | 270 | 10,313 / 2.00 M | 26 % / 19 % | 31 % / 1 % | 28 % / 1 % | 42 % / 2 % | 7,989: 12 % / 3 % | 20 % / 18 % |
| mountain foot (save, ground) | 0 | 4,190 / 0.17 M | 18 % / 8 % | 19 % / 4 % | 18 % / 4 % | 23 % / 5 % | 12,118: 12 % / 16 % | 8 % / 7 % |
| mountain foot | 90 | 4,341 / 0.18 M | 12 % / 45 % | 60 % / 7 % | 60 % / 7 % | 69 % / 14 % | 11,100: 14 % / 28 % | 6 % / 5 % |
| mountain foot | 180 | 4,143 / 0.18 M | 17 % / 2 % | 34 % / 3 % | 34 % / 3 % | 41 % / 3 % | 8,012: 15 % / 14 % | 5 % / 5 % |
| mountain foot | 270 | 4,297 / 0.18 M | 9 % / 26 % | 14 % / 1 % | 14 % / 1 % | 16 % / 2 % | 8,212: 16 % / 7 % | 9 % / 8 % |

Without the wind envelope (run 2):
- forest interior, yaw 0: 20 % / 16 % outside, 35 % / 9 % occluded;
- forest edge, yaw 225: 16 % / 19 % outside, 34 % / 20 % occluded.

Per LOD, forest interior, yaw 0 (instances in total / outside / occluded):
- LOD0: 256 trees (the cap), 1.72 M triangles. 22 outside, **0 occluded**.
- LOD1: 640 trees, 1.06 M triangles. 64 outside, 8 occluded.
- LOD2: 2,350 to 2,484 trees, 2.1 M triangles. 521 outside, 397 occluded.
- LOD3 cards: 4,682 trees, 0.17 M triangles. 1,124 outside, 2,649 occluded.
- LOD4 quads: 3,800 to 6,000 trees, 0.02 M triangles. 551 outside, 2,010 occluded.

Other geometry:
- POIs: the 10 to 11 POI meshes in range were far away, and none was hidden.
- The global ring mesh (11 nodes) is never hidden.

Godot renderer-level occlusion (the whole renderer AABB behind the depth) caught **no GPU tree renderer** at any pose. The only renderers ever wholly hidden were small CPU far tiles, up to 122 of them at the forest edge.

### 3.3 What the hidden share costs (oracle timing)

- **Method.**
  - The probe hides the tree camera renderers (layers set to 0; shadow proxies untouched).
  - It draws CPU-filled copies with the same mesh, material, instance parameters and rows, keeping only a subset.
  - `copy_all` costs the same as the baseline (within 0.03 ms everywhere), so the copies are a fair stand-in.
- **Windows.** A-X-A, 8 s windows, with a baseline between every case. The baseline spread is 0.03 to 0.30 ms.
- **Repeats.** Runs 1, 3 and 4 agree within 0.1 ms. Run 4 ran with a frame 1.1 ms slower than run 1 (other agents used the machine in between); its deltas match.
- **Columns.** "Colour pass" is the GPU time between the `PRE_OPAQUE` and `POST_OPAQUE` callbacks.

| pose | case | instances kept | wall Δ ms | colour pass Δ ms |
|---|---|---|---|---|
| forest interior (17.9 ms) | in frustum | 13,432 | −0.86 to −0.96 (no wind: −1.04) | −0.61 (−0.70) |
| | in frustum, not hidden by terrain | 7,736 | −1.47 (−1.68) | −0.97 (−1.15) |
| | in frustum, not hidden by anything | 7,676 | −1.41 to −1.47 (−1.66) | −1.02 (−1.19) |
| | no tree camera draws | 0 | −6.42 | −4.50 |
| forest edge (17.2 ms) | in frustum | 13,899 | −0.88 to −0.90 (−1.06) | −0.58 (−0.70) |
| | not hidden by terrain | 8,471 | −1.82 (−2.27) | −1.23 (−1.57) |
| | not hidden by anything | 8,445 | −1.85 to −2.02 (−2.35) | −1.25 (−1.59) |
| | no tree camera draws | 0 | −6.63 | −4.44 |
| plains (13.8 ms) | in frustum | 9,473 | −0.41 | −0.28 |
| | not hidden by anything | 5,476 | −0.48 | −0.36 |
| | no tree camera draws | 0 | −2.55 | −1.78 |
| mountain foot (10.5 ms) | not hidden by anything | 2,667 | −0.06 | −0.03 |
| | no tree camera draws | 0 | −0.27 | −0.16 |

- **Total cost.** The camera side of the trees costs 6.4 to 6.6 ms in forest, out of the 7.1 ms "trees hidden" ablation; the rest is tree shadows. 70 % of it is in the colour pass.
- **The saving follows triangles,** at about 1.2 to 1.6 ms per million camera triangles removed. Hidden cards and quads save almost nothing.
- **A colour-pass-only occlusion stage** would keep 65 to 70 % of the occlusion saving: 0.41 of 0.61 ms in the forest interior and 0.67 of 0.96 ms at the edge.
- **Side finding: the primitive counter is inflated.** Godot's `RENDER_TOTAL_PRIMITIVES_IN_FRAME` counts an indirect multimesh at its full capacity. `copy_all` draws the same work as the baseline but reports 7.2 M fewer primitives at the forest poses. The "15 to 17 million tree primitives" in perf doc section 5.1 is inflated by this. About 5 M tree triangles are drawn per camera pass.

### 3.4 Collapsing in the vertex shader instead of removing (run 4)

- **Method.** Same copies as 3.3, but every row is kept. Hidden rows get `INSTANCE_CUSTOM.w = 100`, and a copy of the tree shader (untracked, built at runtime) wraps the original `vertex()` body in `if (INSTANCE_CUSTOM.w > 50.0) { VERTEX = vec3(0.0); } else { ... }`. The materials sit on mesh copies, because Godot ignores surface override materials on multimesh instances.
- **Result:**

  | pose | kill shader with nothing hidden | collapse off-screen | remove off-screen | collapse off-screen + occluded | remove off-screen + occluded |
  |---|---|---|---|---|---|
  | forest interior | +0.20 | −0.11 | −0.96 | −0.46 | −1.41 |
  | forest edge | +0.10 | −0.04 | −0.90 | −0.75 | −2.02 |

- **Conclusion.** Collapsing keeps 3 to 12 % of the frustum saving and 35 to 40 % of the combined saving, and the branch itself costs 0.1 to 0.2 ms. The cost of an instance is its vertex invocations, not the heavy vertex code. Off-screen triangles are clipped cheaply anyway; collapsing only saves the rasterisation of occluded on-screen triangles.
- **Consequence.** Culling must shrink the indirect instance count. That decides the design.

## 4. Options

### 4.1 Per-instance frustum culling of the tree camera lists (recommended, Phase 1)

**Design.** Inside the existing per-batch tree cull, add a second compaction that runs after the budget-correct one:

1. **Full list.** Phases 0 to 3 run unchanged. They produce the full list (budget, LOD bands and steep rule exactly as today) in the batch's existing output multimesh `O`, and write its haze and shadow terms at `O`'s slots. The shadow mirror keeps copying `O`.
2. **Camera filter (new phase 6).** After the barrier, per batch: read `O`'s rows `[0, count)` and test each row's bounding sphere against the padded camera frustum. Scan and compact the survivors, in their original order, into a new camera multimesh `C` (indirect, same mesh, same custom data). Copy each survivor's 4 term texels (haze pack0 and pack1, shadow pack0 and pack1) from `O`'s slot to `C`'s slot, rewriting the tag word to `C`'s tag.
   - `C` gets its own slot range (`AssignHaze` with the same capacity).
   - Phase 4 (terms refresh) must re-run the copy for every batch it refreshes.
3. **Renderers.** The camera draws `C`; its renderer gets `C`'s `vegetation_haze_slot`.
   - `O` is no longer drawn by the camera.
   - While a batch's shadow proxy is not ready, `O` is drawn as `ShadowsOnly`, keeping the shadow continuity that exists today. Otherwise it is not instanced at all; the mirror reads its buffer RID.
4. **Bounds.** The mesh bounding radius (from the mesh AABB at registration) times the row's maximum column scale, plus a wind envelope.
   - The envelope comes from the same globals the tree shader reads (`ring_tree_wind_response`, the canopy wind and the gust bound). It is computed per dispatch on the CPU and passed as a factor of instance height. It falls back to the shader's 0.6 cap if those are not known.
   - Culling is off while `ring_megastorm_blast.x > 0`.
   - Shared impostor quads use the radius of the quad's full extent.
5. **Latch.** The frustum is latched and padded, copying `GrassCullingFrustum.Pad` and the grass latch rules: re-latch on more than 3 degrees of rotation or more than a movement threshold, with 5 degrees of angular padding.
   - The translation margin must cover the camera travel between the main-thread request and the frame actually rendered (physics interpolation; at km/s that is hundreds of metres). Re-latch every frame above a speed threshold.
   - A re-latch re-runs only phase 6, but per batch, about 450 dispatches. That is the same order as a camera-move re-cull today (`GPU.TreeCulling` 0.04 to 0.05 ms on the GPU). Check that the extra CPU recording stays inside the tree budget while turning.
6. **Switches.**
   - A/B: `BiomeDecorations.TreeFrustumCulling` (public, not exported).
   - Debug: `BiomeDecorations.TreeCullVerify`. In this mode, culled rows go to a separate verify multimesh drawn unshaded magenta; see section 7.
   - No menu row: AGENTS.md keeps debug and A/B switches out of the grave-key menu, and this is not a look tunable.

**Why it is pixel-identical.** It removes only instances whose bounds (geometry plus the maximum vertex displacement) lie outside the frustum. Those produce no fragment in any camera pass.
- The budget, the LOD bands, the dither words, the haze and shadow terms and the shadow casters are all computed before the filter, from the full list, exactly as today.
- Survivors keep their relative order inside each batch.
- The expected result is therefore a byte-identical frame, not merely a similar one.

**Expected saving.** The measured exact-frustum oracle is 0.86 to 1.06 ms in forest and 0.41 ms on the plains. The padding and the wind envelope will keep some off-screen trees: about 0.7 to 1.0 ms in forest, 0.3 to 0.4 ms on the plains and nothing at altitude. The cost is a second instance buffer per batch (16 floats per capacity row) and twice the tree slots in the haze atlas.

### 4.2 Occlusion on top (user decision; not recommended now)

Three variants, all applied after 4.1's filter:

- **(a) Same-frame Hi-Z in the colour pass (exact).**
  - A `PRE_OPAQUE` effect builds a max pyramid from the prepass depth, then compacts each camera list again for the colour pass only. A `POST_OPAQUE` effect restores the frustum list.
  - It is exact by construction, because occluded instances fail the depth test against that same prepass depth.
  - Per-batch lists would need about 900 dispatches per frame, so this only works with the camera lists merged per mesh (30 variants x LOD0 to LOD2 plus the shared impostors, about 92 lists). Even merged, it means about 90 compactions and 90 restore copies every frame.
  - Gain: 0.41 ms (interior) and 0.67 ms (edge) at the best views, about 0.1 to 0.2 ms averaged over yaws, minus about 0.05 ms for the pyramid and the per-frame dispatch cost.
  - Merging also changes the draw order across tiles, so identity becomes "equal except at exact depth ties".
  - Net: about 0.1 ms. Not worth its size (L).
- **(b) Terrain horizon in the 4.1 filter (both passes, exact only with correct margins).**
  - Terrain does nearly all the occluding, so a horizon map tested in phase 6 would find nearly the same set before the prepass. The map would hold the maximum terrain elevation angle out to each distance ring, per azimuth bin, built once per latch from the height-shadow heightfield.
  - Gain: the full occlusion share, both passes. That is 0.5 to 1.3 ms at slope-facing forest views and about 0.2 to 0.5 ms averaged.
  - The catch: the rendered terrain is Terrain3D LOD meshes, proxy sectors and the global ring, and each lowers ridges by its own LOD error. The horizon needs a per-distance margin covering all three plus the heightfield's sampling. If the margin is too small, a tree shows a hole for as long as the view holds.
  - Only with the verify mode proving zero misses over sweeps, and only with the user's yes. Size M to L.
  - **Designed and measured in section 9 (Phase 2, 2026-11-17).**
- **(c) Previous-frame Hi-Z, reprojected, for both passes.**
  - Disoccluded trees (fast turns, flight, a tree appearing over a ridge) are missing from the prepass for a frame. SSAO, the SSR normals and the contact shadows are then wrong at those pixels.
  - Not invisible. Rejected.

### 4.3 Terrain chunks and POIs (no)

- **Proxy sectors:** 5 to 18 % of in-frustum sectors were hidden (4 to 18 % of their full-detail triangles). Proxy rings and Terrain3D together cost 3.6 to 4.4 ms, and the split between them is unmeasured. The gain is at most 0.2 to 0.4 ms, and any culling error is a hole in the ground.
- **POIs:** none was near a pose.
- Not recommended.

### 4.4 Godot `OccluderInstance3D` from terrain chunks (no)

- It culls whole render instances with a CPU raster. No GPU tree renderer was ever fully hidden: the tiles are 240 m and include the area around the camera.
- Occluders must stay under the rendered surface, which means the LOD error problem again.
- The occluder BVH rebuilds on every add, remove or move, so terrain streaming and floating-origin rebases would cause hitches.
- It would only cull proxy sectors and the CPU far tree tiles (the 4.3 numbers).
- Side note: the 512 bedrock occluders hide nothing a ground camera can see. Whether the per-frame raster they keep alive costs CPU was not measured. It is a cheap A-B for the CPU lane: set `occlusion_culling/use_occlusion_culling` off, or `BuildTerrainOccluder` false.

### 4.5 The shadow pass

- Instances hidden from the camera still cast shadows, so camera culling must not touch the shadow proxies, and 4.1 does not.
- Godot draws each proxy multimesh into every cascade its AABB touches and keeps no per-cascade instance lists. Per-instance cascade culling is not possible without engine changes.
- Receiver-based caster culling (dropping casters whose shadow cannot land on a visible receiver) is a research project.
- Tree shadows cost 0.6 to 0.8 ms at the real poses (perf doc 0.4). No shadow work is recommended.

### 4.6 Grass (no)

- Grass already culls per instance against a padded frustum.
- Occlusion would hide 0 to 28 % of clumps, in a 0.2 to 0.8 ms system.
- Revisit only if 4.2 (a) or (b) is ever built for trees: grass runs through the same compute and could share it.

## 5. Risks (Phase 1)

| risk | effect | mitigation |
|---|---|---|
| Bounds too small (wind, megastorm blast, camera-facing impostor quads, steep-view cards) | a tree missing at the screen edge | live wind envelope from the shader's own globals; culling off during a megastorm blast; impostor radius from the quad's full extent; the magenta verify mode during sweeps (section 7) |
| Main-thread camera differs from the render camera (physics interpolation; at km/s the gap is hundreds of metres) | edge trees culled with a stale frustum | translation margin of at least 2 frames of travel; re-latch every frame above a speed threshold; verify sweeps at 1.2 and 36.8 km/s |
| Floating-origin rebase | rows and renderer transform out of step for one frame | the cull already re-runs when a renderer transform changes (`BatchFrameParameters` carries it); turn culling off for the rebase frame |
| Term copy misses a refresh | wrong haze or shadow on camera rows | phase 4 re-runs the copy; identity test at rest after a cloud move and an 8 m step |
| Slot tag not rewritten | camera rows fall back to per-vertex haze | the copy writes `C`'s tag; `vegetation_haze_gpu_test` and the identity A/B catch it |
| Extra CPU while turning (a phase-6 dispatch per batch on each re-latch) | render-thread cost in fast turns | measure `Trees frame request` in a 360 deg/s sweep; if over budget, re-latch less often with more padding |
| Draw order | none: survivors keep their order per batch | byte A/B |
| `ring_gpu_vegetation_indirect.glsl` and `RingGpuVegetationCompute.cs` are dirty from the haze work | merge conflict | start OC-1 after the mist removal lands |

## 6. Plan

Work orders are sized for one implementer each. Line numbers are as of `34d1799` plus the dirty haze files.

**OC-0 Oracle and verify harness (lane V, S).**
- Promote the probe to `tools/tree_visibility_oracle.gd`, with no game change: depth readback, per-instance classification, the copy oracle and the vertex-kill comparison, with `--poses` and `--yaws`.
- It is the before/after harness for OC-2.
- Manifest entry: GPU tier, not in the gate.

**OC-1 Camera output per tree batch (lane T, M).**
- `RingGpuVegetationCompute.cs`:
  - `BatchRegistration` gains `CameraTarget` (a second `MultiMesh`) for tree batches.
  - Allocate its indirect data the way `AttachOutputMirror` allocates a mirror target (`:1260-1276`): same capacity, same format.
  - Give it a second slot range (`AssignHaze`) and expose `GetCameraHazeSlot(batchId)`.
  - Release both on removal and retirement (the `_retiringMirrorTargets` pattern).
- `ring_gpu_vegetation_indirect.glsl`: add phase 6 as a pass-through (copy every row and its term texels from `O` to `C`, tag rewritten), so `C` equals `O`.
- `RingBiomeDecorationLayer.Gpu.cs` and `GpuShadows.cs`:
  - The camera renderer draws `C`.
  - `O` gets a `ShadowsOnly` renderer only while the proxy is not ready.
  - Set the haze slot on `C`'s renderer.
- Test: byte-identical frames against `main` at the four poses (section 7), plus `gpu_tree_integration_test`, `tree_lod_dither_gpu_test`, `vegetation_haze_gpu_test`, `vegetation_shadow_gpu_test`, `tree_shared_impostor_integration`, `tree_gpu_rebase_smoke`.
- This order proves the plumbing with zero culling.

**OC-2 Frustum filter (lane T, M).**
- `ring_gpu_vegetation_indirect.glsl`: phase 6 with the sphere test against padded planes (push or parameter words: planes, wind factor, mesh radius, megastorm flag), a scan and compaction in order, and the term copy with the tag rewritten.
- `RingBiomeDecorationLayer.Gpu.cs`: the latch (copy the grass latch rules and `GrassCullingFrustum.Pad`), the speed-dependent translation margin, and phase 6 on re-latch even when the full cull is skipped. Also the switches `TreeFrustumCulling` (A/B) and `TreeCullVerify` (debug).
- Tests: OC-1's list plus `tree_gpu_frame_cache_smoke`, `tree_compute_optimization_regression`, `gpu_vegetation_parameter_layout_test` (the new words), `vegetation_wind_gpu_test`.
- Measure with the real-world driver: the four poses A-X-A with `prop:BiomeDecorations:TreeFrustumCulling=false`, plus a 360 deg/s yaw sweep for `Trees frame request`.

**OC-3 Validation (lane V, M).** Section 7. New manifest entries: `tree_frustum_identity_gpu_test` (byte A/B at rest) and `tree_cull_verify_sweep` (magenta count in motion).

**Occlusion (4.2 b), only if the user asks after seeing OC-2's numbers.** A separate lead design, starting from a margin study of the three terrain systems' LOD error.

**Order and ownership**

| lane | owns | orders |
|---|---|---|
| T (trees) | `RingGpuVegetationCompute.cs`, `ring_gpu_vegetation_indirect.glsl`, `RingBiomeDecorationLayer.Gpu.cs`, `RingBiomeDecorationLayer.GpuShadows.cs` | OC-1 → OC-2 (one implementer; the two orders touch the same files) |
| V (tests) | `tools/tree_visibility_*`, `tools/tree_frustum_*`, `tools/tree_cull_verify_*`, `tools/test_manifest.json` (append only) | OC-0 first; OC-3 after OC-1 and after OC-2 |

No shader file changes in Phase 1: the camera rows carry their own slots and terms, so `imported_tree.gdshader` and `imported_tree_impostor.gdshader` stay as they are.

Escalate to the primary agent if:
- the byte A/B shows any difference after OC-1 (plumbing) or OC-2 (filter);
- the verify sweep shows a single magenta pixel;
- the re-latch CPU cost in fast turns exceeds the 2 ms tree budget;
- a shader change turns out to be needed.

## 7. Test plan

- **Byte identity at rest.**
  - Clocks held (`hold_world_clocks`), wind frozen, at the four poses and four yaws.
  - Capture with culling off and on. Expect identical PNGs: an exact byte compare, plus `tools/media/ffmpeg_tools.ps1 compare` for the heatmap if anything differs.
  - Repeat after a cloud move and an 8 m step, which exercises the term refresh and copy.
  - Any difference blocks the order.
- **Verify sweeps in motion.**
  - Turn on `TreeCullVerify` and record with Movie Maker at 30 fps through: a 360 deg/s yaw sweep in the forest interior, a pitch sweep, the forest walk, flights at 72 and 240 m/s near the ground and at 1.2 and 36.8 km/s from the save, a teleport and a floating-origin rebase.
  - Count magenta pixels per frame, with a GDScript scan of `get_image()` or an ffmpeg colour key. The count must be 0 in every frame.
  - Keep only the cited contact sheet and delete the frames (AGENTS.md ffmpeg rules).
- **Performance.**
  - The real-world driver (perf doc 0.8.4) at the four poses, A-X-A with the A/B switch.
  - The flight sweep, to check that the per-frame CPU cost did not grow (`Trees frame request`, `Trees output selection`) and that `wait_builds_idle` still settles.
- **Regression tests per step.** `tools/run_tests.ps1 -Files <changed files> -Label occl-<order>`, with `-DryRun` first, plus the named lists in OC-1 and OC-2.

## 8. Evidence

All under `.godot/perf_pass_real/occl/`, pruned after 7 days.
- Run folders, each with `report.json` and `console.log`:
  - `run1/`: four poses, four yaws, the oracle at the pose yaw.
  - `run2_nowind/`: the forest poses without the wind envelope.
  - `run3_kill/`: an oracle repeat. Its kill cases are invalid, because the surface overrides were ignored.
  - `run4_kill/`: the vertex-kill oracle.
- Scripts: `occlusion_probe.gd`, `run_occl.ps1`, `summarize.py`, `table.py`.

## 9. Phase 2: terrain-horizon occlusion of the tree camera lists (design, 2026-11-17)

Status: design and measurement only. The user approved the direction on 2026-11-17. Nothing in the game changed; the probes are untracked (section 9.8). The only tracked change is three hilly scenarios in `tools/fixtures/perf_bench_scenarios.json`.

### 9.1 Answer in short

- **The margin is known and small while the terrain is settled.** Terrain3D (0 to 112 m) draws exactly the gameplay height field (error 0.00 m). The proxy rings sit below it by their underlay offset (`RadialUnderlayOffsetMeters`: 0.2 m at the 2 and 4 m levels, 0.5 m at 8 m, 1 m at 16 m, 2 m at 32 m; the global ring 10 m) plus their own LOD sag. A min-pooled occluder with a dilation of `max(8 m, 2 x the spacing of the coarsest level drawn there)`, lowered by that level's underlay plus 0.1 m, stayed at least 5 cm under the rendered terrain at every one of about 6 million sampled pixels (27 settled views: ground and 60 to 250 m up, forest, mountain, slope; section 9.2).
- **It does not hold while terrain streams.** 10 frames after a teleport the ground under the camera was 1.7 m low (Terrain3D not yet in, coarse proxies underneath); at 240 m/s the rendered terrain was 8 to 33 m below the field within 1.6 km. At 20 and 72 m/s near the ground the chain had kept up. So occlusion needs a residency gate (section 9.3), not a bigger margin.
- **The design is exact by construction and verified offline.** A CPU-built polar horizon table (1,024 azimuth bins x 108 distance rings out to 1.5 km, from a new field of 8 m cells that hold the minimum of the 2 m height lattice), tested per instance in phase 6 after the frustum test. Replayed on the measured lattices and tree lists of 40 views (10 poses x 4 yaws): 0 trees culled that a per-pixel ray test against the terrain-only depth finds visible, in every variant.
- **What it saves depends on how far the eye may move before the table is rebuilt.** With 4 m horizontal and 1 m vertical (the recommended latch), it removes 73 % of the triangles of terrain-hidden trees. A single 8 m allowance in all directions keeps 39 %.
- **Expected saving (GPU, wall).**
  - At the slope-facing views: forest edge about 1.0 ms of 1.08 possible, valley forest (`hill_forest_valley`) 0.58 of 0.86, a second valley 0.20 of 0.34, mountain valley 0.03.
  - Averaged over four yaws: forest edge about 0.45 ms, valley forest 0.3, second valley 0.1, forest interior 0.06; plains, the other valleys, mountain and slope 0.0 to 0.05.
  - For comparison, the tree camera draws cost 1.4 to 2.4 ms at these poses today (oracle `copy_none`), on a 7.4 to 10.1 ms frame. The 6.4 ms of section 3.3 is from before the frustum filter and the user's current tree settings.
- **Recommendation.** Worth building only for forest among hills: about 0.3 to 0.45 ms where it applies, up to 1 ms facing a slope, and nearly nothing elsewhere. It is M-L work: a new occluder height field, the LOD coordinator, the tree layer and the tree compute. It is the user's call; if yes, the orders are in 9.6. Do it after lane T's current compute work lands.

### 9.2 Margin study (how far below the field the rendered terrain can be)

**Method** (probe `tocc_probe.gd`, run A). Slot 3 copy, real start menu and Continue, High preset with the user's files, 1600 x 900, noon. Trees, grass and gust debris hidden; the main camera's opaque depth read back (radial metres, `RingFarDepthCapture`). Every second pixel became a world point and then a canonical (around, across, height) point, using the explorer's canonical pose (doubles) and the ring frame. That conversion matches the grid's own (float32) conversion within 1 cm vertically and 1.5 cm across. Each point was compared with the gameplay field at the same spot (`Terrain3DGameplay.SampleHeight`: the 2 m presentation lattice plus edits, triangle interpolation). A 2 m lattice of 2 x 2 km was dumped around each pose to replay occluder sources offline.

**Rendered minus field height (m)**, worst / median per 3D distance band. Ground = 21 static views at 1.7 m; lifted = 6 views at 60, 150 and 250 m above the ground.

| views | 0-50 | 50-112 | 112-250 | 250-500 | 500-800 | 800-1100 | 1100-1600 | 1600-2400 | 2400-5500 | 5500-12000 |
|---|---|---|---|---|---|---|---|---|---|---|
| ground, settled | 0.00 / 0.00 | -0.20 / 0.00 | -0.20 / -0.20 | -0.48 / -0.20 | -0.67 / -0.20 | -1.87 / -0.27 | -1.38 / -0.51 | -3.45 / -0.78 | -8.8 / -1.6 | -33 / -3.5 |
| lifted 60-250 m, settled | | 0.00 | -1.13 / -0.20 | -1.66 / -0.20 | -1.99 / -0.20 | -1.75 / -0.24 | -1.85 / -0.50 | -2.43 / -0.78 | -7.1 / -1.4 | -37 / -3.2 |
| fly 20 and 72 m/s, stopped and captured at once | 0.00 | 0.00 | -0.20 | -0.33 | -0.34 | -0.71 | -0.86 | -1.58 | -5.2 | -26 |
| fly 240 m/s, stopped and captured at once | -0.19 | -8.4 | -8.2 | -8.2 / -3.3 | -16.5 / -3.4 | -16.6 / -7.8 | -32.7 / -8.7 | -34 | -122 | -264 |
| 10 frames after a teleport | -1.69 / -1.17 | -1.44 | -1.40 / -1.09 | -1.56 / -1.06 | | | | | -24 | -26 |
| 60 frames after a teleport | 0.00 | 0.00 | -0.20 | -0.45 | -0.57 | -1.18 | -1.18 | -1.01 | | -9 |

- **The medians are the underlay offsets.** The proxy levels are deliberately lowered so a finer level always wins where two overlap: 0.2 m at Lod1 and Lod2, 0.5 at Lod3, 1.0 at Lod4 (scene values; the 2 m and 4 m levels use the default). The worst values add the LOD sag of peaks between lattice points, and the geomorph toward the parent lattice in each fade band.
- **Beyond 12 km** the errors reach hundreds of metres (the far proxy levels and the global ring, with a 10 m underlay). That is outside the tree range (5.5 km) and outside the occluder disc (1.5 km), so it does not matter.
- **Lifted views** are worse at short 3D distances because the coordinator drops the fine levels at altitude (`IsAltitudeActive`). The margin rule must therefore come from the levels actually active, not from a fixed distance table.
- **Skirts, coverage collapse and NT-1 / NT-2** need no margin of their own. Skirts hang below an edge, and collapsed or far-culled sectors are only those another level covers. All of them are inside these measurements (HEAD `b5ab829` plus the working tree).
- **The occluder rule** (`margin_an.py rule_check`). Occluder height over a region = minimum of the source samples over the region dilated by W(d), minus δ(d), where:
  - W(d) = max(8 m, 2 x the spacing of the coarsest level drawn at d), and δ(d) = the underlay offset of that level + 0.1 m;
  - with the High chain: W = 8 m to 800 m, 16 m to 1,600 m, 32 m to 2,400 m; δ = 0.1 m inside the Terrain3D handoff, 0.3 m to 800 m, 0.6 m to 1,600 m, 1.1 m to 2,400 m.
  - Applied to the full 2 m lattice (minimum over every 2 m sample in the region), the smallest rendered-minus-occluder value over all 27 settled views was +0.05 m.
  - **The source must be the minimum of the 2 m samples, not point samples at 4 or 8 m.** On the pixels in view, a 4 m point source (the height shadow's default level 0) with W ≥ 8 m still passed (+0.03 m), and with 4 m of dilation it failed by 0.14 m at the slope. But over every node of 15 lattices (2 to 3 km square each), a 4 m point source was up to 1.32 m above the 2 m minimum on steep slopes, and an 8 m source up to 1.85 m. The user's own setting is `HeightShadowL0TexelMeters` = 8. So the height-shadow clipmap cannot be reused without an empirical margin of about 2 m, which would cost much of the recall.
- **The 0.1 m** covers the GPU-height backend of the 8 to 32 m levels (its self-check allows 5 cm against the CPU height) and float rounding. Ray sag is handled by working in the eye's tangent frame (9.3).

### 9.3 Occluder design

**Source: a new occluder field of 8 m min cells** (`RingTreeOccluderField`, 9.6 HZ-1).
- Each cell holds the exact minimum of the presentation height over the 5 x 5 samples of the 2 m lattice on its closed footprint (shared borders included). That is the same function and lattice that Terrain3D and the 2 m proxy level draw, so the rule of 9.2 applies without an empirical source margin.
- Window: 384 x 384 cells (3 km square) centred on the explorer. It is filled on workers with an independent sampler (`grid.CreateIndependentTerrainSampler()`, as the height shadow does) and scrolled by strips of cells: 9,600 samples per 8 m of travel, and 2.25 million unique samples for a full fill after a teleport. At the 2 to 6 µs a sample measured here, that is a few seconds on two workers; occlusion stays off until it is ready.
- The height-shadow clipmap was the first candidate, but it stores point samples (4 m by default, 8 m in the user's settings), which can miss 2 m minima by up to 1.9 m (9.2).
- **Edits:** the gameplay field's edits (`RingSurfaceHeightField.GetEditsInWindow`, re-read when its `Revision` changes) lower every cell whose footprint holds an edited sample (min with the edited height). Raising edits are ignored, which is conservative.
- **Bore apertures** (`EmitterSiteApertures.GetSurfaceApertures`): any cell overlapping an aperture's liner radius gets minus infinity (no occluder there). Terrain3D holes come only from apertures.
- **No sea clamp.** The seabed may stay below sea level: a segment between two points above sea level never dips below it, because the habitable side is the inside of a cylinder.
- **Reads.** The builder reads a snapshot published by the field (an immutable array swapped on publish), so there is no locking with the fill.

**Table (CPU worker, `RingTreeHorizon`).**
1. Take the field snapshot and build a min pyramid of it (8, 16, 32, 64 m cells).
2. **Frame.** The tangent frame at the latch eye E0: x along the around tangent, y the ring up, z along the axis. Rays are straight lines in this frame.
3. **Sectors.** 1,024 azimuth bins. Radial steps r(j+1) = 1.02 r(j) from 8 m to the disc radius, giving about 262 steps and 268,000 sectors.
   - For each sector, the canonical rectangle covering it, widened by W(d) + ρh + 1 m (the extra metre covers the tangent-to-canonical stretch: x = (R - h) sin(a / R), at most about 1 m at 1.5 km for 2.5 km of height range).
   - m = the pyramid minimum over that rectangle (at most 3 x 3 reads at the level where it spans 2 cells), minus δ(d).
   - Height above the raised eye: y = m - (h(E0) + ρv). Dropping the concave rise term a² / 2R makes this conservative.
   - Contribution: H = y / r_out if y > 0, else y / r_in.
4. **Rings.** Output rings are geometric, ratio 1.05, from 8 m to 1,500 m: 108 rings. A sector adds its H to the first ring at or beyond r_out. The table holds the running maximum along each bin: 1,024 x 108 float32 = 442 KB.
5. **W(d) and δ(d)** come from a snapshot of the active chain taken on the main thread (per active level: spacing, underlay offset, effective fade-out end; the Terrain3D handoff). Use the 3D distance sqrt(r² + Δh²) to look up the level.

**Test (phase 6, after the frustum planes; per row):**
1. Sphere: centre c and radius r as the frustum test computes them (mesh sphere x scale + 0.6 x height + 0.25 m).
2. rel = c - E0 (world, floating origin). Tangent coordinates (x, y, z) from the frame vectors in the frame block, with y measured from the raised eye: y = dot(rel, up) - ρv.
3. d = sqrt(x² + z²); r' = r + ρh; near = d - r'. Keep the row if near < 8 m or the azimuth span exceeds 64 bins.
4. Azimuth span: φ = atan2(z, x), β = asin(r' / d) + 2e-4; bins floor((φ - β) / Δφ) to floor((φ + β) / Δφ), wrapped.
5. Ring k = floor(log(near / 8) / log(1.05)), clamped to the table.
6. top = y + r. tanα = top / near if top ≥ 0, else top / (d + r').
7. Hidden only if every bin in the span has H[bin][k] > tanα. A NaN or out-of-range value keeps the row.
8. The cost is at most 64 buffer reads per mid-distance tree; near trees exit at step 3.

**Why it is exact.**
- Rendered terrain ≥ occluder over every sector, measured with margin (9.2).
- A ray from any eye inside the latch cylinder (horizontal displacement ≤ ρh, height ≤ h(E0) + ρv) to any point of the sphere crosses each sector of its bin between r_in and r_out. A displaced eye sees the dilated sector at the same polar position, and lowering the eye only hides more over a height field.
- The ring lookup uses only sectors that end before the nearest point of the inflated sphere.

**Latch, speed, teleports and rebases.**
- **Validity.** A table is usable for a camera latch only if the latch camera plus the speed margin (speed x 0.1 s, as the frustum latch uses) lies within ρh = 4 m horizontally of E0 and below h(E0) + ρv, with ρv = 1 m.
- **Rebuild.** A rebuild starts on a worker when the camera has moved 2 m horizontally or risen 0.5 m. It is built at the predicted position (camera + velocity x 0.15 s) at the camera's own height.
- **Publish.** When it finishes, the table is uploaded once (`BufferUpdate`, 442 KB) and phase 6 re-runs with the reason "horizon" (the existing re-latch path; descending never needs a rebuild).
- **Fallback.** At a frustum re-latch with no valid table, occlusion is off for that latch (frustum only), so it never culls with a stale table.
- **Speed limits.** Off above 40 m/s horizontal or 3 m/s rising (hysteresis 30 / 50 and 2 / 4 m/s). At walking pace that is 1 to 3 rebuilds a second, and the vertical latch is what keeps recall at 73 %.
- **Residency gate.** Off until the terrain under the disc is the steady-state chain:
  - Terrain3D visual coverage is ready (`grid.SurfaceTerrainVisualReady`);
  - every active proxy level is fully resident out to min(its fade-out end, 1,536 m) around the eye (a new per-level resident radius, 9.6 HZ-2).
  - After a teleport this is false for the first second or so (run A: errors gone 60 frames later). In flight above about 100 m/s it is false anyway.
- **Rebase.** The frustum filter's 0.5 s hold already covers the rebase. E0 is kept in canonical doubles and converted to world coordinates at every latch from the current geometry snapshot.
- **Other off conditions:**
  - the occluder field not yet filled around the eye (after a teleport, or above 300 m AGL where it pauses to save work);
  - `UndergroundHold` (already on the layer);
  - the eye below sea level (`IsSwimming`), because the source reads the seabed as the sea surface;
  - a megastorm blast, inherited from phase 6;
  - `TreeFrustumCulling` off.
- **Switches.** A/B `TreeTerrainOcclusion` (public, not exported, default on once verified). Verify: the existing `TreeCullVerify` already draws every row phase 6 drops in magenta, horizon drops included. No grave-key row: this is not a look tunable.

### 9.4 Saving (oracle and replay)

**Ceiling, measured.** `tools/tree_visibility_run.ps1 -Test oracle` at the pose yaw, A-X-A, 8 s windows, 1600 x 900, RTX 4070. The camera lists are already frustum-culled, so `copy_all` equals the baseline. The poses are in `oracle_poses.json`; the hilly ones were found by a relief scan of 20 x 20 km around the forest and the mountain save (9.8).

| pose (yaw) | frame wall p50 | tree camera draws (`copy_none`) | hidden by terrain (`copy_vis_terrain`) | horizon, recommended latch (4 m / 1 m) | horizon, 8 m all round |
|---|---|---|---|---|---|
| forest edge (225) | 10.08 | 2.39 | 1.08 (opaque 0.75) | 1.01 | 0.57 |
| hill forest valley (90) | 8.05 | 1.45 | 0.86 (0.59) | 0.58 | 0.17 |
| hill forest valley 2 (90) | 9.46 | 1.41 | 0.34 (0.25) | 0.20 | 0.06 |
| mountain valley (0) | 7.40 | 0.30 | 0.04 | 0.03 | 0.03 |

- **Baseline frames** (`tools/perf_bench.ps1 -Label tocc_hills`, the new scenarios, frozen slot 3 snapshot of 2026-11-16, High, VSync off, 2 x 12 s): `hill_forest_valley` 8.18 ms (GPU 7.92), `hill_forest_valley2` 9.61 (9.30), `mountain_valley` 7.44 (7.18), `forest_edge` 10.28 (10.00). `GPU.TreeCulling` is 0.02 to 0.03 ms. The snapshot's settings have `HeightShadowL0TexelMeters` = 8 and tree near detail 10 to 20 m (billboards from 150 m), which is why the tree camera cost is far below section 3.3. Report: `perf_bench/report.md` in the evidence folder (9.8).
- **How the horizon columns are derived.** The terrain-hidden saving times the share of terrain-hidden triangles the replayed horizon test removes at that view. The terrain-hidden set costs about 1.3 to 1.5 ms per million triangles, as in section 3.3.
- **Averaged over four yaws**, recommended latch (ms, from culled triangles x 1.45 ms per million):

  | forest edge | hill forest valley | hill forest valley 2 | forest interior | slope | others (plains, two more valleys, mountain) |
  |---|---|---|---|---|---|
  | 0.44 | 0.31 | 0.10 | 0.06 | 0.05 | 0.00 to 0.02 |

- **Replay over 40 views** (10 poses x 4 yaws), share of terrain-hidden triangles removed:

  | latch (horizontal / vertical) | 0 / 0 (no latch) | 2 / 0.5 | **4 / 1** | 8 / 1 | 4 / 4 | 8 / 8 | 16 / 16 |
  |---|---|---|---|---|---|---|---|
  | share removed | 0.87 | 0.79 | **0.73** | 0.64 | 0.61 | 0.39 | 0.15 |

  - With the margins of 9.2 removed (W 2 m, δ 0), the share is 0.92: the margins cost about 5 points, the eye allowance the rest.
  - 2,048 bins with finer rings gain 2 points; not worth four times the table.
- **Correctness of the replay.** Every tree the replay culled in 14 views with saved depth was ray-tested per pixel against the terrain-only depth (`exact_an.py`): 0 visible, in every variant including the no-margin one. The Hi-Z classes of the oracle mark up to 461 of those trees "visible". That is the pyramid's conservatism: a sanity check found all 150 sampled "terrain-hidden" trees per view hidden, and 53 to 95 % of the "visible" ones visible.
- **Costs.**
  - Not measured: the occluder field fill (a strip of 9,600 samples per 8 m of travel, est. 20 to 60 ms of worker time), the builder (estimate 2 to 5 ms on a worker per build, at 1 to 3 builds a second when walking; one 442 KB upload per build) and the extra phase-6 re-runs, one per publish. Each re-run is the same work as a frustum re-latch, about 450 dispatches.
  - The GPU test adds at most 64 reads to the rows that survive the frustum test.
  - Budget: under 0.1 ms main thread per frame on average, and no change in `Trees frame request` p95 in the 360 deg/s and walk sweeps.

### 9.5 Validation plan

1. **Builder property test (headless, new `tree_horizon_builder_test`).** Synthetic lattices: ridge, cone, a 2 m notch through a ridge, noise, an edit crater, an aperture. The "rendered" surface is the lattice minus the underlay, with LOD sag modelled by sampling it at the coarser spacing. Random trees; eyes sampled on the latch cylinder (edge, top and centre).
   - Every tree the table hides must be hidden from every sampled eye by a brute-force ray march (step 0.25 m) against the rendered surface. Zero failures over 100,000 trees.
   - Also asserts the field's cell minima against a brute-force 2 m scan, the clipping to the window, the edit fold and minus infinity at apertures.
2. **Parameter layout.** `gpu_vegetation_parameter_layout_test` gains the frame block words and the table binding.
3. **Byte identity at rest** (`tree_frustum_identity_gpu_test`, extended). Occlusion on against off at the forest edge (225), hill forest valley (90), hill forest valley 2 (90) and forest interior (0), with clocks and wind held: identical PNGs. Also after a cloud move and an 8 m step.
4. **Ridge sweep (`tree_cull_verify_sweep`, new sweeps; magenta must be 0 in every frame):**
   - `ridge_rest_<pose>`: the three hilly poses at four yaws, occlusion and verify on; also proves the table is in use (the counter `TreeHorizonCulledRows` above 0).
   - `ridge_walk`: from the hill forest valley floor, walk (ground controller, W held) up and over the crest found by `SampleHeight` (the highest point within 600 m), and 200 m down the far side.
   - `ridge_fly5`, `ridge_fly15`, `ridge_fly40`: scripted terrain-following flight (`BeginScriptedFlight` / `SetScriptedFlightCommand`, vertical velocity from the slope ahead) at 3 m above the ground over the same crest at 5, 15 and 40 m/s, facing forward and then 90 degrees off the path.
   - `ridge_popup2`, `ridge_popup5`: vertical climb at 2 and 5 m/s from the valley floor facing the ridge, 0 to 60 m, the case that most tests ρv and the 0.5 m rebuild trigger. 5 m/s must find occlusion switched off by the rising-speed limit.
   - `ridge_teleport`: teleport to the valley floor facing the crest, every frame counted until streaming is idle; occlusion must stay off until the residency gate opens.
   - `ridge_altitude`: rise from 1.7 to 400 m AGL at 20 m/s over the hills; the field's altitude pause must turn occlusion off without a magenta frame.
   - **Occlusion canary:** at the valley floor facing the crest, after a re-latch, move the eye 30 m up within one frame (`PlaceScriptedFlight`). Horizon-culled trees then show in magenta. The count must be above 0, or the zero counts prove nothing.
   - The existing sweeps (yaw360, pitch, walk, fly72 to fly36800, teleport, rebase) rerun with occlusion on.
5. **Performance.**
   - `tools/perf_bench.ps1 -Scenarios hill_forest_valley,hill_forest_valley2,mountain_valley,forest_edge,forest_interior` with `PERF_BENCH_TREE_PROPS="TreeTerrainOcclusion=0"` against `=1` (A-B-A).
   - The oracle at the four poses for the ceiling.
   - The walk and yaw360 sweeps for `Trees frame request` and the new `Trees horizon build` scope.
6. **Tests per step:** `tools/run_tests.ps1 -Files <changed files> -Label occl-hz-<order>` (`-DryRun` first), plus the named list in each order.

### 9.6 Work orders (Phase 2; one implementer, in order)

Start after lane T's current changes (shadow cascade culling and the small tree fixes in `ring_gpu_vegetation_indirect.glsl`, `RingGpuVegetationCompute.cs` and `RingBiomeDecorationLayer.Gpu*.cs`) are committed. Line numbers are as of `b5ab829`.

**HZ-0 Harness (lane V, S).**
- Promote `tocc_probe.gd` to `tools/terrain_margin_probe.gd`, and its runner, with the `static`, `fly_stop`, `teleport_now` and `trees` capture kinds and a `--rule` check that applies W(d) and δ(d) to every capture.
- Add the poses `hill_forest_valley_eye`, `hill_forest_valley2_eye` and `mountain_valley_eye` to `tools/fixtures/tree_visibility_poses.json`, with the values from `perf_bench_scenarios.json`.
- Manifest: GPU, manual tier, own runner.
- Purpose: rerun the margin study whenever a terrain LOD, underlay or Terrain3D change lands.

**HZ-1 Occluder field (M; new `scripts/Voxel/LOD/RingTreeOccluderField.cs`, pure C# plus a thin owner on the tree layer).**
- **Cells.** 8 m cells; each value = the minimum of `sampler.GetPresentationSurfaceHeight` over the 5 x 5 samples of the 2 m lattice on the cell's closed footprint. Window 384 x 384 cells, toroidal slots, unwrapped around index (the pattern of `RingHeightShadowLattice`). Recentre when the explorer is 32 cells from the centre; fill only the new strips, nearest first.
- **Workers.** Two, each with its own `grid.CreateIndependentTerrainSampler()`, below-normal priority as in `RingHeightShadowClipmap.WorkerPriority`.
- **Publish.** An immutable snapshot (cells copied into a fresh array, or double buffer) with its window, a revision and a ready flag (the whole window filled).
- **Edits and apertures.** On publish and when the field `Revision` changes, fold the edits in (min), and write minus infinity for aperture cells. That needs a read accessor on `Terrain3DGameplay` for its `RingSurfaceHeightField`.
- **Altitude pause.** Above 300 m AGL the field pauses (not ready).
- **Cost check.** The CPU scope `Trees occluder fill`. Measure the full fill and the per-strip cost; escalate if a strip over a walking frame exceeds 2 ms of worker time.
- **Do not touch the height shadow.**
- Tests: a headless test of the cell minima against brute force on a fixed-seed sampler window, the scroll (no stale slot after a seam crossing and a recentre), the edit fold and apertures.

**HZ-2 Chain snapshot and residency (M; `RingTerrainLodCoordinator.cs`, `RingTerrainProxySector.cs`).**
- **Snapshot.** `RingTerrainLodCoordinator.CaptureOcclusionChain(Vector3 eyeWorld)` returns, per active level in order: spacing, underlay offset, the effective fade-out end used by its material, the morph target spacing, and `ResidentRadiusMeters`. The last is the largest radius around the eye within which every sector of the level's window is installed at the current request and edit version.
- Plus `TerrainHandoffMeters` and whether Terrain3D visual coverage is ready.
- **Cost.** Main thread, at most 0.05 ms. Compute the resident radius incrementally on install and removal, or over the window's missing-sector set; no per-frame scan of all sectors.
- **No rendering change.** Tests: `proxy_partial_coverage_test`, `lod_altitude_activation_contract_test`, `proxy_coverage_culling_settings_smoke`, and a new headless test of the snapshot on a fake chain.

**HZ-3 Horizon builder (M; new `scripts/Voxel/LOD/RingTreeHorizon.cs`, pure C#, no Godot node).**
- **Inputs:** the occluder field snapshot (HZ-1), the eye canonical pose and tangent frame, the chain snapshot (HZ-2), and the parameters of 9.3. Defaults: N 1,024, sector ratio 1.02, ring ratio 1.05, r0 8 m, disc 1,536 m, ρh 4 m, ρv 1 m, δ extra 0.1 m, W floor 8 m.
- **Output:** a `float[]` table (bin-major), the frame (E0 canonical, the R used) and a build id.
- Runs on a worker (one at a time; a newer request supersedes a queued one). A `Trees horizon build` CPU scope.
- **Exact formulas as in 9.3,** including H for y ≤ 0 using r_in, the 1 m tangent slack, and minus infinity for aperture regions and NaN heights.
- Test: the property test of 9.5 item 1, `tools/TreeHorizonBuilderTest.cs` plus `tools/tree_horizon_builder_test.gd`, headless, gate tier.

**HZ-4 Phase 6 test and plumbing (M; `ring_gpu_vegetation_indirect.glsl`, `RingGpuVegetationCompute.cs`).**
- **Frame block.** `CameraCullState` gains `Horizon` (null = off): eye world position, the tangent vectors (around, up, axis), ρh, ρv, N, ring count, r0, ring ratio, and the table as `float[]`. `CameraCullBlockBytes` grows from 7 to 11 vec4s (std140): word 28 = horizon on, then eye, the three vectors with N, ring count, r0 and log ratio in their w words, then ρh and ρv.
- **Table buffer.** Set 1, binding 9: a storage buffer `Horizon { float hz[]; }` sized for 1,024 x 128, allocated once, with a placeholder when off. It is updated in the same `BufferUpdate` step as the frame block, only when the build id changes.
- **Shader.** `camera_keeps()` calls `horizon_keeps(world, radius)` after the plane loop, only when `cull.settings.x > .5` and horizon on. Write the test exactly as in 9.3, steps 1 to 8: keep on NaN, on a span over 64 bins, and on near below r0.
- **Counter.** `TreeHorizonCulledRows` comes from the verify list count in verify mode, otherwise from a debug readback in the audit only (no per-frame readback).
- Tests: `gpu_vegetation_parameter_layout_test` (new words), `tree_frustum_identity_gpu_test`, `gpu_tree_integration_test`, `tree_lod_dither_gpu_test`, `vegetation_haze_gpu_test`, `vegetation_shadow_gpu_test`, `tree_gpu_rebase_smoke`, `tree_compute_optimization_regression`.

**HZ-5 Latch and gates (M; `RingBiomeDecorationLayer.Gpu.cs`, `UpdateTreeCameraCulling`).**
- **Rebuild and publish.** The rebuild trigger (2 m horizontal, 0.5 m rise from E0, predicted position) and the async publish with re-latch reason "horizon".
- **Validity check at every latch** (9.3), including the speed margin and the latch movement threshold.
- **Gates.** Speed limits, residency gate (HZ-2), occluder field ready (HZ-1), underground, swimming, megastorm, rebase hold, `TreeFrustumCulling`.
- **Switch** `TreeTerrainOcclusion` (public, not exported). Counters `TreeHorizonBuilds`, `TreeHorizonPublishes`, `TreeHorizonLastOffReason`.
- Tests: HZ-4's list plus `tree_gpu_frame_cache_smoke`, `vegetation_wind_gpu_test`, and the identity test with the switch.

**HZ-6 Validation (lane V, M).**
- 9.5 items 3 to 5. New sweeps in `tools/tree_cull_verify_sweep.gd`, the identity extension, and perf_bench A-B-A.
- Contact sheets of any magenta frame; keep only the cited ones (AGENTS.md ffmpeg rules).

**Escalate to the primary agent if:**
- any magenta pixel appears, or the identity A/B differs;
- the property test finds a single failure;
- the builder takes over 5 ms per build or the main thread over 0.1 ms per frame on average;
- the residency radius cannot be computed without a per-frame scan;
- a margin rerun (HZ-0) finds rendered minus occluder below 0 at any settled view.

| lane | owns | orders |
|---|---|---|
| T (trees) | `ring_gpu_vegetation_indirect.glsl`, `RingGpuVegetationCompute.cs`, `RingBiomeDecorationLayer.Gpu.cs`, new `RingTreeHorizon.cs` and `RingTreeOccluderField.cs` | HZ-1, HZ-3, HZ-4, HZ-5 (one implementer, in order) |
| LOD | `RingTerrainLodCoordinator.cs`, `RingTerrainProxySector.cs`, the field accessor in `RingTerrain3DGameplay.cs` | HZ-2 (can run in parallel with HZ-1 and HZ-3) |
| V (tests) | `tools/tree_*`, `tools/terrain_margin_probe*`, `tools/fixtures/*`, `tools/test_manifest.json` (append) | HZ-0 first, HZ-6 last |

### 9.7 Risks

| risk | effect | mitigation |
|---|---|---|
| A terrain change (new underlay, LOD profile, Terrain3D handoff, T4 vertex work) lowers the rendered surface | a tree hidden over a ridge | W and δ come from the live chain, not constants; HZ-0 rerun is part of any terrain LOD order; the property test |
| Streaming lag shows a coarse underlay near the eye | same | residency gate (HZ-2); teleport and fast flight already off |
| Player edits lower the terrain | a dug notch through a ridge hides a tree | edits folded into the cell minima on every field revision; edit crater in the property test |
| Eye moves faster than the rebuild | stale table | validity check at every latch, frustum-only fallback, speed limits |
| A point-sampled source misses 2 m gullies | occluder above the surface in a gully | cells hold the minimum of every 2 m sample (measured: 4 m and 8 m point samples err by up to 1.3 and 1.9 m) |
| Occluder field fill cost | worker CPU when travelling | strips only; altitude pause; budget check in HZ-1 |
| Builder or extra re-latches cost CPU | frame time while walking | worker build, one upload per build, budget in the escalation list |
| Lane T's work in the same files | merge conflict | start after it lands |

### 9.8 Evidence

All in `.godot/audit_baseline/common/perf/tree-occlusion/` (kept by the prune):
- `tocc_probe.gd` (probe), `tocc_run.ps1` (runner: own optimized build `bin\OptTocc`, guarded Debug-slot restore), plans `plan_a/b/c.json`, `oracle_poses.json`.
- Analysis scripts:
  - `margin_an.py`: per-band errors and the slack of occluder candidates; `rule_check` applies the occluder rule;
  - `hz_an.py`: the horizon builder and test, replayed;
  - `exact_an.py`: per-pixel ray check.
- `runs/tocc_a_*`: margin study, 32 captures and the relief scans; `margin.txt` and `margin.json` hold the tables; the 2 m lattices are kept, the per-pixel point dumps were deleted after analysis.
- `runs/tocc_b_*`: tree census of 40 views; `hz_sweep.json` holds the variant results.
- `runs/tocc_c_*`: 14 views for the exact check (the depth dumps were deleted after it; rerun plan C to repeat it).
- `oracle/`: the oracle run's `report.json` and `console.log` (summary: `python tools/tree_visibility_summary.py oracle/report.json`).
- `perf_bench/`: `report.md` and `report.json` of the hilly baseline run.
