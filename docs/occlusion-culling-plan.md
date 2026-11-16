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
