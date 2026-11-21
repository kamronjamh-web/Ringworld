# Proxy terrain coverage culling

The proxy LOD chain can now omit terrain triangles already covered by a ready finer proxy. This supplements the existing coverage-atlas shader discard and whole-sector visibility checks.

## Runtime behavior

- Enabled by default. Open the grave-key tuning drawer, then **LODs → Proxy coverage culling → Cull triangles covered by finer terrain**. The preference is saved separately in `user://gameplay_proxy_coverage_culling_defaults.tres`.
- A changed coverage mask immediately restores the original complete mesh. The shader atlas remains authoritative while an optimized replacement waits in the queue.
- At most one queued candidate is processed per frame across all proxy levels. Unchanged coverage does not rebuild meshes. Inactive and fast-travel-suspended levels skip optimization work.
- Filtering removes only regular terrain-grid triangles proven covered. Skirts and other unrecognized triangles remain, ocean indices remain unchanged, and the opaque shadow caster retains the original mesh.
- The default minimum saving is 64 triangles. Retained CPU sources and estimated replacement mesh allocations share a global 64 MiB reservation limit. If admission fails, the sector continues using the original shader-based path.
- Disabling restores full meshes and releases optimized replacements. Bounded source data stays available so re-enabling can optimize resident sectors without terrain regeneration.
- Sector recycling and scene exit release the retained data and reservation accounting.

The nearby Terrain3D radial handoff still uses its existing shader clipping. This optimization targets partially covered proxy sectors farther along the LOD chain; it does not change terrain generation, collision, edit data, or the native Terrain3D clipmap.

## Validation

Validated September 30, 2026:

- `.NET` build: zero warnings and errors.
- `tools/proxy_partial_coverage_test.gd`: production mesh lifecycle, source retention while disabled, enable/disable, coverage restoration, full/uncovered masks, frame budget, warm sectors, recycling, water and shadow preservation, memory accounting.
- Dense fixture: 8,192 grid triangles, 2,048 removed for quarter coverage; 6,144 grid triangles plus both special skirt/non-grid triangles remain. Ocean indices are unchanged.
- `tools/proxy_coverage_culling_settings_smoke.gd`: isolated preference save/reload.
- Existing proxy seam geometry, ocean replacement ownership, terrain LOD shadow coverage, and radial morph contracts passed.

These tests establish geometry reduction and lifecycle correctness. They are not an FPS benchmark or human visual acceptance. Compare the saved toggle at the same camera position after the rebuild queue settles. The tuning menu reports queued work, currently removed triangles, cumulative removals, retained source bytes, and estimated filtered-mesh bytes. Shadow geometry savings and reduced draw-call counts are not claimed.

## Resident coverage: radial ownership between proxy levels (2026-11-21)

**Problem (play test 2026-11-20, "random different coloured squares").** The strict coverage above certifies a coarse cell only when the finer provider cell (one finer *sector*, 64 x the finer spacing) lies wholly inside the coarse level's inner clip radius, and only when the finer sector's whole visual bound lies inside its own far handoff. The screen-space bands (`RingTerrainLodChainPlanner.ScreenSpaceBand`) put that radius at about 53-67 x the finer spacing, so almost no cell ever certifies: on the live slot 3 world every level from LOD6 up showed 0 covered cells, at 868 m altitude and on the ground. The near-fade discard is off for every level but the one next to Terrain3D (readiness-gated, to avoid holes), so each coarse level drew its whole window under the finer level and was hidden only by its radial underlay offset (2-64 m). On rugged ground the coarse facets poke through; in `ground.surface` worlds they shade differently (per-level slope, no C64 above 64 m spacing), so they read as flat, differently coloured patches with straight lattice edges. With the diagnostic level colours, the finest active level owned only 23-26 % of the lower half of the frame at 868 m above the mountain at arc 18,253.8 km.

**Rule.** A coarse level must not draw where its finer level does. The finer level draws every fragment closer than its far handoff (`(fade_out_start + fade_out_end) / 2`) in each sector it has installed and visible. The coordinator gives the coarse level the finer level's band as its near band, so the coarse level's near handoff is the same distance. So:

- **Resident cell.** A coarse provider cell (one finer sector) is *resident* when every finer sector under it is active, installed, not warm and visible. A finer sector that is hidden because its own finer level strictly covers it also counts, and so does a strictly inner-excluded finer sector. The finer level must be streaming and not transit-hidden. A cell that touches the Terrain3D discard disk of the level next to Terrain3D is never resident: mesh residency cannot prove that disk drawn.
- **Atlas.** The coverage atlas (R8) holds 255 for strict covered (unchanged), 64 for resident and 0 otherwise. Every existing reader tests `> 0.5`: the vertex collapse, the opaque shadow caster, the ocean and the CPU partial-coverage culling (which reads `CoveredCells`, not the atlas). So all of them ignore 64 and keep their behaviour.
- **Shader.** `ring_terrain_proxy_ground.gdshader` and `ring_terrain_proxy.gdshader` discard a fragment when the atlas is above 0.5 (as before), or when it is above 0.125 and the fragment is closer than the level's near handoff. Ownership is then exact per pixel, with the same distance and formula the finer level uses for its far discard. The finer level geomorphs onto its parent by that distance, so the two surfaces meet at the boundary.
- **Freshness.** Resident bits are written live whenever a tile is installed (`InstallCoverageMask`). The finer level republishes them, through the same dependents the ocean replacement uses, in the cases below. Notifications in one frame coalesce into one refresh per level, run from `RenderingServer.FramePreDraw` (after every `_Process`, before the frame draws). Installs, departures and warm reactivations name the finer sectors they changed, so the refresh rewrites only the tiles over those sectors. The other events rewrite every tile. The texels live in a byte mirror of the R8 atlas, uploaded once per refresh that changed one. The cases:
  - after an upload batch;
  - after departures (sectors go warm and hidden in the same call);
  - after warm reactivation;
  - when it becomes visible again after a transit hide.

  The bits are cleared at once (not deferred) when the finer level is deactivated, transit-hidden or freed, when the provider changes, and when the safety pass drops an inner exclusion. A stale bit could only keep the coarse level drawn (the old overlap), never open a hole, except in the same-frame retirement case. That case is covered by the synchronous clear.
- **A/B.** `RingTerrainProxySector.EnableResidentCoverage` (per level, default on, not a menu row) clears the bits, for tests and the perf bench.
- **Cost.** `RingTerrainProxySector.TakeResidencyRefreshStats()` returns refresh calls, main-thread ms and atlas uploads per frame (p50 / p95 / max) since the last take; `RINGWORLD_RESIDENCY_STATS=1` prints a `RESIDENCY_STATS` line every 5 s.

Tests: `proxy_level_ownership_gpu_test` (diagnostic-colour ownership at a mountain pose, plus a hole count against the old behaviour and against a no-proxy frame, at rest and during an 11.1 km/s fly-in), the proxy coverage contract tests, and `terrain_altitude_lod_flight_test`.
