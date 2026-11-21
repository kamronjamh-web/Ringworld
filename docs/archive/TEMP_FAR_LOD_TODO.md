# Temporary Far LOD TODO

Status legend:

- TODO: not started
- IN PROGRESS: currently being implemented or validated
- DONE: implemented and verified in project context

## Milestone 1: Shared Deterministic Terrain Sampler

Goal: voxel chunks and every future terrain LOD sample one authoritative terrain definition.

- DONE: Add `RingTerrainSampler` as a reusable deterministic terrain service.
- DONE: Move seeded base/detail noise and periodic around-ring sampling into the shared sampler.
- DONE: Move surface-height and baseline subsurface block selection into the shared sampler.
- DONE: Route `VoxelChunkGridManager` baseline generation through the shared sampler.
- DONE: Expose the active sampler from `VoxelChunkGridManager` for future LOD consumers.
- DONE: Add an explicit around-ring seam validation helper.
- DONE: Run the shared sampler through the integration scene and startup seam self-test.
- DONE: Verify voxel output, edits, player collision, and ring seam behavior in editor.

## Milestone 2: Single Curved Proxy Sector (Current)

- DONE: Define canonical far-sector coordinates and dimensions.
- DONE: Generate one `(N+1) x (N+1)` heightfield grid from `RingTerrainSampler`.
- DONE: Curve sector vertices using the same 80 km ring mapping.
- DONE: Match proxy samples to voxel top faces at shared canonical coordinates.
- DONE: Add a simple vertex-color terrain material.
- DONE: Correct proxy triangle winding so the terrain renders from the habitable side.
- IN PROGRESS: Visually validate placement, winding, color, and alignment in editor.
- TODO: Add a boundary/alignment debug overlay if visual validation exposes ambiguity.

## Later Milestones

- DONE: Stream an initial LOD 1 sector grid around the player as sector boundaries are crossed.
- DONE: Split streaming grids into independently cached sector meshes.
- DONE: Pool sector nodes and rebuild only newly entered sectors after boundary crossings.
- DONE: Profile sector creation, reuse, rebuild counts, and main-thread upload time.
- DONE: Add per-grid sector creation/reuse/rebuild counters and build-time tracking.
- DONE: Add an F6 live LOD profiling HUD for sustained-flight testing.
- DONE: Move height sampling, curvature conversion, normals, colors, and index generation onto background tasks.
- DONE: Restrict Godot `ArrayMesh` creation/upload to the main thread.
- DONE: Give each background batch an independent deterministic sampler to prevent worker/main-thread noise-lock contention.
- DONE: Correct background-generated proxy normals to point toward the habitable side.
- DONE: Cache deterministic surface height once per voxel X/Z column during chunk population.
- DONE: Skip concave collision generation for visual-only voxel chunks.
- DONE: Reduce the editable mesh-collision window independently from the visual window.
- DONE: Stop newly created voxel chunks from queueing an immediate duplicate self-rebuild.
- DONE: Add collision-prioritized incremental voxel chunk loading.
- DONE: Limit integration-scene voxel generation to one newly loaded chunk per frame.
- DONE: Remove unnecessary neighbor remesh invalidation when loading new chunks; retain it for unloads and border edits.
- DONE: Enforce the physical 20 km across-ring terrain boundary for voxels and streaming proxy LODs.
- DONE: Clamp partial proxy sectors to the exact ring edge and skip sectors wholly outside it.
- DONE: Add one immutable 32-meter structural bedrock chunk outside each terrain edge.
- DONE: Extend structural edge hulls from canonical Y=-256 through Y=+255.
- DONE: Add both structural edge walls to the full-circumference global LOD.
- DONE: Crossfade global hull walls into their voxel versions instead of popping near the player.
- DONE: Add structural edge-wall geometry to LOD 1 and LOD 2 sector meshes.
- DONE: Carry edge walls continuously through voxel, LOD 1, LOD 2, and global LOD transitions.
- DONE: Add a blocky heightfield mesh mode for LOD 1.
- DONE: Configure LOD 1 for 4-meter columns, 2-meter height steps, flat tops, and vertical faces.
- DONE: Distribute completed LOD mesh uploads across frames with a two-sector-per-frame budget.
- DONE: Add an Escape pause menu with runtime voxel/LOD quality controls.
- DONE: Add a 2-32 surface chunk-radius preference and derive the voxel-to-LOD 1 transition from it.
- DONE: Add an optional LOD 1-to-world configuration that disables LOD 2 and widens blocky LOD 1.
- DONE: Add an independent 2-32 vertical visual chunk-radius preference.
- DONE: Add pause-menu aspect-ratio and resolution selection with window recentering.
- DONE: Add a depth-safe dithered voxel/proxy distance transition.
- DONE: Add conservative global-ring occlusion geometry for curvature-hidden chunks and LOD sectors.
- DONE: Add four-cascade mile-scale directional shadows with LOD 1 and LOD 2 terrain casters.
- DONE: Synchronize voxel atmosphere, procedural sky, and LOD 1-3 ring-side day/night lighting.
- DONE: Expand absolute build bounds to Y=-256..2048 with lightweight known-empty high-altitude streaming.
- TODO: Add generated horizon/macro-shadow data for terrain beyond the 14 km real-time shadow bubble.
- TODO: Add sector skirts where differing LOD resolutions meet.
- DONE: Add multi-kilometer LOD 2 regional sectors from the shared seed.
- DONE: Crossfade LOD 1 out while regional LOD 2 fades in.
- DONE: Add a complete terrain-aware global ring LOD using shared-seed height and color samples.
- DONE: Crossfade regional LOD 2 into the global terrain ring.
- TODO: Define how large player edits invalidate or modify distant representations.
