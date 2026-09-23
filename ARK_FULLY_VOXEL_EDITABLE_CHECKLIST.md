# Fully Voxel-Editable Ark Checklist

This checklist covers the large-scope option: a generated, explorable Ark-class
megastructure whose habitable surfaces support voxel terrain editing, streaming,
saving, flight, gravity, water, atmosphere, clouds, and consistent terrain LODs.

The implementation should remain compatible with the existing ringworld mode.
An original Ark-class design is preferable for any public or commercial release.

## 0. Scope and acceptance contract

- [ ] Define the Ark's original visual identity, silhouette, terminology, and lore.
- [ ] Define the target hub radius, petal count, petal length, petal width, and structural depth.
- [ ] Define which surfaces are habitable, structural, decorative, or inaccessible.
- [ ] Decide whether petals are flat, curved, terraced, or partially enclosed.
- [ ] Define the maximum terrain relief, excavation depth, and construction height.
- [ ] Define the intended atmosphere model: petal-local, structure-wide, or contained fields.
- [ ] Define gravity rules for petals, the hub, structures, edges, and open space.
- [ ] Define expected surface-to-surface flight and landing behavior.
- [ ] Define minimum and recommended hardware targets.
- [ ] Establish frame-time, memory, streaming, and save-size budgets.
- [ ] Establish near, proxy, regional, global, and scaled-far LOD distance bands.
- [ ] Record the first playable milestone and final acceptance criteria.

### Gate 0 — Design lock

- [ ] All dimensions and gameplay limits are represented by one versioned configuration.
- [ ] Coordinate, gravity, atmosphere, and accessibility rules are unambiguous.
- [ ] Performance targets and supported quality presets are documented.

## 1. Generalize the world-type architecture

- [ ] Add a `World Type` selector to the world-generation launcher.
- [ ] Preserve the existing ringworld option and saved-world compatibility.
- [ ] Add an Ark generation configuration resource.
- [ ] Store the selected world type and Ark parameters in save metadata.
- [ ] Introduce a shared megastructure interface for coordinate conversion.
- [ ] Introduce a shared megastructure interface for gravity queries.
- [ ] Introduce a shared megastructure interface for terrain sampling.
- [ ] Introduce a shared megastructure interface for streaming-region queries.
- [ ] Introduce a shared megastructure interface for LOD and far-render data.
- [ ] Move ring-specific entry points behind the shared interfaces.
- [ ] Add explicit validation for unsupported world/configuration combinations.
- [ ] Add unit or runtime contract tests for both world types.

### Gate 1 — Two world types

- [ ] The launcher can create and reload both ringworld and Ark saves.
- [ ] Existing ringworld startup, traversal, editing, and saving still work.
- [ ] Ark mode loads an empty but correctly configured world scene.

## 2. Ark topology and canonical coordinates

- [ ] Define stable region identifiers for the hub, petals, foundry arms, walls, and auxiliary structures.
- [ ] Define an `ArkSurfaceAddress` containing region ID and local surface coordinates.
- [ ] Define double-precision Ark-centric Cartesian coordinates.
- [ ] Implement petal-local surface-to-Ark transforms.
- [ ] Implement Ark-to-petal surface transforms.
- [ ] Implement hub-local surface transforms.
- [ ] Define deterministic ownership rules where regions overlap.
- [ ] Define adjacency across chunk boundaries within a region.
- [ ] Define adjacency across petal-to-hub boundaries.
- [ ] Define behavior at petal edges and inaccessible boundaries.
- [ ] Implement canonical wrapping only where a region is genuinely periodic.
- [ ] Make chunk keys include their Ark region identity.
- [ ] Make terrain-edit keys include their Ark region identity.
- [ ] Add coordinate round-trip tests at centers, edges, corners, and seams.
- [ ] Add large-coordinate precision tests at every petal extremity.
- [ ] Add floating-origin rebase tests for every region type.

### Gate 2 — Stable coordinate graph

- [ ] Surface-to-world-to-surface round trips stay within the agreed tolerance.
- [ ] Neighbor lookup never crosses into an unrelated region.
- [ ] Petal/hub seam ownership is deterministic.
- [ ] Floating-origin shifts do not change canonical addresses.

## 3. Procedural Ark structure

- [ ] Generate the central hub mesh from configuration.
- [ ] Generate the configured number of petals.
- [ ] Generate petal undersides and structural thickness.
- [ ] Generate edge walls, retaining structures, and transition collars.
- [ ] Generate foundry arms and non-habitable structural silhouettes.
- [ ] Add deterministic structural variation without changing topology.
- [ ] Assign stable region IDs to all generated components.
- [ ] Generate collision proxies for non-voxel structural regions.
- [ ] Generate navigation/landing metadata for accessible surfaces.
- [ ] Add debug colors for each structural region and topology boundary.
- [ ] Add mesh validation for winding, holes, degenerate triangles, and bounds.

### Gate 3 — Complete analytic Ark

- [ ] The full Ark is visible from representative exterior viewpoints.
- [ ] There are no obvious holes, inverted surfaces, or disconnected petals.
- [ ] Every accessible surface resolves to a valid canonical region.

## 4. Deterministic Ark terrain

- [ ] Extend terrain seeds with world type and region identity.
- [ ] Define petal-level continent or biome layout rules.
- [ ] Define hub terrain rules if the hub is habitable.
- [ ] Generate elevation consistently across all petal coordinates.
- [ ] Generate temperature, moisture, erosion, and ridge fields.
- [ ] Generate coastlines, oceans, lakes, and rivers where permitted.
- [ ] Prevent water from crossing open petal edges unintentionally.
- [ ] Reserve structural transition zones near hub and petal boundaries.
- [ ] Reserve authored zones for facilities, lifts, foundries, or landmarks.
- [ ] Enforce terrain floor, build ceiling, and structural-clearance limits.
- [ ] Add deterministic terrain fingerprints and cache versioning.
- [ ] Add distribution audits for land, water, biomes, and elevation.
- [ ] Add border continuity tests for every terrain region.

### Gate 4 — Authoritative terrain sampler

- [ ] Identical seed/configuration pairs generate identical terrain.
- [ ] All terrain bounds and reserved structural zones are respected.
- [ ] Terrain and hydrology remain valid at region boundaries.

## 5. Ark voxel chunk system

- [ ] Generalize chunk coordinates to support Ark region IDs.
- [ ] Generate voxel blocks from the Ark terrain sampler.
- [ ] Generate structural bedrock beneath habitable terrain.
- [ ] Generate voxel edge walls where local collision is required.
- [ ] Support negative and positive vertical chunks within configured bounds.
- [ ] Update greedy meshing for Ark-local coordinate frames.
- [ ] Update mesh placement for petal and hub transforms.
- [ ] Update camera-relative chunk transforms for Ark regions.
- [ ] Update authoritative voxel collision queries.
- [ ] Update swept player collision for Ark-local frames.
- [ ] Update block placement and removal across chunk boundaries.
- [ ] Update dirty-neighbor rebuild logic across region seams.
- [ ] Add separate transparent surfaces for glass and similar blocks.
- [ ] Add debug visualization for chunk IDs, seams, and collision residency.
- [ ] Add voxel seam tests within petals and at supported region transitions.

### Gate 5 — Editable local surface

- [ ] A player can walk, dig, and build on at least one petal.
- [ ] Chunk boundaries have no visible or physical gaps.
- [ ] Edits remain correct after streaming out and back in.
- [ ] Collision and rendering agree after edits.

## 6. Ark streaming and predictive loading

- [ ] Generalize streaming focus from ring coordinates to surface addresses.
- [ ] Stream visual and collision chunks around the active surface location.
- [ ] Predict loading along surface-relative movement.
- [ ] Predict loading along free-flight trajectories.
- [ ] Detect the likely destination petal or hub region.
- [ ] Preload destination terrain and collision before landing.
- [ ] Support safe cancellation when a flight trajectory changes.
- [ ] Apply circular or visibility-bounded streaming windows within regions.
- [ ] Avoid loading neighboring petals merely because their Cartesian positions overlap.
- [ ] Add per-region chunk-load budgets and fair scheduling.
- [ ] Add memory-pressure eviction across inactive regions.
- [ ] Add telemetry for desired, queued, resident, and collision-ready chunks.
- [ ] Add automated high-speed routes across representative regions.

### Gate 6 — Reliable streaming

- [ ] Normal traversal never exposes a terrain hole.
- [ ] High-speed flight does not land on unloaded collision.
- [ ] Memory remains within the agreed budget after visiting every petal.

## 7. Gravity and player movement

- [ ] Define a gravity-provider interface shared by ringworld and Ark modes.
- [ ] Implement petal-local gravity fields.
- [ ] Implement hub-local gravity fields if the hub is habitable.
- [ ] Implement bounded gravity volumes for artificial structures.
- [ ] Fade gravity with altitude according to the design contract.
- [ ] Resolve competing fields deterministically.
- [ ] Prevent sudden orientation flips near region boundaries.
- [ ] Preserve physical velocity when changing gravity frames.
- [ ] Update walking, jumping, stepping, and grounding for arbitrary surface frames.
- [ ] Update noclip and free-flight controls for Ark-centric space.
- [ ] Add safe landing detection for petals, hub areas, and structures.
- [ ] Add edge-fall behavior and transition into open-space flight.
- [ ] Add debug visualization for active gravity field and surface frame.
- [ ] Add automated jump, fall, edge, boundary, and landing tests.

### Gate 7 — Movement acceptance

- [ ] Walking and jumping feel stable on all supported surface types.
- [ ] Gravity transitions do not cause camera snaps or velocity discontinuities.
- [ ] A player can fly from one petal to another and land safely.

## 8. Terrain LOD hierarchy

- [ ] Generate near voxel terrain for the active surface window.
- [ ] Generate proxy terrain for the surrounding petal region.
- [ ] Generate regional terrain for complete petals.
- [ ] Generate hub terrain LOD where applicable.
- [ ] Generate a complete global Ark surface representation.
- [ ] Use the authoritative terrain sampler at every LOD.
- [ ] Propagate runtime terrain edits into affected LOD snapshots.
- [ ] Add geomorphing or dithered transitions between LOD tiers.
- [ ] Add structural walls and undersides at every required LOD.
- [ ] Prevent unrelated petal geometry from appearing through active terrain.
- [ ] Add topology-aware culling for petals and hub regions.
- [ ] Add debug colors and statistics for all active LOD tiers.
- [ ] Add automated LOD traversal and edit-propagation routes.

### Gate 8 — Seamless terrain hierarchy

- [ ] Terrain identity remains consistent from voxels to the global Ark.
- [ ] No persistent holes, cracks, doubled surfaces, or severe LOD pops are visible.
- [ ] Terrain edits remain visible at every relevant distance.

## 9. Scaled far-space renderer

- [ ] Create an isolated scaled `World3D` for the complete Ark.
- [ ] Generate scaled copies of the hub, petals, undersides, and foundry structures.
- [ ] Synchronize the far camera with the gameplay camera.
- [ ] Synchronize lighting, sky, world seed, and time-of-day state.
- [ ] Render the global seeded terrain on all visible habitable surfaces.
- [ ] Render structural materials and emissive landmarks.
- [ ] Composite the far Ark behind normal-depth terrain and structures.
- [ ] Define handoff distances between normal and scaled rendering.
- [ ] Prevent depth inversions and double rendering during handoff.
- [ ] Support external views and interior views between petals.
- [ ] Add automated captures from representative Ark viewpoints.

### Gate 9 — Whole-Ark visibility

- [ ] The complete structure reads correctly from exterior and central viewpoints.
- [ ] Near terrain cleanly occludes the far-space composite.
- [ ] Handoffs do not produce obvious clipping, scale changes, or duplicate surfaces.

## 10. Materials and lighting

- [ ] Define an Ark terrain material library.
- [ ] Add canonical-coordinate triplanar materials for nearby voxels.
- [ ] Add simplified matching materials for terrain proxy LODs.
- [ ] Add macro-only materials for regional and global LODs.
- [ ] Add structural hull, wall, foundry, and machinery materials.
- [ ] Add separate glass and emissive material surfaces.
- [ ] Preserve vertex color as deterministic biome and elevation tint.
- [ ] Preserve consistent day/night behavior across every LOD.
- [ ] Add cloud or structure shadowing where affordable.
- [ ] Add quality-tier controls for normal maps, texture resolution, and shader detail.
- [ ] Verify floating-origin shifts do not cause texture swimming.

### Gate 10 — Material parity

- [ ] Nearby and distant terrain clearly represent the same biome/material.
- [ ] Structural surfaces remain legible at all supported distances.
- [ ] Materials do not swim, stretch excessively, or expose LOD seams.

## 11. Atmosphere, sky, and clouds

- [ ] Implement the selected Ark atmosphere model.
- [ ] Restrict atmosphere to intended habitable or contained regions.
- [ ] Keep open structure cavities and exterior space visually distinct.
- [ ] Implement surface-local horizon and sky orientation.
- [ ] Blend between atmosphere and space during ascent.
- [ ] Add petal-local haze that respects terrain and structural occlusion.
- [ ] Generate deterministic cloud coverage for each atmospheric region.
- [ ] Add inexpensive far cloud representations.
- [ ] Add bounded local volumetric clouds where supported.
- [ ] Synchronize clouds between local, regional, and scaled renderers.
- [ ] Add night-side and structure-shadow tuning.
- [ ] Add atmosphere and cloud quality presets.

### Gate 11 — Environmental continuity

- [ ] Atmosphere never fills unintended open-space regions.
- [ ] Sky orientation remains correct on every habitable surface.
- [ ] Cloud and haze transitions remain convincing during surface-to-space flight.

## 12. Water and hydrology gameplay

- [ ] Generate dedicated ocean, lake, and river surfaces.
- [ ] Clip or contain water correctly at petal boundaries.
- [ ] Render water consistently across all LOD tiers.
- [ ] Add local wave normals, Fresnel, depth color, and foam.
- [ ] Add swimming and buoyancy.
- [ ] Add underwater rendering and audio state.
- [ ] Define how terrain edits interact with water.
- [ ] Update water after edits without unbounded simulation cost.
- [ ] Save persistent water-affecting edits.
- [ ] Add hydrology validation at region and LOD boundaries.

### Gate 12 — Water acceptance

- [ ] Water never visibly spills into open space unless intentionally designed.
- [ ] Local and distant water surfaces agree in elevation and shape.
- [ ] Swimming and underwater transitions work after streaming and reload.

## 13. Saving, loading, and replay

- [ ] Version save data by world type and Ark configuration.
- [ ] Save edits using region-aware canonical chunk keys.
- [ ] Save anchored objects with Ark surface addresses.
- [ ] Save player position, physical velocity, and active gravity frame.
- [ ] Save world-generation and terrain fingerprints.
- [ ] Reject or migrate incompatible saves safely.
- [ ] Extend edit journals and deterministic replay to Ark regions.
- [ ] Extend replication snapshots if multiplayer remains in scope.
- [ ] Test saves made on every petal and supported hub region.
- [ ] Test saves made during open-space flight.
- [ ] Test copying and deleting Ark save slots.
- [ ] Add corruption and partial-write recovery tests.

### Gate 13 — Persistence

- [ ] Terrain edits and anchored objects survive save/load on every region type.
- [ ] Loading during flight restores a safe and physically equivalent state.
- [ ] Replay produces the same final world state.

## 14. Minimap and navigation

- [ ] Generalize minimap coordinates to Ark surface addresses.
- [ ] Add a local terrain view for the active surface.
- [ ] Add a whole-Ark navigation view.
- [ ] Display the active hub/petal/structure region.
- [ ] Display altitude relative to the active surface.
- [ ] Display local gravity direction and strength.
- [ ] Display other petals and valid landing regions during flight.
- [ ] Add region-aware waypoints and trackable objects.
- [ ] Add routes through hub/petal transition areas.
- [ ] Preserve north-up and heading-up behavior in local surface frames.
- [ ] Add zoom transitions between local terrain and whole-Ark views.

### Gate 14 — Navigation acceptance

- [ ] A player can identify their current region and navigate to another petal.
- [ ] Waypoints remain stable through floating-origin and coordinate-frame changes.
- [ ] Local terrain remains readable during walking, flight, and landing.

## 15. Structures, landmarks, and generation variety

- [ ] Define deterministic landmark placement rules.
- [ ] Generate foundries, lifts, transit routes, and control structures.
- [ ] Reserve terrain before placing large structures.
- [ ] Add collision and simplified distant LODs for major structures.
- [ ] Add emissive navigation and identity lighting.
- [ ] Add unique petal-level biome or architectural themes.
- [ ] Add configurable damaged, dormant, active, or overgrown variants.
- [ ] Add procedural points of interest without blocking critical routes.
- [ ] Save destructible or editable structure state where supported.

### Gate 15 — World identity

- [ ] Every petal is navigable and visually distinguishable.
- [ ] Major structures remain recognizable across LOD transitions.
- [ ] Generated landmarks do not conflict with terrain or accessibility rules.

## 16. Tooling and debugging

- [ ] Add a world-type and active-region debug HUD.
- [ ] Add canonical Ark coordinate readouts.
- [ ] Add gravity-field visualization.
- [ ] Add streaming and residency visualization.
- [ ] Add LOD band visualization.
- [ ] Add scaled-far renderer toggles.
- [ ] Add terrain/material debug modes.
- [ ] Add automated teleport points for every region and boundary.
- [ ] Add deterministic screenshot locations.
- [ ] Add command-line smoke tests for Ark startup.
- [ ] Add command-line routes for surface traversal and inter-petal flight.
- [ ] Add machine-readable validation reports.

## 17. Performance and memory

- [ ] Profile cold Ark generation.
- [ ] Profile cached Ark startup.
- [ ] Profile local voxel generation and meshing.
- [ ] Profile proxy and regional LOD generation.
- [ ] Profile the scaled far-space renderer.
- [ ] Profile local and far atmosphere/cloud rendering.
- [ ] Profile rapid flight across several region boundaries.
- [ ] Profile dense editing and rebuild queues.
- [ ] Bound terrain, LOD, texture, save, and chunk caches.
- [ ] Add adaptive generation concurrency.
- [ ] Add adaptive material and cloud quality.
- [ ] Add memory-pressure eviction and recovery.
- [ ] Add benchmark stages for worst-case viewpoints and lighting.
- [ ] Enforce preset-specific CPU, GPU, memory, and streaming budgets.

### Gate 17 — Performance certification

- [ ] Meet the minimum frame-rate target on minimum hardware.
- [ ] Meet the target frame rate on recommended hardware.
- [ ] Stay within the memory budget after visiting all regions.
- [ ] Complete flight and dense-edit benchmarks without terrain holes or stalls.

## 18. Human acceptance

- [ ] Confirm the Ark silhouette reads clearly from space.
- [ ] Confirm scale reads correctly from the hub and petal surfaces.
- [ ] Walk and edit terrain on every petal.
- [ ] Traverse representative voxel chunk boundaries.
- [ ] Traverse every supported petal/hub transition.
- [ ] Fly from each petal to at least one other petal.
- [ ] Confirm destination terrain is ready before every landing.
- [ ] Confirm gravity and camera orientation remain comfortable.
- [ ] Confirm no objectionable LOD cracks, holes, or duplicate surfaces.
- [ ] Confirm atmosphere remains localized correctly.
- [ ] Confirm water stays contained and matches across LODs.
- [ ] Confirm materials remain consistent through floating-origin shifts.
- [ ] Confirm saves reload correctly on surfaces and during flight.
- [ ] Confirm minimap and waypoints remain understandable.
- [ ] Confirm cloud, lighting, and night-side readability.
- [ ] Confirm the original design is sufficiently distinct for its intended release.

## Final certification

- [ ] All automated coordinate and topology tests pass.
- [ ] All automated voxel and streaming tests pass.
- [ ] All automated gravity and flight tests pass.
- [ ] All automated save/replay tests pass.
- [ ] All automated LOD and scaled-render tests pass.
- [ ] All required performance budgets pass.
- [ ] All human acceptance checks pass.
- [ ] Ringworld mode passes its regression suite.
- [ ] Ark save format and generation version are frozen for the release.
- [ ] The fully voxel-editable Ark milestone is certified complete.
