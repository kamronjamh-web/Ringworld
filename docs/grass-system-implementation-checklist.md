# Ringworld grass implementation checklist

Status: persistent streaming, editable-surface placement, exclusion masks, dense ribbons, distance LOD, texture gusts, local density/tint zones, GPU frustum culling, and bounded gameplay budgets are implemented. Optional distant camera-facing blades are available for comparison. The grave-key Grass tab controls these features with saved sparse overrides. A first full-scene grass render comparison is available on the RTX 4070. Interactive artistic approval, active streaming-hitch attribution, compute experiments, and target-hardware certification remain open.

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
- [x] Match grass toward the underlying biome's average terrain albedo at every distance, retaining nearby brightness relief and finishing ground convergence before the final coverage fade.
- [x] Add conservative tile bounds and deformation margins for the current blade and wind response.
- [x] Add bounded view-dependent widening and update the deformation bounds accordingly.
- [x] Add grass-specific spatial gusts and smaller ripples using periodic canonical phase and the shared simulation clock.
- [x] Add basic player bending with a bounded interaction field.
- [x] Add biome-specific density, height, and palette settings for clearings, forests, and sparse terrain.
- [x] Verify root anchoring, rotated/rebased frames, wind direction/strength, and pause/frozen time.
- [ ] Verify the final appearance through camera rotation, day/night/eclipse lighting, and storm transitions in gameplay.

The interaction field retains eight canonical samples, projects them through the current floating-origin frame, and uses a three-second recovery by default. Bending follows each blade's local tangent plane, keeps roots pinned, and updates the deformation normals. Vertical separation rejects unrelated surfaces; teleports and fast/high travel clear the trail. It requires no per-blade physics queries or placement rebuilds.

Biome density and height enter immutable worker snapshots. Palette changes remain material-only updates. Terrain-palette color matching uses cached linear averages of the actual terrain albedo textures at every distance, including the terrain's grass tint multiplier. Nearby blades retain bounded scalar brightness relief (0.8 to 1.2); the distance band removes this relief and converges ground lighting before roots disappear. The averages do not reproduce exact terrain texels or slope blending. Existing saved color strength remains authoritative, with authored biome tints contributing at partial strength. The production instance payload is transform + biome COLOR + existing custom wind/variation channels. Deformation bounds grow with the tallest blade in each tile (`2 * height + 0.1 m` margin), covering enlarged biome heights and player interaction.

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

## Texture gusts, local zones, and distant blade comparison

These additions extend the existing persistent tile and GPU compaction paths. The Unity reference was used for design ideas; terrain placement, canonical tile ownership, weather publication, and the 20-float instance-buffer layout retain their existing contracts.

| Feature | Initial settings | Live tuning location |
| --- | --- | --- |
| Texture gusts | Enabled; strength 0.45; size 600 m; travel multiplier 1; base breeze 0.65 | Grass > Wind gusts |
| Local modifiers | Enabled; density and tint response 1; no zones until authored | Grass > Local ground detail |
| Camera-facing blades | Disabled; strength 1; blend from 12 to 48 m; intermediate/far passes only | Grass > Experimental distant blades |
| Frustum padding | 0.15 m beyond the bounded blade envelope | Grass > GPU culling |

Gusts use one seeded, seamless 512 x 512 `NoiseTexture2D` generated by Godot in the background. A double-precision offset integrates smoothed weather wind using `PublishedVegetationWindTime`; a frozen weather clock freezes both travel and the existing flutter. Sampling uses canonical positions minus the accumulated offset. The around repeat is adjusted to fit an integral number of repeats around the ring; the across repeat uses the requested size. Shared dual covectors preserve sampling under frame rotation, scale, shear, and floating-origin changes. Texture modulation follows the established wind clamp and is bounded again, allowing visible lulls during strong wind without enlarging the deformation envelope.

To author a zone, add a `RingGrassModifier3D` node to the gameplay scene and place it over the ground. Set its around/across `HalfSizeMeters`, `FeatherMeters`, `DensityMultiplier`, `Tint`, and `TintStrength` in the Inspector. Give each zone a unique `ModifierId`; leaving it empty derives an ID from its scene path. `GrassLayerPath` can target one grass layer. Canonical around/across overrides are available for procedural placement. The footprint is an ellipse in canonical ground coordinates; feathering occurs inside its edge, with full strength at the center. Tint strength controls the blend weight; the tint color's alpha is ignored. Scene-authored nodes are saved with their scene, and runtime projection keeps their canonical location planted through rebases.

For an immediate runtime comparison, use **Place test zone here** in **Grass > Local ground detail**, then adjust its radius, density, tint, and soft edge. The preview stays at the planted location until moved with the button. Preview settings follow the usual sparse persistence; the planted location is temporary and is anchored again in a new session. Gameplay owners can upsert a canonical `RingGrassModifierZone` with `SetGrassModifier(id, zone)` and remove it with `RemoveGrassModifier(id)`. The caller-owned API is bounded to 256 IDs and is the foundation for later editing or gameplay marks. Painting and gameplay mark lifetimes are separate future additions.

Density changes remove actual roots using an independent stable hash; a zero response or disabled modifiers restore the original placement. Workers filter zones once per tile, generate optional 32 x 32 RGBA8 tint maps, and retain immutable snapshots. Main-thread installation uploads affected maps into a shared bounded array texture. Moving, editing, disabling, or removing a zone invalidates its old/new footprint while retaining unaffected tiles and valid worker results. Modifier zones use a horizontal footprint; construction exclusions retain their existing vertical occupancy rules.

GPU root culling combines transformed blade height, the maximum bend envelope, registered mesh width, and configurable padding. Prototype bounds are captured on the main thread. Whole-tile culling uses cached maximum source height and expanded AABBs without scanning every root each frame. Camera changes invalidate the existing frustum cache, and stationary requests continue to skip unchanged work. The live counters report rejected **tiles**, rather than asserting a GPU root count without readback.

Camera-facing mode rotates each distant source around its planted subroot in the local tangent plane, retaining the crossed-card source frame and the main-camera direction for other render passes. Near-pass output stays unchanged. It remains disabled by default pending an interactive comparison of shimmer, coverage, and silhouettes in gameplay lighting.

- [x] Validate gust state precision, turns, pause, seams, scaled/sheared frames, and both material toggles.
- [x] Validate modifier soft edges, immutable ordering, actual tint-map composition, seam filtering, and localized streaming invalidation.
- [x] Validate real GPU frustum rejection, camera/projection changes, transformed roots, grouped-phase parity, and CPU fallback.
- [x] Capture production-shader camera-facing and tint comparisons; verify nonempty output and unchanged near-pass pixels.
- [x] Validate menu controls, sparse save/reload, preset changes, ordering, and reset.
- [x] Capture the dense meadow with frozen texture-on/off comparisons, frozen gusts, and animated gusts.
- [ ] Approve artistic behavior in gameplay, including turning views, low sun, storm wind, and modifier boundaries.
- [ ] Measure a controlled moving-camera performance comparison before claiming frame-time or memory savings.

## Validation record

### Tuning popup and eyedropper isolation, 2026-10-05

The gameplay environment uses `BG_CANVAS` to compose the scaled far view. Embedded UI windows inherited that environment even without a 3D camera, entering an unnecessary 3D postprocessing path. The eyedropper's frozen viewport image was tone-mapped again when drawn into its popup. A controlled AgX/exposure fixture reproduced the visible color shift and restored the original pixels after isolating the popup world.

`TuningPopupIsolation` now gives owned `Popup` and `AcceptDialog` descendants an empty shared `World3D` and disables their 3D rendering. Both tuning drawers attach it before building controls and unsubscribe during exit. Synchronous `SceneTree.NodeAdded` handling covers automatic tooltips, dropdowns, color-picker popups, and nested eyedropper windows. Main/far viewports, ordinary 3D windows, and explicitly marked preview boundaries retain their worlds.

Primary review and validation: .NET build passed with zero warnings/errors; `tools/tuning_popup_isolation_test.gd` passed rendered pixel equality, color sampling, tooltip creation, dropdown selection, scope, and disposal checks; `tools/grass_live_tuning_smoke_test.gd` passed editing, save/reload, and reset. Captures and logs are under `artifacts/tuning-popup-isolation/`. The existing broad grave-menu interaction fixture passed flyover-menu checks but stopped at its outdated ten-tab gameplay assertion. No FPS or GPU-cost measurements were run; the user will assess performance.

### Terrain color and lighting overlap fixture

Default grass lighting uses each planted instance's ring-surface up direction on both blade faces at every distance, removing blade-orientation darkening, artificial root AO, backlight, and blade specular. Real illumination, local lights, the primary-sun day/night/eclipse gate, and cloud effects remain active. **Blade side shading (legacy)** in Grass > Biome appearance restores the previous blade shading and follows the existing sparse persistence and quality-reset conventions. This is a material setting on both near and intermediate grass, with no placement rebuild.

The grave-key menu's Grass > Biome appearance group exposes terrain color match at every distance, ground convergence start/end as percentages of configured draw distance, and ground lighting match. Nearby color matching retains bounded blade brightness relief; the distance band finishes relief and ground-lighting convergence before final root disappearance. These material settings reuse the existing saved keys, sparse grass overrides, and reset conventions.

Run the focused rendered regression from `C:\Projects\Ringworld` with the installed Godot .NET console executable:

```powershell
& $Godot --path C:\Projects\Ringworld --rendering-method forward_plus --script res://tools/grass_terrain_blend_gpu_test.gd
```

The fixture uses production grass and voxel soil shaders, constant textures for quantitative albedo/daylight checks, and real terrain textures for paired wide cutoff captures. It loads no player preferences or save data. Reports and images go to `artifacts/grass-terrain-blend/`. Its historical-band reference explicitly restores the distance-gated color interpolation and old final-fade appearance-call input, disables lighting matching, and uses the old 0.55 strength; roots, geometry, current palette targets, terrain, and camera stay equal. This isolates overlap and lighting behavior rather than reconstructing every historical terrain palette implementation.

Color matching applies independently of draw distance. Defaults start final brightness-relief and ground-lighting convergence at 70% of the configured grass draw distance and finish at 90%, capped before geometric disappearance begins. Terrain color and ground lighting match default to 1. Explicit saved strengths remain authoritative. Both materials follow configured draw-distance changes without changing the placement snapshot. This color change does not alter the budget-limited resident extent or resolve the existing tile-window/fade mismatch. Texture targets come from the terrain's own cached linear averages; the shader adds no terrain texture samples.

2026-10-05 blade side shading removal, primary review and verification:

- `dotnet build RingworldTechDemo.csproj --no-restore` passed with zero warnings/errors. Forward+ `grass_night_lighting_test.gd` passed the legacy engine-reference check and default front/back/rotated ribbon checks; all three default views had mean interior luminance 0.9358361. Default grass remained dark at night and during totality, and responded to a local light.
- `grass_appearance_gpu_test.gd` and `grass_terrain_blend_gpu_test.gd` passed with the new default lighting. The appearance fixture uses lower illumination to avoid display saturation while retaining its original color assertions. Runtime checks verify the blade-shading toggle reaches both cached materials without changing placement. The primary agent inspected the two-face captures and updated actual-texture meadow capture.
- `grass_live_tuning_smoke_test.gd` passed the new toggle's default, live application, boolean validation, save/reload, quality changes, and reset. Logs are in `artifacts/grass-side-lighting/`; controlled captures remain in `.godot/grass_night_lighting/` and `artifacts/grass-terrain-blend/`.
- No performance benchmarks were run. Existing optional native-extension, certificate, shader-cache, and broad-scene shutdown diagnostics persist; no shader compilation or script errors were reported in the final runs.

2026-10-05 all-distance color matching, primary review and verification:

- `dotnet build --no-restore` passed with zero warnings/errors. Forward+ `grass_appearance_gpu_test.gd` and `grass_terrain_blend_gpu_test.gd` passed, including strict nearby terrain convergence, saved partial strength 0.53, biome targets, retained brightness relief, separate lighting behavior, and near foreground coverage response. The forced near-coverage check identified 20,008 opaque foreground pixels with mean RGB response 0.0534766.
- `grass_live_tuning_smoke_test.gd` passed save/reload, quality changes, and reset. `grass_night_lighting_test.gd` passed, with daytime engine-reference error approximately 0.000001.
- The primary agent inspected `artifacts/grass-terrain-blend/near_old.png`, `near_new.png`, `near_saved_partial_strength.png`, `near_forest_old.png`, `near_forest_new.png`, and the actual-texture `daylight_cutoff_before.png` / `daylight_cutoff_after.png` captures in that directory. The comparison retains identical roots and camera; nearby grass moves toward ground hue while retaining relief. Quantitative fixture results are in `artifacts/grass-terrain-blend/report.json`.
- No performance benchmarks were run for this color change, and these checks provide no new FPS or gameplay performance certification. Known native-extension, certificate, and shader-cache diagnostics persist, as do previously recorded broad-scene shutdown diagnostics.

2026-10-02 primary review and validation, Godot 4.7.2 .NET / RTX 4070 / Forward+:

- Build passed with zero warnings/errors. The focused terrain blend fixture passed, and the primary agent inspected its constant-texture and real-texture captures.
- The real-texture comparison uses paired two-segment near clumps and camera-facing single-ribbon intermediate clumps, 22 m near / 32 m far / 4 m transition, 25% intermediate density, and the same 12,000 clump source identities per pass before and after. Its measured outer region contains 1,592 populated pixels: mean absolute RGB contrast against the hidden-ground control fell from 0.428259 to 0.335666, **21.62%**. The near ground-foot region contains 38,614 populated pixels and retains exactly the same mean contrast, 0.485994. Regions are fixed across both captures; the historical shader uses the same current palette targets.
- Constant-texture grassland, forest, gradual overlap and directional-light convergence passed. Near appearance, disabled appearance, zero strength, finite endpoints, CPU/GPU-selected render flags before fade, rock exclusion and the positive soil-coverage control passed. These flags test the render path; they do not constitute a new compute-compaction benchmark.
- Existing appearance, paired LOD/rebase, camera-facing, modifier tint, interaction and primary-sun/night-lighting rendered regressions passed. The engine-reference daytime lighting error was approximately 0.00000060. The material-update cache check passed headlessly.
- Live menu presence, numeric range ordering, application, saving, reloading, quality changes and reset passed in the production integration scene with isolated preferences.
- A broader gameplay fixture completed its profile/streaming/day-night-weather assertions, but its dedicated camera reported a zero-distance `look_at` error. Those gameplay screenshots and its whole-frame timing are excluded from the appearance and performance conclusions here. An initial headless invocation of that rendered fixture timed out awaiting a screenshot; the Forward+ rerun is the applicable assertion result.

Reports, reviewed before/after captures and logs are in `artifacts/grass-terrain-blend/`. Existing missing native mesher, certificate and shader-cache diagnostics remain; full-scene fixtures also emit previously recorded RenderingDevice shutdown resource warnings. The focused fixture exits without resource-leak warnings. These results supply controlled appearance evidence; gameplay performance and user artistic acceptance remain unmeasured.

2026-10-02, gusts, modifiers, camera-facing blades, and frustum bounds (primary-agent review):

- `dotnet build .\RingworldTechDemo.csproj --no-restore -v:minimal` passed with zero warnings/errors.
- CPU/runtime contracts passed: `grass_gust_state_test.gd`, `grass_modifier_state_test.gd`, `grass_modifier_streaming_test.gd`, `ring_grass_mesh_validation_test.gd`, `grass_surface_validation_test.gd`, `grass_exclusion_validation_test.gd`, and `grass_streaming_contract_smoke.gd`. Modifier streaming checks cover unaffected renderer retention, no-op upserts, full zero-density clearing, strength/enable restoration, zone movement/removal, and planted preview changes.
- Forward+ checks passed on NVIDIA RTX A1000 6GB Laptop GPU: `gpu_grass_frustum_culling_test.gd`, `gpu_grass_integration_test.gd`, `gpu_grass_grouped_phases_test.gd`, `grass_camera_facing_test.gd`, `grass_modifier_tint_test.gd`, `grass_live_tuning_smoke_test.gd`, `vegetation_wind_gpu_test.gd`, and `vegetation_wind_state_test.gd`.
- `GrassMeadowBenchmark.tscn -- --grass-meadow-capture` passed with 10,245 retained sources across 16 tiles (40,980 equivalent blades). It waits for the generated texture, verifies a frozen texture-on/off rendered difference, and checks frozen versus advancing wind. Representative captures under `artifacts/grass_meadow/`, `artifacts/grass-camera-facing/`, `artifacts/grass-modifiers/`, and `artifacts/grass_lod/` were inspected by the primary agent. This inspection does not replace user artistic acceptance.
- Logs are under ignored `artifacts/grass-validation/`. Runs still report the checkout's missing native mesher extension, sandbox certificate/cache write diagnostics, and broader scene shutdown RID warnings. The listed assertions passed and the runs exited 0; this record does not certify a warning-free full-game launch.
- These are correctness and rendered-behavior checks. No new frame-time improvement, grass memory saving, or hardware certification is claimed.

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

2026-10-07/08, adaptive grass output grouping and stationary work caching (primary review and validation complete):

- New deterministic fixture: `tools/grass_adaptive_grouping_test.gd` calls the production grouping contract hook and checks the actual visibility, transform and GPU preparation cache keys. Camera movement/turn/projection/FOV/offset/size, settings edits, membership/data/output revision, external layer transforms, floating-origin changes and enable restoration must invalidate or restore equality correctly. The streaming fixture additionally reparents a renderer, moves its external parent, verifies canonical world restoration and verifies stationary caching resumes after direct parenting is restored. On Forward+, the same driver also compares GPU readback root identities from four leaf batches against a merged production grouping batch across near/intermediate handoff and fractional rank/hash density. The GPU fixture additionally submits five batches under a 50-candidate limit, checks actual dispatched candidate counts (allowing one individually oversized group), requires unchanged deferred submissions to complete every visibility revision, and verifies final identity parity. A separate GPU production lifecycle fixture checks real group activation, retention inside the hysteresis band, camera-driven splitting/regrouping, edits to a grouped leaf, and disable/re-enable, with ownership assertions at every step. Deferred GPU completion is additionally covered by the existing budget/revision assertions in `gpu_grass_grouped_phases_test.gd`.
- New matched production capture: `tools/grass_adaptive_grouping_capture.gd` reuses `FarRingGameplayCapture` isolation and reports at the captured ground pose (source altitude offset -340 m, approximately 12.39 m above terrain). Five phases are A cache off/groups off, B cache on/groups off, C cache on/max grouping level 1, D cache on/max level 2, and restored B2. D uses an explicit 160 m far grouping threshold to exercise level 2 within the retained resident footprint; the original threshold is restored. C and D fail when their requested group level produces no actual groups, retaining a failure-time diagnostic snapshot in the suite manifest. Physical group sizes depend on the retained leaf size; the suite does not change density, coverage, placement, quality or leaf size to force a result.
- Every phase requires identical canonical leaf membership and source bytes (including rank order, variation and wind channels), canonical world root poses, stationary camera and authoritative weather/day-night clocks. Canonical source builds must remain unchanged; native output uploads during grouping handoff are allowed, with zero builds/uploads required during each settled collection. Output slots, native object IDs and batch IDs can change. Per-phase artifacts include production timing reports, screenshots, native batch/instance slots, cache scan/hit deltas, grouping diagnostics and build/upload deltas.
- Manual CLI: run the headless deterministic fixture with `--headless --path C:\Projects\Ringworld --script res://tools/grass_adaptive_grouping_test.gd`. Run the production capture on Forward+ with `--path C:\Projects\Ringworld --rendering-method forward_plus --script res://tools/grass_adaptive_grouping_capture.gd -- --artifact-label adaptive-groups --warmup-seconds 30 --collection-seconds 10 --settle-seconds 2 --regenerate-seconds 90`. Add `--detailed-gpu` after `--` when desired. Suites and copied preferences stay below `res://.godot/far-ring-gameplay/`.
- Primary-agent serial validation: `artifacts/grass-grouping-20261007/build-capture.log` reports a successful build with zero warnings/errors. `contracts-final.log` records a Forward+ / RTX 4070 pass for the deterministic grouping/cache contracts, external renderer-parent correction, GPU selector identity parity, candidate budgeting/deferred completion, and production movement/hysteresis/edit/disable/re-enable checks. The convergence regression retained 11 groups and 44 owned leaves after movement, with the same 414 roots and unchanged 96 builds/96 uploads.
- Existing regressions passed: `grass_live_tuning_smoke_test.gd`, `grass_output_residency_test.gd`, `grass_lifecycle_test.gd`, `gpu_grass_grouped_phases_test.gd`, `gpu_grass_registration_test.gd`, and `grass_material_update_cache_test.gd`. Their logs are in `artifacts/grass-grouping-20261007/`.
- The final five-phase matched capture passed with identical 311,015 canonical roots across 1,024 leaves, identical source/world-pose hashes, camera and authoritative clocks, and zero builds/uploads/completed compute updates during each settled collection. Default 32 m grouping produced 51 level 1 groups and reduced native batches from 454 to 313 (31.1%); the optional 64 m fixture produced 41 level 1 plus three level 2 groups and 306 native batches. D uses a test-only 160 m far threshold; the configured default remains 256 m with maximum level 1. The first 192 m fixture did not produce eligible visible level 2 groups and is excluded from the completed comparison.
- The primary agent inspected A, C, D and restored B2 screenshots: grass coverage remained consistent and no new gaps were visible. Artifacts are archived under [capture-v2](../artifacts/grass-grouping-20261007/capture-v2/); the [comparison summary](../artifacts/grass-grouping-20261007/comparison-summary.json) records all phase boundaries and timing values. Cache-only grass manager CPU fell from 1.974 to 0.197 ms (1.777 ms saved). Default grouping used 0.161 ms manager CPU and 871 active presentations while retaining all 1,024 source-cell bookkeeping identities. GPU buffers rose from 26.643 to 27.043 MB, plus merged CPU copies.
- The restored cache-only endpoint improved by 0.556 ms relative to B, so the additional FPS benefit of grouping is not established. Render setup fell from 4.936/4.906 ms in B/B2 to 4.493 ms in C; these scopes overlap other work and must not be summed as independent frame savings. This was a controlled stationary Forward+ / RTX 4070 comparison with the Godot editor open and detailed GPU telemetry enabled. It supplies no 60 FPS guarantee, cross-run FPS conclusion or moving-route performance claim. Default grouping merges four 16 m source cells into 32 m outputs at 128 m start/16 m hysteresis with a 16,384-source cap; optional level 2 merges sixteen cells. Grave > Grass > Adaptive grass groups exposes persisted settings and reset behavior.

2026-10-08, adaptive grouping menu investigation:

- Both adaptive checkboxes passed actual viewport mouse press/release tests, live property updates, saved false overrides, reload and reset. No click callback or persistence fault was reproduced. The earlier integration test exercised direct setting calls; the expanded fixture now covers GUI hit testing and section expansion as well.
- Added local feedback beside the controls: requested grouping/cache state, separate 2-by-2 and 4-by-4 group counts, effective admission distances including near-transition and hysteresis margins, inactive-grass reasons, and cache reuse counters. Standard profiles whose geometry ends before grouping begins now explain that condition. Cache help explains why the visual appearance stays the same.
- Primary review and validation passed: build with zero warnings/errors, `GRASS_ADAPTIVE_POINTER=PASS`, and the full `GRASS_LIVE_TUNING=PASS` on Forward+ / RTX 4070 at 1600x900. Inspected the expanded grouping section screenshot. All 14 checked user preference files remained unchanged; existing startup/shutdown warnings remain. See [validation and screenshot artifacts](../artifacts/grass-group-menu-20261008/validation-summary.json).
