# Why the old voxel terrain looked smoother

Investigated 2026-09-29 alongside the second terrain CPU optimization pass. The user reported that the editable terrain around the player looked like adjoining squares. A saved v3 playable capture also shows small square lighting patches, so this investigation concerns surface shading and geometry, not an assumed gap between streamed chunks.

## Old near-player path

The previous editable backend was [SmoothTerrainGrid](../scripts/Terrain/Editable/SmoothTerrainGrid.cs), rather than the legacy cubic voxel mesher. Its [regular-cell mesher](../scripts/Terrain/Editable/SmoothTerrainMesher.cs) uses Transvoxel tables to extract an isosurface from signed density values. Surface vertices sit at interpolated zero crossings, using `t = da / (da - db)`, and adjacent cells share edge vertices.

The mesher computes a central density gradient and interpolates it at each generated vertex. These smooth mesh normals are then interpolated by rasterization. The [snapshot contract](../scripts/Terrain/Editable/SmoothTerrainContracts.cs) includes a two-sample halo specifically to support normal calculation at chunk edges: 32 cells plus boundary and halo samples form a 37-by-37-by-37 density array.

The near-player configuration in [VoxelRingIntegrationTest.tscn](../scenes/VoxelRingIntegrationTest.tscn) uses the 1 m fine lattice. Hot patches split a 32 m parent into eight 16-cell pieces for faster edits; they do not increase resolution. Coarser representations and Transvoxel transition cells handle the surrounding area.

[voxel_ring_terrain.gdshader](../materials/voxel_ring_terrain.gdshader) consumes the interpolated mesh `NORMAL` and interpolated `COLOR` biome weights. Its triplanar textures follow those smooth normals. It does not replace the normal with one constant lighting direction per horizontal grid cell.

## Current Terrain3D path

[RingTerrain3DSurface](../scripts/Terrain/Surface/RingTerrain3DSurface.cs) uploads a 2 m RF height lattice. Native rendering, curved collision, saved edits, and CPU queries share that surface definition. Terrain3D uses a regular heightmap grid and coarser surrounding mesh levels; its [architecture documentation](https://terrain3d.readthedocs.io/en/stable/docs/system_architecture.html) describes that arrangement.

Our previous custom [ring shader](../materials/terrain3d_ring_surface.gdshader) reconstructed a forward-difference normal from three height texels at `floor(UV)`. That normal stayed constant throughout a 2 m cell. Biome weights and the height used for snow/triplanar coordinates were also read from the nearest texel. These choices expose the grid through lighting and color even when neighboring height samples connect correctly. They differ from the old smooth voxel material and are not a requirement of Terrain3D; the plugin's [shader documentation](https://terrain3d.readthedocs.io/en/stable/docs/shader_design.html) describes interpolated material and normal reconstruction.

The two geometry representations also differ. A heightmap has one height per horizontal sample. The old density surface can move crossing vertices in three dimensions and represent overhangs or caves. Smooth shading can improve a heightmap's appearance without reproducing that geometric freedom.

## Presentation change

The `Smooth terrain shading` control in the grave-key Terrain3D menu selects a shader-only reconstruction:

- Sample the central-difference gradients at the four surrounding lattice vertices, using region-aware height lookups.
- Bilinearly blend those gradients at the fragment position and normalize the resulting lighting normal.
- Blend the four biome-weight texels and the height used for fragment material coordinates.
- Retain the old shading path when disabled for a controlled comparison.

This transfers the old renderer's smooth-gradient and smooth-color principles to the heightfield. It does not regenerate terrain, change RF heights, alter collision, change the saved recipe, or smooth away mountain geometry. The option is stored with existing surface appearance preferences and follows active, staged, idle, and rebuilt material owners.

There is a GPU tradeoff: the smooth path uses twelve height fetches instead of three and, when biome textures are active, four weight fetches instead of one. These are texture reads over existing data; they add no terrain-generation jobs or height samples. Visual and viewport timing results must be measured separately from CPU generation time.

## Geometry alternatives

| Approach | What it changes | Main cost or constraint |
| --- | --- | --- |
| Smooth normal/material reconstruction | Lighting and color continuity over the current surface | More fragment shader texture reads; silhouettes remain on the existing mesh. |
| 1 m authoritative height samples | Finer edits, silhouettes, and collision | Approximately four times as many samples for the same horizontal area; requires a deliberately versioned lattice/save contract. |
| Render-only subdivision/interpolation | Finer visual geometry | Must define an acceptable render-versus-collision error and avoid seams during LOD changes. It cannot recover missing terrain data automatically. |
| Smooth voxel geometry near the player | Density-based edits, arbitrary surface crossings, overhangs | Requires a deliberate hybrid ownership, transition, collision, and edit-persistence design. The current runtime surface contract explicitly excludes density/caves. |

Start with smooth shading because it directly addresses the observed cell lighting and preserves the existing CPU sampling budget. If remaining polygon silhouettes or edit shapes are the problem after visual inspection, measure a smaller 1 m near-player region before changing the whole streamed window or restoring volumetric meshing.

## Validation tools

[terrain_surface_shading_test.gd](../tools/terrain_surface_shading_test.gd) toggles the actual live control, checks material state and unchanged committed height/collision revisions, and reopens isolated preferences. With `--capture`, it uses a fixed camera and lighting for the old/smooth/old sequence and records viewport render timings. [SurfaceTerrainSettingsSmoke.cs](../tools/SurfaceTerrainSettingsSmoke.cs) covers default, persisted false, and reset behavior.

```text
dotnet build RingworldTechDemo.csproj --no-restore -v:minimal
godot --headless --path . --script res://tools/terrain_surface_shading_test.gd
godot --path . --resolution 1920x1080 --script res://tools/terrain_surface_shading_test.gd -- --capture
```

The tests use fixtures under `.godot/terrain-generation-tests/`; they do not modify the player's saved terrain or appearance preferences. Human visual acceptance and the full RTX 4070 1080p frame-time target remain separate from test assertions.

## Measured validation, 2026-09-29

- C# build: zero warnings/errors.
- Surface preference defaults, save/load of disabled shading, and reset: passed.
- Headless UI/state/persistence regression: passed, with five renderer-uniform checks explicitly unavailable in headless mode.
- Vulkan Forward+ regression on the RTX A1000 laptop GPU: 18 checks passed, including all five actual material-uniform assertions and unchanged committed height/render/query/collision revisions.
- The same fixed mountain camera produced [old cell shading](benchmarks/terrain-surface-shading-flat.png) and [smooth shading](benchmarks/terrain-surface-shading-smooth.png). Inspection shows the abrupt square lighting boundaries removed in this view, while the outline and terrain geometry remain the same. This capture uses the plain slope material; textured biome transitions and full gameplay travel still need visual acceptance.

The [portable GPU report](benchmarks/terrain-surface-shading-2026-09-29.json) records an off/on/off sequence with 60 warmup and 120 measured frames per state. Viewport GPU medians were 2.841 / 4.561 / 4.565 ms. The repeated off state drifted substantially, so this run cannot isolate the incremental shader cost. Do not interpret those numbers as a reliable overhead estimate or a 60 FPS result. A 1920-by-1080 window was requested, but the captured client area was 1920 by 1061; this is not exact 1080p acceptance.

Existing certificate-store and deprecated interpolation diagnostics appeared during these runs. The GPU run also logged shader-cache file-write failures; shaders nevertheless compiled/rendered and the native uniform assertions passed. Those environment diagnostics were not repaired by this change.

- [x] Shader compilation, material updates, persistence, and unchanged terrain state.
- [x] Fixed-view capture inspected for the reported square lighting.
- [ ] User visual acceptance in the runtime-generation gameplay world, including edits and streamed boundaries.
- [ ] Controlled full-environment GPU overhead and RTX 4070 1080p frame-time acceptance.
