# Temporary Terrain Generation TODO

Target vertical envelope:

- Build floor: Y = -256
- Deepest ocean floor: approximately Y = -206
- Sea level: provisionally Y = 0
- Highest rare mountain peaks: approximately Y = 1848
- Build ceiling: Y = 2048

## Phase 1 — Shared Regional Metadata

- DONE: Add a deterministic `TerrainSample` shared by voxels and every terrain LOD.
- DONE: Add broad periodic continentalness, erosion, ridge, temperature, and moisture fields.
- DONE: Add preliminary ocean, coast, lowland, highland, and mountain-region classification.
- DONE: Preserve the current heightfield while metadata integration is validated.
- TODO: Add a debug visualization for regional fields and classifications.
- TODO: Validate every regional field across the around-ring seam in the editor.

## Phase 2 — Continental Height Remap

- DONE: Remap deep-ocean floors toward Y = -206.
- DONE: Add continental shelves, coasts, plains, and highlands around provisional sea level Y = 0.
- DONE: Add smooth regional transition curves and bounded local detail.
- DONE: Update empty-chunk streaming bounds for elevated continental terrain.
- TODO: Visually validate continent scale, coastline frequency, and elevation distribution in editor.

## Phase 3 — Mountain Belts

- DONE: Generate seam-safe domain-warped ridged mountain chains.
- DONE: Apply rarity-shaped uplift with an absolute peak ceiling at Y = 1848.
- DONE: Double the peak-rarity exponent and add summit-safe lighting/shadow bias defaults.
- DONE: Restrict snow to exposed mountain ridges and stop coarse regional LOD shadow silhouettes.
- DONE: Decouple distant terrain colors from high-energy directional lighting and explicitly serialize rare-summit snow settings.
- DONE: Lower the legacy far-ring surface to canonical Y=-256, restore distant shadows, and make cap-height summits exceptionally rare.
- ROLLED BACK: Phase 4 biome/snow color resolver; LOD 1-3 now use a clean low/high elevation-gradient baseline for a fresh biome-color implementation.
- DONE: Suppress mountain height through erosion and carve narrow passes/valleys.
- DONE: Expose mountain masks, warp scale, peak rarity, erosion strength, and valley controls in terrain settings.
- TODO: Visually measure the resulting peak-height distribution and tune most ranges toward 400-1000.
- TODO: Verify rare landmark peaks reach 1500-1848 without forming broad ceiling plateaus.

## Phase 4 — LOD And Materials

- ROLLED BACK: Deterministic regional colors driven by terrain metadata.
- ROLLED BACK: Moisture-varied lowlands, rocky ridges, and temperature-shifted high-altitude snow.
- ROLLED BACK: Shared biome colors across LOD 1, LOD 2, and global LOD 3.
- DONE: Restore a simple low/high elevation-gradient baseline across all distant terrain LODs.
- TODO: Verify the neutral baseline and transition alignment at every LOD boundary.

## Phase 5 — Oceans

- DONE: Add a shared ocean/lake/river surface representation without filling distant water bodies with water voxels.
- DONE: Keep ocean water at Y=0, bound seeded lake basins to 8-90 m, and carve moisture-gated rivers 3-18 m below their local water surface.
- DONE: Route the same water classification, surface height, and colour through the minimap, regional terrain, global HLOD, and scaled far ring.
- TODO: Add near-field swimming, buoyancy, shoreline, and edit behavior.
- TODO: Decide whether local water becomes a voxel block or a separate volume representation.

## Phase 6 — Biome Coloring Rebuild

- TODO: Define a small initial biome palette without snow or lighting-dependent color logic.
- TODO: Separate biome selection from final surface-material selection.
- TODO: Add a deterministic `BiomeSample` derived from existing region, temperature, moisture, erosion, elevation, and ridge metadata.
- TODO: Add a biome debug mode that displays discrete biome IDs before introducing blended colors.
- TODO: Validate biome scale, borders, and around-ring seam continuity using the debug menu.
- TODO: Add controlled biome-border blending only after discrete classifications are validated.
- TODO: Route the same biome result into LOD 1, LOD 2, and global LOD 3.
- TODO: Apply matching biome tinting to near-field voxel top and side faces.
- TODO: Validate color agreement through every voxel/LOD transition.
- TODO: Add rock, shoreline, and snow overlays as independent passes after the base biome colors are stable.
- TODO: Confirm distant shadows and day/night lighting preserve biome hue without white clipping or black terrain.
- DONE: Use complementary dither masks across overlapping LOD transition bands to remove visible distance rings.
- DONE: Give global LOD 3 a dedicated shaded material so the world terrain can cast directional shadows onto itself.
- DONE: Add a pre-load logarithmic ring-radius selector from 80 km to an experimental 8,000 km and configure dependent systems before scene startup.
- DONE: Harden far-side rendering at enlarged radii with double-sided global terrain, occlusion-safe legacy fallback, scaled occluders, and additional far-plane margin.

## Large-World Coordinate Migration

- DONE: Add a 64-bit `RingCanonicalPosition` authority type alongside the existing 64-bit around-voxel IDs.
- DONE: Enable same-frame floating-origin rebasing in the active gameplay scene without double-counting manager motion.
- TESTING: Player authority and circumference wrapping now use doubles; collision runs in an 8,192 m local sector and reconstructs 64-bit voxel coordinates.
- TODO: Migrate the explorer motor, collision queries, edits, saves, and streaming focus from float canonical positions to sector origin plus local float offsets.
- TODO: Make ring circumference and conversion APIs use `double`/`long` end-to-end.
- TODO: Rebuild LOD1/LOD2 sectors in local tangent frames instead of absolute radial coordinates.
- TODO: Replace global LOD3 absolute vertices with camera-relative angular reconstruction.
- TODO: Keep dynamic physics and directional shadows inside a bounded local bubble.
- TODO: Add automated rebase continuity tests for movement, velocity, edits, LOD transitions, and the around-ring seam.
