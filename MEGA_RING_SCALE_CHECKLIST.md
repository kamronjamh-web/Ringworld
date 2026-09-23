# Mega Ring Scale Checklist

Status: active implementation plan

## Target Contract

- [ ] Target diameter is 150,000,000 km.
- [ ] Target radius is 75,000,000 km.
- [ ] Target width is 1,600,000 km.
- [ ] Target structural depth is 500,000 km.
- [ ] Circumference is derived from an integer count of 32 m arc chunks.
- [ ] The Mega Ring has a versioned world definition and does not use free-form launcher values.
- [ ] A headless scale-contract self-test validates all target dimensions and topology limits.

## Canonical Topology

- [ ] Promote global chunk coordinates to signed 64-bit values.
- [ ] Promote around-ring chunk counts, voxel lengths, wrap arithmetic, and floor division to signed 64-bit values.
- [ ] Keep local voxel coordinates as 32-bit `Vector3I` values only inside a 32 m chunk.
- [ ] Audit all streaming queues, async build records, hash keys, revision maps, and cache keys for 64-bit chunk identities.
- [ ] Migrate chunk save keys, natural SDF edit keys, journals, and replication IDs without invalidating existing worlds.
- [ ] Add seam, negative-wrap, multi-circumference, and extreme-coordinate regression tests.

## Precision And Local Runtime

- [ ] Make canonical doubles/integers authoritative for players, terrain, entities, weather, and travel.
- [ ] Complete one floating-origin service shared by terrain, physics, navigation, VFX, audio, clouds, and debug rendering.
- [ ] Ensure rendering and physics receive camera-relative float transforms only.
- [ ] Add origin-shift soak tests during high-speed travel and seam crossings.

## Terrain And LOD

- [ ] Finish fine/coarse ownership boundaries and install streamed Transvoxel transition meshes.
- [ ] Make all LOD tile, sector, and geomorph sampling canonical-coordinate safe.
- [ ] Replace full-ring preprocessing with sparse, hierarchical, on-demand terrain summaries.
- [ ] Use bounded disk caches with seed, world-definition, and generator-version keys.
- [ ] Keep 1 m editable SDF terrain limited to local gameplay bubbles.

## Global Presentation

- [ ] Build an analytic, camera-relative inner shell for the full ring.
- [ ] Represent the 500,000 km structure with scalable shell tiers, not voxel volume.
- [ ] Add global procedural biome, color, cloud, and weather fields with canonical coordinates.
- [ ] Implement large-scale terrain and structure lighting without full-distance shadow maps.
- [ ] Retune horizon, haze, cloud scale, and atmospheric extinction for a 75-million-km radius.

## Persistence, Simulation, And Travel

- [ ] Partition saves and edits by 64-bit canonical regions.
- [ ] Compact journals incrementally and load only nearby world regions.
- [ ] Separate active, warm, and dormant simulation regions.
- [ ] Add route planning, transit, fast travel, and deterministic region generation.
- [ ] Keep physics, AI, props, and collision inside bounded local simulation bubbles.

## Performance And Certification

- [ ] Define CPU, GPU, RAM, VRAM, disk-cache, worker, upload, draw-call, and triangle budgets.
- [ ] Add Mega Ring benchmark routes for travel, origin shifts, edits, storms, save/load, and seam crossings.
- [ ] Report average FPS, 1% low, worst frame, memory high-water marks, jobs, uploads, and cache activity.
- [ ] Require no global full-resolution allocation, mesh build, or startup preprocessing pass.
- [ ] Certify a cached playable startup and sustained travel on the target hardware.

## Implementation Order

- [x] Define the exact Mega Ring dimensions and topology contract.
- [ ] Complete the 64-bit topology migration.
- [ ] Complete universal floating origin.
- [ ] Finish current SDF LOD ownership and transitions.
- [ ] Build sparse global terrain and structural shell tiers.
- [ ] Add regional persistence, simulation, and travel.
- [ ] Implement visual systems and certification benchmarks.