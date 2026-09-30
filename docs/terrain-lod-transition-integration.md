# Terrain LOD transition integration

Implemented September 30, 2026. All original terrain tiers remain available, with their legacy quality-profile and saved-setting identities preserved.

## Changes

- Proxy geomorph uses the next active tier's actual spacing. Disabled bridge tiers and suspended near tiers are skipped when selecting parents and transition bands.
- Added 512 m, 1024 m, 4096 m, and 16384 m bridge tiers around the existing 256 m, 2048 m, 8192 m, and 32768 m tiers.
- The mega-world configuration renders distant terrain through `RingScaledFarViewport`. Its regional mesh bands receive the bridge tiers too; duplicate absolute-world far proxies stay disabled in this mode.
- Scaled regional bands use shared boundary positions and welded normals. Their geometry defines coverage relative to the snapped mesh anchor, avoiding camera-relative distance clipping that could open gaps.
- Far meshes are prepared on workers, uploaded one band per frame into a hidden set, and published together. The previous complete set stays visible until replacement is ready; stale layout builds are rejected.
- The terminal active proxy can morph to an immutable snapshot of the published `RingTerrainGlobalLod` triangles. Sampling uses the actual triangle diagonal and the worker's current coordinate frame. Cache identity includes parent spacing and global topology/height content.
- Terrain3D's default near handoff radius is now 112 m, adjustable from 84 to 128 m. Coverage checks require the whole visual disk inside the uploaded window and retain the independent collision-readiness gate. The 2 m height/collision sampling lattice is unchanged.

## Live controls

Open the grave-key Live Tuning Drawer:

- **LOD / Terrain LOD transitions:** toggle intermediate terrain LOD tiers. This controls both proxy and scaled-far paths and persists through `user://gameplay_lod_transition_defaults.tres`.
- **Surface terrain:** native mesh size, default 48, range 16–64 in steps of 8; Terrain3D handoff radius, default 112 m, range 84–128 m. These follow the existing surface-terrain settings persistence.

Native mesh size is a topology setting, not a distance in meters. Reducing it brings coarser native rings closer to the camera; it needs visual comparison before adopting a smaller default.

## Resource cost and scope

The scaled regional layout grows from approximately 60,334 to 85,457 vertices (about 42%). It retains 96 angular columns and caps radial rows at 256 per band. Stated far-band spacing is a radial sampling target, not square-grid resolution across the entire annulus. Additional tiers improve resolution progression and do not imply lower frame time.

The separate outermost scaled-regional to full-ring backdrop boundary at approximately 7.96 million meters retains its existing behavior. The new published-triangle target concerns `RingTerrainGlobalLod`; it does not establish an exact triangle match for every separate backdrop renderer.

## Validation

Primary-agent checks passed:

- `dotnet build RingworldTechDemo.csproj --no-restore -v:minimal`: zero warnings/errors.
- `tools/surface_terrain_settings_smoke.gd`.
- `tools/terrain_generation_staging_test.gd`: 52 checks.
- `tools/terrain3d_gameplay_stream_test.gd`: 125 checks.
- `tools/lod_transition_settings_smoke_test.gd`.
- `tools/intermediate_lod_topology_test.gd`: legacy indices, active parents, previous active bands, logarithmic bands, exclusive scaled routing.
- `tools/proxy_seam_geometry_contract_test.gd`.
- `scripts/Tools/far_regional_transition_contract.gd`: band layout and shared boundaries.
- `tools/global_terrain_preparation_contract_test.gd`: published snapshot lifecycle, edit identity, real mesh triangles, seam wrapping, coordinate-frame changes.
- `tools/proxy_partial_coverage_test.gd`.
- `tools/ocean_replacement_ownership_contract_test.gd`.
- `tools/lod_radial_morph_contract_test.gd`.
- `tools/terrain_near_lod_parity_gpu_test.gd`: rendered native/proxy receiver parity.
- `scripts/Tools/far_regional_transition_contract.gd -- --rendered`: Forward+ far-material smoke; drawing with regional clipping disabled changed pixels relative to an empty black viewport. This is a material smoke test, not a traversal capture.

Use Godot 4.7.2 Mono with `--path . --script res://<test path>`. CPU contracts run with `--headless`; rendered tests require a rendering device. Windows certificate-store warnings were emitted during tests. The rendered far-material test also emitted a shader-cache file-write error while producing visible pixels and passing. Some scene fixtures emit shutdown resource warnings; a passing contract does not certify leak-free gameplay. The isolated far-band fixture was cleaned up and rerun without ObjectDB/resource leak warnings.

Human inspection of moving terrain transitions and a controlled laptop/desktop frame-time comparison remain outstanding. No FPS improvement is claimed by these checks.
