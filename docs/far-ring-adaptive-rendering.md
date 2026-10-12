# Far-ring rendering and texture streaming

Implementation target: Godot 4.8 development .NET, Forward+, October 12, 2026.

## Requested behavior

The far renderer uses the Cinematic / RTX 5090 surface budget (4096 around by
64 across) and a full-resolution viewport. Other graphics-preset features keep
their own settings. Explicit saved far-rendering overrides take precedence.

The renderer allocates geometric detail using projected error and bounded
terrain relief estimates, partitions terrain into independently cullable
sections, and adds cached generated surface color and normal tiles. Imported
asset textures use Godot's native mip streaming. Generated terrain tiles have
their own bounded residency system because they are not imported textures.

## Integration contracts

- Canonical terrain sampling, seed, recipe, surface edits, and saved-world
  identity remain authoritative for every generated mesh and tile.
- Workers receive immutable configuration and edit snapshots. Scene, GPU, and
  material mutations happen on the main thread.
- The currently complete terrain set remains visible while a replacement is
  prepared. Stale work cannot publish after an edit, reanchor, or quality change.
- Sector boundaries share positions and shading normals. Culling uses bounds
  containing the actual rendered geometry in the correct coordinate frame.
- Pending or evicted surface tiles fall back to the existing mesh colors and
  normals. Texture readiness never creates a terrain coverage hole.
- Geometry, texture residency, generation, and upload work have explicit bounds.
- Existing grave-key controls and sparse saved overrides expose the new quality
  controls. Runtime status distinguishes requested quality and available data.

## Validation gates

- [x] Build with the pinned 4.8 .NET toolchain.
- [x] Load the vendored Terrain3D extension and production cloud shaders.
- [x] Verify adaptive geometry, seam topology, sector bounds, and stale results.
- [x] Verify tile identity, eviction, edit invalidation, and coordinate seams.
- [x] Verify native streaming imports and tree catalog CPU image access.
- [x] Verify defaults, live controls, sanitization, and saved override precedence.
- [x] Run existing far-regional and proxy coverage regression contracts.
- [x] Inspect isolated rendered fixtures and verify actual terrain pixel contribution.
- [ ] Inspect moving-camera gameplay, high altitude, wrap seams, and fast travel.
- [ ] Measure matched global/regional viewport GPU time and peak memory on target
  hardware. Headless success does not certify visuals or performance.

## Reference behavior

Godot 4.8 introduced opt-in imported 2D mip streaming. The native memory budget
covers streamed textures, not meshes, generated terrain tiles, shadow atlases,
or viewport render targets. Raising a viewport's linear resolution scale from
0.75 to 1.0 increases pixel count by about 78%; this is not an FPS prediction.

- [Godot 4.8 dev 5 release notes](https://godotengine.org/article/dev-snapshot-godot-4-8-dev-5/)
- [TextureStreaming API](https://docs.godotengine.org/en/latest/classes/class_texturestreaming.html)
- [Existing terrain transition contracts](terrain-lod-transition-integration.md)

Validation results and outstanding limitations are recorded below as the
implementation is reviewed.

## Measured checks and implementation limits

The pinned engine builds with zero warnings and errors. CPU contracts pass for
adaptive topology, global periodic seams, regional welded boundaries, bounds,
integer/float edit semantics, tile identity and eviction, flat regional water,
settings persistence, preset precedence, and actual asynchronous global and
regional replacement. The shifted-anchor test exercises the actual global material
cutoff. Existing proxy partial coverage, intermediate topology, global terrain
preparation, LOD settings, texture palette compression, and tree catalog
contracts pass.

The patched Terrain3D debug and release libraries pass native asset-name/resource metadata
and region-copy compatibility, actual terrain editing/save smoke, and GPU
near/proxy lighting parity. The terrain streaming contract passes 158 checks,
including literal legacy biome bytes and synchronous/worker image consistency
after Godot 4.8's native RGBA8 rounding changed. Production cloud shaders loaded
in the full rendered scene without shader compilation errors.

The real far viewport rendered at 1600 by 900 (100%) with a 4096 by 64 requested
target. On the small development-ring fixture, adaptive selection used 128 by 64
and all 16 global terrain tiles became resident. A fixed-black, terrain-only
comparison changed 2344 of 4096 sampled pixels when terrain was hidden, with a
maximum channel difference of 0.870588. The screenshot was inspected at
`.godot/far_adaptive_geometry_rendered.png`. This confirms mesh/shader submission;
it is not visual parity with a forced 4096 mesh or full gameplay acceptance.
The fixture uses a 16 km local cutoff because the production default 500 km
cutoff intentionally hides this entire 160 km-diameter development ring. Sky,
clouds, water and structural meshes cannot satisfy this comparison.

On the NVIDIA RTX A1000 6 GB laptop GPU using Vulkan Forward+, rendered tile
fixtures pass color, normal, fallback and rebinding checks. The native streaming
fixture starts the fir bark at 256 by 512, recovers its full 2048 by 4096 image,
reduces it to 512 by 1024 under a 1 MiB test budget, and recovers after restoring
512 MiB. Invisible-texture decay and visible recovery also pass. This narrow
residency test is not a total VRAM or frame-time benchmark. The real impostor
catalog passes GPU texture-array layer readback; the dummy headless renderer
explicitly skips that unsupported readback while retaining metadata checks.

- Global angular/across resolution is shared between sectors; regional tiers
  share an angular lattice and adapt radial subdivisions. Sectors cull
  independently, but this is not a per-sector quadtree. Bounded midpoint probes
  estimate terrain relief; they do not guarantee every unsampled feature meets
  the pixel-error target.
- The vertex budget applies separately to global terrain/water and the complete
  regional set. Old and replacement sets coexist during publication. Upload
  deadlines are soft: one mesh or texture upload can exceed its time allowance.
- Global fallback remains available beyond the local 125 km coverage radius
  when regional terrain is enabled. This covers gaps caused by the regional
  anchor differing from the camera, at the cost of overdraw. An anchor-aware
  exclusion of the covered annulus is a possible later optimization.
- Generated tiles contain macro biome colors and procedural slope normals.
  Their independent CPU/GPU caches are not native imported texture streaming.
  Reported texture bytes exclude driver allocations and native staging buffers.
- Tile edge samples agree at full resolution. Coarse mip transitions, motion,
  fast travel, and final terrain/cloud composition still need gameplay review.
- Native streaming covers 15 opaque bark and normal/material imports. Alpha
  foliage albedo stays on its current importer because dev7 lacks the authored
  alpha-coverage mip options.

Detailed test logs are under ignored `artifacts/engine-upgrade`. Toolchain setup,
import eligibility and Terrain3D compatibility are documented in
[the engine upgrade notes](godot-48-native-texture-streaming.md).
Some smoke logs include the existing missing optional native voxel mesher,
restricted certificate-store access, or fixture shutdown resource warnings;
the build and listed contract results are successful. The Terrain3D duplicate
method-registration errors found during upgrade are fixed.
