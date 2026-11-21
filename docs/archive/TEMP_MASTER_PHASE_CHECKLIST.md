# Temporary Master Phase Checklist

This file is the consolidated checkoff view for the currently planned voxel, ring LOD, lighting, and long-range shadow phases.

Status legend:
- [x] Implemented and validated in the current project context
- [~] Implemented or underway, but still awaiting its stated runtime test gate
- [ ] Not started or blocked by an earlier test gate

## Core Voxel Phases

### [x] Phase 1 — Single Editable Voxel Chunk

- [x] Core voxel types and compact 32×32×32 storage
- [x] Prototype face-culling mesh and collision
- [x] Runtime place/remove interaction
- [x] Block color resource and editor play-mode validation

### [x] Phase 2 — Multi-Chunk Editing and Persistence

- [x] Adjacent chunk grid management
- [x] Border-safe edit invalidation
- [x] Greedy meshing and debounced asynchronous rebuilds
- [x] Sparse chunk override persistence

### [x] Phase 3 — Procedural Streaming Terrain

- [x] Deterministic natural terrain
- [x] Excavation, bedrock, and above-surface construction limits
- [x] Independent visual and collision streaming radii
- [x] Untouched chunk memory and disk avoidance

### [x] Phase 4 — Ring Coordinates and Precision Foundation

- [x] Canonical ring coordinates and seam wrapping
- [x] Curved tangent placement
- [x] Around-ring seam tests
- [x] Floating-origin bridge with canonical state preservation

### [x] Phase 5 — Navigation, Replication, and Anchored Objects

- [x] Chunk-local navigation invalidation
- [x] Server-authoritative block edit path
- [x] Chunk revision and replication structures
- [x] Anchored non-voxel objects
- [x] Performance and memory-budget pass

### [x] Phase 6 — Operational Tooling

- [x] Runtime Phase 5 debug HUD
- [x] Replication snapshot capture and reapply controls
- [x] Authority rejection simulation
- [x] Navigation-region bridge updates

### [x] Phase 7 — Deterministic Replay Validation

- [x] Authoritative command journal
- [x] Replay baseline capture and lifecycle controls
- [x] Deterministic replay and sparse-state hash validation
- [x] Runtime replay test controls

## Ring Integration

### [~] Editable Voxel Ring Integration

- [x] Habitable ring voxel axes and large circumference support
- [x] Exact circumference configuration and seam-safe radius
- [x] Radial-gravity explorer and swept voxel collision
- [x] Integrated voxel, local proxy, regional proxy, and global terrain layers
- [x] Launcher-selected radius and width propagation
- [x] Legacy near/far slab overlap constrained in the integration path
- [ ] Complete visual, movement, collision, and edit smoke test
- [ ] Validate surface and far-shell radial alignment around the ring

## LOD Modernization

### [x] Phase A — Transition Stability and Diagnostics

- [x] Predictive voxel and proxy streaming
- [x] Proxy skirts and coordinated fade ranges
- [x] Local-only terrain shadow policy for conventional dynamic shadows
- [x] Streaming, worker, upload, and memory telemetry
- [x] Ambient and hemisphere direct-light corrections validated

### [~] Phase B — Gameplay-Correct, Frame-Budgeted LOD

- [x] Versioned edit-aware LOD1/LOD2 sampling
- [x] Seam-safe coarse edit aggregation
- [x] Braking-distance prefetch and bounded predictive corridors
- [x] Cancellable prioritized proxy jobs and stale-result rejection
- [x] Collision-first wrap-aware voxel load priority
- [x] Legacy FarRing disabled in the launcher and integration scene
- [x] Fade-independent opaque local terrain shadow caster
- [x] Clean builds, diagnostics, and static lifecycle audits
- [ ] Complete edit and LOD transition runtime test
- [ ] Complete rapid reversal and noclip-speed streaming test
- [ ] Complete seam and legacy-slab regression test

### [~] Phase C — Canonical Clipmap/HLOD Architecture

- [x] Add a centralized LOD coordinator and shared count/millisecond frame budgets
- [x] Replace fixed proxy windows with bounded nested canonical ring clipmap windows
- [x] Add screen-space error selection and geomorphing
- [x] Segment the global terrain representation with budgeted dirty-segment rebuilds
- [x] Add incrementally invalidated min/max terrain mipmaps for distant feature preservation
- [x] Propagate sparse edits into proxy morph targets, min/max cells, and affected global HLOD segments
- [x] Add clipmap/HLOD memory, upload, worker, segment, and mip diagnostics
- [x] Pass clean build, headless startup, hierarchy self-test, and LOD configuration self-test
- [ ] Complete the in-editor visual and sustained-travel performance test before Phase D

### [~] Fast-Travel Main-Thread Offload

- [x] Move full voxel classification, deterministic population, unloaded-neighbor sampling, greedy meshing, and optional collision-face expansion to bounded workers
- [x] Replace whole-neighbor chunk clones with six direct immutable boundary-face copies
- [x] Build visual/collision streaming windows and load priorities in a cancellable latest-wins planner
- [x] Suspend obsolete local voxel work during high-speed noclip while allowing cross-ring destination collision preloading
- [x] Resume local voxel and fine regional terrain streaming after 0.25 seconds of actual stopped movement instead of treating the selected noclip speed dial as motion
- [x] Keep LOD3 and coarser regional terrain following during fast travel, retain coarse high-altitude guard tiers, and keep whole-world terrain outside a 16 km local exclusion
- [x] Suspend regional proxy, structural-wall, local-cloud, union, and projected-shadow refreshes during fast travel while retaining cached far tiers
- [x] Move projected cloud-shadow rasterization and structural-wall raw geometry generation to workers
- [x] Reuse one immutable ring-geometry snapshot for all chunk transforms after a floating-origin shift
- [x] Pass zero-warning .NET build, focused benchmark, and 1,000 km/s cross-ring landing smoke test
- [ ] Human-check fast walking, warp travel, abrupt reversal, cloud/terrain continuity, and the landing handoff in Forward+

### [ ] Phase D — Extreme-Scale Precision

- [ ] Complete floating-origin integration for every LOD layer
- [ ] Move movement and streaming authority to 64-bit canonical coordinates
- [ ] Reconstruct terrain in camera-relative local frames
- [ ] Stress-test maximum launcher radius and width
- [ ] Pass Phase D precision test before Phase E

## Atmosphere and Weather

### [~] Deterministic Multi-Scale Cloud Weather

- [x] Replace uniform cloud-cell placement with seeded 32 km macro-weather systems
- [x] Add cumulus clusters, cloud banks, major fronts, and rare exceptional fronts up to 110 km
- [x] Add heavy-tail multi-cell formation scaling and system-aligned cloud families
- [x] Replace connector overlays and enlarged replacement archetypes with true waterdrop-style implicit-surface unions
- [x] Allow every cloud profile and weather-system pairing to union when their horizontal volumes touch and their vertical bands overlap
- [x] Preserve every member's original metaball lobes, transform, dimensions, and vertical profile inside the unified surface
- [x] Use a spatial hash and connected-component pass so touching groups have one continuous outer mesh with no internal intersections
- [x] Build camera-local unions off-thread while retaining original instances as a no-hole fallback until atomic installation
- [x] Adapt detailed/transition extraction resolution, cap every grid axis, and batch completed unions into at most three draw owners
- [x] Limit expensive extraction to a 14 km local range while keeping every distant cloud eligible as the player approaches
- [x] Remove probability, per-cloud connection limits, connector budgets, connector owners, and connector update timers from the active renderer
- [x] Feed unified surfaces through detailed/transition handoff, conventional shadows, and the formation-derived projected shadow mask
- [ ] Human-check union silhouettes, seam removal, storm preservation, and patch-boundary stability from ground and high altitude
- [x] Add distinct fair-cumulus, low-bank, storm-tower, and anvil-storm merged-mesh archetypes
- [x] Place cloud bases relative to edited terrain or visible water instead of the sea-level datum
- [x] Give major storms 5.5-11 km tops and exceptional anvils up to 15 km above local ground
- [x] Advect near, transition, far-shell, and projected-shadow weather with the same default wind
- [x] Keep 3D merged clouds through 10 km, transition clouds through 50 km, and the scaled far shell beyond
- [x] Feed enlarged formations into the existing 50 km, 512 px projected cloud-shadow mask
- [x] Split the near 3D pool at 6.5 km so nearby clouds retain conventional shadows without duplicate mesh submissions
- [x] Anchor retained-caster bounds to the player and include complete storm/anvil geometry outside the camera frustum
- [x] Leave the 6.5-50 km shadow range on the projected mask to bound directional-shadow cost
- [x] Add height-aware internal self-shadowing so merged formations retain bright tops and darker lower lobes
- [x] Reuse the altitude-corrected 50 km projected mask for soft cloud-on-cloud occlusion without extra cloud draw calls
- [x] Expose deterministic local coverage, system identity, formation profile, AGL base/top, wind, and precipitation potential for future rain
- [x] Reduce cold cloud-patch generation from 167 ms to 49 ms on the Arc 140V test machine
- [x] Move existing clouds and their projected-shadow frame continuously with wind; rebuild placement only on cell crossings, relevant edits, quality changes, or explicit coordinate refreshes
- [x] Keep suspended noclip clouds canonically anchored across floating-origin intervals instead of snapping the entire cached patch back to the player
- [x] Keep installed waterdrop unions visible across event-driven supersession and replace them atomically when the next worker result is ready
- [x] Use complete merged-group bounds for conventional-shadow eligibility and projected-shadow coverage
- [x] Darken massive storm undersides from merged optical thickness, storm profile, and precipitation instead of profile alone
- [~] Complete the in-editor ground, cloud-band, high-altitude, and far-handoff visual-tuning gate
- [ ] Add localized rain emitters, rain occlusion, wetness, audio, and optional storm effects

## Ring Lighting

### [x] HDR sunlight calibration

- [x] Replace linear highlight clipping with matched AgX output in the main and scaled far viewports
- [x] Limit sky ambient contribution to `0.35` so explicit day/night ambient controls remain effective
- [x] Separate analytic atmospheric extinction and in-scattered radiance from sun-lit surface albedo
- [x] Reduce secondary day-fog scatter, cloud readability emission, hull emission floor, and rough-terrain specular
- [x] Audit procedural vertex-color space and retain the existing linear-calibrated palette with opt-in sRGB conversion for future imports
- [x] Preserve the `4.0` sun energy until calibrated ground, 26 km, and scaled-space captures show a direct-light reduction is necessary

### [ ] Phase E — Reflected Ring Illumination

- [ ] Add `RinglightController` driven by spatial day factor
- [ ] Add editor-tweakable ambient ringshine response
- [ ] Add weak shadowless fill from the visible illuminated ring
- [ ] Add opposite-ring emissive glow and atmospheric haze
- [ ] Keep sunlight, ringshine, ambient, local lights, and future planet light independent
- [ ] Add live-refresh tuning and night/twilight validation
- [ ] Pass Phase E lighting test before Phase F

### [ ] Phase F — Long-Range Sun Occlusion

- [ ] Add `RingSunOcclusionController` and live editor controls
- [x] Reuse hierarchical min/max terrain data for canonical sun-ray queries
- [ ] Generate a camera-centered coarse direct-sun visibility mask
- [x] Generate one bounded whole-ring R8 terrain mask as the current terrain-only alternative
- [x] Search distant unloaded terrain toward the sun for occluders
- [x] Handle ring curvature, seam wrapping, width bounds, and floating origin
- [x] Affect direct sunlight only; preserve ambient, ringshine, local, and emissive light
- [ ] Share far-shadow reception across terrain, characters, structures, vegetation, and vehicles
- [ ] Add penumbra, temporal stabilization, edit invalidation, and debug views
- [~] Verify distant mountains cast stable kilometer-scale shadows without degrading near cascades

## Current Gate

- [ ] Finish the Phase B runtime checklist and the Phase C in-editor performance gate before beginning Phase D.
- [~] Keep full Phase F gated on Phase E; the terrain/ocean-only macro-shadow prototype is active for the present sunlight calibration.
