# Regional forests implementation checklist

Current milestone: one Certified-seed playable forest now uses the production
canonical vegetation tiles, imported catalog, authored LOD pools, and nearby
trunk colliders. Near-to-far tuning and performance acceptance remain open.

## Agreed direction

- A regional mix of open woodland and dense forest, with clearings and soft edges.
- Maple is the most prominent tree overall; fir and pine have regional pockets.
- Forest layout comes from world seed, canonical coordinates, and stable terrain
  climate. Current storms, camera movement, and graphics settings must not move it.
- Target: at least 60 FPS at 2560 x 1440 on Very High on the initial RTX 4070
  target machine, with higher frame rates possible at reduced quality. This is a
  full-game target, not a measured result of the sampler or asset-preview scenes.

## 1. Regional map and debug view

- [x] Add an allocation-free, deterministic forest sampler independent of rendering.
- [x] Combine kilometer-scale cover, smaller woodland variation, and clearings.
- [x] Respect terrain climate, water, ring boundaries, and slope suitability.
- [x] Give maple the overall preference while allowing fir/pine-dominant pockets.
- [x] Verify ring-seam continuity, including non-divisible circumferences and
  very large canonical coordinates.
- [x] Add a bounded, on-demand debug map using the existing terrain sampler.
- [x] Inspect habitat, density, and species weights without loading a saved world.
- [x] Verify repeatability, map refresh behavior, and a rendered preview.

The preview's density is a normalized cover input, not a tree-count promise.
Production placement additionally applies local slope/water exclusion and
humidity-aware local spacing before canonical records are accepted.

### Try this milestone

Open `scenes/ForestRegionPreview.tscn` and press **F6** (Run Current Scene).

- Use **Habitat / Density / Species**, or keys **1 / 2 / 3**, to compare maps.
- **Left / Right** moves around the ring; **Across - / Across +** moves across it.
- **Zoom in / Zoom out** changes the sampled extent, not tree density.
- Change **Seed**, then press **Generate**. Returning to the same seed and
  coordinates reproduces the same map.
- Hover over the map for canonical coordinates, terrain height, biome/water,
  cover density, and maple/fir/pine weights. Colors have an on-screen legend.

The initial reference is the Certified preset, seed 1337, around 0 km, across
159 km, with a 128 x 96 km window. The 256 x 192 raster samples every 500 m at
this zoom: its pixels are not terrain chunks or tree placement cells. Smaller
views resolve the forest fields more clearly. It uses the current terrain
generator and duplicated default preset settings, not a saved slot's edits or
custom settings. Water/coast shapes are existing terrain, not new terrain work.

Sampling runs only on a request, with at most 192 samples and approximately a
2 ms sampling budget per frame. A single sample, setup, and final texture upload
can exceed that soft budget. The previous completed map remains visible until
the newest request is complete. Switching modes recolors cached samples without
resampling terrain. Map generation is normally a few seconds, depending on the
frame rate. None of this preview work runs in the normal game.

### Verification

- `dotnet build RingworldTechDemo.csproj --no-restore`
- Godot headless: `--script res://tools/forest_region_regression_test.gd`
- Godot headless: `--script res://tools/forest_region_preview_test.gd`
- Godot Vulkan/Forward+: `--script res://tools/forest_region_preview_test.gd -- --capture`
- Existing-tree regression: `res://scenes/TreeRenderingRegressionSmoke.tscn`

Run the Godot commands with `--path .` from the project root. Captures are written
only to the ignored `.godot/forest_regions/` folder. The core tests cover seeded
repeatability, threaded sampling, ring seams, exclusions, species pockets,
habitat coverage, and zero hot-path allocation. The preview tests cover cached
recoloring, deterministic real-terrain results, superseded requests, and teardown
mid-build. GPU checks also exercise every pan/zoom control, seed regeneration,
and the hover readout. The rendered maps were inspected on the RTX 4070. Build
and these regressions pass. The sandboxed Godot runs still print the environment's
existing root-certificate-store warning; no forest test failed. These checks do
not establish the full-game 60 FPS target.

## 2. One representative playable forest

- [x] Connect the regional sampler to the existing persistent vegetation tiles.
- [x] Keep stable tree IDs, species, variant, scale, and yaw in the canonical
  placement record; changing LOD must not change tree species.
- [x] Version vegetation cache identity for forest algorithm/settings changes;
  do not invalidate terrain data or leave old and new vegetation overlapping.
- [x] Load imported maple, fir, and pine meshes/materials by authored LOD and
  preserve their composed transforms and appropriate root embedding.
- [x] Adapt the maple bark mixed-UV material and foliage cutouts for production.
- [x] Integrate wind, sun/night/eclipse response, cloud shadows, and tree shadows
  using the existing shared lighting/weather state.
- [x] Add or verify nearby trunk collisions independently of render quality.
- [x] Provide a grounded route through one dense grove, open woodland, and a clearing.
- [ ] Verify slope/water exclusion and vegetation invalidation after terrain edits.

### Try the playable forest

Open `scenes/PlayableForest.tscn` and press **F6**. This disposable entry
configures a private Certified Ring instance (seed 1337), removes its save
manager, and disables its vegetation disk cache; it does not load or write a
normal world slot. It starts in a grounded clearing at canonical
`(186672, 140520)`, faces the short route through woodland `(186336, 140760)`
to the dense grove `(186000, 141000)`. Use WASD, Shift, and Space normally;
V toggles flight.

`forest_route_discovery_test.gd` independently sampled the Certified terrain
without loading a world and found max regional density 0.90484 at `(186000,
141000)`, Forest biome, temperature 0.79184, moisture 0.75605. Its bounded
coarse scan found 199 dense, 2488 open-woodland, and 1103 clearing samples.

### Playable-forest verification

- `godot --headless --path . --script res://tools/forest_route_discovery_test.gd`
- `godot --headless --path . --script res://tools/playable_forest_runtime_test.gd`
- `godot --headless --path . --script res://tools/imported_tree_catalog_smoke.gd`
- `godot --headless --path . --script res://tools/imported_forest_placement_regression_test.gd`
- `godot --headless --path . --script res://tools/imported_forest_edit_regression_test.gd`
- Forward+: `godot --path . --script res://tools/imported_tree_gpu_regression.gd`
- Forward+: `godot --path . --script res://tools/playable_forest_runtime_test.gd -- --capture`

The headless runtime fixture verifies a grounded route, visible imported
MultiMesh instances, canonical records, authored LOD pools, and an actual
physics ray hit on an imported trunk. Its initial Certified run passed with 30
catalog variants, four authored LOD pools, 35 canonical records, and 11 nearby
trunk colliders. The Forward+ fixture writes clearing, woodland, and grove
viewport captures under `.godot/forest_playable/`. A controlled full-scene run
on the RTX A1000 laptop GPU exited successfully after all three captures and
the trunk-ray check. Each stop requires five grounded physics observations
and visible imported instances. The tour warp restores three metres of spawn
clearance above the sampled surface before settling onto smooth terrain.
At 1600 x 900, Medium, the final run reported 30.0 FPS at the grove
and a worst sampled frame interval of 57.480 ms, including traversal streaming. These
observations are neither isolated tree cost nor the RTX 4070 2560 x 1440
certification benchmark; the 60 FPS target remains unverified.

The edit regression runs the actual worker tile builder with a disposable 3 x 3
surface snapshot around a selected root. Lowering or raising the root columns
removes that canonical ID instead of relocating it; no override restores exact
IDs, species, variants, and transforms. It does not replace the remaining
full-runtime terrain-edit invalidation acceptance gate.

Placement regression also verifies repeatability, neighboring tile order,
humidity-dependent spacing across tile boundaries, cache round trips, and
rejection of incompatible cache versions or species. The GPU material fixture
passes geometric night and eclipse gates, local/moon fill, cloud attenuation,
and wind-deformed cutout shadows. These focused checks do not replace the
full-game visual and performance gates below.

## 3. Scalable near-to-far presentation

- [x] Batch each species/material/LOD into bounded spatial MultiMesh tiles.
- [ ] Use authored mesh LODs nearby and billboards farther away, with stable
  transitions and quality-dependent render budgets.
- [ ] Keep expensive tree shadow casters local; validate foliage shadow cost
  and reduce distant shadow detail separately from forest coverage.
- [ ] Represent distant forests with low-cost canopy coverage rather than
  attempting to draw individual trees across the ring.
- [x] Keep collisions and the underlying forest layout identical across presets;
  quality may change visible detail, distance, shadows, and streaming budgets.
- [ ] Verify slow travel, fast flight, abrupt turns, wrap seams, and origin shifts
  without duplicates, stale tiles, frame spikes, or runaway memory use.

## 4. Performance and visual acceptance

- [ ] Record a repeatable full-game baseline with trees off/on and clouds/shadows
  on, using the same camera, weather, resolution, and graphics preset.
- [ ] Measure CPU/GPU frame time, draw calls, visible instances, upload time,
  VRAM/cache memory, and traversal hitching, not only average FPS.
- [ ] Test the worst nearby dense canopy in daylight, heavy storms, night, and
  total eclipse, plus distant forests from flight and space.
- [ ] Meet the 16.67 ms total frame budget for 60 FPS at the target settings;
  tune forest sub-budgets from profiling instead of assuming instancing is free.
- [ ] Verify lower presets reduce measured rendering cost while preserving
  deterministic forest identity and nearby gameplay collision.

## Existing foundation retained

The production decoration layer already has canonical vegetation tiles,
background generation, bounded uploads, deterministic ranks, cache limits,
frustum-aware streaming, and tree lighting hooks. This project extends that path;
it should not introduce a second overlapping tree/terrain streamer.

The imported packs can still be inspected separately in
`scenes/TreeAssetPreview.tscn`. Import details and licensing remain in
`resources/trees/lolipop/README.md` and `THIRD_PARTY_ASSETS.md`.
