# Final Ring Scale and Certification Target

Status: engineering target and gap assessment  
Target platform: Godot 4 / current RingworldTechDemo architecture  
Last updated: 2026-07-21

## 1. Certified target

The following is the final production-scale target. It replaces the current small-ring defaults as the size the project must be able to run reliably.

| Property | Certified value |
|---|---:|
| Ring radius | 5,000 km |
| Ring diameter | 10,000 km |
| Ring width | 318 km |
| Ring circumference | 31,416 km |
| Structural depth | 22.3 km |
| Local voxel size | 1 m |
| Terrain elevation range | -2 km to +8 km |

The circumference implied by a 5,000 km radius is 31,415.93 km. Runtime topology should use an exact integer number of 32 m arc chunks and derive the effective radius from that integer circumference. This keeps the seam exact; the resulting radius difference is negligible.

### Baseline certification

| Item | Requirement |
|---|---|
| Hardware | Intel Core Ultra 7 265V, Intel Arc 140V, 16 GB system RAM |
| Output | 1920 x 1080, High preset |
| Frame-rate target | 60 FPS |
| Frame-rate floor | 45 FPS |
| Process working set | no more than 6 GB |

### Discrete-GPU certification

| Item | Requirement |
|---|---|
| Hardware | RTX 3060 / RX 6600 class |
| Output | 2560 x 1440, Very High preset |
| Frame-rate target | 60 FPS |

Very High on the Arc 140V is an optional 30 FPS mode, not the baseline certification target.

## 2. Scale implications

The playable surface is approximately 9.99 million square kilometres. At 1 m resolution it would contain nearly 10 trillion surface columns before vertical voxels are considered. The complete ring must therefore never be instantiated, sampled, meshed, or saved as a uniform voxel volume.

The intended representation is hierarchical:

1. A small, editable 1 m voxel neighbourhood around the player.
2. Progressively coarser terrain clipmaps outside the editable neighbourhood.
3. Regional HLOD for hundreds of kilometres.
4. A cheap analytic or precomputed global ring for the distant arc and opposite side.

The 22.3 km structural depth is primarily a visual and collision-shell requirement. It must not become a fully allocated 1 m-deep voxel volume. Terrain elevation is measured relative to the habitable datum and is independent of structural depth.

At a 5,000 km radius, the surface departs from the local tangent by approximately 10 m at 10 km, 250 m at 50 km, 1 km at 100 km, and 6.25 km at 250 km. These are useful visual tests for whether curvature and scale read correctly.

## 3. Done — preserve as-is

These systems already match the final direction. They should be retained unless profiling identifies a specific defect.

| System | Current result | Why it remains valid |
|---|---|---|
| 1 m local voxel basis | `VoxelConstants.VoxelSizeMeters` is 1 m | Matches the certified local voxel size exactly. |
| Sparse chunk streaming | Only nearby chunks are generated and meshed | This is the only viable basis for a ring with almost one trillion 32 m surface chunks. |
| Integer wrapped topology | Around-ring positions wrap through canonical chunk/voxel coordinates | Avoids a special seam and lets edits and sampling remain periodic. |
| Exact circumference from chunk count | Runtime ring length is an integer number of chunks | Preserves deterministic wrapping at the certified radius. |
| Deterministic periodic terrain sampling | Terrain can be reproduced from seed and canonical coordinates | Enables distant proxies, edits, saves, and multiplayer to reference the same terrain without storing the world. |
| Radial ring frame | Terrain placement, gravity, and surface orientation follow the ring | Correct model for movement around the inside of the ring. |
| Phase C LOD coordinator | Central quality profiles, screen-space-error bands, fair scheduling, worker limits, and upload-time budgets | This is the correct control plane for scalable terrain. |
| Canonical proxy sector windows | Proxy sectors wrap consistently and exclude covered inner regions | Prevents duplicate terrain and seam instability as the player moves. |
| LOD transition treatment | Parent-height geomorph data, dithering, skirts, stale-result rejection, and retained old sectors | Provides the right tools for hiding cracks and asynchronous replacement. |
| Background proxy generation | Cancellable workers build terrain away from the main thread | Required for stable frame pacing. |
| Segmented global HLOD | Global terrain can rebuild in bounded segments instead of as one mesh | Appropriate foundation for edit-aware regional terrain. |
| Edit-aware propagation | Terrain edits dirty proxy/global regions rather than forcing full reconstruction | Necessary for sparse, persistent changes. |
| Min/max feature preservation | Coarse terrain retains important elevation extrema | Helps mountains and depressions survive aggressive reduction. |
| LOD diagnostics | Worker, upload, mesh-memory, segment, and mip statistics exist | Provides the measurements needed to enforce certification budgets. |
| Phase C automated checks | Project builds, headless startup runs, and LOD hierarchy self-tests pass | The implemented LOD foundation has a repeatable regression baseline. |

## 4. Done — needs targeted changes

These systems exist and should be evolved, not replaced.

| System | Current state | Required change for certification |
|---|---|---|
| Launcher scale controls | Radius supports up to 8,000 km; width stops at 80 km; terrain build height stops at 4,096 m | Add a named **Certified Halo Ring** preset and support 318 km width plus the full -2,000 m to +8,000 m terrain domain. Keep experimental values clearly separate from certified ones. |
| Coordinate types | Long/integer canonical coordinates exist, but some geometry snapshots and render-facing calculations still use floats | Make 64-bit/integer canonical coordinates authoritative for streaming, edits, saves, and traversal. Convert to float only after subtracting the local origin. |
| Floating origin | Origin shifting exists conceptually/in parts of the stack | Complete it across player movement, terrain proxies, global HLOD, physics, particles, atmosphere, clouds, audio, and debug rendering. No world-space float should contain a multi-million-metre position. |
| Terrain vertical limits | Current terrain and hull bounds are substantially smaller than -2 km to +8 km | Separate logical elevation range from actively loaded vertical cells. Stream sparse vertical chunks; never allocate all 10 km of height locally. Audit bounds, serialization, collision, shaders, and editor exports. |
| Min/max pyramid | Certified preprocessing uses scale-aware 4,096 m cells, asynchronous generation, and disk caching | Keep the cache keyed by the complete terrain-definition fingerprint (seed, generator version, dimensions, and every height-affecting setting). Retain finer regional data only where needed. |
| Global HLOD resolution | Current limits and default segment count were tuned for much smaller rings | Treat current global HLOD as regional terrain. Benchmark approximately 4,096 x 128 for High and up to 8,192 x 256 for Very High, divided into at least 128–256 independently culled segments. Do not render every segment. |
| Global terrain startup | Global summaries and meshes can perform significant work during initial construction | Move preprocessing and mesh generation to cancellable background jobs, use cached assets, and expose progress. Cached startup should not regenerate the entire ring. |
| Structural hull | A global hull exists, but it is not yet a certified 22.3 km structural representation | Give the ring a separate low-cost underside/edge shell with 22.3 km depth. Its distant form can be analytic; only nearby exposed edges need detailed geometry or collision. |
| Atmosphere and haze | Thin fog and terrain haze are implemented, with earlier fixes to avoid turning distant terrain uniformly blue | Retune all extinction distances to the certified radius. Preserve surface colour and value contrast; use height, sun angle, and line-of-sight distance instead of a strong unconditional blue blend. Add visual tests at 10, 50, 100, and 250 km. |
| Cloud layer | A curved cloud shell and cloud shadows exist | Make cloud coordinates canonical/camera-relative, tile or synthesize them without repetition at planetary distances, and assign High/Very High density, shadow, and update-rate budgets. |
| Terrain sunlight/shadows | Local terrain shadow coverage exists; lower-LOD light leaks have been addressed locally | Complete long-range sun occlusion and validate that every LOD casts/receives equivalent large-scale terrain shadowing. Avoid full-distance shadow maps. |
| Quality profiles | High/Very High have SSE, worker, and upload budgets | Retune against the certification machines. High must be the Arc 140V 60 FPS preset; expensive visual features must not silently consume the terrain budget. |
| Memory reporting | Terrain mesh and scheduling diagnostics exist | Add actual process working-set, GPU-resource estimates, cache counts, and high-water marks. Enforce unload hysteresis and hard caps rather than reporting only. |
| Visibility/culling | Segmentation enables frustum and distance culling | Ensure bounds are conservative under curvature and morphing. Divide instancing spatially; do not rely on occlusion culling as the main solution for the open ring landscape. |

## 5. Not done — must be started

### 5.1 Precision completion

- Finish Phase D: 64-bit/integer authoritative position, complete floating origin, and camera-relative reconstruction in every shader and render system.
- Add seam-crossing, long-distance travel, save/load, edit, and physics tests at the certified circumference.
- Add an automated precision test that travels at least one full circumference without coordinate drift or visible snapping.

### 5.2 Global analytic ring tier

- Add a representation below regional HLOD for the full arc and opposite side of the ring.
- It should express ring silhouette, 318 km width, 22.3 km depth, broad terrain colour/elevation, atmosphere, and cloud coverage without carrying metre-scale geometry.
- It must be camera-relative, segmented, and cheap enough to remain present at all times.
- Cross-fade it with regional HLOD; regional HLOD then cross-fades with terrain clipmaps.

### 5.3 Certified world preset and data pipeline

- Add the exact certified dimensions as a versioned preset.
- Generate/cache scale-dependent terrain summaries, min/max data, global colour/elevation maps, and global mesh data by seed and generator version.
- Make first-generation work resumable and bounded. Target less than 30 seconds for first preparation and less than 5 seconds for a cached playable startup on the baseline machine.
- Invalidate only the affected cache products when terrain-generator versions change.

### 5.4 Structural presentation

- Build the 22.3 km underside and side walls as separate scalable geometry.
- Define how oceans, cliffs, exposed edges, and damage reveal structural depth.
- Provide collision only where gameplay can reach the structure.

### 5.5 Long-range lighting and atmosphere

- Implement ringshine/large-scale indirect lighting appropriate to the inner surface.
- Implement long-range solar occlusion using analytic horizon/ring/terrain tests or low-resolution masks.
- Finalize haze, aerial perspective, clouds, and cloud shadows using physically plausible distance and height controls.
- Add large-scale landmarks and cloud systems whose size is legible from tens to hundreds of kilometres.

### 5.6 Performance scaling

- Add dynamic resolution or a supported upscaler path for the baseline High preset.
- Separate CPU, GPU, terrain, shadow, atmosphere, vegetation, and post-processing budgets.
- Add adaptive worker/upload throttling based on recent frame time and memory pressure.
- Add density fallbacks for vegetation, props, clouds, shadow casters, and particles before reducing terrain continuity.

### 5.7 Certification harness

- Add deterministic benchmark routes: standing vista, high-speed travel, seam crossing, maximum terrain relief, dense edits, cloud-heavy weather, and worst-case lighting.
- Record average FPS, 1% low, worst frame, CPU/GPU frame time, process working set, terrain memory, draw calls, visible triangles, jobs, uploads, and cache activity.
- Produce a machine-readable report and a concise pass/fail summary.
- Validate on real Arc 140V/16 GB and RTX 3060 or RX 6600-class hardware; estimates from a faster machine do not certify the preset.

## 6. Target runtime hierarchy

Recommended starting distances are budgets to profile, not promises to hard-code:

| Tier | Representation | High starting target | Very High starting target |
|---|---|---:|---:|
| Local editable terrain | 1 m voxels and collision | 192 m visual / 128 m collision | 256–384 m visual / 192 m collision |
| Terrain clipmaps | 2, 4, 8, 16, 32, 64, 128, 256 m samples | to 150–200 km | to 250–300 km |
| Regional HLOD | Coarse segmented height/colour geometry | approximately 200–1,000 km | approximately 300–1,500 km |
| Global ring | Analytic or precomputed segmented shell | beyond regional HLOD | beyond regional HLOD |

Distance alone should not select terrain detail. Continue using screen-space error, with distance bands acting as stable caps and hysteresis boundaries.

## 7. Certification budgets

### Baseline: Arc 140V, 1080p High

| Metric | Pass target |
|---|---:|
| Average/steady target | 60 FPS |
| 1% low | at least 45 FPS |
| GPU frame time at target | no more than 16.6 ms |
| Main-thread frame time at target | no more than 12 ms |
| Terrain proxy uploads | no more than 2.0 ms/frame during normal travel |
| Global edit rebuild work | no more than 2.0 ms/frame |
| Process working set | no more than 6 GB |
| Terrain GPU allocation target | no more than 1.5 GB |
| Visible terrain triangles | no more than 2 million typical |
| Terrain draw calls | no more than 500 typical |
| Severe hitch threshold | no frame over 33 ms during the benchmark route after warm-up |

### Discrete: RTX 3060 / RX 6600, 1440p Very High

| Metric | Pass target |
|---|---:|
| Average/steady target | 60 FPS |
| 1% low | at least 50 FPS |
| Working set | no regression above the 6 GB baseline unless a documented Very High cache budget is approved |

Frame-rate results are valid only when the certified ring dimensions are active and the route includes active streaming. An empty scene, stationary camera, or reduced ring is not a certification result.

## 8. Recommended implementation order

### Stage 1 — unlock the dimensions

- Add the certified preset.
- Widen launcher/config limits to 318 km and -2/+8 km.
- Audit all coordinate and bounds types.
- Confirm integer circumference, wrapping, and seam tests at 5,000 km radius.

Definition of done: the certified world loads, canonical coordinates wrap correctly, and no system rejects or truncates its dimensions.

### Stage 2 — make preprocessing scale safely

- Replace the fixed full-ring 512 m min/max build with scale-aware summaries.
- Move global preparation off the main thread.
- Add seed/version caches and memory caps.
- Retune global segment counts and bounds.

Definition of done: a cold start is bounded, a cached start is fast, memory remains below 6 GB, and startup never constructs a full-resolution ring.

### Stage 3 — finish precision and traversal

- Complete Phase D across rendering, physics, atmosphere, clouds, audio, and tools.
- Run full-circumference, seam, teleport, and high-speed streaming tests.

Definition of done: travel anywhere on the ring shows no precision jitter, cracks, coordinate drift, or persistent stale terrain.

### Stage 4 — complete the visual hierarchy

- Add the analytic global ring tier and 22.3 km structural shell.
- Retune regional HLOD/clipmap cross-fades.
- Retune haze and clouds at canonical distances.
- Add ringshine and long-range sun occlusion.

Definition of done: the ring reads at local, regional, and planetary scale without blue wash, light leaks, obvious repetition, holes, or LOD pops.

### Stage 5 — optimize and certify

- Implement the benchmark harness and quality fallbacks.
- Profile CPU, GPU, memory, and streaming independently.
- Tune High on Arc first, then Very High on the discrete target.
- Run extended traversal and edit soak tests.

Definition of done: both certification tables pass on their named hardware and the reports are checked into the project test results.

## 9. Explicit non-goals and guardrails

- Do not create a uniform 1 m voxel array for the whole ring or its structural depth.
- Do not use render-space floats as authoritative world coordinates.
- Do not keep the current 512 m full-ring summary density simply because it works on the smaller prototype.
- Do not solve distant detail by raising every LOD resolution at once.
- Do not make Very High on an integrated GPU the certification baseline.
- A 30,000 km-diameter first-generation Halo-style ring may remain an experimental stress target, but it is not part of this certification until the 10,000 km ring passes.

## 10. Reference basis

The 10,000 km diameter and approximately 318 km width match commonly documented dimensions for the later Halo Array installations. The exact performance and architecture targets in this document are project engineering decisions, not claims about Halo canon.

- Halo Array dimensions: <https://www.halopedia.org/Halo_Array>
- Halo Encyclopedia provenance: <https://www.halowaypoint.com/news/master-class>
- Godot large-world coordinates and origin shifting: <https://docs.godotengine.org/en/4.4/tutorials/physics/large_world_coordinates.html>
- Godot visibility ranges/HLOD: <https://docs.godotengine.org/en/4.0/tutorials/3d/visibility_ranges.html>
- Godot visibility and occlusion guidance: <https://docs.godotengine.org/en/stable/classes/class_viewport.html>
- Godot MultiMesh spatial-culling considerations: <https://docs.godotengine.org/en/stable/tutorials/performance/using_multimesh.html>
