# GPU offload plan: proxy sector install (item 6) and proxy sector heights (item 7)

Status: design only, 2026-11-05, branch `gpu-offload`. No source changes were made for this document.
Scope: `scripts/Voxel/LOD/RingTerrainProxySector.cs` (the LOD1-LOD5 proxy rings, 2-32 m spacing, 65 x 65 vertices per sector)
and the worker-side height model `scripts/Terrain/Generation/TerrainGenerationHeightModel.cs`.
Sibling work on this branch: GPU-1 (FFT ocean coverage atlas, `FftOceanCoverageAtlas.cs` plus proxy lines ~2310-2360, ~2570, ~2780,
~5730, ~6450-6630), GPU-4 (far weather atlases), GPU-5 (tree root prep).

## 0. Summary and recommendation

1. **Do the CPU fixes first (phase C).** The worker profile shows that every proxy lattice point evaluates the full
   height model three times and the legacy climate noise twice. Only one of each is needed. Removing the duplicates keeps
   the output bit-identical and should cut about half the proxy worker time (section 5). It touches no GPU-1 lines.
   A pure-C# FastNoiseLite is **not** worth doing: native noise arithmetic is about 90 % of model time, and the interop
   overhead is small (section 5.4).
2. **Item 7 buys streaming latency, not desktop frame time.** In ground_fast the workers are 25-50 % busy on a 32-thread
   CPU and the frame is bound by the main thread. GPU heights would shorten sector arrival (batch mean 320 ms, p95 570 ms)
   and reduce contention on the 14-core laptop. Re-measure after phase C before committing to the GLSL port (decision gate G1).
3. **Item 6 is worth doing on its own merits, after GPU-1 lands.** Install is main-thread time (2.5 ms/frame in ground_fast).
   Sectors become slots in a per-level texture-array "vertex table". All sectors of a level share one fixed index buffer,
   and the vertex shader reads its data with `texelFetch(VERTEX_ID)`. Positions stay explicit floats written by the
   producer, not reconstructed in the shader. A CPU-built sector therefore renders bit-identically to today, and the
   GPU producer of item 7 only has to write the same table.
4. **Item 7 (if G1 says go)** is a compute port of the v7/v8 natural height model, the legacy climate and biome weights,
   and the sector assembly (positions, normals, morph, chord, skirts). It writes straight into the item-6 slots and reads
   back a small per-sector summary asynchronously. Sectors that touch edits, emitter sites, Wall Engines or the Weather
   Lung, terminal global-morph levels, blocky levels and coastal sectors (first cut) stay on the CPU path. It rolls out
   coarse levels first; LOD1 (2 m, which abuts Terrain3D) goes last, behind its own parity gate.
5. Budget: about 15-60 us per sector on the 4070 and about 3-4x that on the A1000. A per-frame budget of 0.15 ms (internal
   constant, timestamp-adaptive) covers the about 20-25 sectors/s that ground_fast is estimated to churn. Stationary play
   dispatches nothing. The vertex fetch replaces the attribute fetch at about equal bandwidth, so the expected stationary
   delta is about 0 and must be shown by an A-B-A capture.

## 1. Evidence used

From `.godot/cpu_gpu_survey` (RTX 4070 desktop, Debug build, `scopes_run2.txt`, `traces_parsed/ground_fast.txt`):

| Item | ground_fast |
|---|---|
| Wall frame | p50 33.3 ms, p95 45.7 ms; viewport GPU p50 9.4 ms |
| Main thread (managed sampled) | 12.0 ms/frame; LOD coordinator `_Process` 6.0 ms/frame |
| `ApplyCompletedBatch` (main) | 2.80 ms/frame; scope "Terrain proxy install" 2.52 ms/frame (8955 calls, p95 1.59, max 10.2) |
| "Terrain proxy camera uniforms" | 2.02 ms/frame (mean 0.13, max 11.5; mostly the FFT ocean coverage atlas, GPU-1) |
| Workers, managed busy total | 2314 ms/s; proxy `StartStreamingUpdate` lane 1730 ms/s; `BuildRawBatch` parallel lanes 182 ms/s |
| `BuildRawSector` inclusive | 1905 ms/s |
| `Godot.Noise.GetNoise3D` inclusive | 1887 ms/s (essentially all inside `godotsharp_method_bind_ptrcall`) |
| `TerrainBuildSampleCache.Get` | 1576 ms/s |
| `RingTerrainSampler.GetTerrainSample` (both call sites) | 1387 ms/s |
| `RingLodSurfaceEditSnapshot.GetPresentationTerrainSample` | 796 ms/s (calls `GetTerrainSample` again) |
| `TerrainGenerationHeightModel.SampleDiagnostics` | 675 ms/s |
| `TerrainGenerationHeightModel.CalculateHeight` (via `GetPresentationSurfaceHeight`) | 528 ms/s |
| Legacy climate `SampleRegionalNoiseLocked` | 689 ms/s |
| `BuildFftShorelineTile` (2 m level only) | 592 ms/s |
| "Terrain proxy build" per batch | mean 320 ms, p95 570 ms, max 668 ms (166 batches in the window) |

Thread table: the busiest worker is 515 ms/s and the others are 235-385 ms/s. No worker is saturated, and the i9-14900KF
has 32 hardware threads. The main thread shows 399 ms/s busy and 40 ms/s idle in managed samples; the rest of its time is
native. **Worker time therefore does not appear in desktop frame time.** It shows up as sector latency (coverage holes or
late morph at 250 m/s) and as CPU contention on the laptop. The 0.73 ms `AddSurfaceFromArrays`, 0.46 ms `Image.SetData` and
0.8 ms ocean replacement split of install was supplied by the primary agent's sampling; the ocean part belongs to GPU-1.

The throwaway micro-benchmarks for this document are in `.godot/gpu_offload_design/` (results in section 5.4).

## 2. Every consumer of proxy sector data

| Consumer | Data used today | CPU copy needed? | In the new design |
|---|---|---|---|
| Proxy terrain shader `materials/ring_terrain_proxy.gdshader` | VERTEX, NORMAL, COLOR (biome weights), UV (coverage UV), UV2 (x = radial morph delta, y = water flag), CUSTOM0 (runtime parent chord) | no | Vertex table fetched by `VERTEX_ID` from the level's `sampler2DArray`, slot from `instance uniform int proxy_slot`. Fragment stage unchanged. |
| Opaque shadow caster `materials/ring_terrain_proxy_shadow.gdshader` and the shadow pass of the main material (LOD1 casts) | same vertex data, same geomorph | no | Same fetch include (`ring_terrain_proxy_vertex_fetch.gdshaderinc`) in both shaders, so shadows morph with the surface. |
| Ocean proxy shader `materials/ring_ocean_proxy.gdshader` | water vertices (second half of the vertex arrays), clipped shoreline vertices, UV2.y = 1, COLOR = water kind; instance rect `fft_distant_ocean_mask_rect` | yes for coastal clip geometry | **Fully ocean** sectors draw the shared grid with the water rows of the table. **Coastal** sectors keep a per-sector ArrayMesh, compacted to water and clip vertices only (today the ocean surface re-uploads the full terrain+water arrays). Ocean moves to a child node `OceanSurface`. |
| LOD-to-LOD morph and seams | UV2.x / CUSTOM0 from parent lattice samples; terminal level uses `RingTerrainGlobalMorphSnapshot` | no | Stored per vertex in the table (morph delta, chord xyz). Terminal global-morph recipient levels stay CPU-produced (the data still goes into the table). |
| Seam against Terrain3D / voxel near terrain | LOD1 near-fade discard at the handoff radius; same CPU model as Terrain3D | no | Unchanged shader handoff. A GPU producer is not bit-identical (section 4), so LOD1 goes to GPU last, behind its own handoff compare. |
| Sector skirts | extra vertices built by `AppendSectorSkirts` | no | Fixed skirt block in the vertex table (4 edges x 64 quads x 4 vertices). Skirts omitted at ring edges or structural walls collapse to zero-area quads. |
| AABB / frustum culling, coverage queries | mesh AABB; `_activeVisualBounds` (morph-expanded) used by `IsCompleteVisualBoundInsideFarHandoff` and coverage memo | yes (bounds) | `GeometryInstance3D.CustomAabb` = VisualBounds per sector. CPU producer: as today. GPU producer: min/max reduction in the summary readback. |
| Coverage atlas and partial coverage culling | `_latestCoverageMasks` (CPU), coverage atlas sampled in fragment; CPU triangle filter rebuilds an ArrayMesh (`CreatePartialCoverageMesh`) | masks yes | Masks unchanged. For slot sectors the CPU triangle filter is replaced by a vertex-stage collapse: a vertex whose four incident quads are covered outputs NaN, so its triangles are culled. This is A/B'd against today's filter for GPU cost. |
| FFT ocean coverage and ocean replacement (GPU-1) | `SectorData.OceanSurfaceMask` (64x64 cells + 65x65 deep-water depth bytes), `FullyOceanSurface`, `_activeWaterSectors` | yes | CPU producer: unchanged. GPU producer: bit-packed cell mask (512 B) and depth bytes (4225 B) in the summary readback, handed to GPU-1's atlas exactly as today. |
| FFT shoreline field (`BuildFftShorelineTile`, 2 m level, 129x129 including a 32-cell halo) | CPU bytes read by `RingWaterFftController` (`IsFftShorelineFootprintFullySaturated`, `TryGetFftShorelineFieldUv`) and uploaded into the shared field texture | yes | CPU producer: built from height-only halo samples (phase C2). GPU producer: the kernel writes halo signed depth and wet flags; read back only if the halo contains water, then the CPU seeds and relaxes (cheap). |
| FFT water demand (`InstallFftWaterDemandMetadata`, `CaptureFftWaterDemand`) | derived from the ocean masks | yes | From the same summary. |
| Haze, cloud shadows, wetness, macro shadow (all fragment-stage) | `world_position` varying | no | Unchanged; `world_position` comes from the fetched vertex. |
| Far scaled viewport, global LOD, far surface tiles | do not draw proxy sectors; sample `RingTerrainSampler` themselves | no | Unaffected. (Possible later consumer of the GPU height kernel.) |
| Generated LOD disk cache (`TryLoadCachedTerrainSamples` / `TrySaveCachedTerrainSamples`) | per-sector sample records | CPU path only | GPU-produced sectors bypass it; GPU regeneration (about 20 us) beats a disk read. |
| Gameplay CPU height queries (placement for trees and grass workers, weather audio, explorer and collision, Terrain3D, minimap, cloud-sun visibility, height-shadow fills, macro shadow) | call `RingTerrainSampler` / `TerrainGenerationHeightModel` directly, **never proxy data** | yes, from the CPU model | Unchanged. The CPU model stays authoritative; no proxy height readback serves them. |
| Diagnostics (`EstimatedActiveMeshBytes`, certification monitor, LOD HUD, `WaterRenderingProbe`, `TerrainLodRenderingProbe`) | mesh byte counters, node counts | yes | Count slot bytes instead of mesh bytes; keep the counters' meaning ("bytes resident for this level"). |

Conclusion: the only CPU consumers of proxy-built data are the proxy's own culling and coverage bookkeeping and the FFT
water stack. Each needs a summary of at most about 5 KB per sector, plus a 66 KB halo for 2 m sectors that have water
nearby. Nothing needs full-resolution heights read back.

## 3. Item 6 design: shared grid, per-level vertex-table slots

### 3.1 Data layout

One `Texture2DArrayRD` per proxy level (RenderingDevice texture, bound to the level's terrain, ocean and shadow
materials as `sampler2DArray proxy_vertex_table : filter_nearest`). A layer is one sector slot. The table is a flat vertex
buffer laid out in rows of 65 texels:

| Rows | Content | Format |
|---|---|---|
| 0-64 | terrain surface vertices (row z, column x) | see below |
| 65-129 | water surface vertices (same lattice) | |
| 130-145 | skirt vertices (1024 = 16 rows x 64, with a padding column) | |

Each row block uses three parallel array textures of the same size, so every vertex costs exactly three `texelFetch`:
- `pos_morph` RGBA32F: local position xyz (float, exactly as the worker writes it today) and w = UV2.x morph delta.
- `chord` RGBA32F: CUSTOM0 xyz (runtime parent chord) and w spare (only allocated on runtime-chord levels, which is the default world).
- `attr` RGBA32UI: x = oct-encoded normal (2 x 16-bit snorm), y = COLOR RGBA8 packed, z = UV2.y flag and skirt flag, w spare.
  UV is derived from the row and column (`x / cells`), and skirt UVs come from `ResolveSkirtCoverageUv` packed into w.
  The derivation is checked against the stored UV in the contract test.

Bytes per sector: 146 rows x 65 x 48 B = 455 KB with chord, or 304 KB without. Today a sector uploads 2 x 4225 terrain and
water vertices plus skirts, at about 48 B each, twice when it has water (the second surface repeats the full vertex arrays),
plus about 98 KB of indices per surface. So resident bytes per level stay at or below today's `EstimatedActiveMeshBytes`.
This is checked in acceptance. Precision: positions stay float32 as today. Normals go from float32 to oct16, which is
under 0.01 deg. The proxy-terrain normal GPU test and the image compare must show no change.

Slot pool per level: capacity = (2 * SectorRadiusAround + 1)(2 * SectorRadiusAcross + 1) + MaximumWarmSectorCount + 8
in flight, allocated when `ApplyRuntimeLodConfiguration` runs (levels already rebuild on configuration change). A free
list hands out slots. A slot freed by `RecycleActiveSector` can be reused in the same frame: RD `texture_update` is
recorded in queue order, so frames in flight that still read the old content complete first.

### 3.2 Shared meshes

Each level owns two `ArrayMesh` resources built once: `TerrainGrid` (terrain rows plus skirts, index buffer for the
level's diagonal: positive diagonal when `runtimeParentChord`, legacy otherwise) and `WaterGrid` (water rows, full grid).
Their VERTEX array is a dummy (index only). The shader overwrites VERTEX, NORMAL, COLOR, UV and UV2 in `vertex()`, and
reads chord from the fetched value (CUSTOM0 is input-only and unused). Cells outside the ring width need no special index
handling: across is clamped at the producer, as today, so those cells become zero-area triangles. `AcrossSector` edge
sectors with omitted skirts write identical top and bottom skirt positions (zero area).

Per sector node:
- `Mesh = level.TerrainGrid` (shared), `CustomAabb = VisualBounds`, `SetInstanceShaderParameter("proxy_slot", slot)`.
- Child `OceanSurface`: `WaterGrid` for fully-ocean sectors; a compact per-sector ArrayMesh (water and clip vertices only,
  re-indexed) for coastal sectors; none for dry sectors. FFT `ExtraCullMargin` moves to this child.
- `OpaqueShadowCaster` child keeps working: it gets the same shared mesh, slot parameter and fetch include.

### 3.3 Install path (main thread)

The worker packs the slot bytes (three byte[] blocks) next to the arrays it builds today. Packing is a linear pass and
stays off the main thread. On install the main thread:
1. takes a slot, queues `RenderingServer.CallOnRenderThread(() => rd.TextureUpdate(tex, slot, bytes))` for each block
   (three memcpy-sized updates, about 0.3-0.45 MB total);
2. sets the mesh, slot instance parameter, CustomAabb and position;
3. only for coastal sectors, builds the compact ocean ArrayMesh (smaller than today's second surface).

Not on the main thread any more: both `AddSurfaceFromArrays` calls for dry and fully-ocean sectors, the CUSTOM0 repack loop
(moved to the worker packer), the per-sector `ArrayMesh` and its GPU buffer allocations, and the partial-coverage
`CreatePartialCoverageMesh` rebuilds.

Expected effect in ground_fast: "Terrain proxy install" 2.52 -> about 1.4-1.7 ms/frame before GPU-1's ocean changes are
counted. The `AddSurfaceFromArrays` 0.73 ms becomes about 0.1-0.15 ms of upload queueing, and native buffer creation
time that the managed sampler does not show also goes away. Acceptance numbers are in WO-S1.

### 3.4 Shader cost

Today: one vertex fetches about 52 B of attributes (position 12, normal/tangent 4-8, colour 4, UV 8, UV2 8, CUSTOM0 12).
New: three `texelFetch` totalling 48 B, plus integer unpack (about 10 ALU). On NVIDIA, attribute fetch and texel fetch use
the same L1/texture path, so the bandwidth is equal. About 150 visible sectors x (4225 + 4225 + 1024) vertices is about
1.4 M vertices per pass at 48 B, about 68 MB, about 0.13 ms of 4070 bandwidth at worst. That is roughly what the
attribute fetch costs today, so the expected delta is about 0. It must be measured, because vertex-cache reuse differs:
the shared index buffer has the same ordering as today, so post-transform cache hits should match. If the stationary A-B-A
shows more than +0.05 ms on the 4070, fall back to an RGBA32F position and an RGBA16F normal, colour and flags layout (2 fetches).

### 3.5 Compatibility renderer and fallback

`proxy_vertex_table` needs a RenderingDevice. When `RenderingServer.GetRenderingDevice()` is null (compatibility renderer)
the level installs per-sector ArrayMeshes exactly as today. The two install modes share `SectorData` and differ only in
`InstallSectorMesh` / `InstallSectorSlot`. The choice is internal (`ProxyInstallBackend`) and is not a menu toggle. A
debug override `--proxy-install=mesh|slot` serves the A/B tests.

## 4. Item 7 design: GPU sector heights

### 4.1 What is ported, what is not

Ported to GLSL compute (one include per piece, under `materials/terrain_gpu/`):
- `fnl_opensimplex2s.glslinc`: FastNoiseLite OpenSimplex2S 3D (the only basis used: `SimplexSmooth` in the height model,
  and Godot's default `TYPE_SIMPLEX_SMOOTH` in the legacy climate), with `FractalTypeEnum` None, Fbm and Ridged,
  `weighted_strength = 0`, Godot's `offset = 0`. Start from Auburn's official GLSL port, then match Godot's bundled
  FastNoiseLite version (seed handling, `CalculateFractalBounding`, 3D `TransformNoiseCoordinate` for
  `RotationType3D_None`). All arithmetic is `precise` so the driver cannot contract to FMA.
- `ring_height_model.glslinc`: the **v7/v8 natural path** `SampleLandformNaturalDiagnostics` (`warp`, continental with
  the ocean-distance derivative branch, mountain belt, valley, ridge, trench, foothill, ridged spur, enhanced gullies,
  landform terms, lowland gullies, detail), and `ContinentalHeight`, `OceanDepthForDistance`, `Terrace`, `CellJitter`
  (integer hash, exact). Gully pivots are recomputed per invocation. The CPU stencil cache is only a cache: the result does
  not depend on it.
- `ring_regional_climate.glslinc`: `SampleTerrainFieldsLocked` (continental, erosion, mountain warp with integer rounding,
  ridge, valley, moisture, temperature), the runtime altitude temperature drop, `ResolveBiome`/`ResolveBiomeBlend`,
  `GetNaturalBiomeTextureWeights` and beach weights. Only evaluated at the 16 m biome lattice centres
  (`ResolveBiomeTextureWeight`).
- `ring_proxy_sector_assemble.glsl`: positions, normals (central difference over the 1-texel halo), morph deltas
  (`SampleParentTerrainHeight` / `SampleParentWaterHeight`), runtime parent chord, water render heights, skirts, colours,
  conservative ocean vertex depth, ocean cell mask, and the bounds reduction.

Not ported. These stay CPU, and the sector is routed to the existing CPU build:
- emitter sites, Wall Engines (climate machines), Weather Lung. Sectors whose rectangle plus margin intersects any
  influence region (new `EmitterSiteTerrainShaper.IntersectsInfluence(rect)`).
- sectors touching edits (`SectorTouchesEdits` already exists).
- the terminal level that receives `RingTerrainGlobalMorphSnapshot`.
- blocky levels; recipes other than v7/v8 (`GeneratorId` allow-list); generation-16 (non-runtime) worlds.
- first cut: coastal sectors (any partial wet triangle) on every level, and 2 m sectors whose shoreline halo contains water.
  The GPU summary reports this, and the sector is re-queued to the CPU lane, which costs one round trip of latency only
  where coasts are. Phase G4 can later assemble coastal ocean meshes from a readback instead.

### 4.2 Dispatch pipeline

All on the main `RenderingDevice`, recorded through `RenderingServer.CallOnRenderThread`. Pattern:
`scripts/Shadows/RingTerrainHeightShadow.Gpu.cs` (GpuLink, `Texture2DArrayRD`, timestamps, `TextureGetDataAsync`).

1. Main thread (`StartStreamingUpdate`): classifies desired sectors as GPU or CPU (4.1). GPU sectors go to a per-level
   GPU job queue in the existing priority order (`CompareSectorPriority`). Each job reserves a slot.
2. Render thread, per frame, inside the budget (4.4):
   - K1 `sample`: one invocation per lattice point of the 67 x 67 window (65 + normal/ocean-depth halo), or 129 x 129 for a
     2 m sector's shoreline halo; plus biome-lattice points. Output: scratch SSBO (presentation height, flags, biome RGBA8).
   - K2 `assemble`: one invocation per table vertex, writing `pos_morph`, `chord` and `attr` into the slot, and the summary
     SSBO (bounds min/max with morph reach, flags {dry, fully ocean, coastal, halo-wet}, 64 x 64 ocean-cell bits,
     65 x 65 deep-water depth bytes).
   - `BufferGetDataAsync(summary)`; for halo-wet 2 m sectors also the halo block (66 KB).
3. Readback callback: the main thread installs the sector (CustomAabb, masks into the GPU-1 atlas, demand metadata,
   visibility) using the slot path from item 6. Latency is about 2-3 frames against today's 320 ms mean batch.

Per-sector job constants (push constants and a small UBO): sector coordinate, spacing, parent spacing, cells, underlay,
parent underlay, water-restore flags, `alignSurfaceToTerrainSdf`, slot, and the precision anchor (4.3). The recipe
constants buffer is uploaded once per world: all `TerrainGenerationParameters` values, precomputed noise constants as the
exact float bits the CPU FastNoiseLite uses (frequency `(float)(1/scale)`, gain, lacunarity, fractal bounding, seeds),
octave tables, continental knots, and the legacy climate settings and biome profiles.

### 4.3 Precision at ring scale

The CPU model feeds FastNoiseLite **float32** inputs: `X, Y = (float)(cos/sin(theta) * R)` with R = 5.0 Mm, so one float
ULP is 0.5 m, and `Z = (float)across`, with an ULP of 0.03 m at 318 km. The double arithmetic before the cast decides which
float is chosen. That quantisation is part of the reference and the GPU must reproduce the same float inputs. A 0.5 m
input shift moves the 32 m micro and 256 m secondary fields by up to about 1 m of height.

- **Anchor and rebase.** The CPU sends each job the sector origin wrapped around the ring as an exact integer split
  (hi * 65536 + lo), the angle `theta0 = tau * A0 / C` as a double split into two floats (df64 hi/lo), and `cos theta0`,
  `sin theta0`, R and 1/R as df64. In the kernel, `dA = x * spacing` (exact integer) plus the warp offset
  `noise * WarpAmplitudeM` gives `dtheta = (dA + warp) / R` in df64 (|dtheta| < 2e-3 rad for active levels).
  `cos/sin(theta0 + dtheta)` come from the angle sum with a degree-7 Taylor series for `dtheta`, and `X = R * cos(...)` is
  rounded once to float. The derivative samples (`+/- epsilon`) take the same route. Wrapping across the ring seam is
  handled by the integer anchor, so no float ever holds a 3.1e7 m around coordinate. The gully pivot offset
  `around - pivotA` is formed from integer cell indices relative to the anchor cell.
- **Expected parity of the inputs.** The CPU's own double result is accurate to about 1e-9 m and df64 to about 1e-7 m.
  The two round to different floats only when the true value lies within about 1e-7 m of a rounding boundary: about
  2e-7 per projection, so about 0.02 differing inputs per sector, each at most 1 ULP. `warpedAcross` uses the same df64
  sum and single rounding.
- **Native fp64 option.** If WO-G0 shows that Godot's RenderingDevice exposes `shaderFloat64` (GLSL `double` compiles and
  runs in an RD compute pipeline on both the 4070 and the A1000), the df64 helpers can be replaced by `double` +, -, *
  and the Taylor series. The 4070 runs fp64 at 1/64 rate, but only about 8 projections per sample use it, so the cost is
  negligible. df64 is the default because it needs no device feature.
- **Noise core.** OpenSimplex2S uses only +, -, * on floats, integer hashing and floor. Vulkan requires correctly rounded
  float add, sub and mul. With `precise` (NoContraction) the GLSL noise can be **bit-identical** to Godot's C++ build,
  provided Godot's Windows build does not contract either. WO-G0 tests this directly against `FastNoiseLite.get_noise_3d`.
- **Post-noise arithmetic.** The CPU uses double; the GPU uses float, except df64 for the projections and the gully
  pivot offsets. Expected height error is about 1e-6 relative, or a few mm at 8 km. Terrace risers
  (`TerraceRiserWidth = 0.14`) amplify the slope by about 10x, giving a few cm on risers. `pow` and `exp` in GLSL are within
  a few ULP. Water classification (`presentationHeight < 0`) can flip only where |h| is a few mm.
- **Sector geometry.** Local positions relative to the sector centre in float:
  `x = R sin(dtheta) - h sin(dtheta)` and `y = 2R sin^2(dtheta/2) + h cos(dtheta)`, written in the stable forms (no
  `R - h` subtraction). Error is under 1 mm for sectors up to 2 km. The node transform stays the double-resolved
  `ResolveSectorLocalOrigin` as today.

### 4.4 Seams with CPU-built terrain

- **GPU sector next to a CPU sector** (edits, sites, coast). Shared edge vertices come from two implementations, so they
  differ by the height tolerance, normally mm and at most a few cm on terrace risers. Skirts already hide sub-skirt cracks.
  The normal difference is under 1 deg. Acceptance is a crack test plus image compare (WO-G2).
- **LOD morph.** Parent and child heights at shared parent-lattice vertices come from the same producer when both levels
  are GPU, or from the same CPU model when both are CPU. Mixed levels during rollout differ by the tolerance at morph = 1.
  This is checked by the LOD seam image compare.
- **Terrain3D handoff (LOD1).** A hard fade/discard circle at about 64-104 m. At 1080p and 70 deg vertical FOV one pixel is
  about 0.11 m at 84 m, so mm errors are invisible, and terrace-riser cm errors are at most about 1 px and rare. LOD1 still
  moves to GPU last (WO-G3), behind a dedicated handoff compare. Keeping LOD1 on CPU permanently is the fallback if the
  compare fails. That would forgo about half the GPU benefit, because finer levels churn most (section 5.2).

### 4.5 Parity test definition

New GPU test `proxy_gpu_height_parity_test` (windowed, needs RD; tier gate once stable). It uses the showcase v8 recipe
(`resources/menu/showcase_world.json`) and one v7 recipe; a v6 recipe must route to CPU.

Fixture locations, about 40 sectors per level for spacings 2, 4, 8, 16 and 32:
- deep basin, trench, continental shelf, beach and coast (expected to route to CPU), plains, hill country, plateau edge
  (terraces), mountain belt with gullies;
- ring seam (around sector 0, the last sector, and negative sector indices);
- across edges (0 and 318000) and far around (about 3.1e7).

It also samples 10^6 uniformly random ring points for raw height and climate.

| Check | Reference | Tolerance |
|---|---|---|
| Noise core (each fractal type, octaves 1-5, ring-scale inputs) | Godot `FastNoiseLite.get_noise_3d` | bit-identical on >= 99.99 % of 10^6 inputs, max abs diff <= 2e-7 (target: all identical) |
| Projected inputs X, Y, Z | `(float)(cos/sin(theta) * R)`, `(float)across` | identical on >= 99.999 %, else 1 ULP |
| Natural height | `(float)SampleDiagnostics(...).Height` | p99.9 abs diff <= 0.02 m, max <= 0.25 m (report the location; terrace risers expected), abs mean diff <= 1e-3 m |
| Water kind | `presentationHeight < 0` | flips only where abs(h_cpu) < 0.05 m |
| Biome weights (RGBA8) | `GetBiomeTextureWeights(sample)` | max channel diff <= 2/255; categorical flips <= 0.1 % of points |
| Assembled positions (K2 fed with CPU heights in test mode) | `SectorData.Vertices` | <= 1 mm |
| Morph delta, chord | `SectorData.MorphData`, `ParentChordOffsets` | <= 1 mm + propagated height tolerance |
| Normals | `SectorData.Normals` | p99.9 angle <= 0.5 deg, max <= 2 deg |
| Summary | CPU `VisualBounds`, `OceanSurfaceMask`, `FullyOceanSurface` | bounds contain CPU bounds within 0.3 m; masks identical except at water-flip cells |

The test prints `PROXY_GPU_HEIGHT_PARITY=PASS|FAIL <worst metric and location>` and writes a JSON report under `.godot/`.
The runtime also does a 64-point self-check when the backend initialises (about 1 ms, once per world). If it fails
tolerance, the level falls back to CPU and logs once.

## 5. Budget

### 5.1 GPU cost per sector (estimates, to be replaced by timestamps in WO-G2)

Noise work per natural-height sample in the v8 showcase recipe on land with the landform gate open:
- about 21 `GetNoise3D` calls, about 60 octave evaluations: warp 2x3, continental 4, belt 4, valley 3, ridge 4, foothill 3,
  spur 4, gully ridge derivative 4x4, landform 3, plateau 2, hill 2+2, escarpment 3, lowland gully 2x2, secondary 3, micro 2;
- ocean points are about 40 octave evaluations (continental derivative 4x4 instead of the land terms);
- a climate sample is 8 calls x 5 octaves = 40 octave evaluations, at biome points only.

At about 200 instructions per OpenSimplex2S octave, plus about 8 df64 projections and the glue, a sample is about 15 k
instructions (23 k with climate). Throughput is assumed at 30 % of peak because the integer hashing and the land/ocean
branches diverge: 4070 about 4.4 T instr/s; A1000 (2560 lanes at about 1.4 GHz) about 1.1-1.5 T instr/s, so 3-4x slower.

| Sector | Samples | 4070 | A1000 |
|---|---|---|---|
| 4 m / 8 m (67 x 67, biome 1/16-1/4) | 4489 | about 15-18 us | about 50-70 us |
| 16 m / 32 m (67 x 67, biome about 1 per vertex) | 4489 + 4225 climate | about 23 us | about 70-90 us |
| 2 m without shoreline halo | 4489 | about 15 us | about 50 us |
| 2 m with 129 x 129 halo (height only) | 16641 | about 56 us | about 170-220 us |
| K2 assemble and reduction | 4225 + 1024 | under 2 us | under 6 us |
| Dispatch and barrier overhead per batch | | about 10-20 us | about 20-40 us |

### 5.2 Slicing

Demand: at 250 m/s the levels cross sector rows at speed / size: 2 m about 2 rows/s, 4 m 1, 8 m 0.5, 16 m 0.25, 32 m
0.12, times about 6-7 sectors per row, plus prediction. That is about 20-25 sectors/s in total, and the 2 m and 4 m levels
make up about 75 % of it. WO-A0 replaces this estimate with counted builds.

The scheduler is internal (no menu entry). Constant budget 0.15 ms/frame of GPU time per frame. Like
`RingTerrainHeightShadow.Gpu`, it learns microseconds per sample from RD timestamps per level and admits jobs until the
predicted time would exceed the budget. A 2 m halo sector on the A1000 is split into two dispatches by row range.
Typical 4070 load in ground_fast is about 0.8 sectors per frame at 30 fps, so about 20-30 us per frame (p95 under 0.1 ms).
On the A1000 it is about 60-90 us per frame, with p95 at the 0.15 ms cap. When the queue is empty, nothing is recorded:
**stationary adds 0 dispatches and 0 uploads**. The budget should become a share of one streaming-compute arbiter if
GPU-1/4/5 add their own compute (open question 7).

### 5.3 CPU measurements for the "lighter first step"

C1-C3 are derived from the ground_fast trace; C4 is answered by the micro-benchmarks in 5.4.
- **C1, one evaluation per lattice point.** In runtime-generation worlds `TerrainBuildSampleCache.Get` does three things:
  1. calls `_sampler.GetTerrainSample(around, across)`, which is full model diagnostics plus legacy climate;
  2. immediately overwrites the result with `_edits.GetPresentationTerrainSample(...)`, which calls `GetTerrainSample`
     again;
  3. calls `_edits.GetPresentationSurfaceHeight(...)`, which is a third model evaluation (`CalculateHeight`), although
     `GetRuntimeTerrainSampleLocked` already computed the identical `(float)diagnostics.Height` for even, in-range lattice
     points.

  Per point that is 3 model evaluations plus 2 climate evaluations, where 1 + 1 is enough. From the trace that is about
  337 (duplicate diagnostics) + 345 (duplicate climate) + 528 (`CalculateHeight`), so **about 1.2 s/s of the 2.3 s/s
  worker total, about 50 %**, bit-identical. Expected effect on "Terrain proxy build": mean 320 -> about 170 ms.
- **C2, height-only halo points.** Normal-halo, conservative-depth and especially shoreline-halo points (129^2 - 65^2 =
  12.4 k per 2 m sector, 75 % of `BuildFftShorelineTile`'s samples) only need height and water kind, not climate. A
  height-only `Get` variant saves climate there: about 35-40 % of the 592 ms/s shoreline cost.
- **C3, compact ocean surface on install (item 6 light).** The ocean surface is created from the same full
  terrain+water arrays as the terrain surface, so wet sectors upload every vertex twice. Compacting each surface to the
  vertices it references halves vertex bytes for wet sectors. It touches the install lines and is superseded by item 6, so
  do it only if item 6 is deferred.
- **C4, pure C# FastNoiseLite (no interop).** **Not worth doing.** The measurements in 5.4 show that interop is only
  about 5-10 % of the model's time; the rest is the noise arithmetic itself. A managed port could at best match the
  engine's optimised C++ in ExportRelease, and would be slower in the Debug build. It would also need its own
  bit-parity test.

### 5.4 Measurements (i9-14900KF, Godot 4.8-dev7 mono, Debug C# build, headless via `tools/gpu_queue.ps1`)

Native `FastNoiseLite.get_noise_3d` from GDScript, `TYPE_SIMPLEX_SMOOTH`, ring-scale inputs (X/Y about 5e6 m,
frequency 1/32), 200 k calls, 3 repeats, with the GDScript call overhead subtracted (`get_seed` baseline 12 ns).
Script: `.godot/gpu_offload_design/noise_octave_bench.gd`; log: `.godot/gpu_offload_design/noise_bench.log`.

| Configuration | ns per call (net of call overhead) |
|---|---|
| None, 1 octave | 33-34 |
| Fbm 1 / 2 / 4 / 5 octaves | 35-37 / 55-57 / 109-110 / 132-137 |
| Ridged 4 octaves | 128-129 |

So the native cost is about **25 ns per octave plus about 10 ns per call** (Godot wrapper).

C# `TerrainGenerationSampler.SampleGrid` (existing `tools/terrain_generation_cpu_benchmark.gd`, 128 x 128 at 2 m, 12
rounds; report `.godot/terrain-generation-tests/detail-cpu-benchmark-report.json`):

| Model / tile | median ms | ns per sample |
|---|---|---|
| v7 default, ocean | 18.8 | 1150 |
| v7 default, plains | 23.7 | 1449 |
| v7 default, hill country | 29.0 | 1772 |
| v7 default, plateau edge | 30.8 | 1878 |
| v7 default, mountain | 40.9 | 2496 |
| v7 default, foothill | 45.9 | 2804 |

Check: the hill-country count of about 21 calls and 60 octaves predicts 21 x 10 + 60 x 25 = about 1.7 us of native noise,
against 1.77 us measured. The ocean count of about 14 calls and 40 octaves predicts about 1.1 us, against 1.15 us
measured. **Native noise arithmetic is about 90 % of model time; the managed code and the C#-to-native call add only
the rest.** Fewer evaluations (C1/C2) or a different processor (item 7) are therefore the only big levers.
The same figures size the GPU estimate in 5.1: about 140 scalar CPU cycles per octave, consistent with the assumed
200 GPU instructions.

#### 2026-11-06: K1 timings on the 4070 (WO-G1, replaces the 5.1 estimates for the sample kernel)

RTX 4070, local RenderingDevice, GPU timestamps (`CaptureTimestamp` around one `ComputeListDispatch`), median of 15 dispatches
after a 1.5 s clock warm-up (the first dispatches after idle run 3-8x slower at low clocks). Kernel
`materials/terrain_gpu/ring_proxy_height_k1.glsl` (v8 showcase recipe, df64 projections, `precise` everywhere); log line
`PROXY_GPU_HEIGHT_DETAIL timing ...` of `proxy_gpu_height_parity_test`, report `.godot/proxy_gpu_height_parity/report.json`.
"Height" is the 67 x 67 window, height only. "Biome" is the 16 m centre lattice (size / 16 per side), each point a full height
plus climate plus weights evaluation. "Batched" is 32 adjacent sectors in one dispatch, per sector.

| Sector (spacing, size) | Fixture | Height alone | Biome alone | Alone total | Batched height | Batched biome | Batched total |
|---|---|---|---|---|---|---|---|
| 2 m, 128 m (biome 8 x 8) | hill country | 39 us | 64 us | 103 us | 9 us | 2 us | 11 us |
| 4 m, 256 m (16 x 16) | hill country | 40 us | 66 us | 106 us | 9 us | 2 us | 11 us |
| 8 m, 512 m (32 x 32) | hill country | 40 us | 64 us | 105 us | 10 us | 4 us | 13 us |
| 16 m, 1024 m (64 x 64) | hill country | 41 us | 64 us | 105 us | 11 us | 15 us | 26 us |
| 32 m, 2048 m (128 x 128) | hill country | 43 us | 75 us | 118 us | 9 us | 52 us | 61 us |
| 2 m, 128 m | plateau edge (terraces, lowland gullies) | 40 us | 65 us | 105 us | 9 us | 2 us | 11 us |
| 32 m, 2048 m | plateau edge | 45 us | 77 us | 121 us | 15 us | 73 us | 88 us |
| 2 m, 128 m | mountain gullies | 54 us | 80 us | 135 us | 14 us | 2 us | 16 us |
| 32 m, 2048 m | mountain gullies | 59 us | 91 us | 150 us | 13 us | 65 us | 78 us |
| 2 m, 128 m | deep basin (ocean) | 28 us | 50 us | 78 us | 6 us | 1 us | 7 us |
| 32 m, 2048 m | deep basin | 28 us | 58 us | 86 us | 5 us | 37 us | 43 us |

The 2 m shoreline halo (129 x 129 height only, one sector): 49 us hill country, 45 us plateau edge, 67 us mountain gullies,
38 us deep basin.

Reading against 5.1:
- Batched, a 2-8 m sector costs 11-17 us on land and 7-9 us in ocean, which matches the estimate (15-18 us). A land sample is
  about 2 ns of GPU time per 67 x 67 point batched (9 us / 4489), well under the 15 k-instruction estimate.
- A dispatch of one sector is latency-bound: 28-60 us for the 4489 points, because only about 70 workgroups run and each
  invocation's dependent chain (about 21 noise calls, gullies, df64 projections) takes tens of microseconds. The scheduler in 5.2
  should batch every sector that is ready in a frame into one dispatch, as done in the 32-sector column; the alone column is
  the single-sector worst case, and the lone biome dispatch (64 points at 2 m) is the same latency floor.
- The 5.1 table assumed about 1 climate point per vertex for 16 m and 32 m (4225). The biome lattice is 16 m, so a 32 m sector has
  128 x 128 = 16384 centres, 4x more; that is why the 32 m batched total is 43-88 us rather than the estimated 23 us. It is
  still small (about 0.1 ms per sector, alone 0.12-0.15 ms) against the 0.15 ms/frame budget of 5.2.
- Terrain type moves the cost by about 2x (ocean 7 us to mountain gullies 16 us at 2 m): the gully loops and the 4x4 ridge
  derivative samples are the heaviest land terms, ocean skips most of the land noise.
- The A1000 is not measured (laptop not available in this run): 5.1 stays an estimate for it.

## 6. Fallback

- `ProxyHeightBackend { Cpu, Gpu }` is resolved per level at configuration time. It is `Gpu` only if all hold: an RD
  device exists (Forward+ or Mobile), the compute pipelines compiled, the recipe `GeneratorId` is v7 or v8, runtime
  surface generation is on, the level is neither blocky nor the terminal global-morph level, and the init self-check
  passed. Otherwise `Cpu`.
- The CPU path is the existing `BuildRawBatch` / `BuildRawSector`, kept intact. It is used for the compatibility renderer,
  per-sector exclusions (4.1), the parity test reference, and as a runtime fallback if a dispatch or readback fails (the
  job is re-queued to CPU and logged once).
- There is no menu toggle. Debug override: `--proxy-height-backend=cpu|gpu` and `--proxy-install=mesh|slot` user args, read
  by the level at `_Ready`, for A/B captures and tests only.

## 7. Phased work orders

Ordering: C1 and C2 and G0/G1 touch no GPU-1 lines and can start now. Everything that edits `ApplyCompletedBatchCore`,
`RecycleActiveSector`, `ConfigureOpaqueShadowCaster`, the ocean install or publish, or `CreateMaterial` (S0, S1, G2,
G3) **waits for GPU-1 to merge** into this branch, because GPU-1 is editing lines around ~5730 (ocean publish at the end
of install) and ~6450-6630 (coverage update and materials). Each WO runs `tools/run_tests.ps1 -Since HEAD -Label <wo>`
(dry-run first) and a perf capture with the existing harness `.godot/cpu_gpu_survey/cpu_survey_capture.gd` (ground and
ground_fast, 2 runs, fixed APPDATA label `gpuoffload`).

Common acceptance artefacts for the render-touching WOs (S1, G2, G3):
- perf table before/after for ground_fast and ground (scopes "Terrain proxy install", "Terrain proxy build", worker
  managed ms/s, viewport GPU p50/p95), using the `perf-capture` skill A-B-A method;
- `compare` image pairs (`tools/media/ffmpeg_tools.ps1`) at three seam poses: LOD1/LOD2 morph band, LOD3/LOD4 band, and
  the LOD1/Terrain3D handoff, plus one ring-seam pose. Same machine. Judge the heatmap: SSIM about 0.997 is cloud noise;
- `flicker` over a 250 m/s Movie Maker capture (`--write-movie .godot/captures/<wo>/frame.png --fixed-fps 30`, 20 s).
  No new single-frame spikes against the baseline capture of the same route;
- the tests listed in the WO, plus `-Suite smoke`.

### WO-A0: baseline and counters (no behaviour change; parallel-safe)
- Add worker counters (Interlocked): model evaluations, climate evaluations and lattice points per level per second;
  sectors built per level per second. Expose them in the existing perf export.
- Capture the baseline: ground_fast x2, ground x2, stationary GPU baseline (A-B-A noise floor) on the 4070. If available,
  the same on the A1000 laptop.
- Accept: counters present; the report shows evaluations per point (expected 3 model + 2 climate) and sectors/s per level.
  Tests: `proxy_build_allocation_test` (allocation budget unchanged), smoke.

### WO-C1: one evaluation per lattice point (parallel-safe with GPU-1)
- Files: `RingTerrainSampler.SurfaceGeneration.cs` (new `GetRuntimeTerrainSampleAndPresentationHeight(long, int, out
  TerrainSample, out float)`, reusing `GetRuntimeTerrainSampleLocked`'s computed presentation height and computing the
  wrapped or interpolated height exactly as the existing public calls do), `RingLodSurfaceEditSnapshot.cs` (an
  edit-aware wrapper that falls back to the current three-call path when the column, or any lattice vertex the
  presentation interpolation touches, is edited), and `RingTerrainProxySector.cs` `TerrainBuildSampleCache.Get`
  (lines ~690-800 only).
- Do not change: model arithmetic, wrap order, the generation-16 path, or the disk-cache record format.
- Accept: a new contract `proxy_sample_cache_dedupe_contract_test` (headless) compares every `CachedTerrainPoint` field
  bit-for-bit, old against new, over seam (negative and over-length around), across 0 / width-2 / width, odd
  coordinates, edited columns, float edits, presentation on and off: 100 % identical. ground_fast model evaluations per
  point 3 -> 1 and climate 2 -> 1; worker managed ms/s down >= 35 %; "Terrain proxy build" mean down >= 30 %.
  Tests: `terrain_generation_cpu_parity_test`, `terrain_generation_stream_test`, `proxy_seam_geometry_contract_test`,
  `proxy_surface_normal_contract_test`, `fft_shoreline_field_contract_test`, `proxy_build_allocation_test`, smoke.
- Escalate if any field differs. That would mean the old path relied on the second evaluation (for example, through a
  stateful cache).

### WO-C2: height-only halo samples (after C1; parallel-safe)
- `TerrainBuildSampleCache.GetHeightOnly` for normal-halo, conservative-depth and shoreline-halo points. It returns the
  same height and water fields with climate fields left at default. A point fetched height-only and later fully is
  upgraded in the cache, never shared in a weaker form.
- Accept: `SectorData` and `FftShorelineTile` bytes identical to C1 (hash compare over the C1 fixture). Shoreline tile
  build ms (existing `SharedFftShorelineFieldWorkerBuildMilliseconds`) down >= 30 %. Same tests as C1.

### Gate G1 (primary agent and user)
Re-run ground_fast after C1/C2. If "Terrain proxy build" p95 <= 250 ms and the fast-flight capture shows no late sectors
or coverage holes, item 7 is optional; record the decision. Item 6 proceeds regardless.

### WO-S0: vertex-table infrastructure (after GPU-1 merges)
- New `RingTerrainProxySector.VertexTable.cs` partial: slot pool, the three `Texture2DArrayRD` per level, render-thread
  uploads, the shared `TerrainGrid` and `WaterGrid` meshes, and the worker packer (`SectorData` -> slot blocks).
  New `materials/ring_terrain_proxy_vertex_fetch.gdshaderinc` included by the terrain, shadow and ocean proxy shaders,
  active only when `proxy_vertex_table_enabled` (internal uniform, default false).
- Accept: `proxy_vertex_table_contract_test` (GPU): for 20 fixture sectors per level, read back the slot with
  `TextureGetDataAsync` and compare to `SectorData`. Positions, morph and chord are bitwise equal. Normals are within the
  oct16 tolerance (0.01 deg). Colours are exact. UV is derived exactly. Skirt vertices are equal. No visual change with
  the flag off (image compare identical).

### WO-S1: slot install (after S0)
- Install terrain (and fully-ocean water) via slots; `OceanSurface` child with a compact coastal mesh; `CustomAabb`;
  slot release in `RecycleActiveSector`; shadow caster on the shared mesh; vertex-stage covered-quad collapse replacing
  `CreatePartialCoverageMesh` for slot sectors; byte accounting.
- Accept: ground_fast "Terrain proxy install" <= 1.6 ms/frame (from 2.52; re-baseline after GPU-1 and state both),
  `AddSurfaceFromArrays` calls per dry sector = 0; resident bytes per level <= baseline; stationary viewport GPU delta
  <= +0.05 ms on the 4070 (A-B-A, `--proxy-install=mesh|slot`); seam compares with SSIM >= 0.999 and a clean heatmap
  against mesh mode on the same machine; flicker check clean; compatibility-renderer run still uses mesh mode.
  Tests: `proxy_partial_coverage_test`, `proxy_coverage_culling_settings_smoke`, `proxy_terrain_normals_gpu_test`,
  `terrain_skirt_coverage_gpu_test`, `terrain_lod_shadow_coverage_smoke_test`, `terrain_vertical_lod_coverage_test`,
  `water_fft_lod_seam_gpu_test`, `water_fft_lod_ownership_visual_test`, `water_fft_demand_integration_test`,
  `fft_ocean_coverage_contract_test`, `terrain_altitude_lod_flight_test -Preset 1`, smoke.

### WO-G0: noise core and precision probe (parallel-safe, standalone)
- `fnl_opensimplex2s.glslinc` plus a probe compute shader. Test `gpu_noise_parity_test` (GPU) against
  `FastNoiseLite.get_noise_3d` (table in 4.5, first row). Also probe whether `double` compiles and runs through RD
  (result recorded; df64 remains the default).
- Accept: the noise row of 4.5; df64 projection row of 4.5. Report fp64 availability on the 4070 (and the A1000 when
  available).

### WO-G1: height model and climate kernels plus the parity test (after G0; parallel-safe; only if G1 says go)
- `ring_height_model.glslinc`, `ring_regional_climate.glslinc`, a K1 kernel, and `proxy_gpu_height_parity_test` (4.5),
  run outside the proxy streaming code. Register it in `tools/test_manifest.json` (gpu, gate after one week of stability).
- Accept: every row of 4.5 for v7 and v8; the v6 recipe is reported unsupported. GPU timestamps per sector recorded on
  the 4070 (and the A1000 if available), replacing the 5.1 estimates.

### WO-G2: GPU backend for coarse levels (after S1 and G1)
- Per-level `ProxyHeightBackend`, GPU job queue, budgeted scheduler, K2 assemble into slots, summary readback, CPU
  routing for excluded and coastal sectors. Enable for 8, 16 and 32 m levels only.
- Accept: per-frame GPU added by the proxy compute p95 <= 0.15 ms during ground_fast and exactly 0 dispatches in the
  stationary run (RD timestamp counters); viewport GPU stationary delta within noise; worker managed ms/s down by the
  share of those levels; seam compares (LOD3/LOD4 and LOD5/global) clean; a crack test with one edited sector adjacent to
  GPU sectors (magenta clear colour, zero magenta pixels inside terrain); flicker clean. Tests: S1 list plus
  `proxy_gpu_height_parity_test`, `terrain_generation_streaming_playable_test`.

### WO-G3: GPU for 4 m and 2 m (after G2)
- Enable LOD2 then LOD1, including the 2 m shoreline halo readback and the CPU seed/relax of the tile.
- Accept: the LOD1/Terrain3D handoff compare at three spots (plains, plateau edge, mountain), with no visible ring in the
  heatmap; `water_fft_shoreline_visual_test`, `water_fft_terrain_shore_smoke`, `fft_shoreline_field_contract_test`
  (fed from GPU halos); ground_fast "Terrain proxy build" lane time down >= 70 % against C2. If the handoff compare fails,
  keep LOD1 on CPU and record it.

### WO-G4 (optional): coastal sectors on GPU
- Readback-based compact ocean mesh assembly for coastal sectors on a worker. Only if coast-heavy routes still show CPU
  build latency after G3.

## 8. Risks and open questions for the user

1. **Is item 7 worth it after C1/C2?** It mainly improves latency and laptop CPU headroom, not desktop frame time. It
   also doubles the maintenance cost of every future recipe change (C# and GLSL must change together; a v9 recipe falls
   back to CPU until ported). Gate G1 asks for this decision with numbers.
2. **LOD1 on GPU.** Heights are within mm, but not bit-identical to the CPU model that Terrain3D uses. Is a cm-level
   difference at terrace risers on the 64-104 m handoff acceptable? The fallback (LOD1 on CPU) forgoes about half of
   item 7.
3. **Bit-exact noise** depends on how Godot's Windows build compiles FastNoiseLite (no FMA contraction assumed).
   WO-G0 answers it; if it is not exact, the height tolerances above still hold, but parity becomes statistical.
4. **fp64 availability in RenderingDevice** (whether Godot enables `shaderFloat64`) is unverified. df64 is the default.
5. **VRAM.** The slot pools add up to about 25-70 MB per level (capacity x 304-455 KB). That replaces per-sector mesh
   buffers of similar or larger size, but the pool is reserved up front. Acceptance checks the bytes.
6. **Readback latency.** GPU sectors arrive 2-3 frames after dispatch; that is far below today's batch latency, but
   install timing changes order. Coverage and handoff logic already tolerates late sectors, which the flicker check
   confirms.
7. **Shared GPU budget.** GPU-1/4/5 may also add per-frame compute. Should one streaming-compute arbiter own a single
   budget (for example 0.2 ms total) instead of each system taking its own?
8. **A1000 numbers** are estimates here. Goldens and GPU timings are machine-specific, so the laptop needs its own capture
   before G2 ships.
9. **Coastal routes.** In the first cut coastal sectors stay on CPU; ocean-heavy flights gain less until G4.
10. **Partial coverage culling** changes from a CPU mesh rebuild to a vertex collapse for slot sectors. It is a GPU-cost
    trade that must not regress the stationary GPU frame (S1 acceptance).
