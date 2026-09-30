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
