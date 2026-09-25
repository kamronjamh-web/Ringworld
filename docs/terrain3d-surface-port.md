# Certified Terrain3D surface integration

The Certified Ring can now run the main gameplay scene with Terrain3D owning the nearby editable surface and collision. New Certified worlds default to **Terrain3D surface**. The world launcher's advanced generation menu exposes the terrain backend; older world settings without a backend marker retain smooth terrain. Mega Ring continues to use its existing backend until a separate validation pass.

The integration keeps the canonical ring coordinates, procedural terrain sampler, distant ring rendering, ocean, weather, and normal player controller. It replaces the near smooth/voxel presentation with Terrain3D. Caves and volumetric construction are unavailable in this mode; old excavation/construction journals are preserved rather than converted into surface heights.

## Play and edit

Create or configure a Certified world in the normal launcher, open the advanced generation controls, and select **Terrain3D surface (Certified only)**. Left/right mouse lower/raise the surface under the cursor. WASD movement, jumping, swimming, and the existing flight controls remain in the main controller. The default Certified spawn is underwater; swimming there is expected.

Press the grave key and use the **Terrain3D** tab to change brush radius, brush height step, mesh detail levels, and movement speed. These preferences persist through the existing surface tuning resource. The full tuning drawer retains its other gameplay tabs. The surface save button commits surface edits and the normal world-state save.

## Coordinates, rendering, and persistence

`RingTerrain3DGameplay` connects the main scene to `RingSurfaceHeightField` and `RingTerrain3DSurface`. Sparse absolute heights use a fixed 2 m canonical lattice on top of `RingTerrainSampler.GetSmoothSurfaceHeight() + 0.5m`. Player spawn settling, altitude queries, movement collision, and cursor editing use this surface. Swimming uses the same physical collision path.

The renderer uploads a bounded 3 by 3 region set, with 128 samples per region at 2 m spacing. Its 768 m window recreates when travel exceeds 128 m from the anchor; curved collision spans 192 m either side of the anchor. A shader transforms the native planar Terrain3D mesh into the ring frame, and the custom collider uses the same sampled heights and transform. Terrain3D's planar native collision stays disabled. The gameplay frame reverses its local across axis to match the existing ring coordinate system without reflecting the physics body.

The Windows x64 addon includes a small native culling patch. Upstream Terrain3D expands its mesh bounds vertically, which can hide valid terrain after the shader rotates it into the ring frame. The patch also applies the bounded `cull_margin` to clipmap instances on all axes. The margin follows the local surface window and floating origin, not the ring radius. Source pins, the patch, toolchain details, and rebuild instructions live in [Terrain3D provenance](../third_party/Terrain3D/PROVENANCE.md). Other platforms still contain upstream binaries and need the same patch rebuilt and tested before using this backend.

The nearby surface uses the game's biome texture weights and triplanar textures. Terrain3D hands presentation to the existing proxy stack at 84 m, the midpoint of the 64–104 m transition, using the same explorer position for both distance tests. Proxy exclusion is enabled only when the near surface is ready. Sparse edits are copied into immutable integer-height snapshots for existing LOD consumers; full shoreline and distant-shadow agreement still needs validation.

| Data | Location in an active world slot |
| --- | --- |
| Surface height edits | `surface_terrain3d_edits_v1.json` |
| Terrain3D player/world state | `world_state_terrain3d_v1.json` |
| Existing backend state | `world_state.json` and existing voxel/smooth journals, preserved separately |

Height journals are validated against the terrain definition, dimensions, and lattice before restoration. World state carries a backend marker. Rejected or malformed surface/world saves block surface editing and overwrites, including during scene teardown. The journal uses temporary-file replacement; the surface journal and world-state file are separate commits, not a cross-file transaction. Without an active slot, gameplay surface edits go under `user://terrain3d_surface_gameplay/<terrain-definition-hash>/`.

## Isolated prototype

`Terrain3DSurfacePilot.tscn` is an isolated, playable proof of the Terrain3D surface path. It uses the same deterministic `RingTerrainSampler` baseline as the Certified Ring and stores only sparse, absolute surface-height overrides on a fixed 2 m canonical lattice. It does not load, alter, or replace voxel deltas, smooth-terrain journals, caves, water, weather, or the production scene.

The visible window is a bounded 3 by 3 Terrain3D region set (128 samples per region at 2 m spacing, 768 m across). The local frame is tangent to the curved Certified Ring; the surface bridge recreates the bounded window and matching curved collision when the player crosses the 128 m recenter threshold. Collision covers 192 m in each direction from the local origin. A material override bends the native Terrain3D mesh, while collision uses the same height samples and ring transform. Terrain3D's planar native collider is disabled. The prototype intentionally does not render a distant ring, ocean, weather, or caves.

Open it from the grave-key live tuning drawer's Terrain3D tab while playing a Certified world with the smooth terrain backend, or run `scenes/Terrain3DSurfacePilot.tscn` directly with F6. Launching from the main game completes its save before changing scenes and passes the current terrain settings and location. WASD walks, Shift sprints, Space jumps, F toggles inspection flight, Q/E move vertically in flight, and left/right mouse lower/raise the terrain under the crosshair. The same drawer controls brush radius, brush step, mesh detail levels, and movement speed.

Edits save atomically to `surface_terrain3d_edits_v1.json` in the active world directory when a world slot is active. Without a slot, they go under `user://terrain3d_surface_pilot/<terrain-definition-hash>/`. The pilot never reads or writes the voxel or smooth journals. A corrupt or mismatched surface save is reported in the HUD and automatic overwrites are disabled, so no edits are silently discarded.

`tools/terrain3d_surface_pilot_smoke.gd` starts the real isolated scene, verifies the Terrain3D native height and collider through a raise/lower edit, checks player ground contact, fractional and automatic rebasing, and reloads an edited collider from a fresh `.godot` fixture. Run it with Godot Mono using `--script res://tools/terrain3d_surface_pilot_smoke.gd`. Adding `-- --capture` on a GPU run writes terrain and tuning-drawer PNGs into that fixture. Captures are skipped for headless runs. Separate field, frame, settings, and native-addon scripts cover seam wrapping, large coordinates, save validation, preference persistence, and native height import.

## Validation and remaining work

`tools/terrain3d_gameplay_smoke.gd` drives the real launcher's Certified configuration and main gameplay scene using isolated `.godot` save fixtures. It checks backend selection, swimming, edited ground contact, fractional coordinates through recentering, a nonzero edit and player state after reload, the full tuning tabs, and corrupt-state protection. GPU runs with `-- --capture` also write gameplay and tuning PNGs. Optional decoration/weather processing is disabled in this smoke; it is not an all-features performance benchmark. `tools/terrain3d_launcher_smoke.gd` checks saved backend selection and the Mega restriction without opening user save slots.

`tools/terrain3d_ring_visibility_gpu_test.gd` renders an isolated surface at four ring orientations and checks that the ground covers both sides of the viewport. This test reproduces the missing terrain with the stock Windows addon and passes with the culling patch. It requires a GPU renderer. `tools/ring_surface_lod_bridge_test.gd` checks immutable edit snapshots, seam wrapping, revision changes, and clearing edits against the real grid and terrain sampler.

This stage does not establish a performance gain. Region generation, upload, and collider rebuilds are synchronous; long travel and rapid editing need profiling and asynchronous work before production use.

1. Validate and complete surface edit propagation through water shorelines, vegetation, distant terrain, and far-ring heightmap shadows; match near-surface lighting to those representations.
2. Add queued region generation, incremental edit uploads, and collider updates; measure frame times and memory while travelling and editing.
3. Extend traversal and visual checks across biomes, structural walls, world seams, and quality presets with all environment systems active.
4. Enable Mega Ring using the shared canonical-coordinate design after its own precision, streaming, and persistence validation.

Caves remain a later system. This height field represents one surface height per canonical location; future underground geometry and surface entrances will require their own representation and integration.
