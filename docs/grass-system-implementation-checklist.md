# Ringworld grass implementation checklist

Status: persistent streaming, editable-surface placement, exclusion masks, dense ribbons, distance LOD, and bounded gameplay budgets are implemented. The grave-key Grass tab now controls coverage, wind, player bending, biome appearance, debug views, and streaming budgets, with saved sparse overrides. A first full-scene grass render comparison is available on the RTX 4070. Interactive artistic approval, active streaming-hitch attribution, compute experiments, and target-hardware certification remain open.

## Project requirements

- Extend `RingStandingGrassLayer` and retain the existing terrain, biome, weather, and cloud-shadow integrations.
- Target the project's Godot.NET SDK 4.7.2 and Forward+ renderer.
- Keep permanent positions and tile identities in canonical around/vertical/across coordinates; upload only local float positions to the GPU.
- Preserve deterministic placement and wind through ring wrapping, floating-origin shifts, reloads, and terrain edits.
- Keep vegetation work bounded on the Arc 140V/16 GB laptop baseline as well as the discrete GPU profile.
- Store disposable reports under `user://generated/`; meadow review captures use the ignored `artifacts/grass_meadow/` directory for direct inspection.

## 1. Persistent streaming foundation — first runnable milestone

- [x] Replace camera-neighborhood regeneration with persistent canonical grass tiles.
- [x] Generate only entering or invalidated tiles; retain unchanged resident tiles.
- [x] Make placement keys wrap at the ring seam and remain independent of scene coordinates.
- [x] Store blade transforms relative to a canonical tile anchor; refresh tile transforms after origin/frame changes without regenerating blades.
- [x] Bound queued work, resident tiles, total instances, and per-frame uploads.
- [x] Reject stale worker results and release samplers/rendering resources safely.
- [x] Invalidate affected tiles after edits, conservatively refreshing when edit history cannot establish the complete changed region.
- [x] Suspend unnecessary streaming during fast travel/high-altitude flight and resume at the current location.
- [x] Expose useful tile, instance, worker, and installation diagnostics.
- [x] Verify stable tiles during movement, deterministic unload/reload, wrap seams, origin shifts, edit invalidation, and bounded streaming.

The existing `StandingGrass` node in `scenes/VoxelRingIntegrationTest.tscn` uses this implementation automatically. Standalone defaults retain legacy clumps, 48 m tiles, a 115 m radius, and an 18,000-instance limit. Gameplay overrides these through `RingGrassQualityProfile` (see below). Upload time budgets stop work between tiles; one tile upload can exceed the budget. Streaming and visibility suspend above 350 m terrain-relative altitude or 180 m/s travel speed.

The implementation uses worker-generated MultiMesh buffers and GPU vertex-shader wind. Smooth terrain placement resolves the authoritative one-metre terrain mesh; the legacy fallback retains its height-column placement. Distance transitions use staggered, complementary per-root selection. Compute generation and hardware performance certification remain later work.

## 2. Authoritative editable surface placement

- [x] Resolve the actual smooth surface and normal, including terrain edits, instead of relying only on rounded height columns.
- [x] Filter by surface material, slope relative to local gravity, biome weights, and water coverage.
- [x] Remove unsupported grass after digging and suppress grass on exposed rock and in cave interiors.
- [x] Capture construction occupancy automatically and invalidate only affected grass tiles on placement/removal.
- [x] Expose canonical exclusion volumes for paths and placed rocks, with regional invalidation.
- [x] Keep terrain sampling in worker-safe snapshots and avoid per-blade physics raycasts during normal frames.
- [x] Verify root/mesh agreement after digging and filling, construction changes, water-plane eligibility, slopes, and cave roof/ceiling rejection.

`RingGrassSurfaceResolver` intersects the same regular-cell triangle tables and integer density samples used by the terrain mesher. Rounded columns only guide its search. It returns the top supported triangle, a geometric normal, and conservative material eligibility; mixed soil/rock triangles do not grow grass. Queries retain at most 4,096 density samples and examine at most 256 vertical cells. Missing support, an invalid search start, or an intersected underside produces no placement. Roots sit 3.5 cm into the resolved surface, with blade alignment and slope checks accounting for the ring's local arc scale.

`SmoothConstructionOverlay.CaptureGrassExclusionSnapshot()` provides cached immutable occupied volumes. `RingStandingGrassLayer.SetExclusionRegions(...)` accepts additional `RingGrassExclusionBox` volumes in canonical metres for authored paths or placed rocks. Around bounds can cross the seam, for example `[circumference - 1, circumference + 1)`. These explicit volumes are supplied by their owning gameplay system; this milestone does not create a path or rock-placement editor. Occupancy has a 10 cm horizontal margin and checks root/blade height, so an elevated roof does not automatically clear grass below it.

Sequential terrain edits retain unrelated resident tiles and valid worker results. Replaced fields, incomplete edit history, and changed exclusion regions invalidate affected work before installation. A new mask revision with unchanged occupancy leaves existing work valid. The terrain field's independent worker contexts are acquired before dispatch and released after completion/cancellation; merged masks are rebuilt only when their inputs change.

## 3. Blade appearance and gameplay response

- [x] Add a reusable procedural segmented blade mesh with pinned roots and tapered tips.
- [x] Add stable per-instance yaw, dimensions, tint, and wind-response variation, including stiffness.
- [x] Add independent, height-scaled resting curves and lean, with normals following the deformation.
- [x] Add rounded blade normals and preserve root-to-tip colors, directional lighting, and cloud shadows.
- [x] Add restrained backlighting, darker roots, and a muted meadow palette.
- [x] Capture and inspect the palette in actual gameplay daylight, night, and a forced strong-wind/rain state.
- [ ] Validate the palette during an actual local eclipse.
- [x] Add inexpensive intermediate geometry, complementary LOD transitions, stable density reduction, and coverage fading to existing terrain.
- [x] Blend distant grass toward the underlying biome's average terrain albedo during the final coverage fade.
- [x] Add conservative tile bounds and deformation margins for the current blade and wind response.
- [x] Add bounded view-dependent widening and update the deformation bounds accordingly.
- [x] Add grass-specific spatial gusts and smaller ripples using periodic canonical phase and the shared simulation clock.
- [x] Add basic player bending with a bounded interaction field.
- [x] Add biome-specific density, height, and palette settings for clearings, forests, and sparse terrain.
- [x] Verify root anchoring, rotated/rebased frames, wind direction/strength, and pause/frozen time.
- [ ] Verify the final appearance through camera rotation, day/night/eclipse lighting, and storm transitions in gameplay.

The interaction field retains eight canonical samples, projects them through the current floating-origin frame, and uses a three-second recovery by default. Bending follows each blade's local tangent plane, keeps roots pinned, and updates the deformation normals. Vertical separation rejects unrelated surfaces; teleports and fast/high travel clear the trail. It requires no per-blade physics queries or placement rebuilds.

Biome density and height enter immutable worker snapshots. Palette changes remain material-only updates. The final color handoff uses cached linear averages of the actual terrain albedo textures, including the terrain's grass tint multiplier; it does not reproduce exact terrain texels, slope blending, or terrain lighting. The production instance payload is transform + biome COLOR + existing custom wind/variation channels. Deformation bounds grow with the tallest blade in each tile (`2 * height + 0.1 m` margin), covering enlarged biome heights and player interaction.

## 4. GPU generation/culling prototype

- [x] Establish a frozen full-scene render comparison for the persistent MultiMesh foundation before choosing work to move to compute.
- [ ] Attribute active streaming costs on a repeatable movement route before choosing a generation/compaction optimization.
- [ ] Prove a small compute-to-MultiMesh path on the main RenderingDevice with no gameplay readback.
- [ ] Validate the installed SDK's instance-buffer and indirect-command APIs, buffer layout, custom bounds, render-thread scheduling, and resource ownership.
- [ ] Upload authoritative terrain/mask tiles only when changed; keep vertex-shader wind independent of per-frame instance regeneration.
- [ ] Adopt compute generation/compaction only where measured results justify it, retaining a working fallback.

RTX 4070 comparisons measured approximately 0.83 ms initially and 0.85 ms with the final shader for visible grass at 1600x900. This supports keeping the current rendering baseline while measuring active streaming costs. It does not establish that CPU generation or compaction needs replacement, or certify other hardware. A compute prototype remains an experiment rather than a prerequisite for the current playable feature.

## 5. Quality settings and certification

- [x] Add a grave-key Grass tuning tab with saved sparse overrides, debounced density changes, live streaming diagnostics, and reset to the active quality profile.
- [x] Connect grass settings to existing hardware/graphics profiles.
- [x] Set initial nearby/intermediate/terrain-only distances; keep individual grass in the main viewport.
- [x] Set separate laptop and discrete budgets for density, range, memory, worker work, and uploads; keep individual blade shadow casting disabled.
- [x] Add debug views for tile bounds/identity, LOD selection, density rank, actual wind bending, and player interaction.
- [ ] Add dedicated frustum/occlusion rejection diagnostics beyond the current bounds and LOD views.
- [ ] Benchmark Arc 140V at 1080p High and RTX 3060/RX 6600 class at 1440p Very High with the rest of gameplay enabled.
- [x] Track approximately 1.5 ms grass GPU time as a measured target rather than a guarantee; the current measurement includes both LOD passes with blade shadows disabled.
- [ ] Record steady frame time, streaming hitches, memory high-water marks, draw calls, and visible triangles.
- [ ] Run the final route through terrain edits, wrap/origin changes, storms, fast flight, and landing.

## Dense meadow benchmark

- [x] Build `scenes/GrassMeadowBenchmark.tscn`: a 32 x 32 m Forward+ scene using the production scheduler, authoritative flat soil fixture, actual biome filtering, and path/rock exclusion volumes. Its isolated daylight/backlight setup supports visual review; it does not reproduce the full gameplay environment.
- [x] Add an explicit target of 96 candidate blades/m² with individual ribbons, deterministic stratified roots, and bounded instance selection. Filtering leaves approximately 44,000 blades in this fixture. A zero target preserves legacy candidate-per-cell placement and three-strip clumps.
- [x] Add automated calm, gust, backlight, and overview captures. Run `godot --path . --rendering-method forward_plus --scene res://scenes/GrassMeadowBenchmark.tscn -- --grass-meadow-capture`; inspect `artifacts/grass_meadow/`.
- [x] Add interactive B/G/C wind presets, P to freeze wind, O for overview, and L for daylight/backlight. Open the scene and press F6 in Godot.
- [ ] Approve the final artistic target through interactive review in gameplay lighting.
- [x] Implement and verify near/intermediate/far LOD transitions and density reduction in gameplay. Keep CPU worker placement plus GPU vertex animation as the baseline.
- [x] Measure an initial grass GPU render delta in the real integration scene.
- [ ] Complete interactive artistic sign-off and active streaming profiling before adopting compute generation/compaction.

Dense workers reuse the resolver's bounded density-sample cache. Each root still intersects authoritative terrain triangles; GPU blade generation and a reusable triangle/terrain-tile cache remain future optimization work. This benchmark does not certify dense streaming over the full ring or the laptop GPU budget.

## Validation record

2026-09-13, live grass tuning menu:

- Build passed with zero warnings/errors. `grass_live_tuning_smoke_test.gd` passed on Forward+ / RTX 4070 and produced `artifacts/grass_lod/grass_menu.png`, which was visually inspected.
- Verified live slider debounce, numeric entry, enable/disable, successful save/reload, explicit overrides surviving graphics changes, untouched settings following the new profile, and reset restoring profile control. Tests use isolated `res://.godot/` preferences.
- Player grass overrides live in `user://gameplay_grass_defaults.tres`; profile values remain the baseline. AGENTS.md now requires relevant new feature settings in the grave-key menu.

2026-09-13, player interaction, biome appearance, and debug views (primary-agent review):

- Final build passed with zero warnings/errors. The expanded `grass_live_tuning_smoke_test.gd` passed: repeated color edits, interaction/biome/debug save and reload, visible control refresh, and reset all work. Inspected `grass_menu.png` and `grass_menu_appearance.png`; color buttons have visible swatches and suitable click targets.
- `grass_interaction_gpu_test.gd` passed on the production shader: 13,477 grass pixels, 1,419 changed upper-blade pixels, and zero changed root pixels. Disabled/expired/vertically separated interactions preserve the baseline; joint frame rotation and translation preserve the result. The CPU field checks include seam travel, slow movement, stationary refresh, decay, and actual floating-origin reprojection.
- The same fixture exercises Off/LOD/density/wind/interaction/tile-color debug modes. Wind and interaction views use actual displacement magnitude. `grass_lod_gpu_test.gd` additionally verifies that debug rendering honors the final cutoff. Streaming checks verify populated bounds, empty bounds, and turning bounds off without regenerating grass.
- `grass_appearance_gpu_test.gd` passed: the default grassland palette preserves near appearance, changing its tint changes rendered pixels, a zero-strength handoff preserves near/far color, and grassland/forest payloads select their respective terrain averages. The fixture uses explicit zero biome weights rather than transparent white.
- Expanded `grass_streaming_contract_smoke.gd` passed on Forward+: biome color changes retain placement/renderers, zero density removes all roots, restoring density deterministically repopulates, and a 1.5x height multiplier scales the actual MultiMesh transforms while retaining root identities. COLOR/custom buffer ordering is checked against the actual installed MultiMesh. `vegetation_wind_gpu_test.gd` passed for both grass prototypes and existing trees after the final shader changes.
- `grass_quality_gameplay_test.gd` passed with 69,053 roots across 26 tiles and 4,002 roots in the focus tile. Inspected daylight, night, 15/5 m/s forced-wind/rain, and elevated-view captures in `artifacts/grass_lod/`. Diagnostics confirm day/night factors of 1/0 and 0/1 and resolved storm wind of (15, 5) m/s. The fixture restores its weather and absolute simulation time; it explicitly records that no local eclipse event was selected.
- This gameplay capture's 120 full-scene samples averaged 41.82 ms with 90.53 ms p99. These include other terrain/weather work and weather transitions; they are not a grass-only measurement, a repeatable movement route, or a performance certification. The existing transparent-viewport SSR warning and RenderingDevice shutdown warnings remain; isolated grass tests exit cleanly.

2026-09-13, final grass render comparison:

- `grass_render_profile.gd` passed on RTX 4070 / Forward+ at 1600x900. It waits for terrain and grass worker/upload queues to settle, disables processing on every gameplay descendant, freezes GPU particles, and measures visible/hidden/visible grass windows with 32 warmup and 108 sampled frames each. Hidden grass retains its buffers. Results are in `artifacts/grass_lod/profile.json` and `artifacts/grass-render-profile-final.log`.
- Mean GPU time: 13.66 ms with grass and 12.81 ms without it, for an estimated **0.85 ms grass render delta**. The two visible windows differ by 0.15 ms. Grass adds 33 draw calls and 545,208 submitted primitives; these are not post-discard visible triangle counts.
- Render CPU delta was -0.09 ms, within measurement variation, and does not show a CPU optimization. Total retained video memory was about 3.14 GB for the whole scene; hiding grass intentionally does not free buffers, so this is not isolated grass allocation or a memory high-water mark.
- This frozen rendering comparison does not measure moving-player simulation, tile generation hitches, terrain streaming, or laptop performance. Those measurements remain explicit checklist items.

### Gameplay LOD budgets (2026-09-13)

`RingGrassQualityProfile` applies when the graphics controller selects a preset. All profiles use 16 m canonical tiles. Density is a candidate target before biome/surface filtering and per-tile trimming, not a guarantee of visible blades per square metre.

| Profile | Candidate blades/m² | Near/far centre (m) | Root cap | Workers | Tile uploads/frame | Upload budget |
| --- | ---: | --- | ---: | ---: | ---: | ---: |
| Laptop baseline | 8 | 12 / 28 | 73,500 | 1 | 1 | 1.5 ms |
| Desktop Low | 4 | 12 / 28 | 73,500 | 1 | 1 | 1.5 ms |
| Medium | 8 | 16 / 32 | 147,000 | 2 | 2 | 2 ms |
| High | 24 | 18 / 32 | 245,000 | 3 | 3 | 3 ms |
| Very High | 32 | 22 / 32 | 245,000 | 4 | 3 | 4 ms |
| Ultra Video | 48 | 28 / 48 | 1,487,000 | 4 | 4 | 5 ms |
| Cinematic 5090 | 96 | 32 / 64 | 4,154,000 | 6 | 6 | 7 ms |

Intermediate density falls to 25% of the retained root set. The near ribbon has four segments; the intermediate ribbon has two. A stable per-root threshold selects exactly one mesh during handoff and progressively removes roots at the far edge. This staggered coverage selection is accompanied by the optional biome-average color handoff. Intermediate widening preserves root vertices and caps its multiplier at 1.35 by default. Shared canonical wind and cloud shadows remain active; blade shadow casting stays off.

Both passes retain separate GPU instance buffers (80 bytes/root each), plus one retained CPU source buffer. At the 245,000-root gameplay cap that is about 39.2 MB of GPU instance data and 19.6 MB of retained CPU instance data, excluding worker reservations, meshes, engine overhead, and transient uploads. The larger capture profiles deliberately permit substantially more memory; they are not hardware certifications. Density/profile changes can rebuild placements, while camera LOD changes retain existing roots and buffers. Upload limits count a tile's paired installation, and the time budget is checked between tiles.

2026-09-13 correctness validation on Godot 4.7.2 .NET / RTX 4070:

- Build passed with zero warnings/errors.
- Mesh contracts passed for matching near/intermediate root, midpoint, tip, and winding.
- Production streaming contracts passed headlessly and with Forward+, including queued intermediate installation, retained identities, prefix reduction, edit invalidation, seam reloads, and origin shifts. Both final runs exited cleanly with code 0.
- `grass_lod_gpu_test.gd` passed: separate pass masks have no overlap, their union matches joint rendering, the final cutoff clears all blades, and large translations/rotations retain root selection.
- `vegetation_wind_gpu_test.gd` passed after the shader changes, including dense/legacy grass, canonical gusts, frozen time, roots, seam behavior, and existing tree prototypes.
- `grass_quality_gameplay_test.gd` checks every profile's resident-window/root-cap contract and captures the actual integration scene. Captures and whole-frame statistics are stored in `artifacts/grass_lod/`; they do not measure isolated grass GPU cost.
- Final gameplay capture passed with 69,053 retained roots across 26 tiles, including 4,002 roots in the player's tile. Personally inspected both fixed-camera captures after correcting an integer Variant supplied to the float `grass_instance_count` uniform, which had incorrectly left only the first root visible in each tile. Streaming contracts now check the binding's type and original full count on both passes.
- The runtime's `Desktop High` display label selected the enum `VeryHigh` grass profile: 32 candidates/m², 22 m near handoff and 32 m far cutoff. The final 120-sample full-scene run at 1600x900 reported 50.28 ms mean and 96.92 ms p99. This includes terrain/weather work and is not a grass-only timing or a performance pass. GPU profiling and streaming-hitch attribution remain open.
- Final gameplay run exited 0 with the existing transparent-viewport SSR warning and RenderingDevice shutdown resource warnings. An earlier capture attempt also reported a terrain-proxy worker `NullReferenceException` in `RingLodSurfaceEditSnapshot.TryGetEditedSurfaceHeight`; it did not recur in the final two captures and remains an unresolved terrain issue. Isolated grass streaming/LOD/wind checks exit cleanly.

2026-09-09, first milestone validation with Godot 4.7.2 .NET:

- `dotnet build RingworldTechDemo.csproj --no-restore`: passed, zero warnings/errors.
- `godot --headless --path . --script res://tools/ring_grass_mesh_validation_test.gd`: passed. Checks roots, tips, UVs, triangle winding, bounds, partial seam tiles, and composed tile/blade positions at certified and mega scales.
- `godot --headless --path . --script res://tools/grass_streaming_contract_smoke.gd`: passed. Exercises the production scheduler with real terrain sampling, retained tile identities, no stationary regeneration, origin shifts, sequential/skipped edits, runtime setting changes, flight suspension/resume, and populated seam reloads. A separate stress case changes the generation epoch during work and reaches a 1,000-instance cap without stale placement or repeated impossible builds.
- `godot --path . --rendering-method forward_plus --script res://tools/grass_streaming_contract_smoke.gd`: passed on NVIDIA RTX 4070, exit code 0, no stderr diagnostics. Validates actual MultiMesh buffer/resource installation and cleanup in addition to the streaming assertions.
- `godot --path . --rendering-method forward_plus --script res://tools/vegetation_wind_gpu_test.gd`: passed on NVIDIA RTX 4070. Checks direction, strength, frozen time, nonuniform instance scale, rotated/rebased frames, calm guards, and root/seam behavior for grass and the existing tree prototypes.
- `godot --path . --rendering-method forward_plus --script res://tools/vegetation_wind_state_test.gd`: assertions passed in the production scene. Includes GPU global binding, shared-clock precision, pause, publisher replacement/exit, and current imported-tree/flat-LOD material wiring.
- GPU captures use `user://generated/grass/vegetation_wind_gpu/`. Sandboxed runs need access to the local Godot user-data/cache directories.

The production wind-state fixture emits a no-save-slot warning and shutdown resource diagnostics (17 ObjectDB instances and two resources, plus RenderingDevice RIDs). A comparison run with `StandingGrass` removed produced the same diagnostics and exit code 0. The isolated grass GPU fixture exits cleanly. The broader scene cleanup issue remains separate from this milestone. Sandboxed headless runs also report an OS root-certificate-store access error before completing their assertions.

These are correctness checks, not laptop/discrete performance certification. The Arc 140V and RTX 3060/RX 6600 targets, GPU timing, final visual density, and unchecked feature work remain outstanding.

2026-09-09, editable-surface milestone validation:

- Build: passed with zero warnings/errors.
- `grass_surface_validation_test.gd`: passed. Compares fractional roots with production terrain triangles for a tilted surface, soil/rock fills, and excavation; also checks material boundaries, cave exterior/ceiling handling, seam/far coordinates, cancellation, and search exhaustion.
- `grass_exclusion_validation_test.gd`: passed. Covers immutable snapshots, duplicate volumes, placement/removal diffs, glass/concrete occupancy, vertical clearance, fractional bounds, and padded seam boundaries.
- `SmoothConstructionOverlaySmoke.tscn -- --smooth-construction-overlay-smoke`: passed. Checks the actual overlay snapshot cache through concrete/glass placement, removal, origin changes, and journal replay.
- `grass_streaming_contract_smoke.gd`: passed headlessly and with Forward+ on RTX 4070. Added matching sequential field revisions, sub-metre surface changes, field replacement, retained unrelated tiles, exclusion changes during work, and production sampler ownership. The final GPU run exited with code 0 and empty stderr.
- The production-field fixture uses 48 m tiles and nine candidates per cell with four resident tiles. Its final GPU run reported 198.9 ms to settle the initial/moved views and 39.0 ms for the last background tile build. This small fixture is a diagnostic, not a gameplay frame-time or hardware certification result.
- `ring_grass_mesh_validation_test.gd`: passed after adding surface-normal alignment.
- `vegetation_wind_state_test.gd`: passed again in the production scene with Forward+, exit code 0. The previously recorded scene shutdown diagnostics remain unchanged; the isolated grass GPU test exits cleanly.

2026-09-09, dense meadow benchmark validation (primary-agent review):

- `dotnet build RingworldTechDemo.csproj --no-restore`: passed, zero warnings/errors.
- Mesh, surface, and exclusion validation: passed. The mesh test covers the individual ribbon and legacy clump compatibility; authoritative roots and terrain/exclusion behavior remain intact.
- `grass_streaming_contract_smoke.gd`: passed headlessly and on Forward+. Added dense hard-cap coverage, runtime density invalidation, origin stability, normalized phase payloads, and buffered roots checked against the authoritative soil surface. The editable-field fixture retains sufficient budget to exercise unaffected tiles and local exclusions.
- `vegetation_wind_gpu_test.gd`: passed on RTX 4070 for legacy grass, dense ribbons, and all existing tree prototypes. Checks include spatial phase differences, frozen clocks, direction/strength, nonuniform scale, rotated/rebased frames, and calm guards. Across-wind grass bounds were unchanged (0 pixels); bounds replace centroid checks because vertical shortening changes the pixel centroid of curved blades without sideways motion.
- `vegetation_wind_state_test.gd`: passed in the production scene with exit code 0. The known no-save-slot warning and broader scene RenderingDevice shutdown diagnostics remain; isolated meadow and wind runs exited cleanly.
- Final meadow capture: passed with 44,350 individual blades and 16 resident tiles. Inspected the ground-level and backlit/overview images and corrected palette, missing ambient illumination, rest lean, and rock mask alignment. Automated checks cover nonempty captures, frozen nonzero gusts, and visible motion between gust frames.
- The final meadow run reported 111.1 ms for its last background tile build and 0.1 ms for its last installation step. These are CPU diagnostics, not GPU frame timing or a full-game performance result. Dense streaming, LOD coverage, player interaction, compute generation, and hardware certification remain unchecked.
