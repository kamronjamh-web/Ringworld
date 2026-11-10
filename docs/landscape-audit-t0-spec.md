# Landscape audit (T0) implementation spec

Status: spec, 2026-11-09, written by the Lead role (`claude-lead`) for phase T0 of `docs/terrain-vegetation-revamp-plan.md`. Nothing here is implemented. T0 changes no production code: every file it adds lives under `tools/`. The doc fixes (plan item 7) are being done separately and are not specified here.

Read with: the plan (sections 1, 2.2, 3.1-3.3, 7.1, 11 T0/T1, 12), `docs/vastness-plan.md` (V0 in section 7, section 11), `AGENTS.md` (testing, manifest, captures, pruning, ffmpeg), and the `godot-test-run`, `test-scaffold` and `perf-capture` skills.

## 0. Summary

T0 delivers numbers and pictures about the current (generation 1) world: a relief spectrum, an offline drainage bake with a river map coloured by danger class, an inland-water gap list, an in-engine tree pack audit, the generation 1 fingerprint, look references and a cost baseline. It shares one harness with vastness V0: the pinned world, the pose table, the time-of-day search, the band table and the GPU boot and capture path.

### Work orders

| WO | Title | Mode | Depends on | Runs in parallel with |
|---|---|---|---|---|
| T0-1 | Shared audit foundation: pinned world, classes, pose fixture, band table, GPU boot and capture module, smoke | headless + one GPU smoke | nothing | T0-4, T0-5 |
| T0-2 | Relief spectrum | headless | T0-1 (headless part) | T0-3, T0-4, T0-5, T0-6 |
| T0-3 | Drainage-bake prototype and river map | headless | T0-1 (headless part) | T0-2, T0-4, T0-5, T0-6 |
| T0-4 | Inland water and swim audit (tests and gap list) | headless | nothing | everything |
| T0-5 | Tree pack audit in engine | headless | nothing | everything |
| T0-6 | Generation 1 fingerprint (`world_generation_fingerprint_test`) | headless | T0-1 (headless part); capture after the Cards default is committed | T0-2, T0-3, T0-4, T0-5 |
| T0-7 | Look references and cost baseline (look, cost and LOD-switch parts) | GPU | T0-1; Cards default committed; the cost part on the A1000 | headless WOs only (GPU is exclusive) |
| V0-1 | Vastness views: analytic distance, band and ladder tables | headless test + GPU | T0-1 | T0-2..T0-6 |
| V0-2 | Vastness fly-out and vanish distances | GPU | V0-1 | headless WOs only |

There are seven T0 work orders rather than about four. Each one is small enough for the `implementer` agent at medium effort and can be verified on its own. T0-4 and T0-5 can start at once. T0-2, T0-3 and T0-6 can start as soon as T0-1's headless part is in, so four headless work orders can run at the same time. The GPU work orders run one at a time.

## 1. Facts settled while writing this spec

These came from reading the code; none needed a Godot run.

1. **World slots carry a display name.** `world.json` has a `name` field. `scripts/world_session.gd` writes it in `_create_slot` ("World N", line 412), `rename_slot` (156-172) and `copy_slot`, and the launcher and start menu read it (`get_slot_metadata(slot).get("name")`). The user's three slots are named "World 1" to "World 3". T1's `tools/new_generation_world.ps1` can set `name` to `g02-rivers-s1337`; no `label` field is needed.
2. **The world recipe is known before the atmosphere settings load (a T1 concern).** `world_size_launcher.gd` `_configure_game` writes `SurfaceGenerationRecipeJson` onto the grid's terrain settings (line 1627). `_start_game` calls `_configure_game` (line 1088) before `host.add_child(game)` (line 1104). The atmosphere settings are loaded in `RingGraphicsQualityController._Ready` (`EnsureAtmosphereDefaults`, line 220), or earlier on first access from the live drawer's `_Ready` (comment at lines 223-226). Both happen after `add_child`. The warm menu world goes through the same launcher path (`ring_warm_world.gd` `start` calls `request_load_selected_world`). So the generation can be published before any settings owner reads it, as long as T1 publishes it from the launcher's configure step and not from some node's `_Ready`, where sibling order would decide. Harnesses that instantiate the game scene without the launcher (for example `tools/tree_cpu_profile.gd`) never publish a generation, so T1's owner must default to generation 1 when nothing has been published.
3. **Headless test worlds use non-production climate.** `tools/EmitterSiteTestWorlds.cs` builds `new VoxelTerrainSettings { ... }` with code defaults: `TemperatureFrequency` 1e-5 and `MoistureFrequency` 2e-5 (`scripts/Voxel/Phase3/VoxelTerrainSettings.cs:59-60`). The game uses `resources/voxel/voxel_terrain_settings_ring_integration.tres` (2.5e-6 and 5e-6, lines 20-21, plus a biome catalog). Heights match the game, but moisture, temperature, biomes, trees and grass do not. T0's pinned world therefore builds its sampler from the production resource (T0-1). T1 should fix this when it adds a generation argument to `EmitterSiteTestWorlds`, or tests pinned to it will disagree with the game.
4. **The shadow harness never copies the user's atmosphere or surface-terrain settings.** `tools/shadow_harness_common.gd` `_install_isolated_preferences` (line 272) copies `user://atmosphere_defaults.tres` and `user://surface_tuning.tres`. The real files are `atmosphere_visual_defaults.tres` and `gameplay_surface_terrain_defaults.tres` (the `UserPath` constants in `scripts/ui/AtmosphereVisualDefaults.cs` and `GameplaySurfaceTerrainDefaults.cs`). So the shadow S0 baselines ran with the default haze, not the user's. T0 does not edit the shadow harness, because fixing it would change shadow baselines. The landscape harness has its own corrected list (T0-1). Whether to fix the shadow harness is for the primary agent to decide.
5. **The harness world is not pinned by `create_game`.** For a new world the launcher loads `user://terrain_generation_preview/recipe_v3.tres` when it exists (`_prepare_runtime_surface_generation`, line 1460) and adopts its field values. The user's real profile has that file. The runner's isolated profiles do not, because `Initialize-TestProfile` (`tools/testing/TestRunner.psm1`, lines 478-503) copies only `worlds/slot_1`, the top-level `*.tres` files and the caches. `create_game` also forces v7 and v8 off unless a `wl_` pose is requested (lines 224-230). The landscape harness therefore sets both on and then checks the loaded recipe against the pinned one (T0-1).
6. **Raster passes have no GPU markers.** `RingPerformanceGpu` scopes (line 15) exist only for compute work: `TreeCulling`, `GrassCulling`, `Vegetation`, `ProxyHeights`, `HeightShadow`, `WaterFft` and the cloud stages. Godot 4.8-dev7 engine timestamps give one interval per viewport. Terrain, tree and grass draw cost can only be measured as A-B-A hide deltas (the method of `tools/terrain_render_attribution_test.gd`), plus exact draw and primitive counts. T0-7 records both and tries a test-only opaque-pass bracket. This limits how precisely the T4-T6 GPU gates in plan section 12 can be judged.
7. **The visible tree LOD switch is at 25-40 m.** The compact profile cross-fades LOD0 into LOD2 between `TreeNearDetailFadeStartMeters` 25 and `TreeNearDetailFadeEndMeters` 40, and LOD2 into the billboard between 300 and 350 m (`scripts/Voxel/LOD/RingBiomeDecorationLayer.RenderPolicy.cs`, lines 13-16 and 37-43). This is exactly the condition plan 7.1 item 8 names, so T0-7 checks the switch visually.
8. **Seams the audits can use without touching production code.**
   - Tree placement: `RingBiomeDecorationLayer.BuildImportedForestReviewTile` (internal, `RingBiomeDecorationLayer.TreeStreaming.cs:25`) runs the production `BuildVegetationTile`.
   - Grass placement: `RingGrassTileBuilder.Build` (used by `tools/GrassCardPlacementTest.cs`).
   - Proxy sectors: `RingTerrainProxySector.CreateAllocationFixtureSession` (public, in the test-only partial `RingTerrainProxySector.Allocation.Tests.cs`). The private `BuildRawSector` (line 3816) and the private record `SectorData` (line 461) can be reached by reflection from `tools/`.
   - Samplers: the game grid exposes `ChunkGridManager.CreateIndependentTerrainSampler()`.
9. **Runner selection pitfalls that affect every work order.**
   - `-Changed` and `-Since HEAD` would also pick up other agents' uncommitted grass and PDA changes. Use `-Files` (or `-Tests`) per work order.
   - Only top-level `tools/<Name>.cs` helpers are mapped to the wrappers that preload them.
   - A changed `tools/*.gd` module that is not a manifest entry and matches no tag glob counts as "unmapped", which selects every headless gate test (about 280). T0 therefore adds a `landscape` tag whose globs cover its own files, and never edits `tools/shadow_harness_common.gd`.
   - `-Files` always selects wrappers directly, whatever their kind, and any wrapper whose text names a changed helper `.cs`. GPU tool wrappers must therefore reach the C# helpers only through `tools/landscape_harness_common.gd` and never name a `.cs` file themselves. Otherwise touching a helper would select a 30-minute GPU run.
10. **This session runs on the RTX 4070 desktop** (`nvidia-smi`: GeForce RTX 4070). Its GPU numbers are not authoritative. The T0-7 cost part has to run on the A1000 laptop (section 7).

## 2. Harness architecture

### 2.1 What is headless and what needs the GPU

| Part | Mode | Why |
|---|---|---|
| Pinned world, classes, pose finder and pose fixture validation (T0-1) | headless | pure sampler and model calls |
| Relief spectrum (T0-2) | headless | height model only |
| Drainage prototype and river map (T0-3) | headless | sampler, model and `Image.save_png` all work headless |
| Inland water audit (T0-4) | headless | samplers and proxy sector build data, no rendering |
| Tree pack audit (T0-5) | headless | catalog meshes and materials load headless (`imported_tree_prepared_catalog_test` is headless) |
| Fingerprint (T0-6) | headless | placement builders are CPU |
| Vastness ray geometry (V0-1 test) | headless | closed-form geometry |
| Harness smoke (T0-1), look and cost baseline (T0-7), vastness views and fly-out (V0-1, V0-2) | windowed GPU | pixels, GPU timings, streaming |

All files are flat in `tools/` (the runner maps only top-level helpers). Shared GDScript modules are `extends RefCounted` with static functions, like `shadow_harness_common.gd`, so `validate_manifest.ps1` does not need entries for them.

### 2.2 The pinned world: `tools/LandscapeWorld.cs`

One C# static class that every T0 and V0 part uses for "the generation 1 world":

- Recipe: `EmitterSiteTestWorlds.Gen17WeatherLungRecipe(1337, climate: false)`, the default `ringworld_surface_weather_lung_v8` recipe. According to the plan, the user's `slot_1` stores the same recipe with the v7 defaults. T0-1 prints the canonical JSON hash and, behind a flag, compares it with a slot's stored JSON (read only).
- Settings: the production resource `res://resources/voxel/voxel_terrain_settings_ring_integration.tres`, duplicated, with exactly the overrides that `world_size_launcher.gd` applies for a certified runtime-generation world (`_build_runtime_terrain_settings` and `_configure_game` lines 1622-1632: seed 1337, `BedrockY` -8000, `SeaLevelY` 0, recipe JSON, contract id `surface_runtime_generation_v1`, caves, overhangs and 3D density off, plus every other `runtime_settings.set(...)` in `_build_runtime_terrain_settings`, using the certified default launch values).
- Sampler: `new RingTerrainSampler(settings, true, 31_415_936, 31_415_936 / Tau, 318_000, 8400)`. The T0-1 GPU smoke proves this matches the in-game sampler exactly at 64 points.
- Models: `CreateShapedModel()` and `CreateNaturalModel()`, one per worker thread (the gully cache is not thread-safe). `SampleDiagnostics` on the shaped model returns the natural diagnostics with `Height` replaced by the shaped height and the site fields filled in. The natural height is `Clamp(MacroHeight + DetailHeight, min, max)`; T0-1 asserts this against the natural model.
- A GDScript-facing `LandscapeWorldBridge : RefCounted` with `CanonicalRecipeJson()`, `DefinitionHashHex()`, `VerifyGame(Node game) -> Dictionary` (recipe JSON equality plus the 64-point sampler comparison) and `GroundHeight(double around, double across)`.

### 2.3 Landscape classes

`LandscapeWorld.Classify(in Diagnostics d)` uses the shaped-model diagnostics. It applies the first rule that matches. The thresholds are the ones `LandformTerrainV7Test` already uses.

| Order | Class | Rule |
|---|---|---|
| 1 | Site | `SiteWeight > 0` or `WeatherLungWeight > 0` |
| 2 | Ocean | shaped `Height < 0` |
| 3 | Mountain | `AlpineWeight >= 0.02` |
| 4 | CoastalBelt | `LandformGate < 0.999` (the shore buffer, where landform terms ramp in) |
| 5 | Breaks | `PlateauWeight >= 0.5` and `Ruggedness >= 0.7` |
| 6 | PlateauInterior | `PlateauWeight >= 0.8` and `Ruggedness < 0.3` |
| 7 | HillCountry | `Ruggedness >= 1` |
| 8 | Plains | `Ruggedness == 0` |
| 9 | Rolling | everything else (`0 < Ruggedness < 1`) |

### 2.4 The shared pose and time module

**Fixture.** The pose finder (`tools/LandscapePoseFinder.cs`) resolves every pose once. The result is committed as `tools/fixtures/landscape_poses_v8_s1337.json`. The GPU tools read the JSON and never search at run time. `landscape_world_test` checks on every run that the committed rows still hold. Schema:

```json
{
  "schema": 1,
  "world": {"generator_id": "ringworld_surface_weather_lung_v8", "seed": 1337, "definition_hash": "0x...", "settings": "res://resources/voxel/voxel_terrain_settings_ring_integration.tres"},
  "eye_height_m": 1.7,
  "times": {
    "noon": {"mode": "noon"},
    "sun_8_set": {"mode": "elevation", "value": 8.0, "rising": false},
    "sun_2_set": {"mode": "elevation", "value": 2.0, "rising": false},
    "night_planet": {"mode": "night_planet"}
  },
  "headings": {
    "along_pos": {"yaw": 0.0, "pitch": 0.0}, "along_neg": {"yaw": 180.0, "pitch": 0.0},
    "wall_hi": {"yaw": 90.0, "pitch": 0.0}, "wall_lo": {"yaw": 270.0, "pitch": 0.0},
    "arch_up": {"yaw": 0.0, "pitch": 30.0}
  },
  "poses": [
    {"name": "plains_eye", "reports": ["t0"], "class": "Plains", "habitat": "None",
     "around_m": 0.0, "across_m": 0.0, "ground_m": 0.0, "vertical_mode": "agl", "lift_m": 1.7,
     "yaw_deg": 0.0, "pitch_deg": -3.0, "notes": "..."}
  ]
}
```

- `times` values are passed unchanged to `shadow_harness_common.gd` `apply_time`, which searches the sun state at the explorer's current location. So the time is always applied after the teleport.
- Yaw follows the explorer convention (`RingVoxelExplorerController.cs:983`): yaw 0 faces +around, yaw 90 faces +across (toward the wall at 318 km), yaw 270 faces the wall at 0.
- `vertical_mode` is `agl` (vertical = `ground_m + lift_m`) or `absolute`. Every teleport uses flight, so gravity cannot drift the pose.

**Pose rows.** Every row except `saved_18km` comes from a deterministic scan. The lattice has `around_i = floor(i * 78539.25) + 0.125` for i in 0..399 (outer loop) and `across_j = 9000 + j * 7500` for j in 0..39 (inner loop). The scan takes the first candidate that passes. Every ground pose must have `SiteWeight == 0` and `WeatherLungWeight == 0` at the point and at 8 points on a 2 km ring around it.

| Pose | Reports | Rule |
|---|---|---|
| `plains_eye` | t0 | Plains at the point and at 8 points on a 1.5 km ring; forest habitat at the point is not DenseForest; yaw 0, pitch -3 |
| `hills_eye` | t0 | HillCountry at the point and on the 1.5 km ring; slope 6-18 degrees at a 16 m baseline; yaw = steepest descent of the shaped height at 64 m, rounded to 1 degree; pitch -4 |
| `hills_300m`, `hills_2km` | t0, v0 (2 km) | `hills_eye` location, `agl` 300 m with pitch -15, and 2,000 m with pitch -25 |
| `coast_eye` | t0, v0 | From CoastalBelt lattice points, search a 2 km radius at 50 m steps for land with shaped height 2-15 m and an ocean sample 150-400 m away along one of 16 directions; yaw = that direction; pitch -2 |
| `forest_interior_eye`, `forest_edge_eye`, `forest_clearing_eye` | t0 (clearing also v0) | One "forest focus" within 3 km of a Plains or Rolling lattice point: on a 50 m grid over 3 x 3 km, at least 20 % DenseForest, at least 5 % Clearing and some Edge (`ForestRegionSampler.Sample(...).Habitat`, fed by the production sampler's `TerrainSample` and the 4 m slope). Interior: DenseForest at least 150 m from any other habitat. Clearing: Clearing with DenseForest in at least 6 of 8 directions at 150 m. Edge: Edge with DenseForest 100 m ahead (that is the yaw) and none 100 m behind. Each must be confirmed with tree records from `BuildImportedForestReviewTile`: interior at least 6 trees within 30 m; clearing 0 trees within 20 m and at least 6 within 120 m; edge at least 3 trees within 60 m ahead. If a check fails, try the next candidate |
| `saved_18km` | t0, v0 | Constants: the user's saved pose from `worlds/slot_1/menu_snapshot.json` (read 2026-11-09): around 27,984,760.943 m, across 159,549.757 m, `absolute` vertical 18,645.815 m, yaw 28.974, pitch -25 |
| `v0_centre_eye` | v0 | First lattice `i` with land (not Ocean, Site or Mountain) within 159,000 +- 3,000 m across, searched at 500 m steps; yaw 0, pitch 0 |
| `v0_wall_lo_eye`, `v0_wall_hi_eye` | v0 | Same rule at across 30,000 +- 3,000 m and 288,000 +- 3,000 m |
| `v0_ridge_eye` | v0 | Shaped height 1,300-1,700 m and a local maximum (16 samples each on 250 m and 500 m rings are all lower); yaw 0, pitch -2 |
| `v0_alt_12km` | v0 | `v0_centre_eye` location, `agl` 12,000 m, pitch -25 |

> 2026-11-09 amended by lead (clearing pose): clearings are 768 m-scale openings; original 150 m / 6-of-8 / 20 m rule had no candidate (best was 5 of 8 at 150 m, and every candidate had trees within 20 m). New rule for `forest_clearing_eye`: Clearing habitat inside the focus window; test rings of 150, 250 and 400 m and accept the first ring with at least 4 of 8 directions DenseForest; no tree within 10 m of the eye. Best point: most dense directions, then smallest ring, then largest tree-free radius (capped at 120 m), grid order as the final tie-break. The ring radius, dense-direction count and nearest-tree distance are written to the pose notes. The `--clearing-dense-dirs` test flag was removed.
>
> 2026-11-09 implementer note (no rule change): `v0_ridge_eye` is found by hill-climbing from lattice points whose shaped height is 600-1700 m (a raw lattice point is almost never a local maximum on 250 m and 500 m rings); for `absolute` poses the teleport vertical is `lift_m`.

The rim-wall-top view is geometric, not terrain, and V0-1 adds it at run time (section 4).

**Report sets.** T0 uses the nine `t0` poses at `noon` and `sun_8_set`. V0 uses the `v0` poses with the headings and times in section 4.

### 2.5 The band table: `tools/landscape_audit_bands.gd`

One distance-band table for both reports. The edges are the union of T0's near decades and V0's bands:

| Band | 0-10 m | 10-30 m | 30-100 m | 0.1-0.5 km | 0.5-2 km | 2-8 km | 8-30 km | 30-120 km | 120-500 km | 500-2,000 km | 2,000-7,000 km | beyond 7,000 km | no distance |

- `compute(image, distance, w, h, stride)` returns, per band: pixel count, share of the screen, mean linear luma (sRGB decoded through a 256-entry LUT, Rec.709 weights), mean HSV saturation, local contrast (mean absolute difference between linear luma and its 3 x 3 box mean, computed at full resolution) and luma p10 and p90.
- Distance comes from the main-viewport linear depth of `tools/shadow_depth_capture_effect.gd`. Pixels at 1e8 m or more go to "no distance". V0 replaces those with its analytic ray distance.
- `band_image(distance, w, h)` writes a false-colour band map with one fixed colour per band.
- `adjacent_steps(table)` returns the mean-luma step between neighbouring non-empty bands. This is the V0 tier metric.

### 2.6 GPU boot and capture: `tools/landscape_harness_common.gd`

The module preloads `shadow_harness_common.gd` as `COMMON` and reuses its `machine_info`, power log, `power_window`, stats, `apply_time`, `wait_builds_idle`, `builds_idle` and `hold_world_clocks`. Its own boot is a copy of `COMMON.create_game` with three differences:

1. It sets `ringworld/runtime/surface_landforms_v7` and `surface_weather_lung_v8` to true for every pose.
2. It installs the isolated preferences under their real file names, with `--prefs=user|defaults|<dir>`:
   - `user` (default) copies from `user://`, which under the runner is the isolated profile holding copies of the user's files;
   - `defaults` copies nothing;
   - `<dir>` copies from a saved snapshot.
   The files used are always copied into the run's `prefs_snapshot/` folder. Each drawer `Install*Defaults` method must be paired with the matching `UserPath` constant.
3. After the world is ready it calls `LandscapeWorldBridge.VerifyGame(game)` and stops with `<MARKER>=FAIL world mismatch: ...` if the recipe or any of the 64 sample points differs.

Other functions:

- `apply_preset(game, name)`. `--preset=medium` (the default) calls `GraphicsQuality.ApplyPreset(1, true, false)` and then waits for builds to go idle. `--preset=keep` leaves the preset alone. Either way the effective preset and the grass render style are recorded, and the run fails if the style is not Cards, unless `--allow-blades` is passed.
- `load_poses()` reads the fixture.
- `go_pose(tree, game, pose, time_name, heading := {}, settle_s := 2.0, idle_timeout_s := 300.0)`:
  1. teleports with `TeleportToCanonicalMeters(around, vertical, across, yaw, pitch, true)`;
  2. waits for builds to go idle;
  3. applies the time;
  4. settles;
  5. for `agl` poses, checks `abs(Terrain3DGameplay.SampleHeight - ground_m) < 0.5`;
  6. returns sun elevation, the idle flags and any error.
- `install_depth_effect(world_environment)` and `capture(tree, depth_effect)` follow `tools/shadow_quality_capture.gd` (arm, wait up to 10 frames, take).
- `transient_dir(kind)` and `baseline_dir(machine, phase, kind)` (section 2.7).

The harness world directory is `res://.godot/captures/landscape/fixture_world/<stamp>`. It starts empty, so the per-world vegetation cache under `<world>/lod_cache` is cold and tile builds are real builds. The harness deletes this directory when it finishes.

### 2.7 Output layout

```
.godot/
  captures/landscape/t0/                    transient; pruned by the normal 7-day / 20 GB policy
    fixture_world/<stamp>/                  harness world directory, deleted at exit
    smoke/<stamp>/                          T0-1 smoke capture and bands
    look/<stamp>/                           raw views before promotion
    lod_switch/<stamp>/frame%08d.png        T0-7 dolly frames, deleted once the sheet and flicker are made
  captures/vastness/v0/...                  same, for V0 (fly-out frames)
  audit_baseline/                           durable; added to the keep list in tools/prune_test_output.ps1
    common/t0/relief/<stamp>/               relief_spectrum.json, relief_spectrum.md
    common/t0/drainage/<stamp>/             drainage_<res>.json/.md, river_map_<res>.png, river_map_<res>_crop.png
    common/t0/water/<stamp>/                inland_water_audit.json/.md
    common/t0/trees/<stamp>/                tree_pack_audit.json/.md
    <machine>/t0/look/<stamp>/              <pose>__<time>.png, <pose>__<time>_bands.png, look.json, reference_set.json, prefs_snapshot/
    <machine>/t0/cost/<stamp>/              cost.json, cost.md, power_log.csv, prefs_snapshot/
    <machine>/t0/lod_switch/<stamp>/        sheet.png, control_sheet.png, flicker.txt
    <machine>/v0/{views,flyout}/<stamp>/    V0 tables, sheets
  test_runs/<label>/<run id>/               runner results.md, results.json, logs/
  test_runs/appdata_<label>/                runner profile, one per work-order label, reused
```

`<machine>` is `machine_info().tag`: `a1000`, or a cleaned adapter name such as `rtx4070`. Headless outputs are machine-independent and go under `common/`. The durable folder holds only small or cited files: tables, references, the river maps and sheets. It is never committed. The tables are pasted into section 8 of this doc and into the plan docs.

### 2.8 Profiles, GPU queue, pruning and ffmpeg

- Run everything through `tools/run_tests.ps1`. It builds, isolates the profile, takes the GPU queue for GPU entries and prunes before each run. Never wrap it in `gpu_queue.ps1 -Wrap`, and never kill another agent's Godot process.
- Headless work orders use `-Cache none`; they need no shader or ring caches, so the profile stays small. Each uses its own label (`t0_foundation`, `t0_relief`, `t0_drainage`, `t0_water`, `t0_trees`, `t0_fingerprint`, `v0_geometry`), so they can run in parallel.
- All GPU work orders share one label, `landscape_gpu`, with the default `-Cache reuse`. The first run copies the warm caches (about 2.5 GB) once. GPU runs are serial anyway.
- At the end of every work order run `tools/prune_test_output.ps1 -Apply`.
- ffmpeg (T0-7 LOD switch, V0-2 fly-out) goes only through `tools/media/ffmpeg_tools.ps1`. After making the sheet and the flicker report, delete the raw frame folder. Keep only what the report cites, in `audit_baseline`.

### 2.9 Manifest conventions for T0 and V0

- New tags in `tools/test_manifest.json` `tags`:
  - `"landscape": {"globs": ["tools/Landscape*", "tools/landscape_*", "tools/WorldGenerationFingerprint*", "tools/world_generation_*", "tools/fixtures/landscape_*", "tools/fixtures/world_generation_*"]}`
  - `"vastness": {"globs": ["tools/Vastness*", "tools/vastness_*"]}`
- Report generators (relief, drainage, look and cost, vastness views, fly-out) are `kind: tool`, `tier: manual`. They are run on purpose with `-Tests` and are never auto-selected. The plan says "full tier only". In this runner a 30-minute GPU capture in the full suite would cost every pre-merge run, and these tools produce evidence rather than verdicts (see the test-scaffold skill: "captures ... are manual tier evidence").
- Contract tests (`landscape_world_test`, `landscape_inland_water_audit_test`, `landscape_tree_pack_audit_test`, `world_generation_fingerprint_test`, `vastness_geometry_test`) are `kind: test`, `tier: gate`, because they pin generation 1 behaviour that later phases must not break.
- `expected_s` values below are estimates. Replace them with measured values after the first green run.

## 3. Work orders (T0)

All work orders share these constraints:

- **Do-not-change:** no edits under `scripts/`, `scenes/`, `materials/`, `resources/` or `addons/`. No edits to `tools/shadow_harness_common.gd`, `tools/terrain_render_attribution_test.gd` or any existing test (copy code instead). No new grave-menu settings. Never write to the user's real `user://` files or save slots. No commits.
- **Required:** every new SceneTree wrapper has a header comment with its purpose and the exact run command, a watchdog timer, `<MARKER>=PASS` on stdout, `<MARKER>=FAIL <reason>` through `push_error`, and exit codes 0 or 1. Every new wrapper gets a manifest entry, and `pwsh -File tools/testing/validate_manifest.ps1` must pass. Build with `./tools/use_godot_48.ps1 -Build` (the runner does this too). Keep the generated `.uid` files.
- **Test runs:** run the dry run first and check that the selection matches the "expected selection" given. If it selects more than that (an "unmapped" or "full headless" reason, for example), stop and fix the cause instead of running the whole suite. Report the `results.md` summary line and any row that did not pass. Run `tools/prune_test_output.ps1 -Apply` at the end.
- **Escalate** (stop and report to the lead) if a seam the work order names does not exist or does not behave as described, if production code would have to change, or if an acceptance check fails for a reason that looks like a real game bug rather than a harness bug.

---

### WO T0-1. Shared audit foundation

**OBJECTIVE.** Build the shared parts every T0 and V0 tool uses: the pinned generation 1 world, the landscape classes, the committed pose fixture, the band table, and the GPU boot and capture module. Prove them with one headless contract test and one GPU smoke.

**FILES.**

| Action | File |
|---|---|
| create | `tools/LandscapeWorld.cs`: static `LandscapeWorld` plus `LandscapeWorldBridge : RefCounted` (section 2.2), `enum LandscapeClass`, `Classify` (2.3) |
| create | `tools/LandscapePoseFinder.cs`: the scan rules in 2.4, a JSON writer and a row validator |
| create | `tools/LandscapeWorldTest.cs` and `tools/landscape_world_test.gd` |
| create | `tools/fixtures/landscape_poses_v8_s1337.json`, written by `--capture` |
| create | `tools/landscape_audit_bands.gd` (2.5) |
| create | `tools/landscape_harness_common.gd` (2.6) |
| create | `tools/landscape_harness_smoke.gd` |
| edit | `tools/prune_test_output.ps1`: add `"audit_baseline"` to `$keep`; also update the comment above `$keep` |
| edit | `tools/test_manifest.json`: the `landscape` and `vastness` tags (2.9) and two entries |

**REQUIRED BEHAVIOUR.**

1. `LandscapeWorld` exactly as in 2.2. List every launcher override you mirrored in a comment that cites the launcher line.
2. `landscape_world_test` (headless) checks:
   - (a) the recipe's generator id is v8, the seed is 1337, and the canonical JSON round-trips through `TerrainGenerationParameters.FromJson`;
   - (b) the settings came from the production resource (assert `TemperatureFrequency == 2.5e-6` and `MoistureFrequency == 5e-6`);
   - (c) on 1,000 lattice points, the natural height derived from shaped diagnostics equals the natural model's height (bitwise);
   - (d) every fixture row: its class and habitat predicates still hold, and `ground_m` equals the shaped model height within 1e-6 m (the tree-record checks are not repeated, to keep the test fast);
   - (e) the fixture's `definition_hash` equals the recipe's.
   Flags:
   - `--capture` runs the finder and writes the fixture only if it does not exist (it refuses to overwrite);
   - `--find` runs the finder and prints the differences from the committed fixture without failing;
   - `--compare-slot=<abs path to world.json>` prints whether that slot's stored recipe JSON equals the pinned one (read only, informational).
   It prints `LANDSCAPE_WORLD_RECIPE hash=... json_sha=...` and `LANDSCAPE_WORLD=PASS`.
3. The pose finder must be deterministic and finish within 120 s on the 4070. Parallelise over lattice rows with one model and one sampler copy per worker, then merge in lattice order. If a rule finds no candidate, fail with the rule's name. Never loosen a rule silently; report it and escalate.
4. `landscape_harness_common.gd` as in 2.6. Use `COMMON` for everything it already provides. Write the boot by copying `COMMON.create_game`. Do not call or modify it.
5. `landscape_harness_smoke.gd` (GPU) runs these steps:
   1. boot with `--prefs=user` and `--preset=medium`;
   2. verify the world;
   3. go to `plains_eye` at `noon`;
   4. capture and compute the band table;
   5. write `smoke.png`, `smoke_bands.png` and `smoke.json` to `.godot/captures/landscape/t0/smoke/<stamp>/`.
   It passes when the world is verified, the ground check holds, at least 3 bands are non-empty, the mean linear luma is above 0.02 and `streaming_idle` is true. Print `LANDSCAPE_HARNESS_SMOKE=PASS`. Watchdog: 900 s. The wrapper must not name any `.cs` file (section 1, item 9).

**MANIFEST ENTRIES.**

```json
"landscape_world_test": {"kind": "test", "mode": "headless", "tier": "gate", "tags": ["landscape", "terrain"], "marker": "LANDSCAPE_WORLD", "expected_s": 15, "timeout_s": 90},
"landscape_harness_smoke": {"kind": "tool", "mode": "gpu", "tier": "manual", "tags": ["landscape"], "marker": "LANDSCAPE_HARNESS_SMOKE", "expected_s": 300, "timeout_s": 900}
```

**VALIDATION.**

```powershell
# capture the fixture once (headless manual run; it reads only res:// files and writes only the fixture, so no isolated profile is needed)
& artifacts\engine-upgrade\Godot_v4.8-dev7_mono_win64\Godot_v4.8-dev7_mono_win64_console.exe --headless --path . --script res://tools/landscape_world_test.gd -- --capture *> .godot\t0_foundation_capture.log
.\tools\run_tests.ps1 -Label t0_foundation -Cache none -Files tools/LandscapeWorld.cs,tools/LandscapePoseFinder.cs,tools/LandscapeWorldTest.cs,tools/landscape_world_test.gd,tools/fixtures/landscape_poses_v8_s1337.json,tools/test_manifest.json -DryRun
.\tools\run_tests.ps1 -Label t0_foundation -Cache none -Files <same list>
.\tools\run_tests.ps1 -Label landscape_gpu -Tests landscape_harness_smoke -DryRun
.\tools\run_tests.ps1 -Label landscape_gpu -Tests landscape_harness_smoke
.\tools\prune_test_output.ps1            # dry run: audit_baseline must not be listed
.\tools\prune_test_output.ps1 -Apply
```

Expected selection for the headless run: `landscape_world_test` (direct and tag `landscape`) and the manifest validator (meta). Nothing marked "unmapped" or "full headless". Then prove the test can fail: temporarily change one `ground_m` in a scratch copy of the fixture and point the test at it with a hidden `--fixture=<path>` flag. Do not edit the committed file. Also open `smoke.png` with the Read tool and confirm it shows plains at eye height.

**ESCALATE IF** the in-game sampler differs from `LandscapeWorld` at any of the 64 points (the production settings were not mirrored correctly, or the game applies something the spec missed), or a pose rule finds no candidate.

---

### WO T0-2. Relief spectrum

**OBJECTIVE.** Measure how much height variation the generation 1 land has in each wavelength band, per landscape class, plus slope and relief percentiles. These are the numbers T7 sets its targets from.

**FILES.** Create `tools/LandscapeReliefAudit.cs` and `tools/landscape_relief_audit.gd`, and add a manifest entry.

**INPUTS.** `LandscapeWorld` (shaped model, one per worker). Classes: CoastalBelt, Plains, Rolling, HillCountry, PlateauInterior, plus Breaks and Mountain as reference rows.

**METHOD (required).**

1. **Anchors.** Scan the section 2.4 lattice and take the first 24 anchors per class. An anchor passes when:
   - its class holds at the point and at 8 points on a 1.5 km ring (CoastalBelt: 6 of 8 points on a 500 m ring);
   - `SiteWeight == 0` at both ends and the middle of each transect;
   - for the across transects, `across` lies within 33-285 km.
2. **Transects.** Each anchor gets two transects in each direction (around and across), centred on it:
   - fine: 4,096 samples at 1 m;
   - coarse: 4,096 samples at 32 m along around, 2,048 samples at 32 m across.
   Heights come from shaped `CalculateHeight`. For CoastalBelt, clamp negative heights to 0 (the water surface), and say so in the report.
3. **Spectrum.** For each transect:
   1. remove the least-squares linear trend;
   2. apply a Hann window;
   3. take a radix-2 FFT (a small implementation in the file);
   4. compute the one-sided power and divide it by the window power, `mean(w^2)`;
   5. sum it into octave wavelength bands [2, 4), [4, 8) ... [32,768, 65,536) m.
   Band RMS height = sqrt(band power). The fine transects cover bands 2 m to 1,024 m; the coarse ones cover 64 m to 32 km.
4. **Consistency.** For the bands 128-256 m and 256-512 m, the median over anchors of the fine and coarse estimates must agree within 25 % in each class. If they do not, the method is wrong. Fix it or escalate, but never widen the tolerance.
5. **Statistics per class.**
   - Band RMS per octave: median and p90 over anchors and both directions.
   - Decade RMS (the root of the summed band power) for 2-30 m, 30-300 m, 0.3-3 km and 3-30 km. These map onto the plan's relief ladder in section 2.1.
   - Directional slope percentiles p50, p90 and p99 at 2, 8 and 32 m baselines, from the fine transects.
   - The 2D slope at 4 m (central differences on both axes at 400 points per anchor), p50, p95 and max. This is the metric of the lowland plan's tables.
   - 4 km relief (max minus min over each fine transect), p50 and p95.
   - The class coverage share over the whole lattice.
6. **Report.** Write `relief_spectrum.json` and `relief_spectrum.md` to `.godot/audit_baseline/common/t0/relief/<stamp>/`. The markdown has one band table (classes as columns), one decade table, one slope table and one relief table, plus the run time and the anchor count per class. Print `LANDSCAPE_RELIEF=PASS` when every class has 24 anchors (Breaks and Mountain may have fewer, which only gives a warning) and the consistency check holds.

**ACCEPTANCE.**

- It is deterministic: running it twice gives byte-identical JSON, apart from the stamp and timing fields.
- It finishes within 120 s on the 4070.
- Plains decade RMS at 30-300 m is below HillCountry's. This is only a sanity check; if it fails, investigate rather than retune.
- Paste the markdown tables into section 8.1 of this doc.

**MANIFEST.** `"landscape_relief_audit": {"kind": "tool", "mode": "headless", "tier": "manual", "tags": ["landscape", "terrain"], "marker": "LANDSCAPE_RELIEF", "expected_s": 60, "timeout_s": 300}`

**VALIDATION.** `.\tools\run_tests.ps1 -Label t0_relief -Cache none -Files tools/LandscapeReliefAudit.cs,tools/landscape_relief_audit.gd,tools/test_manifest.json -DryRun`. The expected selection is `landscape_relief_audit` (direct), `landscape_world_test` (tag) and the validator. Then run it without `-DryRun`, then prune.

**ESCALATE IF** fewer than 24 anchors are found for a lowland class with the rule as written, or the consistency check fails after the FFT normalisation has been verified on a synthetic sine (add that check to the tool: a sine of 3 m amplitude at 256 m wavelength must give a band RMS of 2.12 m +- 2 % in the 256-512 m band).

---

### WO T0-3. Drainage-bake prototype and river map

**OBJECTIVE.** Prototype the plan section 3.1 bake offline on a 2,000 km strip at 1 km and at 500 m. Report depression, lake, channel, hydraulic and danger-class statistics, and draw a river map coloured by danger class for the user to review.

**FILES.** Create `tools/LandscapeDrainagePrototype.cs` and `tools/landscape_drainage_prototype.gd`, and add a manifest entry.

**INPUTS.** `LandscapeWorld` (shaped model and sampler, one copy per worker). Arguments:

| Argument | Default | Meaning |
|---|---|---|
| `--strip-start=<around m>` | auto | Auto: among 16 evenly spaced 2,000 km windows, pre-scanned at 4 km, the one with the most land; the start is recorded |
| `--strip-km=2000` | 2000 | strip length |
| `--cells=1000,500` | both | cell sizes (m) |
| `--breach=5,15,40` | | depression depth limits (m); deeper depressions become lakes |
| `--thresholds=25,50,100` | | channel catchment thresholds (km^2) |
| `--runoff=0.02` | 0.02 | m^3/s per km^2 at moisture 0.5, linear in moisture, floored at 0.1x |
| `--n=0.035` | 0.035 | Manning roughness |
| `--depth-coef=0.27`, `--depth-exp=0.4` | | depth `d = a Q^b` |
| `--gradient-km=5` | 5 | gradient baseline along the flow path |

**METHOD (required).**

1. **Grid.** Cell centres at `around = start + (i + 0.5) c`, `across = (j + 0.5) c`. Use `Parallel.For` over rows. For each cell:
   - one shaped `SampleDiagnostics` call: natural height `h = Clamp(MacroHeight + DetailHeight)`, shaped height, `SiteWeight`, `WeatherLungWeight`;
   - one `GetTerrainSample` on a per-worker sampler copy, for `Moisture`.
   Time the sampling.
2. **Cell types.**
   - Outlet: shaped height < 0 (ocean, including the Weather Lung basin).
   - Barrier: `SiteWeight > 0`, or `WeatherLungWeight > 0` with shaped height >= 0.
   - Edge outlet: land cells in the first or last column, tagged separately.
   - The across boundaries are walls (cells there have no neighbours outside the grid).
3. **Routing: priority-flood with flow directions** (Barnes et al. 2014).
   1. Seed the queue with every outlet and edge-outlet cell. The key is (height, cell index), so ties are deterministic.
   2. Pop the lowest cell. For each unvisited 8-neighbour that is not a barrier, set `filled[n] = max(h[n], filled[c])` and `down[n] = c`, then push n.
   3. Record the pop order.
   4. Land that the flood never reaches is "enclosed"; count it.
4. **Depressions.** A cell is depressed where `filled > h + 0.01`. Take 8-connected components, each with its area, maximum depth and spill level. For each breach limit B, a component is a lake if its maximum depth is above B; otherwise it is breached.
5. **Accumulation.** In reverse pop order, add each cell's catchment area (km^2, unweighted) and its runoff `Q` (m^3/s, moisture-weighted) to `down`.
6. **Channels and hydraulics.** Channels are cells whose catchment is at or above the threshold. Per channel cell:
   - gradient on the filled surface: `S_f = max((filled[c] - filled[c_L]) / pathlen, 1e-5)` over the `--gradient-km` downstream walk;
   - gradient on the original surface: `S_o = max((h[c] - h[c_L]) / pathlen, 1e-5)`;
   - depth `d = a Q^b`, mean speed `U = d^(2/3) S^(1/2) / n`, width `w = Q / (U d)`;
   - centre pool speed `1.5 U`, capped at 10 m/s.
   Class: calm (under 1.5), swift (1.5-4), fierce (4-7), torrent (over 7). Cells inside a lake at the middle breach limit are "lake" and are excluded from the class statistics. Confinement is not modelled; say so in the report.
7. **Statistics per cell size.**
   - Depressions: count, depth p50, p90 and max, area p50 and p90.
   - Lake area share of land, lake count and the lake size distribution, for each breach limit.
   - Drainage density (km of channel per km^2 of land; a diagonal step counts sqrt 2) for each threshold.
   - At the 50 km^2 threshold, p10, p50, p90 and max of Q, `S_f`, `S_o`, d, centre U and w.
   - Danger-class share by channel length and by cell count, under both `S_f` and `S_o`.
   - The downstream exit: for each fierce or torrent cell, the downstream distance to the first calm or swift cell, lake or ocean. Report the share within 5 km.
   - Where flow ends: share of channel cells that end at the ocean, at the Weather Lung or at a strip edge; also the enclosed land share and the share of the strip that is barrier.
   - Graph size: the node count at each threshold, and the bytes per node an equivalent graph would need for the plan's 16 MB gate.
8. **Sensitivity** (1 km only, post-processing only). Danger-class shares by length when each of `n` (0.030, 0.045), the depth coefficient (0.20, 0.35) and runoff (0.01, 0.04) is varied by itself away from the default.
9. **Timing.** Time for sampling, flood, accumulation and statistics, plus the thread count and the core count. Also the extrapolated whole-ring bake at 1 km: the strip time times 31,416 / strip-km.
10. **Maps.** `river_map_<cell>.png`, one pixel per cell, around to the right and across downward.
    - Land: hillshade grey, `0.35 + 0.55 * max(0, n . l)`, with light from (-1, 1, -1) and the normal from central differences.
    - Ocean: blue (0.10, 0.20, 0.45), darkened with depth.
    - Lakes (middle breach limit): (0.30, 0.65, 0.85).
    - Barriers: magenta (0.80, 0.20, 0.80).
    - Edge-outlet cells: yellow.
    - Channels at 50 km^2, coloured by class under `S_f`: calm (0.20, 0.45, 0.95), swift (0.15, 0.75, 0.35), fierce (0.98, 0.60, 0.10), torrent (0.90, 0.10, 0.10). Line width is 1 px below 100 m^3/s, 2 px for 100-1,000 m^3/s and 3 px above.
    - Also `river_map_<cell>_crop.png`: the 300 km window with the most fierce plus torrent length, scaled up 3x with nearest-neighbour sampling.
    The markdown gives the colour legend as text.
11. **Output.** `drainage_<cell>.json` and `.md`, and the PNGs, go to `.godot/audit_baseline/common/t0/drainage/<stamp>/`.

**ACCEPTANCE.**

- The invariants hold, as asserted checks: `filled >= h` everywhere; `filled` never increases downstream; every pointer chain ends at an outlet within `cells` steps (no cycles); the accumulated area at the outlets equals the reachable land area within 1e-6.
- It is deterministic: running the 1 km bake twice in-process gives the same hash of `down`.
- The whole run takes 180 s or less on the 4070.
- The report gives the strip start and whether the user's saved pose (around 27,984,761) lies inside the strip.
- Open both maps with the Read tool and describe them in the report: do the rivers reach the sea, and are there visible artefacts (parallel D8 lines, rivers along strip edges)?
- Paste the statistics tables into section 8.2.

**MANIFEST.** `"landscape_drainage_prototype": {"kind": "tool", "mode": "headless", "tier": "manual", "tags": ["landscape", "terrain"], "marker": "LANDSCAPE_DRAINAGE", "expected_s": 120, "timeout_s": 900, "parallel_safe": false}`

**VALIDATION.** `.\tools\run_tests.ps1 -Label t0_drainage -Cache none -Files tools/LandscapeDrainagePrototype.cs,tools/landscape_drainage_prototype.gd,tools/test_manifest.json -DryRun`. The expected selection is `landscape_drainage_prototype` (direct), `landscape_world_test` (tag) and the validator. Run it, then prune.

**ESCALATE IF** more than 20 % of the land is enclosed, more than 15 % of channel cells end at a strip edge with the auto strip, or more than 90 % of the channel length falls into a single danger class under both gradients. That last case is plan risk "the danger mix" and needs a design decision, not a constant change.

---

### WO T0-4. Inland water and swim audit

**OBJECTIVE.** Establish with tests, and with code reading where tests cannot reach, which inland-water paths (Lake, River) still work in a gen-17 world. Produce the gap list for T2 and T3.

**FILES.** Create `tools/LandscapeInlandWaterAuditTest.cs` and `tools/landscape_inland_water_audit_test.gd`, and add a manifest entry. Put the gap list in section 8.3 of this doc.

**TESTS (required).**

1. `gen17_has_no_inland_water`. On the `LandscapeWorld` sampler, take 100,000 lattice points plus 20,000 lowland local minima (found by steepest descent on the shaped height from deterministic starts at 64 m steps). Assert that no sample has `WaterKind` Lake or River, and that every water sample is Ocean with `WaterSurfaceHeight == 0`. This pins generation 1. It stays true after T2, because T2's rivers are a generation 2 feature.
2. `gen16_inland_samples_exist`. On `EmitterSiteTestWorlds.CreateGen16(1337)`, find at least one Lake and one River sample (deterministic scan, at most 2,000,000 probes). Record the coordinates. If none is found, `=SKIP` that sub-check with a note; do not fail.
3. `proxy_sector_inland_water`. Build a 64 m sector (32 cells at 2 m) over the gen-16 lake and another over the gen-16 river, using `RingTerrainProxySector.CreateAllocationFixtureSession`.
   - Take the session's private `_cache`, `_edits` and `_geometry` fields by reflection.
   - Invoke the private static `BuildRawSector` with the same arguments `AllocationFixtureSession.Build` passes.
   - Read `Colors`, `Vertices`, `Indices`, `OceanIndices` and `ShorelineTile` from the returned private `SectorData` by reflection.
   - Decode the water kind per vertex with the inverse of `RingWaterModel.EncodeWaterKind`. Write a local decoder after reading the encoder; do not change it.
   Assert and record: Lake and River vertices are present; how many triangles go to `OceanIndices` (expected: none, because the FFT and shoreline paths are Ocean-only, at `RingTerrainProxySector.cs` lines 3989-3995, 4179 and 4371); and whether a water surface is emitted for inland cells at all.
4. `water_kind_encoding_roundtrip`. `RingWaterModel.EncodeWaterKind` round-trips for None, Ocean, Lake and River.
5. `ocean_proxy_shader_profiles`. Parse `materials/ring_ocean_proxy.gdshader` (and its includes) as text. Record which kind profiles (lake, river) exist and which uniforms drive them. Assert that they are present, since plan 1.1 item 5 says they exist.

**CODE READING (required; each row cites file:line).** Cover, at least:

- the controller's gen-17 ocean override (`RingVoxelExplorerController.cs:1154`) and the swim entry and float logic (`ProcessSwimming`, swim entry at 0.55 m, the integer `WaterSurfaceHeight`);
- the water ambience kind (`_waterAmbienceKind`, around line 1281) and the river ambience asset path;
- `RingTerrainSampler.SurfaceGeneration.cs:65` and `:158` (the only gen-17 water rule);
- `RingWaterFftDemand`;
- `RingTerrainMinimap` (Lake and River colours);
- far terrain and global LOD water (`RingTerrainGlobalLod`, `RingScaledFarViewport`: is water represented by kind?);
- grass and tree water masks (`ForestRegionSampler.Sample` rejects `HasSurfaceWater`);
- the wet-world and wetness consumers;
- `RingGustFrontDebris`;
- `RingTerrainVisuals` (the Lake and River biome tint);
- `TerrainBiomeId.Lake` and `TerrainBiomeId.River` in biome selection.

**GAP LIST FORMAT** (section 8.3):

| Path | File:symbol | Gen-17 today | Works for Lake/River? | Evidence (test or line) | Needed by (T2 WO / T3 WO) | Size (S/M/L) |

**OUTPUT.** Write `inland_water_audit.json` and `.md` (the found coordinates and the per-test results) to `.godot/audit_baseline/common/t0/water/<stamp>/`. Print `LANDSCAPE_INLAND_WATER=PASS`.

**MANIFEST.** `"landscape_inland_water_audit_test": {"kind": "test", "mode": "headless", "tier": "gate", "tags": ["landscape", "water", "terrain"], "marker": "LANDSCAPE_INLAND_WATER", "expected_s": 40, "timeout_s": 180}`

**VALIDATION.** `.\tools\run_tests.ps1 -Label t0_water -Cache none -Files tools/LandscapeInlandWaterAuditTest.cs,tools/landscape_inland_water_audit_test.gd,tools/test_manifest.json -DryRun`. The expected selection is `landscape_inland_water_audit_test` (direct and tag). Tag `landscape` may also select `landscape_world_test` if T0-1 has landed; tags `water` and `terrain` add nothing, because the changed files are not under their globs. Run it, then prune. Prove that test 1 can fail by temporarily asserting that Lake is present.

**ESCALATE IF** the reflection route cannot call `BuildRawSector`. Do not add a seam to `scripts/`. Report it, so the lead can decide whether a test-only partial next to `RingTerrainProxySector.Allocation.Tests.cs` is justified.

---

### WO T0-5. Tree pack audit in engine

**OBJECTIVE.** Confirm plan section 7.1 against the prepared catalog the game loads, and pin the facts T6 builds on.

**FILES.** Create `tools/LandscapeTreePackAuditTest.cs` and `tools/landscape_tree_pack_audit_test.gd`, and add a manifest entry. The tables go in section 8.4.

**REQUIRED BEHAVIOUR.**

1. Load `ImportedTreeCatalog.Load()`, the production path, which uses the prepared catalog. For each of the 30 variants record:
   - index, species and name;
   - authored size class: parsed from the name (sapling, small, medium, big, large). When the name does not say, fall back to the height rank within the species, and mark the row "inferred";
   - `HeightMeters`, `WidthMeters`, `TrunkRadiusMeters` and `TrunkHeightMeters`;
   - per LOD (0-3): surface count, triangle count per surface, AABB height;
   - per surface material: the shader resource path, and which texture uniforms are bound (albedo, normal, metallic-roughness or ORM), with each texture's size.
2. Record the shared far impostor: layer count and atlas or texture sizes, from the catalog's impostor resource. Find it through `ImportedTreeCatalog.Prepared.cs`.
3. Record the default distances of the compact LOD profile: create a `RingBiomeDecorationLayer` without adding it to a tree and read `TreeNearDetailFadeStartMeters`, `TreeNearDetailFadeEndMeters`, `TreeBillboardFadeStartMeters`, `TreeBillboardFadeEndMeters`, `Lod1DistanceMeters` to `Lod3DistanceMeters`, `MaximumLod5DistanceMeters` and `VisibilityRadiusMeters`. Free it afterwards. Record also whether the user's saved vegetation settings could override them: name the `GameplayVegetationDefaults` fields that map onto them; do not read the user's file.
4. Selection check. Build 64 production tree tiles with `BuildImportedForestReviewTile` on the `LandscapeWorld` sampler, at the forest-focus poses from the T0-1 fixture if it exists, otherwise at the first 64 lattice points with DenseForest habitat. Build the variant histogram per species. Assert that, for each species, the share of each variant is uniform within 3 binomial standard errors. This is the plan's claim that `SelectForestVariant` ignores size classes. Report the sapling share of maples (expected about 25 %).
5. Contract assertions (they pin what T6 assumes):
   - `ImportedTreeCatalog.VariantCount == 30`, split 12, 3 and 15 (maple, fir, pine);
   - every variant has 4 LODs;
   - every LOD of every variant has separate bark and foliage surfaces (2 surfaces, or name the exception);
   - LOD3 has 64 triangles or fewer.
   If an assertion contradicts plan 7.1, do not weaken it. Report the difference as a finding and escalate.
6. Write `tree_pack_audit.json` and `.md` to `.godot/audit_baseline/common/t0/trees/<stamp>/`. Print `LANDSCAPE_TREE_PACK=PASS`.

**MANIFEST.** `"landscape_tree_pack_audit_test": {"kind": "test", "mode": "headless", "tier": "gate", "tags": ["landscape", "vegetation"], "marker": "LANDSCAPE_TREE_PACK", "expected_s": 25, "timeout_s": 150}`

**VALIDATION.** `.\tools\run_tests.ps1 -Label t0_trees -Cache none -Files tools/LandscapeTreePackAuditTest.cs,tools/landscape_tree_pack_audit_test.gd,tools/test_manifest.json -DryRun`. The expected selection is `landscape_tree_pack_audit_test` (direct and tag), plus `landscape_world_test` (tag) once T0-1 has landed. Run it, then prune.

**ESCALATE IF** the prepared catalog cannot load headless, or a contract assertion contradicts plan 7.1.

---

### WO T0-6. Generation 1 fingerprint

**OBJECTIVE.** Create `world_generation_fingerprint_test` with its golden and the generation 1 row: hashes that prove a later generation step did not change generation 1. T1's work order 3 then only adds generation keying and the contract test (section 6).

**FILES.** Create `tools/WorldGenerationFingerprint.cs` (the computer), `tools/WorldGenerationFingerprintTest.cs` and `tools/world_generation_fingerprint_test.gd`, and the golden `tools/fixtures/world_generation_fingerprint_golden.json`. Add a manifest entry.

**ROW CONTENT (required).** Everything is computed from `LandscapeWorld`; nothing reads `user://`.

1. `heights`: 4,096 points from a fixed 64-bit LCG (seed `0x5EED_1337`), with `around` in [0, C) and `across` in [0, 318,000]. FNV-1a 64 over the IEEE bits of shaped `CalculateHeight` (double).
2. `sampler`: the same points through `GetTerrainSample`. FNV-1a over `SurfaceHeight`, `WaterKind`, `WaterSurfaceHeight`, `Biome`, `Region`, and the bits of `Moisture` and `Temperature`.
3. `trees`: eight 240 m production tiles, the ones containing the poses `plains_eye`, `hills_eye`, `coast_eye`, `forest_interior_eye`, `forest_edge_eye`, `forest_clearing_eye`, `v0_ridge_eye` and `v0_centre_eye` (tile `tx = floor(around / 30) / 8`, `tz = floor(across / 30) / 8`, as in `ImportedForestPlacementRegressionTest`). For each tile: the record count and an FNV-1a over every record field in order (`Id`, `Around`, `Across`, `SurfaceHeight`, `Width`, `Height`, `Yaw`, `Variant`, `Species`).
4. `grass`: eight 32 m card tiles at the same poses, built by `RingGrassTileBuilder.Build` in the Cards placement mode. Take the arguments from the production call in `RingStandingGrassLayer` with the layer's default card settings at the Medium preset, and list every argument value in the row as `grass_params`. For each tile: the instance count and an FNV-1a over the instance floats. Copy the fixture setup (geometry snapshot, empty edit snapshot) from `GrassCardPlacementTest`.
5. `scatter`: `{"count": 0, "hash": "none"}`. The slot is reserved for T5.
6. `catalog`: `CatalogVersion`, `VariantCount`, and an FNV-1a over each variant's (index, species, name, height bits, width bits, trunk radius bits, then per LOD the surface count and the vertex count per surface).
7. Metadata: `generation: 1`, `label: "gen1-v8-s1337"`, generator id, seed, definition hash, the T0 fixture path, and the date it was captured.

**GOLDEN.** The golden is `{"schema": 1, "rows": [<row>]}`. `--capture` appends a row only when no row with that generation exists, and never rewrites one. Without flags, the test recomputes row 1 and compares every field; it prints the differing fields and fails if any differ. Hashes are printed as `0x%016X`.

**ORDER.** Capture the golden only after the Cards default change is committed and the working tree has no uncommitted grass or vegetation changes from other agents. Until then, develop and run without `--capture`.

**ACCEPTANCE.**

- Two runs give identical rows.
- It finishes within 60 s.
- Prove that it can fail: temporarily change one LCG constant in a scratch build and see the heights hash change, then revert.
- Paste the row summary into section 8.5.

**MANIFEST.** `"world_generation_fingerprint_test": {"kind": "test", "mode": "headless", "tier": "gate", "tags": ["landscape", "terrain", "vegetation"], "marker": "WORLD_GENERATION_FINGERPRINT", "expected_s": 30, "timeout_s": 150}`

**VALIDATION.** `.\tools\run_tests.ps1 -Label t0_fingerprint -Cache none -Files tools/WorldGenerationFingerprint.cs,tools/WorldGenerationFingerprintTest.cs,tools/world_generation_fingerprint_test.gd,tools/fixtures/world_generation_fingerprint_golden.json,tools/test_manifest.json -DryRun`. The expected selection is `world_generation_fingerprint_test` (direct and tag `landscape`), `landscape_world_test` (tag) and the validator. Run it, then prune.

**ESCALATE IF** two runs differ, which would mean tree or grass placement is nondeterministic in a way the game would also show, or the production grass card arguments cannot be determined without running the game.

---

### WO T0-7. Look references and cost baseline (GPU)

**OBJECTIVE.** Record what the generation 1 world looks like and costs at the T0 poses: look references per machine for later `compare` heatmaps, and a cost baseline on the A1000 for the plan section 12 gates. Also check visually whether the tree LOD0 to LOD2 switch shows.

**PRECONDITIONS.**

- T0-1 is done and the Cards default is committed.
- Look part: run on both machines.
- Cost part: authoritative only on the A1000 at P0 / 35 W, undocked, with the memory clock at 5,300-5,700 MHz; see the `perf-capture` skill. On the 4070 the output is stamped "non-authoritative".

**FILES.**

| Action | File |
|---|---|
| create | three thin wrappers, one per part, so each has its own manifest entry: `tools/landscape_look_baseline.gd`, `tools/landscape_cost_baseline.gd` and `tools/landscape_lod_switch_capture.gd`. Shared flags: `--poses=a,b`, `--prefs=`, `--preset=`; the cost wrapper also takes `--rounds=2` and `--seconds=8`. The wrappers name no `.cs` file |
| create | `tools/landscape_baseline_common.gd`: the shared part bodies, and the hide cases copied from `terrain_render_attribution_test.gd` (`_capture_original_state`, `_apply_case`, `_restore_original_state`, `_verify_case` with its draw-call delta check, `_enable_viewport_timing`), cut down to the cases below |
| create | `tools/LandscapeTimingBridge.cs`: the `ShadowTimingBridge` pattern, draining both CPU (`RingPerformanceTelemetry.Drain`) and GPU (`DrainGpu`), with per-name lists and window stats |
| create, optional | `tools/landscape_pass_timestamp_effect.gd`: a `CompositorEffect` that calls `rd.capture_timestamp(<name>)` at `PRE_OPAQUE`, `POST_OPAQUE` and `POST_TRANSPARENT` |
| edit | manifest entry |

**REQUIRED BEHAVIOUR.**

1. **Common.**
   - Boot through `landscape_harness_common.gd` (`--prefs=user` and `--preset=medium` by default) at 1920x1080 (`root.size`), with VSync off and `Engine.max_fps = 0`.
   - Hold the clocks.
   - Record: machine info; preset; grass style; the effective tree fade distances (read from `BiomeDecorations`); the preferences snapshot; the hash of the atmosphere settings file used; the Godot version.
2. **Look part (`landscape_look_baseline`).**
   - For the nine `t0` poses at `noon` and `sun_8_set`, keeping AA as configured: capture, compute bands, and write `<pose>__<time>.png` and `_bands.png` into `audit_baseline/<machine>/t0/look/<stamp>/`.
   - Write `look.json` (band tables, sun elevation, idle flags) and `reference_set.json` naming the compare references: `plains_eye__noon`, `hills_eye__sun_8_set`, `forest_edge_eye__noon`, `coast_eye__noon` and `hills_2km__noon`.
   - It passes when every view is non-black (mean linear luma above 0.01), the world is verified, `sun_8_set` lands within +-0.5 degrees, and `streaming_idle` is true (a false is recorded with its reason and fails the view).
   - Print `LANDSCAPE_LOOK=PASS`.
3. **Cost part (`landscape_cost_baseline`).**
   - Start the power log with `COMMON.start_power_log` into the baseline folder. Disable `RingPerformanceMonitor` (`process_mode = DISABLED`), as `shadow_attribution_test.gd` does, and start `LandscapeTimingBridge` with engine timestamps on.
   - Pose-times: the nine `t0` poses at `noon`, plus `hills_eye` and `forest_edge_eye` at `sun_8_set` (11 in total).
   - For each pose-time:
     1. **Fill.** From the teleport until builds are idle, collect the CPU scopes `Trees build`, `Trees disk load` and `Grass build` (count, median, p95, sum) and the wall time to idle.
     2. **Cases.** Run `baseline`, `near_terrain_hidden` (Terrain3D plus the near proxy sectors), `far_terrain_hidden`, `trees_hidden`, `grass_hidden` and `baseline_repeat`, with `--rounds` rounds, the second in reverse order. Each window: 60 warm-up frames, 1 s settle, then `--seconds`.
     3. **Per window:** root and far viewport GPU ms (median, p95); CPU frame ms; draw calls and primitives (Performance monitors); the GPU markers `TreeCulling`, `GrassCulling`, `Vegetation`, `ProxyHeights`, `HeightShadow` and `WaterFft`; the optional opaque bracket; `COMMON.power_window` validity.
   - Derived values: the hide delta per class (median of baseline minus hidden), the noise floor (`|baseline - baseline_repeat|`), the draw-call and primitive delta per class, the medians of the compute markers, and tree counts from `TreeRenderTimingBridge.Capture`.
   - Write `cost.json` and `cost.md` (pose rows by metric columns, the noise floor in its own column, invalid power windows marked).
   - It passes when every case passed `_verify_case` and at least 80 % of the windows are power-valid. On the 4070 they are always valid.
   - Print `LANDSCAPE_COST=PASS`.
   - **Optional opaque bracket, timeboxed to 1 hour.** Attach the timestamp effect at the three callbacks. If the names do not appear in `ListEngineTimestampNames()` within 120 frames, record "opaque bracket unavailable" and go on without it.
4. **LOD-switch part (`landscape_lod_switch_capture`).**
   - At `forest_edge_eye`, take the nearest tree record ahead (from a `LandscapeWorld` tile build). Hold the camera at eye height looking at its trunk midpoint.
   - Dolly sequence: 180 frames from 15 m to 60 m in 0.25 m steps. Control sequence: 180 frames at a fixed 15 m.
   - Save each frame by grabbing `root.get_texture().get_image()` after 2 frames, as `frame%08d.png`, into `.godot/captures/landscape/t0/lod_switch/<stamp>/{dolly,control}/`.
   - Record the distance per frame in `frames.json`.
   - Hold the vegetation wind if a public hook exists. If not, the control sequence gives the wind's noise floor.
   - Print `LANDSCAPE_LOD_SWITCH=PASS`.
   - Then, in PowerShell:
     ```powershell
     .\tools\media\ffmpeg_tools.ps1 sheet -Path .godot\captures\landscape\t0\lod_switch\<stamp>\dolly\frame.png -Columns 6 -Rows 4
     .\tools\media\ffmpeg_tools.ps1 flicker -Path ...\dolly\frame.png -Top 10
     .\tools\media\ffmpeg_tools.ps1 sheet -Path ...\control\frame.png -Columns 6 -Rows 4
     .\tools\media\ffmpeg_tools.ps1 flicker -Path ...\control\frame.png -Top 10
     ```
   - Copy the two sheets and the flicker output into `audit_baseline/<machine>/t0/lod_switch/<stamp>/`, then delete the frame folders.
   - Verdict: a dolly spike between 25 and 40 m that exceeds the control's largest spike by 2x is a visible switch.
5. **Report.** Paste `cost.md`, the look band summary for the reference set and the LOD verdict into section 8.6.

**MANIFEST.**

```json
"landscape_look_baseline": {"kind": "tool", "mode": "gpu", "tier": "manual", "tags": ["landscape", "render-core"], "marker": "LANDSCAPE_LOOK", "expected_s": 1500, "timeout_s": 2400},
"landscape_cost_baseline": {"kind": "tool", "mode": "gpu", "tier": "manual", "tags": ["landscape", "render-core", "perf-telemetry"], "marker": "LANDSCAPE_COST", "expected_s": 2400, "timeout_s": 3600},
"landscape_lod_switch_capture": {"kind": "tool", "mode": "gpu", "tier": "manual", "tags": ["landscape", "vegetation"], "marker": "LANDSCAPE_LOD_SWITCH", "expected_s": 400, "timeout_s": 1200}
```

**VALIDATION.**

```powershell
.\tools\run_tests.ps1 -Label landscape_gpu -Tests landscape_look_baseline,landscape_cost_baseline,landscape_lod_switch_capture -DryRun
# first a quick pass of each by hand (godot-test-run manual fallback, APPDATA = the landscape_gpu runner profile):
#   -- --poses=plains_eye   (cost also: --rounds=1 --seconds=4)
.\tools\run_tests.ps1 -Label landscape_gpu -Tests landscape_look_baseline
.\tools\run_tests.ps1 -Label landscape_gpu -Tests landscape_lod_switch_capture
.\tools\run_tests.ps1 -Label landscape_gpu -Tests landscape_cost_baseline     # A1000 for the authoritative numbers
.\tools\prune_test_output.ps1 -Apply
```

The expected selection is exactly the named tool or tools. Look at the reference PNGs and the two LOD sheets with the Read tool before reporting.

**ESCALATE IF**:

- the hide cases do not change the draw count (the tree or grass geometry is not reachable by layers; the GPU-driven trees may need a different hide path, and that needs a design decision);
- the A1000 drops out of P0 for more than 20 % of the windows (split the run with `--poses=`);
- the world fails verification on either machine.

## 4. Work orders (vastness V0, separable)

V0 reuses T0-1 unchanged: the pinned world, the pose fixture rows marked `v0`, the time table, the band table and the GPU boot. Its own parts are the analytic distance for backdrop pixels, the ladder table and the fly-out. The vastness plan's matrix is too large to run in full (nine poses x five headings x four times, plus A/B per class). This spec reduces it as follows: every `v0` pose with every heading at `noon`, plus `v0_centre_eye`, `v0_wall_lo_eye` and `saved_18km` with every heading at `sun_8_set`, `sun_2_set` and `night_planet`. That is about 50 views and fits the plan's estimate of about 15 GPU minutes.

### WO V0-1. Vastness views: analytic distance, band and ladder tables

**OBJECTIVE.** For the V0 view set, produce plan V0 outputs 1-3: the colour capture with a distance map, the band table with the tier metric, and the ladder table.

**FILES.**

| Action | File |
|---|---|
| create | `tools/VastnessGeometry.cs`: the ray to ring floor and rim walls, closed form |
| create | `tools/VastnessGeometryTest.cs` and `tools/vastness_geometry_test.gd` |
| create | `tools/VastnessLadder.cs`: the ladder objects from the registries, with their screen projection |
| create | `tools/vastness_audit_common.gd`: the module that loads the two C# helpers; the wrappers reach them only through it (section 1 item 9) |
| create | `tools/vastness_audit.gd`: the wrapper; it names no `.cs` file |
| edit | manifest entries |

**REQUIRED BEHAVIOUR.**

1. **`VastnessGeometry.Distance(cameraVertical, cameraAcross, dirAround, dirVertical, dirAcross)`.** The direction is in the local canonical frame at the camera.
   - Use the floor cylinder of radius `R = C / Tau` from the ring axis, with the camera at radius `R - v0`.
   - In the cross-section, solve `t^2 (da^2 + dv^2) - 2 t dv (R - v0) + (R - v0)^2 - R^2 = 0` and take the positive root, where t is the 3D path length.
   - If `across + t * dz` leaves [0, W], intersect the wall plane. It is a wall hit if the radial height there is within the wall height. Take the wall height and geometry from `RingTerrainShadowGeometry`, which already holds `RayHitsStructuralWall`; do not hard-code 10 km. Otherwise the ray leaves over the wall and counts as "beyond".
   - Return the distance and the kind (floor, wall or beyond).
   - Per-pixel rays use the camera projection (vertical FOV) and the explorer yaw and pitch basis. Forward is `(cos yaw cos p, sin p, sin yaw cos p)` in (around, vertical, across). Right is `(sin yaw, 0, -cos yaw)`; that sign follows `RingVoxelExplorerController.cs:984`.
2. **`vastness_geometry_test`** (headless) has closed-form cases: straight down gives `v0`; horizontal along the ring gives the chord `2 sqrt(2 R v0 - v0^2)` within 1e-6 relative; horizontal toward the wall at across 30 km from 2 m height gives 30 km; straight up from the floor gives `2R`; and a downward ray toward a wall at 5 km hits the floor before the wall. Print `VASTNESS_GEOMETRY=PASS`.
3. **`VastnessLadder.List(game, camera)`.** Enumerate:
   - emitter sites and their spires (`EmitterSiteRegistry`, through the shaper of the loaded recipe);
   - projector towers (`AtmosphereProjectorSites`);
   - scale anchors (`RingScaleAnchorLayer`: use a public accessor if there is one; otherwise reproduce its deterministic scatter from its exported cell size, chance and seed, and verify 10 positions against the live nodes);
   - climate stations (`ClimateMachineRegistry`; the v8 default has `climate_machines` 0, so this row is "absent");
   - trees within 5.5 km (from `LandscapeWorld` tile builds, the nearest 200).
   For each object in the frustum: its type, distance, band, projected height in px (`h * (H/2) / tan(vfov/2) / d`), the screen rect, and a detection flag: `|mean L(rect) - mean L(3 px annulus)| >= 2/255` in sRGB, with the rect at least 1 px. V0-2 validates this heuristic.
4. **`vastness_audit.gd`** (GPU) works through the view set above.
   - The wall-top view is added at run time: over the wall at across 0, at the wall-top height from `RingTerrainShadowGeometry`, plus 5 m, looking along the ring. Use the same placement as `tools/rim_wall_check.gd`'s `top_along` view.
   - Per view, write the capture and the band table, with the main depth where it exists and the analytic distance for "no distance" pixels on a 240 x 135 grid, upsampled by nearest neighbour.
   - Write the tier metric (`adjacent_steps`) and the ladder table.
   - `results.md` contains the three tables and a list of empty bands: bands holding 2 % or more of the screen with no detected ladder object and no regular-pitch feature in at least 80 % of the ground views. The list is computed, not judged.
   - Output: `audit_baseline/<machine>/v0/views/<stamp>/`. Print `VASTNESS_AUDIT=PASS`.
5. **Acceptance.** On `plains_eye`-like flat ground, the analytic floor distance agrees with the main depth within 10 % for terrain pixels at 2-8 km. Check this in the tool and fail if it does not hold.

**MANIFEST.**

```json
"vastness_geometry_test": {"kind": "test", "mode": "headless", "tier": "gate", "tags": ["vastness"], "marker": "VASTNESS_GEOMETRY", "expected_s": 5, "timeout_s": 60},
"vastness_audit": {"kind": "tool", "mode": "gpu", "tier": "manual", "tags": ["vastness", "atmosphere-haze"], "marker": "VASTNESS_AUDIT", "expected_s": 1200, "timeout_s": 2400}
```

**VALIDATION.** `.\tools\run_tests.ps1 -Label v0_geometry -Cache none -Files tools/VastnessGeometry.cs,tools/VastnessGeometryTest.cs,tools/vastness_geometry_test.gd,tools/test_manifest.json -DryRun`. The expected selection is `vastness_geometry_test` (direct and tag `vastness`) and the validator. If `vastness_audit` appears, a wrapper names a `.cs` file; fix that. Then run `.\tools\run_tests.ps1 -Label landscape_gpu -Tests vastness_audit` and prune.

### WO V0-2. Vastness fly-out and vanish distances

**OBJECTIVE.** Measure, for each ladder object type, the distance at which it actually vanishes, compared with the theoretical 1.5 px distance, and produce sheets.

**FILES.** Create `tools/vastness_flyout.gd`. It reaches `VastnessLadder` only through `tools/vastness_audit_common.gd`. Add a manifest entry.

**REQUIRED BEHAVIOUR.**

1. Object types: tree, minor tower, major tower, emitter spire, projector tower, and a wall cue line. For each, pick the nearest instance to a fixture pose.
2. Camera path: along the line from the object outward, at a fixed 3 degree elevation above the object's base, looking at its mid-height. Use 64 log-spaced distances from 10 m to 300 km (ratio about 1.178).
3. At each distance:
   - **Visible:** 24 frames to let TAA settle, then a capture.
   - **Hidden:** the same, with the object's class hidden (layer or visibility toggle; for trees, the tree layer).
   - **Diff:** the maximum absolute sRGB difference inside the projected rect.
   - Save the visible frame as `frame%08d.png` into `.godot/captures/vastness/v0/flyout/<type>/`.
4. The vanish distance is the first distance after which the difference stays below 3/255 for 3 steps in a row. The theoretical distance is `h * focal_px / 1.5`. Also re-run the V0-1 detection heuristic on each visible frame, and report the agreement rate with the A/B result.
5. ffmpeg: `sheet` per type (6 x 4 tiles). Copy the sheets into `audit_baseline/<machine>/v0/flyout/<stamp>/`, then delete the frame folders.
6. `results.md` gives per type: height, theoretical distance, measured vanish distance, their ratio, and the heuristic agreement. Print `VASTNESS_FLYOUT=PASS`.

**MANIFEST.** `"vastness_flyout": {"kind": "tool", "mode": "gpu", "tier": "manual", "tags": ["vastness"], "marker": "VASTNESS_FLYOUT", "expected_s": 1200, "timeout_s": 2400}`

**VALIDATION.** `.\tools\run_tests.ps1 -Label landscape_gpu -Tests vastness_flyout -DryRun`, then the run, then `ffmpeg_tools.ps1 sheet`, then delete the frames and prune.

## 5. What each output means for later phases

| Output | Consumer | How it is used |
|---|---|---|
| Relief spectrum, decade RMS per class (8.1) | T7 design and calibration | Targets for the 3-30 m and 30-300 m decades per class. The section 1.3 gap is confirmed or corrected. The band-limit rule in T7 uses the bands coarse LODs must drop |
| Slope and 4 km relief percentiles (8.1) | T5, T6, T7 | Scatter and community slope thresholds; T7 must keep plains p95 slope at or below the lowland plan's 10 degrees |
| Drainage statistics at 1 km and 500 m (8.2) | T2 design | Grid size, breach limit (the default lake abundance), catchment threshold, node count against the 16 MB gate, bake time against the 20 s gate G1 (from the extrapolation), and how the bake treats sites and walls |
| Danger-class mix and sensitivity (8.2) | T3 design | The roughness, caps and ferocity default needed to reach "at least 70 % calm or swift, 5-10 % fierce, at most 3 % torrent". The downstream-exit share sizes the softening rule. A one-class result is a design stop (plan risk "the danger mix") |
| River map (8.2, PNG) | the user | Their first look at rivers before T2 builds anything |
| Inland water gap list (8.3) | T2 WOs 5-7, T3 WOs 3 and 7 | Which water paths need work (water kind, float level, proxy mesh, shader profiles, far water, minimap, controller, ambience) and which already work |
| Tree pack audit (8.4) | T6 WOs 1-4, plan 7.1 items 8-9 | Size-class metadata, the understory class, bark-only snags, shrub atlases. The LOD-switch verdict decides whether T6 restores LOD1 |
| Generation 1 fingerprint (8.5) | T1 WO3 and every freeze | Becomes the first row of the append-only ledger test |
| Look references (8.6) | every phase report | The required `compare` heatmap of a generation 1 pose (plan 2.2), per machine |
| Cost baseline (8.6) | section 12 gates for T2-T8 | Baseline hide deltas, noise floors, draw counts per class, compute markers, and the "Trees build" median (the T5 gate of "tile build at most 1.3x") |
| Poses, times, band table | V0, V1, and every later look review | One set of views for both plans |

## 6. Notes for T1

- **Publication point.** Publish the generation and the feature mask in the launcher's `_configure_game`, before `add_child` (section 1 item 2), and default to generation 1 when nothing has been published.
- **Slot names.** Use `world.json` `name` (section 1 item 1).
- **Test-world climate.** Give `EmitterSiteTestWorlds` the production climate settings when it gains its generation argument (section 1 item 3), or document that its worlds are not production climate.
- **Fingerprint scope.** `world_generation_fingerprint_test` exists after T0-6. T1 WO3 shrinks to generation keying (a generation argument for `WorldGenerationFingerprint`, so rows are computed with the ledger features of each generation) plus `world_generation_contract_test`.

## 7. Risks, unknowns and decisions

**Risks.**

- **Raster cost precision.** Terrain, tree and grass draw costs exist only as hide deltas over a 3-6 ms noise floor (section 1 item 6). Gates of 0.3-0.5 ms (T4-T6) need several A-B-A rounds per pose, and some may only be judged by draw counts and compute markers. The optional opaque bracket in T0-7 may help; if it does not work, that becomes a known limit of the budget table.
- **A1000 throttling.** The cost part takes about 40 minutes. The laptop can drop to P5 after 45-60 minutes. The power-log validity rule and `--poses=` re-runs cover this.
- **Drainage prototype bias.** Point samples at 1 km alias the 256 m and 32 m detail. Gradients on the filled surface make breached sills look calm, while the plan expects water gaps to be fierce; the original-surface gradient brackets this. Confinement is not modelled. The moisture field is the settings noise, which plan 3.1 later moves into the recipe with equal defaults. The strip's ends drain off the strip. Every one of these is reported, and none should be fixed inside T0.
- **Harness world fidelity.** If the in-game sampler and `LandscapeWorld` ever differ, every headless number is about the wrong world. The T0-1 smoke and every GPU boot check 64 points exactly.
- **Pose fixture drift.** If generation 1 terrain changes by accident, `landscape_world_test` fails on `ground_m`. That is intended: it is a second fingerprint.
- **Reflection in the water audit.** It depends on private names in `RingTerrainProxySector`. If they are renamed, the test fails loudly; it never falls back silently.
- **Fingerprint brittleness.** It hashes float bits by design. A refactor that changes rounding in generation 1 code is a generation 1 change and must fail.
- **Known Godot 4.8-dev7 issues.** It can hang on exit (WorkerThreadPool). A PASS marker with a non-zero exit is a known engine problem, not a failure (godot-test-run skill).
- **GPU contention.** Other agents share the GPU queue. The T0-7 and V0 runs are long; use `-YieldMinutes` if another agent is waiting.

**For the primary agent (not user decisions).**

- Whether to fix the shadow harness preference file names (section 1 item 4). Fixing them changes what future shadow baselines measure.
- The deviation from "full tier only": report generators are manual tools (section 2.9).

**Needs a user decision.**

1. **Where the cost baseline runs.** The authoritative T0-7 cost part must run on the A1000 laptop, undocked at 35 W. This session is on the RTX 4070 desktop. The look part should run on both machines.
2. **Whose settings the baseline uses.** The look and cost baseline uses the user's current grave-menu settings, snapshotted with each run, not the shipped defaults (recommended: it measures the game as they play it, and the snapshot keeps later comparisons fair). Their atmosphere settings differ from the defaults (haze brightness 0.52, aerosol 0).
3. **River map strip (minor).** The default is the 2,000 km window with the most land. The alternative is a window centred on their usual area, around their saved pose.

## 8. Results (filled in by the work orders)

### 8.1 Relief spectrum (T0-2)

Run 2026-11-09 on the RTX 4070 desktop (headless), world `0x1480032CB30B96CA`, 2.1 s in the tool. Output: `.godot/audit_baseline/common/t0/relief/20261109_233516/relief_spectrum.json` and `.md`. Deterministic: two runs give identical JSON apart from the trailing `run` object.

Deviations from the spec, all forced by the data:

- **Sine check** uses 362 m (the band centre), not 256 m. 256 m sits on the 128-256 / 256-512 band edge, and Hann leakage then always puts about a sixth of the power in the neighbouring band (measured 1.94 m in 256-512 and 0.87 m in 128-256, total 2.121 m). At 362 m the band reads 2.1213 m.
- **Consistency check** compares 1 m sampling with 32 m sampling over the same 4,096 m window. All classes agree within 7.5 % (worst CoastalBelt, where the signal is about 0.004 m). The spec's 65-131 km coarse transects cannot be compared with the 4 km fine ones: they leave the anchor's class and read 2-3 times higher in Plains and PlateauInterior at 128-512 m (last table, informational). The main table therefore uses the fine transects below 512 m (class-local) and the wide coarse transects from 512 m up, which average over neighbouring landscape.
- **PlateauInterior has 22 anchors, not 24.** Only 67 lattice points are PlateauInterior; 38 fail the 1.5 km ring and 7 the across range. The rule as written cannot give 24, so the tool warns instead of failing. Lead decision 2026-11-09: accept 22 anchors with the warning; the class is 0.4 % of the lattice and 22 is enough for medians. Revisit only if T7 targets depend on plateau percentiles.
- The site check at transect ends and middle also rejects `WeatherLungWeight > 0`; around positions wrap modulo the circumference; the 2D slope grid is 20 x 20 at 100 m spacing centred on the anchor.
- Lattice class shares: Ocean 48.6 %, Mountain 33.1 %, CoastalBelt 11.9 %, Rolling 2.0 %, Plains 1.7 %, HillCountry 1.7 %, Breaks 0.5 %, PlateauInterior 0.4 %.

Relief spectrum, world 0x1480032CB30B96CA, stamp 20261109_233516, run time 2.2 s. CoastalBelt heights are clamped at 0 (water surface).

Anchors per class (lattice share): CoastalBelt 24 (11.9 %), Plains 24 (1.7 %), Rolling 24 (2.0 %), HillCountry 24 (1.7 %), PlateauInterior 22 (0.4 %), Breaks 24 (0.5 %), Mountain 24 (33.1 %)

Band RMS height in metres, median (p90) over anchors and both directions. Wavelengths below 512 m come from the 4,096 m transects sampled at 1 m (local to the anchor), 512 m and above from the 32 m transects (65-131 km long, so they average over neighbouring landscape).

| Band | CoastalBelt | Plains | Rolling | HillCountry | PlateauInterior | Breaks | Mountain |
|---|---|---|---|---|---|---|---|
| 2 m-4 m | 0.001 (0.011) | 0.012 (0.020) | 0.033 (0.046) | 0.054 (0.070) | 0.009 (0.015) | 0.053 (0.094) | 0.033 (0.106) |
| 4 m-8 m | 0.001 (0.009) | 0.008 (0.014) | 0.021 (0.029) | 0.036 (0.052) | 0.007 (0.012) | 0.038 (0.075) | 0.027 (0.073) |
| 8 m-16 m | 0.001 (0.016) | 0.014 (0.018) | 0.044 (0.070) | 0.071 (0.089) | 0.014 (0.017) | 0.079 (0.140) | 0.046 (0.097) |
| 16 m-32 m | 0.001 (0.043) | 0.040 (0.044) | 0.117 (0.184) | 0.194 (0.226) | 0.037 (0.043) | 0.245 (0.544) | 0.102 (0.283) |
| 32 m-64 m | 0.001 (0.104) | 0.099 (0.117) | 0.328 (0.508) | 0.521 (0.642) | 0.100 (0.117) | 0.817 (1.394) | 0.292 (0.944) |
| 64 m-128 m | 0.001 (0.289) | 0.237 (0.285) | 0.778 (1.312) | 1.287 (1.679) | 0.240 (0.305) | 1.998 (2.792) | 0.795 (2.150) |
| 128 m-256 m | 0.004 (0.713) | 0.498 (0.687) | 1.571 (2.727) | 2.879 (3.795) | 0.501 (0.654) | 3.559 (4.754) | 1.675 (4.407) |
| 256 m-512 m | 0.008 (1.351) | 0.911 (1.619) | 2.620 (4.601) | 4.428 (6.280) | 0.864 (1.441) | 5.140 (7.487) | 3.500 (8.389) |
| 512 m-1.024 km | 2.642 (9.101) | 5.397 (8.473) | 5.521 (8.507) | 7.159 (9.539) | 6.273 (9.623) | 9.192 (10.959) | 10.551 (21.778) |
| 1.024 km-2.048 km | 5.041 (15.893) | 9.473 (16.290) | 10.356 (15.739) | 13.561 (20.068) | 11.313 (17.258) | 16.432 (20.500) | 20.495 (46.310) |
| 2.048 km-4.096 km | 10.298 (39.873) | 16.506 (36.481) | 17.863 (30.862) | 22.511 (32.744) | 19.174 (29.961) | 24.508 (34.521) | 42.441 (99.844) |
| 4.096 km-8.192 km | 20.563 (62.773) | 31.017 (71.255) | 31.802 (61.594) | 34.305 (60.649) | 28.378 (61.396) | 41.665 (68.955) | 79.971 (186.074) |
| 8.192 km-16.384 km | 24.908 (133.579) | 44.414 (119.270) | 45.414 (107.541) | 49.996 (93.263) | 47.941 (85.292) | 52.476 (96.738) | 117.068 (242.892) |
| 16.384 km-32.768 km | 44.116 (192.491) | 68.015 (156.586) | 60.150 (146.085) | 58.864 (135.358) | 57.946 (147.990) | 82.044 (155.310) | 207.265 (445.221) |

Decade RMS height in metres, median (p90). The 30-300 m decade is the one plan section 2.1 calls the rolling band.

| Decade | CoastalBelt | Plains | Rolling | HillCountry | PlateauInterior | Breaks | Mountain |
|---|---|---|---|---|---|---|---|
| 2-30 m | 0.001 (0.046) | 0.043 (0.047) | 0.122 (0.181) | 0.198 (0.231) | 0.039 (0.046) | 0.239 (0.507) | 0.110 (0.303) |
| 30-300 m | 0.005 (0.982) | 0.738 (0.912) | 2.137 (4.116) | 3.902 (5.171) | 0.736 (0.955) | 5.226 (6.806) | 2.258 (5.836) |
| 0.3-3 km | 8.653 (28.708) | 15.649 (27.427) | 17.807 (26.972) | 22.985 (30.019) | 18.230 (27.960) | 25.946 (32.117) | 36.784 (88.982) |
| 3-30 km | 54.792 (239.888) | 98.421 (204.845) | 86.498 (178.408) | 88.600 (199.668) | 80.506 (177.557) | 111.117 (189.678) | 246.226 (538.450) |

Slope in degrees (atan of the height difference over the baseline). Directional rows pool the 1 m transects of both directions; the 2D row uses central differences at 4 m on a 20 x 20 grid per anchor (100 m spacing).

| Slope | CoastalBelt | Plains | Rolling | HillCountry | PlateauInterior | Breaks | Mountain |
|---|---|---|---|---|---|---|---|
| 2 m baseline p50 | 0.10 | 1.92 | 4.73 | 8.36 | 1.77 | 8.58 | 6.27 |
| 2 m baseline p90 | 3.40 | 4.98 | 13.72 | 20.39 | 5.43 | 23.23 | 24.71 |
| 2 m baseline p99 | 18.60 | 10.36 | 24.32 | 32.12 | 18.61 | 50.85 | 53.79 |
| 8 m baseline p50 | 0.10 | 1.85 | 4.55 | 8.06 | 1.70 | 8.28 | 6.08 |
| 8 m baseline p90 | 3.33 | 4.73 | 13.15 | 19.52 | 5.26 | 22.52 | 24.22 |
| 8 m baseline p99 | 18.36 | 9.93 | 23.11 | 30.83 | 18.39 | 49.68 | 53.13 |
| 32 m baseline p50 | 0.10 | 1.70 | 4.11 | 7.17 | 1.50 | 7.47 | 5.84 |
| 32 m baseline p90 | 3.11 | 4.39 | 11.81 | 17.54 | 4.87 | 21.73 | 23.35 |
| 32 m baseline p99 | 17.42 | 9.36 | 21.00 | 28.45 | 18.13 | 42.13 | 50.95 |
| 2D 4 m p50 | 0.18 | 3.27 | 8.36 | 14.08 | 2.93 | 14.12 | 10.65 |
| 2D 4 m p95 | 9.40 | 7.71 | 21.86 | 29.20 | 10.73 | 38.81 | 42.31 |
| 2D 4 m max | 57.35 | 46.82 | 42.67 | 49.41 | 37.87 | 76.49 | 82.70 |

4 km relief (max minus min over each 4,096 m transect), metres.

| Relief | CoastalBelt | Plains | Rolling | HillCountry | PlateauInterior | Breaks | Mountain |
|---|---|---|---|---|---|---|---|
| p50 | 11.9 | 94.8 | 141.7 | 170.7 | 63.7 | 228.6 | 254.7 |
| p95 | 251.5 | 285.4 | 306.5 | 414.3 | 244.3 | 515.2 | 984.6 |

Method consistency, median band RMS in metres: 1 m sampling / 32 m sampling over the same 4,096 m window (must agree within 25 %) / wide 32 m transects (65-131 km, informational: they leave the anchor's class).

| Band | CoastalBelt | Plains | Rolling | HillCountry | PlateauInterior | Breaks | Mountain |
|---|---|---|---|---|---|---|---|
| 64 m-128 m | 0.001 / 0.002 / 0.492 | 0.237 / 0.257 / 0.711 | 0.778 / 0.860 / 1.018 | 1.287 / 1.410 / 1.339 | 0.240 / 0.259 / 0.863 | 1.998 / 2.103 / 1.595 | 0.795 / 0.817 / 1.310 |
| 128 m-256 m | 0.004 / 0.004 / 0.992 | 0.498 / 0.516 / 1.518 | 1.571 / 1.590 / 2.059 | 2.879 / 2.861 / 2.707 | 0.501 / 0.501 / 1.857 | 3.559 / 3.539 / 3.076 | 1.675 / 1.665 / 2.682 |
| 256 m-512 m | 0.008 / 0.008 / 1.596 | 0.911 / 0.915 / 2.710 | 2.620 / 2.630 / 3.534 | 4.428 / 4.446 / 4.360 | 0.864 / 0.864 / 3.414 | 5.140 / 5.155 / 4.893 | 3.500 / 3.495 / 4.790 |
| 512 m-1.024 km | 0.028 / 0.028 / 2.642 | 0.935 / 0.934 / 5.397 | 3.847 / 3.842 / 5.521 | 6.211 / 6.237 / 7.159 | 1.359 / 1.360 / 6.273 | 7.590 / 7.545 / 9.192 | 5.677 / 5.660 / 10.551 |

### 8.2 Drainage prototype (T0-3)

Run 2026-11-09 on the RTX 4070 desktop (16 worker threads of 32 logical CPUs) by `tools/landscape_drainage_prototype.gd` (`tools/LandscapeDrainagePrototype.cs`). Full tables, JSON and maps: `.godot/audit_baseline/common/t0/drainage/20261109_233403/` (`drainage_1000m.md/.json`, `drainage_500m.md/.json`, `river_map_{1000m,500m}.png`, `river_map_{1000m,500m}_crop.png`).

**Strip.** Auto choice: start 9,804,000 m, length 2,000 km (window 5 of the 16 pre-scanned; land at 4 km is 25,560 of 39,500 cells; windows are spaced so that none wraps past the ring seam). The saved pose (around 27,984,761 m) is outside the strip. 1 km grid 2,000 x 318, 500 m grid 4,000 x 636. Land 64.2 % of the strip, barrier 0.48 %, enclosed land 0 %, 1.3-1.4 % of channel cells end at a strip edge. The 1 km bake is deterministic (down-array hash `0x0AAE96EA9FF7DB20` twice in one process). All invariants (filled >= h, filled non-increasing downstream, down pointer always popped earlier, every chain ends at an outlet, accumulated area at outlets equals reachable land) hold at both cell sizes.

**Depressions and lakes (natural height, no fill of anything).** Closed basins are everywhere, so a large share of the land would be lake.

| Cell | Depressions | depth p50 / p90 / max m | area p50 / p90 km^2 |
|---|---|---|---|
| 1 km | 6,912 | 28.9 / 163 / 1,582 | 2 / 12 |
| 500 m | 20,701 | 16.2 / 110 / 1,595 | 0.5 / 4.3 |

| Breach limit | 1 km lakes | 1 km share of land | 500 m lakes | 500 m share of land | 1 km size bins (<10 / 10-100 / 100-1,000 / >1,000 km^2) |
|---|---|---|---|---|---|
| 5 m | 5,864 | 20.1 % | 15,724 | 22.3 % | 5,046 / 730 / 77 / 11 |
| 15 m | 4,522 | 19.2 % | 10,736 | 21.2 % | 3,725 / 712 / 74 / 11 |
| 40 m | 2,900 | 17.7 % | 5,655 | 19.5 % | 2,159 / 663 / 67 / 11 |

The largest lake is about 8,100 km^2 (8,300 at 500 m). The lake share barely moves with the breach limit, so the basins are deep (p90 160 m), not shallow noise.

**Channels** (lakes at the 15 m limit excluded; the whole-ring node count is the strip count x 15.71).

| Cell | Threshold km^2 | Nodes | Length km | Density km/km^2 | Whole-ring nodes | Bytes/node for 16 MB |
|---|---|---|---|---|---|---|
| 1 km | 25 | 25,800 | 33,192 | 0.081 | 405,000 | 41 |
| 1 km | 50 | 16,896 | 21,647 | 0.053 | 265,000 | 63 |
| 1 km | 100 | 10,815 | 13,802 | 0.034 | 170,000 | 99 |
| 500 m | 25 | 48,278 | 31,073 | 0.076 | 758,000 | 22 |
| 500 m | 50 | 32,036 | 20,561 | 0.050 | 503,000 | 33 |
| 500 m | 100 | 20,243 | 12,930 | 0.032 | 318,000 | 53 |

Both resolutions agree on length and density within 6 %, so the 1 km bake is adequate for topology. The density is low because the 19-21 % of land that is lake carries no channels and many catchments end in lakes.

**Hydraulics at 50 km^2 (1 km / 500 m).**

| Quantity | p10 | p50 | p90 | max |
|---|---|---|---|---|
| Q m^3/s | 1.38 / 1.36 | 3.50 / 3.50 | 31.5 / 31.9 | 752 / 749 |
| S_f | 0.0015 / 0.0013 | 0.016 / 0.015 | 0.059 / 0.058 | 0.53 / 0.56 |
| S_o | 0.0019 / 0.0016 | 0.018 / 0.017 | 0.062 / 0.060 | 0.53 / 0.56 |
| depth m | 0.31 / 0.31 | 0.45 / 0.45 | 1.07 / 1.08 | 3.8 / 3.8 |
| centre speed m/s (S_f) | 1.04 / 0.97 | 3.36 / 3.25 | 6.75 / 6.74 | 10 / 10 |
| width m | 1.7 / 1.7 | 4.2 / 4.3 | 18.6 / 19.6 | 888 / 887 |

**Danger-class mix at 50 km^2.** By cell count the shares are within 0.2 points of the by-length ones.

| Cell | Gradient | calm | swift | fierce | torrent | calm + swift |
|---|---|---|---|---|---|---|
| 1 km | S_f | 16.9 % | 43.8 % | 30.5 % | 8.8 % | 60.6 % |
| 1 km | S_o | 14.7 % | 42.5 % | 33.0 % | 9.9 % | 57.2 % |
| 500 m | S_f | 18.9 % | 43.6 % | 28.7 % | 8.8 % | 62.5 % |
| 500 m | S_o | 16.7 % | 43.1 % | 30.7 % | 9.5 % | 59.9 % |

At the other thresholds (1 km, S_f) calm + swift is 68.0 % at 25 km^2 and 55.8 % at 100 km^2, torrent 6.5 % and 12.6 %.

**The plan targets are missed with the plan's constants.** Calm + swift is 57-63 % (target at least 70 %), fierce 29-33 % (target 5-10 %), torrent 8.8-9.9 % (target at most 3 %). No class exceeds 90 %, so this is not the escalation case. The cause is gradient: the median channel cell has S about 0.016, steeper than the plan's worked lowland examples, because the generation 1 terrain is rough at 1 km (the p50 gradient over 5 km is 1.6 %). Confinement is not modelled, so these shares are lower bounds on speed.

**Sensitivity (1 km, S_f, 50 km^2, by length).**

| Variation | calm | swift | fierce | torrent | calm + swift |
|---|---|---|---|---|---|
| default (n 0.035, a 0.27, runoff 0.02) | 16.9 % | 43.8 % | 30.5 % | 8.8 % | 60.6 % |
| n = 0.030 | 13.7 % | 37.5 % | 33.5 % | 15.3 % | 51.2 % |
| n = 0.045 | 24.6 % | 51.6 % | 20.5 % | 3.4 % | 76.1 % |
| depth coef = 0.20 | 22.6 % | 50.5 % | 22.5 % | 4.3 % | 73.2 % |
| depth coef = 0.35 | 13.3 % | 36.8 % | 33.7 % | 16.2 % | 50.1 % |
| runoff = 0.01 | 22.2 % | 50.1 % | 23.2 % | 4.5 % | 72.3 % |
| runoff = 0.04 | 13.0 % | 36.3 % | 33.8 % | 16.9 % | 49.4 % |

No single constant brings both fierce into 5-10 % and torrent under 3 %; n = 0.045 reaches torrent 3.4 % and calm + swift 76 %, fierce still 20 %. Meeting the targets needs a steeper-than-linear mapping (for example the plan's "river ferocity" scaling of n and the caps), or a gradient baseline longer than 5 km. That is a design decision for the primary agent.

**Downstream exit.** Of the fierce or torrent cells, 55.7 % (1 km) and 65.8 % (500 m) reach a calm or swift cell, a lake or the ocean within 5 km; the rest are runs longer than 5 km. None ends dangerous at a strip edge.

**Where flow ends.** 98.6-98.7 % of channel cells end at the ocean, none at the Weather Lung, 1.3-1.4 % at a strip edge.

**Timing (4070, 16 threads).**

| Cell | Sampling | Flood | Accumulation | Hydraulics | Statistics | Bake | Whole-ring extrapolation |
|---|---|---|---|---|---|---|---|
| 1 km | 0.37 s | 0.28 s | 0.01 s | 0.01 s | 0.10 s | 0.7 s | about 11 s |
| 500 m | 1.23 s | 0.86 s | 0.07 s | 0.04 s | 0.13 s | 2.2 s | about 34 s |

The whole prototype run (strip pre-scan, both bakes, a second 1 km bake for the determinism check, statistics, four PNGs) took about 5 s against the 180 s limit. The whole-ring 1 km bake extrapolates to about 11 s here, within the plan's 3-5 s laptop budget only if the laptop pool is about 3x faster per core than assumed; to be measured on the A1000 laptop.

**Maps** (`river_map_1000m.png` 2,000 x 318, `river_map_500m.png` 4,000 x 636, the crops are the 300 km window starting at around 10,134,000 m (1 km) / 10,139,000 m (500 m) with the most fierce plus torrent length). Legend: hillshade grey land (vertical exaggeration 6), dark blue ocean, light blue lakes (15 m limit), magenta barriers, yellow edge outlets, channels at 50 km^2 blue calm / green swift / orange fierce / red torrent, 1-3 px by discharge.

What the maps show:
- Rivers rarely look like rivers. Most channels are short stubs of a few to a few tens of km that end in a lake, because roughly a fifth of the land is lake basins; only the coastal and plains areas show longer systems that reach the sea (98.6 % of channel cells do end at the ocean once the lake outlets are followed).
- Lakes are huge and numerous (largest about 8,100 km^2) and sit directly beside rough hill country; the hill country shows red and orange runs in short steep sections.
- Artefacts: in the flat coastal plains (lower middle of the crops) there are straight, parallel, axis-aligned and 45 degree D8 lines and radial fans of blue channels, as expected from priority-flood on near-flat ground. No river runs along a strip edge (yellow edge cells are only visible as end-caps; 1.3 % of channels exit there). Barriers show as a lattice of magenta dots (site discs) and a few larger discs (site or Weather Lung influence areas).

**Deviations from the spec.** (1) The 16 auto windows are spaced `k * (ring - 2000 km) / 15`, not `k * ring / 16`, so none wraps over the ring seam; the chosen window is recorded. (2) Outlet cells (ocean, Weather Lung basin) route with `min(natural, shaped)` height so the basin drains. (3) Hillshade uses a vertical exaggeration of 6 (flat at true scale on 1 km cells). (4) Channel width in the maps is centred: 2 px extends one pixel right and down. (5) The determinism check re-runs the sampling and bake of the 1 km grid only. (6) Channel cells are land cells including edge outlets, excluding lake cells at the middle (15 m) breach limit; statistics use that definition for every threshold. (7) `-Files` selected the whole smoke set because the manifest changed, so the run used `-Tests landscape_drainage_prototype,landscape_world_test`. Escalation conditions: enclosed land 0 %, edge exits 1.4 %, no single class above 90 %; none triggered. The plan targets are nevertheless missed (above).

### 8.3 Inland water gap list (T0-4)

Run 2026-11-09 (RTX 4070 desktop; machine-independent, headless). Test `landscape_inland_water_audit_test` (`tools/LandscapeInlandWaterAuditTest.cs`, wrapper `tools/landscape_inland_water_audit_test.gd`), PASS in about 14 s. Full data: `.godot/audit_baseline/common/t0/water/<stamp>/inland_water_audit.json` and `.md` (latest stamp in that folder). The gen-17 sampler used is `EmitterSiteTestWorlds.CreateGen17WeatherLung(1337)` because `LandscapeWorld` (T0-1) was not available; the water rule does not depend on climate or biome settings.

**Test results.**

| Test | Result |
|---|---|
| `gen17_has_no_inland_water` | PASS. 100,000 lattice points and 20,000 descent end points (11,529 reached a true 64 m minimum within 24 steps): 58,706 water samples, all Ocean with `WaterSurfaceHeight == 0`; 0 Lake or River |
| `gen16_inland_samples_exist` | PASS after 6,616 probes. Lake at around 24,631,099, across 83,020 (surface 497, water 550, depth 53). River at around 28,678,651, across 95,237 (surface 26, water 41, depth 15) |
| `proxy_sector_inland_water` | PASS. Lake sector: 1,089 Lake water-half vertices, 2,048 Lake triangles in `OceanIndices`. River sector: 550 River and 539 dry water-half vertices, 1,098 River triangles in `OceanIndices` (partial triangles clipped). Neither sector is `FullyOceanSurface`. A shoreline tile exists but is empty of inland water (see row 3) |
| `water_kind_encoding_roundtrip` | PASS. Alpha None 0, Ocean 1/3, Lake 2/3, River 1 |
| `ocean_proxy_shader_profiles` | PASS. Lake profile (coordinate scale 1.65, height scale 0.24, shore foam 0.30) and river profile (scale 2.15, height 0.11, foam 0.46, advection uniform `water_river_advection`) are in `ring_water_common.gdshaderinc`; the FFT surface requires kind 1 (Ocean) |

**Correction to the spec text (WO T0-4 test 3).** The spec expected `OceanIndices` to be empty for inland water. It is not: `OceanIndices` is the displayed-water index list for every kind (`RingTerrainProxySector.cs:4007-4019` adds every fully wet triangle, `AppendClippedWaterTriangle` adds the clipped partial ones, no kind test). The proxy therefore does emit a water surface for Lake and River, with the kind in vertex alpha (`:3885`), and the water shader picks the lake or river wave profile from it. Only the FFT coverage mask (`oceanSurfaceCells`, `:3996-4000`), `FullyOceanSurface`, the shoreline field depth (`:4371-4375`) and the FFT demand are Ocean-only.

**Gap list.** "Gen-17 today" is what a gen-17 world does when a sample carries Lake or River. T2 is the generation 2 river work, T3 the later water and swim work; the WO numbers are for the lead to map.

| Path | File:symbol | Gen-17 today | Works for Lake/River? | Evidence | Needed by | Size |
|---|---|---|---|---|---|---|
| Sampler water rule | `RingTerrainSampler.SurfaceGeneration.cs:65`, `:158`, `CreateRuntimeTerrainSampleLocked` | Water only where presentation height < 0: Ocean, `WaterSurfaceHeight` 0; elsewhere None and surface = terrain height | No. The only gen-17 water rule; a river needs a per-sample kind, a surface height and a bed carve here | Test 1 (0 Lake or River in 120,000 samples) | T2 (river sampler/bake WO); gen-2 recipe | M |
| Biome selection | `RingTerrainSampler.cs:813-825` (`ResolveBiome`, `ResolveBiomeBlend`) | Receives only None or Ocean from runtime generation | Yes. Lake and River map to `TerrainBiomeId.Lake` and `River` once a kind is passed in (gen-16 samples show it) | Test 2 (biome Lake, River) | none | S |
| Swim entry and float | `RingVoxelExplorerController.cs:1150-1156` (gen-17 override), `:1168`, `:1208-1216`, `:1344-1371` (`ProcessSwimming`), `:80` (`SwimEnterDepthMeters` 0.55) | The override rebuilds the sample from the float surface height: `WaterKind` = Ocean if surface < 0 else None, `WaterSurfaceHeight` 0 or `FloorToInt(surface)`. Water top is the integer `WaterSurfaceHeight` (+0 under runtime generation, `:1168`), plus the FFT offset for Ocean only (`:1172-1183`) | No. The override would erase a Lake or River kind and force surface 0 or the terrain height, so a river would never be swimmable; a sloping river needs a fractional water top per position instead of an integer | Code reading (the gen-16 branch without the override swims; no runtime test of swim in gen-17 water exists) | T2 river sampler WO (needs the sampler to expose the exact water surface) and the T3 swim WO | M |
| Water ambience kind | `RingVoxelExplorerController.cs:1281`, `:1325` (`_waterAmbienceKind`), `:1339` (meta), `scripts/water_ambience.gd:58-75` | Reads the (overridden) sample kind and probes 8 directions at 48, 128, 320, 640 m with `GetTerrainSample` | Partly. Lake and River kinds are honoured by the script (kind 2 restrained open water, kind 3 river layer) but the center sample is the overridden one (row above) and probes use the raw sampler, so nearby rivers would work and standing in one would not | Code reading | T3 water ambience WO | S |
| River ambience asset | `scenes/VoxelRingIntegrationTest.tscn:247-250`, stream `resources/audio/water/shore_wave_cc0.ogg` | `River` player reuses the shore wave sample at pitch 1.22 | Placeholder only: no river-specific loop exists | Scene text | T3 audio WO | M |
| Proxy mesh water surface | `RingTerrainProxySector.cs:3870-3890`, `:4007-4019`, `:4126-4129` | CPU path emits water for any kind, kind in vertex alpha, partial triangles clipped | Yes. Lake and River triangles reach `OceanIndices` with correct kind | Test 3 | none | n/a |
| GPU proxy heights (default for 8/16/32 m) | `RingTerrainProxySector.GpuHeights.cs:213-228` (`OceanAlpha` only), `:283-298`; `ProxyGpuSectorBackend.cs:28` (`ProxyGpuSectorClass { Dry, Ocean, Coastal }`) | The GPU kernel knows one water kind (Ocean); `Coastal` sectors fall back to the CPU builder | Not verified in a run. The class set and the single `OceanAlpha` suggest an inland sector would be built as Dry (water dropped) or sent to the CPU, depending on how the kernel classifies; needs a GPU or code check before T2 | Code reading only; test 3 covers the CPU path | T2 river render WO (before enabling gen-2 water at 8-32 m) | M |
| Shoreline field and FFT coverage | `RingTerrainProxySector.cs:3996-4000`, `:4179`, `:4371-4375` (`BuildFftShorelineTile`), `RingWaterFftController.cs:495` | Ocean only; non-ocean cells get `-FftShorelineFieldHaloMeters` | Intentionally Ocean-only. Inland water renders through the plain vertex-displaced profile (no FFT, no shore foam from the field); the shader foam scale (`ring_water_profile_shore_foam_scale`) still applies | Test 3 (shoreline tile present, inland not full ocean); test 5 | T2/T3 only if lake or river shore foam or FFT lakes are wanted | M |
| FFT demand | `RingWaterFftDemand.cs:186-197`, `:275-277` (`HasOceanVertex`), `RingTerrainProxySector.WaterDemand.Tests.cs` | A triangle asks for FFT only when a vertex decodes as Ocean; blocky mode fails closed | Correct for inland water (does not request FFT); a mixed ocean/lake triangle does request it | Code reading, existing demand fixture | none | n/a |
| Water shader profiles | `materials/ring_ocean_proxy.gdshader:191,267,318,345-354,529-552`, `ring_water_common.gdshaderinc:10,159-170,386-409,452-456` | Lake and river profiles exist (scales, advection, amplitude, foam) | Yes. River advection is driven by `water_river_advection` and the `ring_water_river_advection_main/far` globals | Test 5 | none (tuning only) | n/a |
| Terrain colour and tint | `RingTerrainVisuals.cs:170-190` (`ResolveSeededColor`), `RingTerrainSampler.cs:731-746` (`GetBiomeColor`) | Water colours by kind: Lake (0.045, 0.29, 0.36), River (0.075, 0.36, 0.43), plus a moisture shift | Yes | Code reading | none | n/a |
| Minimap | `RingTerrainMinimap.cs:605-629` (water-aware height and `ResolveSeededColor`), `:730` (hydrology text), `:1087` (lake waypoint finder) | Draws water by kind through the shared colour function | Yes for colour and label. Narrow rivers (a few metres) need the 1024 x 128 sample grid checked: it will skip them at that resolution | Code reading | T3 minimap WO (river visibility) | S |
| Far terrain and global LOD water | `RingTerrainGlobalLod.cs:993-994`, `RingScaledFarViewport.Geometry.cs:440-441` | Water vertex colour carries the kind; displayed water is `HasSurfaceWater && height < WaterSurfaceHeight` | Yes by kind, but the sampling is coarse (far tiles are hundreds of metres to kilometres per vertex), so a river a few tens of metres wide vanishes or aliases | Code reading; resolution argument | T2 river render WO (a river LOD ladder or a far river overlay) | L |
| Grass and trees | `ForestRegionSampler.cs:56` (`terrain.HasSurfaceWater`), `RingGrassTileBuilder.cs:646-647` (`IsWaterCovered`), `RingBiomeDecorationLayer.cs:982` (`IsSurfaceWater`) | All test `HasSurfaceWater`, not the kind. The decoration layer also treats presentation height < 0 as water (`:982`) | Yes for kinds. Needs the sampler to publish the sample (the layer's `surfaceHeight < 0` clause is a gen-17 ocean rule that a river above sea level avoids only through `HasSurfaceWater`) | Code reading | T2 river WO (bank and wet-edge scatter is a separate T3 item) | S |
| Near terrain (Terrain3D) | `scripts/Terrain/Surface/*` (no `WaterKind`/`HasSurfaceWater` references) | Water is proxy-only; the near terrain has no water concept | A river bed carved in the height field shows in Terrain3D, but nothing near-field draws water except the proxy mesh at 2 m spacing | grep | T2 river WO (check proxy water above the carved bed at 2 m) | M |
| Wet world and wetness | `RingTerrain3DSurface.Wetness.cs`, `RingTerrain3DGameplay.Wetness.cs`, `GameplayWetWorld*` | Rain-driven; no reference to water kind or distance to water | Neutral: no riparian wetness (wet banks, damp ground near water) exists | grep | T3 (optional riparian wetness) | M |
| Gust front debris | `Weather/RingGustFrontDebris.cs:130-135` (`BiomeShares`) | Spray share for `surfaceWater` or Ocean, Lake, River biomes | Yes | Code reading | none | n/a |
| Proxy cache validation | `RingTerrainProxySector.cs:3670-3693` | Cached sample records validate kind in 0..3 and the runtime water plane flag | Yes | Code reading | none | n/a |
| Biome authoring | `Biomes/BiomeAuthoringPreset.cs:69`, `RingTerrainSampler.GpuHeights.cs:33` | Lake and River are excluded as base biomes | Yes (water biomes are never land biomes) | Code reading | none | n/a |

**Summary for T2/T3.** What already works for Lake and River once the sampler produces them: kind in vertex alpha, CPU proxy water surface, lake and river wave profiles, colours, biome ids, vegetation masks, spray, minimap colour. What does not: (1) the gen-17 sampler water rule and the controller's gen-17 override (swim, exact water surface, ambience center), (2) the GPU proxy path (Ocean-only classes; unverified for inland), (3) far-LOD representation of narrow rivers, (4) river sound (placeholder asset) and riparian wetness, (5) FFT and shoreline foam for inland water (by design Ocean-only).

### 8.4 Tree pack audit (T0-5)

Run 2026-11-09 (RTX 4070 desktop; machine-independent, headless). Test `landscape_tree_pack_audit_test` (`tools/LandscapeTreePackAuditTest.cs`, wrapper `tools/landscape_tree_pack_audit_test.gd`), PASS in 5 s. Full data: `.godot/audit_baseline/common/t0/trees/20261109_230103/tree_pack_audit.json` and `.md` (per-surface shader paths and texture sizes are in the JSON). The catalog loaded from `prepared` (`lolipop_catalog.res`), not from source.

**Verdict on plan 7.1: the counts, triangle ranges, LOD3 sizes, texture sizes and the uniform variant selection are all confirmed. One claim is wrong (LOD3 surfaces), and two things the plan did not say matter for T6 (impostor resolution, pine LOD3 shading).**

**Contract assertions (all pass, none weakened):** `VariantCount == 30`, split 12 / 3 / 15 (maple / fir / pine); every variant has 4 LODs; LOD0-LOD2 of every variant have exactly two surfaces, bark then foliage; LOD3 has 64 triangles or fewer (36 maple, 20 fir, 16 pine).

**Findings against plan 7.1**

1. **Disproved: "Bark and foliage are separate surfaces in every variant and LOD."** True for LOD0-LOD2. LOD3 of all 30 variants is a single surface (`tree_foliage_mode` 2, the billboard material) that carries the whole tree. `ImportedTreeImpostorCatalog` also throws if a LOD3 has more than one surface. Snags (bark only) are therefore possible from LOD0-LOD2 only; any bark-only form needs its own far representation (T6 must not assume it can derive a bark-only LOD3).
2. **Not in the plan: the shared far impostor stores every variant at 256 x 256.** `ImportedTreeImpostorCatalog` packs the 30 LOD3 meshes into one shared 108-vertex mesh (vertex, normal, UV, tangent and attachment data textures of 108 x 30) and three `Texture2DArray`s (albedo, normal, metallic-roughness), each 30 layers of 256 x 256 RGBA8 with mips (`TextureLayerSize` = 256). The authored billboards are 1024 (maple, pine), 512 (pine saplings) and 2048 (fir); they are downsampled 4x to 8x for the far form. The plan's "ImportedTreeCatalog.Prepared.cs" holds no impostor code; the impostor lives in `scripts/Vegetation/ImportedTreeImpostorCatalog.cs`. Expanded LOD3 vertices: 108 per maple variant, 60 per fir, 48 per pine (36, 20 and 16 triangles, so the crossed cards are unindexed triangle lists).
3. **Not in the plan (item 9): pine LOD3 has no metallic or roughness map.** Maple and fir LOD3 bind albedo, normal, metallic and roughness; all 15 pine LOD3 materials bind albedo and normal only (the `has_metallic_texture` and `has_roughness_texture` flags are false). The impostor packer substitutes a constant fallback image for the missing maps (`PackMetallicRoughness`; the fallback metallic image has blue = 1, which is worth a look in T6 if far pines look metallic). Everything in LOD0-LOD2 binds all four maps.
4. **Confirmed (item 1): size classes are ignored by selection.** 64 production tiles (18,285 trees) give per-species variant shares that are uniform: the largest deviation from 1/k is 1.83 standard errors for maple, 2.26 for fir, 1.72 for pine (limit 3). Maple saplings are 25.2 % of maples (expected 25.0 %); pine saplings are 20.8 % of pines (expected 20.0 %). Species counts in these tiles: maple 14,357, fir 1,894, pine 2,034.
5. **Confirmed (item 8 and spec fact 7): LOD1 is skipped by the compact profile and the visible LOD0 to LOD2 switch is at 25-40 m by default.** But the switch distance depends on the quality preset (`RingGraphicsQualityController.PresetTrees`): Low 15-25 m, Medium 20-30 m, High 25-40 m, UltraVideo 40-60 m. The harness default preset is Medium, so the T0-7 look runs see the 20-30 m switch, not 25-40 m, unless they pass `--preset=keep`.
6. **Confirmed: the tables in the plan.** Every triangle range quoted in 7.1 matches the catalog (maple sapling 465-551, small 1.7-3.4k, medium 4.8-14.6k, large 13.0-22.5k; pine sapling 587-646, small 1.9-2.3k, medium 2.9-4.4k, big 4.5-5.5k, large 10.1-11.8k; fir 13.0k, 6.8k, 0.6k; LOD2 maple 157-5.3k, pine 270-3.2k, fir 0.3-3.3k). Texture sizes match too: maple bark and leaf 2048 x 2048; pine bark 1024 x 2048 and needles 2048 x 2048; fir bark 2048 x 4096 and boughs 4096 x 4096. Not stated in the plan: fir bark and bough metallic and roughness maps are 4096 x 4096 (the bark albedo and normal are 2048 x 4096), and the fir pack has no size names, so the sizes below are inferred from height rank.

**Variants** (size class parsed from the name; fir inferred). Triangles are per LOD (LOD0 / LOD1 / LOD2 / LOD3); surfaces per LOD are 2 / 2 / 2 / 1 for every row. Heights are authored LOD0 heights; every LOD is normalised to LOD0 height, so LOD1-LOD3 AABB heights are about 1.0 (see JSON).

| # | Name | Class | H m | W m | Trunk r m | Triangles L0 / L1 / L2 / L3 |
|---|---|---|---|---|---|---|
| 0-2 | Acer_Sapling_1-3 | sapling | 3.3-3.9 | 3.1-3.3 | 0.11 | 465-551 / 300-344 / 157-200 / 36 |
| 3-5 | Acer_large_1-3 | large | 18.4-24.1 | 17.4-19.7 | 0.61-0.69 | 12,987-22,490 / 5.5-9.5k / 2.8-5.3k / 36 |
| 6-8 | Acer_medium_1-3 | medium | 10.3-13.5 | 8.8-11.6 | 0.31-0.41 | 4,750-14,577 / 2.2-5.5k / 1.1-2.6k / 36 |
| 9-11 | Acer_small_1-3 | small | 6.0-6.9 | 5.1-5.6 | 0.18-0.20 | 1,732-3,442 / 658-1,233 / 262-531 / 36 |
| 12 | Christmas tree | large (inferred) | 9.3 | 5.3 | 0.13 | 12,969 / 6,633 / 3,268 / 20 |
| 13 | Christmas tree_2 | medium (inferred) | 4.2 | 2.7 | 0.08 | 6,813 / 3,598 / 2,278 / 20 |
| 14 | Christmas tree_3 | small (inferred) | 1.5 | 1.6 | 0.08 | 599 / 437 / 318 / 20 |
| 15-17 | Pine_big_1-3 | big | 16.2-19.6 | 8.4-9.3 | 0.21-0.23 | 4,502-5,485 / 2.6-3.1k / 1.4-1.7k / 16 |
| 18-20 | Pine_large_1-3 | large | 30.1-31.8 | 15.9-17.4 | 0.40-0.43 | 10,142-11,844 / 5.8-6.6k / 2.8-3.2k / 16 |
| 21-23 | Pine_medium_1-3 | medium | 10.5-13.9 | 5.3-6.6 | 0.13-0.17 | 2,923-4,425 / 1.7-2.4k / 0.99-1.25k / 16 |
| 24-26 | Pine_sapling_1-3 | sapling | 1.5-1.6 | 1.4-1.5 | 0.08 | 587-646 / 436-478 / 270-280 / 16 |
| 27-29 | Pine_small_1-3 | small | 4.0-4.7 | 3.2-3.8 | 0.08-0.095 | 1,912-2,333 / 1.3-1.5k / 768-884 / 16 |

Notes on the heights: the maple "sapling" is 3.3-3.9 m tall and 3.1-3.3 m wide (broad for a sapling), the pine sapling and the "small" fir are 1.5 m, and the three pine "large" variants reach 30-32 m (the tallest trees in the catalog, about 1.3-1.7 times the "large" maple's 18-24 m, before the 0.85-1.15 scale jitter). Trunk radius and trunk height are the catalog's geometric estimates (not authored), floored at 0.08 m.

**Materials.** Every surface of every LOD uses `res://materials/imported_tree.gdshader` (LOD0-LOD2 bark: `tree_foliage_mode` 0, no cutout; foliage: mode 1, alpha cutout; LOD3: mode 2, alpha cutout). Textures bound per surface (identical across the variants of a species): see the plan 7.1 table for the albedo sizes; the full per-surface list is in the JSON.

**Compact LOD profile defaults** (`RingBiomeDecorationLayer` created off-tree and freed): `UseCompactTreeLodProfile` true; `TreeNearDetailFadeStartMeters` 25, `TreeNearDetailFadeEndMeters` 40, `TreeBillboardFadeStartMeters` 300, `TreeBillboardFadeEndMeters` 350; `Lod1DistanceMeters` 240, `Lod2DistanceMeters` 480, `Lod3DistanceMeters` 960; `MinimumLod5DistanceMeters` 1,920, `MaximumLod5DistanceMeters` 5,500; `VisibilityRadiusMeters` 2,600. The user's saved vegetation settings can override: `GameplayVegetationDefaults.UseCompactTreeLodProfile`, `TreeNearDetailFadeStartMeters`, `TreeNearDetailFadeEndMeters`, `TreeBillboardFadeStartMeters` and `TreeBillboardFadeEndMeters` (their built-in fallback is the quality preset's `PresetTrees` value, not the layer default above). `Lod1DistanceMeters`, `Lod2DistanceMeters`, `Lod3DistanceMeters`, `MinimumLod5DistanceMeters`, `MaximumLod5DistanceMeters` and `VisibilityRadiusMeters` have no `GameplayVegetationDefaults` field.

**Deviations from the spec**

- `LandscapeWorld` and the T0-1 pose fixture did not exist when this ran, so the selection check used the spec's fallback: the first 64 distinct 240 m tiles (8 x 8 cells of 30 m) whose lattice point is `DenseForest` (14,305 lattice points scanned). The sampler is the production resource `voxel_terrain_settings_ring_integration.tres` with seed 1337, bedrock -8000, sea 0, the weather-lung v8 default recipe, contract `surface_runtime_generation_v1` and caves, overhangs and 3D density off, not the full launcher override list. This does not affect the variant-uniformity result (variant choice is a hash independent of placement) but re-running against `LandscapeWorld` once T0-1 lands would make the pose set the pinned one.
- The impostor facts come from `ImportedTreeImpostorCatalog`, not `ImportedTreeCatalog.Prepared.cs` (which has none).
- The manifest entry carries only the `vegetation` tag; the `landscape` tag did not exist yet. Add it to the entry once T0-1 adds the tag.

### 8.5 Generation 1 fingerprint (T0-6)

Captured 2026-11-09 (RTX 4070 desktop; headless and machine-independent). Test `world_generation_fingerprint_test` (`tools/WorldGenerationFingerprint.cs` computer, `tools/WorldGenerationFingerprintTest.cs`, wrapper `tools/world_generation_fingerprint_test.gd`), golden `tools/fixtures/world_generation_fingerprint_golden.json` (`{"schema": 1, "rows": [row 1]}`, label `gen1-v8-s1337`, definition hash `0x1480032CB30B96CA`). Runs in about 4 s (3 s of computation). Passing run: `.godot/test_runs/landscape-t0-fingerprint/20261109-233236/results.md`, summary line "2 selected: 2 pass. Wall 38.8s. Exit 0." (this test and `landscape_world_test`).

**Captured from the working tree, not from a commit.** The Cards default (`RingGraphicsQualityController`, `RingStandingGrassLayer.CardStyle`, `RingLiveTuningDrawer.Grass`) was uncommitted in the tree at capture, and other agents' uncommitted PDA and grass-test changes were present. **No hash depends on the grass render style.** The grass rows call `RingGrassTileBuilder.Build` in its Cards placement mode directly with a `RingGrassCardPlacement`; the style setting only chooses whether the layer makes that call, so Blades versus Cards, the preset and the user's saved settings do not enter any hash. Blades placement is not fingerprinted (the existing blade golden in `GrassCardPlacementTest` covers it). What the grass hashes do depend on is the Cards builder code, the production terrain sampler, and the card parameters, which are the layer's own defaults (`RingStandingGrassLayer` default values; `RingGrassCardTuning.Default`; the Medium preset sets no card value). The test checks those defaults against a freshly constructed layer and fails if they drift, so a changed default cannot silently change the fingerprint's meaning. If the capture is wanted from a clean commit, delete the row and run `--capture` after the Cards default is committed; the expectation is no change.

**What row 1 covers** (everything is computed from `LandscapeWorld` and the T0 pose fixture; nothing reads `user://` or the user's settings):

| Field | Content | Count | Hash |
|---|---|---|---|
| `heights` | FNV-1a 64 over the IEEE bits of the shaped `CalculateHeight` (double) at 4,096 points of a 64-bit LCG (seed `0x5EED1337`, Knuth MMIX constants; around in [0, C), across in [0, 318,000]) | 4096 | `0xEB8E7C1329848992` |
| `sampler` | the same points through `GetTerrainSample`: `SurfaceHeight`, `WaterKind`, `WaterSurfaceHeight`, `Biome`, `Region`, bits of `Moisture` and `Temperature` | 4096 | `0x0018E13D59CE4D6A` |
| `trees` | eight 240 m production tiles (`BuildImportedForestReviewTile`; every record field in order) at the tiles containing the eight poses below | see below | per tile |
| `grass` | eight 32 m Cards tiles (`RingGrassTileBuilder.Build`, tier 0, empty edit snapshot, no exclusions or modifiers) at the same poses; instance count, low-layer count and FNV-1a over the instance floats | see below | per tile |
| `grass_params` | every argument of the call and the resolved placement values, so a later change to any default shows as a diff | n/a | n/a |
| `scatter` | reserved for T5 | 0 | `none` |
| `catalog` | `CatalogVersion` 1, 30 variants; FNV-1a over (index, species, name, height, width, trunk radius, per LOD surface count and vertex count per surface) | 30 | `0x4D4B7E42C2D59D30` |

Tiles (poses `plains_eye`, `hills_eye`, `coast_eye`, `forest_interior_eye`, `forest_edge_eye`, `forest_clearing_eye`, `v0_ridge_eye`, `v0_centre_eye`, in that order):

| Pose | Tree tile (around, across) | Trees | Tree hash | Grass tile (around, across) | Grass instances (low) | Grass hash |
|---|---|---|---|---|---|---|
| `plains_eye` | 7199, 1068 | 0 | `0xCBF29CE484222325` (empty) | 53995, 8015 | 7358 (4137) | `0xFBF2EC6646A2FAA0` |
| `hills_eye` | 2945, 1131 | 0 | `0xCBF29CE484222325` (empty) | 22089, 8484 | 4413 (2493) | `0x6B830805A750702A` |
| `coast_eye` | 979, 1092 | 64 | `0x439C192239211866` | 7345, 8190 | 4921 (2821) | `0x7E87C0F85B41939B` |
| `forest_interior_eye` | 9811, 785 | 296 | `0x1ED38A3BD6829E9A` | 73588, 5890 | 15859 (8890) | `0x5276C68A646037EE` |
| `forest_edge_eye` | 9813, 786 | 185 | `0x9130C43C12FF53FE` | 73599, 5901 | 16068 (9039) | `0x9C39AA8D519BD6BC` |
| `forest_clearing_eye` | 9813, 787 | 70 | `0x94905D8D2C6B3921` | 73603, 5904 | 16024 (9062) | `0x7C2E06C1253447F8` |
| `v0_ridge_eye` | 656, 1256 | 0 | `0xCBF29CE484222325` (empty) | 4927, 9422 | 2304 (1280) | `0x83851B4043397732` |
| `v0_centre_eye` | 1308, 650 | 0 | `0xCBF29CE484222325` (empty) | 9817, 4875 | 4620 (2588) | `0x4E7D93E06D4DD96B` |

Grass call values (`grass_params` in the golden): tile 32 m, cell 8 m, 9 candidates, density 0, main cap 15,770, low cap 20,276, slope 25 degrees, source fraction 1, tier 0, clumps and tufts off, configured height range 0.10-0.20 m, `RingGrassAppearanceSnapshot.Default`, card tuning default (near density 14, low density 18, tall 0.6, short 0.3, lean 0.12, reach 400 m), placement fingerprint `0x9F0C20834832C376`.

**Acceptance.**

- Deterministic: the capture run and two further runner runs gave identical hashes (`heights` `0xEB8E7C1329848992`, `sampler` `0x0018E13D59CE4D6A`, `catalog` `0x4D4B7E42C2D59D30`, 141 compared fields).
- Can fail: changing the LCG increment constant by one (`...407` to `...408`) in `WorldGenerationFingerprint.cs` gave `FAIL 2 field(s) differ`, `heights.hash` `0xEB8E7C1329848992` to `0x53FFDA4EABF3CD15` and `sampler.hash` `0x0018E13D59CE4D6A` to `0x738B1F45F5685C4E` (run `20261109-233159`); reverted, and the following run passed.
- Time: about 4 s, against the 60 s limit.
- Flags: `--capture` (appends row 1 only if the golden has no row for the generation, never rewrites one), `--print`, `--golden=<path>`. `captured` is the only field not compared.

**Deviations from the spec.**

- Four of the eight tree tiles (`plains_eye`, `hills_eye`, `v0_ridge_eye`, `v0_centre_eye`) have no trees, so their hash is the empty FNV basis. They still pin "no trees here"; the tree content is carried by the coast and the three forest tiles. The spec's pose list was kept as written.
- Captured from the working tree rather than after the Cards commit (see above); the hashes do not depend on it.
- The pose tiles are read from the committed fixture (`ReadFixture`), so a change to the fixture also changes this row (and `landscape_world_test` fails first).
- The grass arguments were taken from `RingStandingGrassLayer.BuildWorker` and the layer's property defaults. Production passes a shared surface cache (`surfaceCache`) that the fixture omits; it is a cache, not an input.

### 8.6 Look references, cost baseline and LOD switch (T0-7)

Run 2026-11-09/10 on the **RTX 4070 desktop**. All cost numbers below are "4070, non-authoritative"; the authoritative cost run is still to do on the A1000. Tools: `landscape_look_baseline`, `landscape_cost_baseline`, `landscape_lod_switch_capture` (wrappers in `tools/`, shared body `tools/landscape_baseline_common.gd`, bridge `tools/LandscapeTimingBridge.cs`, optional `tools/landscape_pass_timestamp_effect.gd`). All three booted the pinned world through `landscape_harness_common.gd` (world verified), at 1920x1080, VSync off, preset Medium (grass Cards), the user's grave-key preference files (`--prefs=user`; snapshot and sha256 in each output folder; `gameplay_wet_world_defaults.tres` was absent so its defaults applied), clocks held, RingPerformanceMonitor disabled.

Runner results (profile `landscape_gpu`):
- Look: `.godot/test_runs/landscape_gpu/20261110-000114/results.md`: "1 selected: 1 pass. Wall 3m04s. Exit 0."
- Cost: `.godot/test_runs/landscape_gpu/20261110-000700/results.md`: "1 selected: 1 crash. Wall 28m55s. Exit 1." The row says "PASS marker printed, then crash exit code -1073741819": `LANDSCAPE_COST=PASS` was printed and every output was written, then Godot 4.8-dev7 crashed at exit (the known exit issue; the look run exits 0, so it is probably the timestamp hooks at shutdown; it persisted after detaching them before cleanup).
- LOD switch: `.godot/test_runs/landscape_gpu/20261110-003622/results.md`: "1 selected: 1 pass. Wall 3m32s. Exit 0." (analysis below uses the earlier by-hand run of the same code, whose frames were kept for the sheets).

**Effective vegetation settings (important).** The user's saved vegetation preferences override the Medium preset: the tree near-detail fade is **25 to 200 m** (not Medium's 20-30 m), the billboard fade 550 to 900 m, compact LOD profile on. So the LOD0-to-LOD2 cross-fade the user sees is a 175 m wide dither, not a 15 m wide one. Use `--prefs=defaults` to measure the shipped defaults instead.

##### Look references

Output: `.godot/audit_baseline/rtx4070/t0/look/2026-11-10_00-01-25/` (`<pose>__<time>.png`, `_bands.png`, `look.json`, `reference_set.json`, `prefs_snapshot/`). 18 views, all PASS: mean linear luma above 0.01 (lowest `forest_interior_eye__sun_8_set` 0.018, highest `hills_2km__noon` 0.231), noon sun elevation 75.0, `sun_8_set` 8.00, streaming idle for every view. Auto-exposure is switched off after the first pose so the references are reproducible. The HUD (title card, status box, minimap) is in every image. Reference set: `plains_eye__noon`, `hills_eye__sun_8_set`, `forest_edge_eye__noon`, `coast_eye__noon`, `hills_2km__noon`.

What the images show (looked at `plains_eye__noon`, `hills_2km__noon`, `forest_interior_eye__sun_8_set`, `saved_18km__noon`):
- `plains_eye__noon`: a flat olive-green plain with a uniform short-grass and tuft carpet (grass cards, no trees), the ring arch rising as a pale blue vertical band, soft cumulus, a blue-grey mountain range on the horizon.
- `hills_2km__noon` (2 km above the hills): a broken cloud deck over the right half with mottled foam-like texture; the ground is dark blue-grey with pale patches and shows no trees or grass at that range (flat tonal patchwork); curved ring horizon with haze.
- `forest_interior_eye__sun_8_set`: almost black under a dense maple canopy, with one patch of blue sky and thin cloud streaks; the understory is near-black grass. The darkest view, and the most sensitive to exposure and ambient settings.
- `saved_18km__noon` (16 km up): cloud puffs over bluish terrain with large flat-coloured regions, a hard-edged rectangular pale tile near the top right where the far terrain ends, a bright blue ring arch; the minimap has a black strip at its top.

Band summary of the five reference views (share of screen and mean linear luma per band; empty bands omitted):

| view | bands |
|---|---|
| plains_eye__noon | 0-10 m 29 % 0.108; 10-30 m 14 % 0.078; 30-100 m 6 % 0.061; 0.1-0.5 km 6 % 0.062; 8-30 km 1 % 0.194; 30-120 km 1 % 0.294; 120-500 km 1 % 0.457; 500-2,000 km 41 % 0.205 |
| hills_eye__sun_8_set | 0-10 m 24 % 0.032; 10-30 m 6 % 0.142; 30-100 m 10 % 0.005; 0.1-0.5 km 9 % 0.002; 0.5-2 km 10 % 0.006; 500-2,000 km 41 % 0.149 |
| forest_edge_eye__noon | 0-10 m 40 % 0.074; 10-30 m 13 % 0.048; 30-100 m 9 % 0.037; 0.1-0.5 km 3 % 0.050; 8-30 km 0 % 0.161; 120-500 km 0 % 0.374; 500-2,000 km 35 % 0.249 |
| coast_eye__noon | 0-10 m 31 % 0.042; 10-30 m 13 % 0.019; 30-100 m 5 % 0.024; 30-120 km 3 % 0.273; 500-2,000 km 41 % 0.082 |
| hills_2km__noon | 0.5-2 km 15 % 0.142; 2-8 km 46 % 0.199; 8-30 km 11 % 0.321; 30-120 km 5 % 0.447; 120-500 km 4 % 0.525; 500-2,000 km 19 % 0.211 |

The 500-2,000 km band holds the sky and the ring arch seen through the depth buffer (35-41 % of every ground view). At `sun_8_set` the 30 m to 2 km terrain bands are nearly black (luma 0.002-0.006 at `hills_eye`) while the sky band stays at 0.15, a large tier step.

##### Cost baseline (4070, non-authoritative)

Output: `.godot/audit_baseline/rtx4070/t0/cost/2026-11-10_00-07-11/` (`cost.json`, `cost.md`, `power_log.csv`, `prefs_snapshot/`). 11 pose-times, 2 rounds (the second reversed), 6 cases, 8 s windows, 132 windows, all power-valid (the 4070 is never rejected). The tables are pasted from `cost.md` after these notes.

- GPU ms are the main-viewport (`root`) and `FarRingViewport` measured render times (Godot viewport timing): median per window, then median over rounds. Hide delta = baseline minus hidden. The noise floor (baseline vs baseline_repeat) is 0.00-0.03 ms on the root viewport, so deltas above 0.05 ms are real on this machine.
- Hide classes. Near terrain = `Terrain3DGameplay` plus the 16 `RingTerrainProxySector` nodes (Lod1 2 m to Lod11 32 km). Far terrain = the 17 `ring_terrain_global` meshes in the far viewport plus `GlobalTerrainLod3` and `AnalyticGlobalRing` (18 nodes; far ocean, hull and clouds stay on). Trees and grass = every `GeometryInstance3D` under `BiomeDecorations` / `StandingGrass` with `layers = 0` (their shadows disappear too). The attribution test's far-terrain names (`ScaledSeededRingSurface`, `ScaledFarLod*`) do not exist in the current far viewport; its terrain meshes are unnamed `MeshInstance3D`s with `ring_terrain_global.gdshader`.
- Every hide class changed draw or primitive counts at several poses, so the escalation condition (hide path unreachable) did not trigger. Grass hide has no effect at `hills_2km` and `saved_18km` (no grass resident), marked "(no change)". Trees hidden at `plains_eye` and `hills_eye` changes 452 draws but no primitives and no GPU time (empty indirect or shadow draws).
- Compute markers appear only when their work runs. With a static camera `GPU.Vegetation`, `GPU.ProxyHeights` and `GPU.HeightShadow` never appeared (not measured, not zero) and `GPU.WaterFft` only at `coast_eye`. TreeCulling 0.02-0.04 ms and GrassCulling 0.01-0.03 ms are negligible next to the raster cost.
- Opaque bracket worked: `lp_pre_opaque`, `lp_post_opaque` and `lp_post_transparent` appeared in the engine timestamps (`rd.capture_timestamp` from three `CompositorEffect`s) and `BRACKET:opaque` is reported. The three effects were attached during all windows (small unmeasured overhead). The opaque pass is 2.5-4 ms on open poses and 8-11 ms in forest, so trees dominate it.
- Fill rows are cold fills only for the first poses visited from far away (`plains_eye`, `hills_eye`, `coast_eye`, `forest_interior_eye`); later poses reuse resident tiles (partial or zero counts), and `saved_18km` waited the full 300 s idle timeout (streaming never went idle at 16 km up in the cost run, though it did in the look run). `Trees disk load` is nested inside `Trees build` in the production scopes, so do not add them. CPU scope ms are per tile on worker threads.
- Headline numbers: root GPU 5.5-6.5 ms on open ground, 8.7 ms at the coast, 11.6 ms in the forest clearing, 15.2-16.2 ms at the forest edge and interior; frame time 17-18 ms (about 57 fps) in the forest on a 4070 at 1080p Medium. Trees cost 8.6-10.7 ms at the forest edge (about 60 %). Near terrain costs 2.3-4.2 ms everywhere and is the largest fixed cost on open ground (about 55-60 % of root GPU at `plains_eye`). Grass costs 0.1-0.5 ms (Cards). Far terrain costs under 0.25 ms on the root viewport and 0.1-0.16 ms on the far viewport. What remains after hiding the four classes (about 1.5-2.5 ms) is sky, cloud composite, haze and post.

##### Baseline per pose-time (root viewport GPU ms from viewport timing; frame ms is wall time between frames)

| pose | time | sun el | root GPU med | root GPU p95 | far GPU med | root CPU render | frame ms | draws | primitives | noise floor root GPU | noise floor far GPU | noise draws |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| plains_eye | noon | 75.0 | 6.49 | 6.81 | 1.20 | 1.19 | 8.03 | 916 | 5038618 | 0.03 | 0.00 | 0.0 |
| hills_eye | noon | 75.0 | 6.37 | 6.65 | 0.95 | 1.23 | 7.65 | 926 | 4641284 | 0.00 | 0.00 | 0.0 |
| hills_300m | noon | 75.0 | 6.03 | 6.31 | 0.89 | 1.05 | 7.24 | 358 | 4137528 | 0.01 | 0.00 | 0.0 |
| hills_2km | noon | 75.0 | 5.61 | 5.85 | 0.98 | 0.95 | 6.91 | 254 | 2948051 | 0.01 | 0.00 | 0.0 |
| coast_eye | noon | 75.0 | 8.66 | 9.01 | 1.24 | 1.56 | 10.36 | 1247 | 7471482 | 0.01 | 0.00 | 0.1 |
| forest_interior_eye | noon | 75.0 | 15.94 | 16.38 | 1.22 | 1.57 | 17.56 | 1144 | 18734142 | 0.00 | 0.00 | 0.1 |
| forest_edge_eye | noon | 75.0 | 15.17 | 15.65 | 1.38 | 1.49 | 16.92 | 745 | 21355312 | 0.01 | 0.00 | 0.0 |
| forest_clearing_eye | noon | 75.0 | 11.63 | 12.04 | 1.22 | 1.38 | 13.23 | 607 | 13935204 | 0.00 | 0.00 | 0.0 |
| saved_18km | noon | 75.0 | 5.49 | 5.73 | 1.89 | 0.94 | 7.71 | 228 | 2314974 | 0.00 | 0.00 | 0.0 |
| hills_eye | sun_8_set | 8.0 | 5.05 | 5.41 | 0.95 | 1.19 | 6.33 | 937 | 4636066 | 0.00 | 0.00 | 0.0 |
| forest_edge_eye | sun_8_set | 8.0 | 16.19 | 16.68 | 1.38 | 1.65 | 18.03 | 1218 | 21495185 | 0.02 | 0.00 | 0.0 |

##### Hide deltas on the root viewport (ms = baseline minus hidden, median of window medians; compare with the noise floor above)

| pose | time | near terrain | far terrain | trees | grass | draws: near / far / trees / grass | primitives: near / far / trees / grass |
|---|---|---|---|---|---|---|---|
| plains_eye | noon | 3.65 | 0.01 | -0.01 | 0.22 | 252 / 28 / 452 / 127 | 3479383 / 201017 / -40 / 526086 |
| hills_eye | noon | 3.84 | 0.02 | 0.00 | 0.09 | 286 / 21 / 452 / 118 | 3686039 / 68778 / -9 / 181412 |
| hills_300m | noon | 3.67 | 0.03 | 0.01 | -0.00 | 276 / 21 / 0 / 11 | 3354469 / 68771 / 537 / 8325 |
| hills_2km | noon | 3.31 | 0.05 | 0.01 | 0.01 (no change) | 188 / 17 / 0 / -0 | 2178560 / 65724 / 4 / -1 |
| coast_eye | noon | 4.16 | 0.07 | 1.61 | 0.09 | 395 / 28 / 656 / 115 | 4660199 / 174071 / 1645881 / 204458 |
| forest_interior_eye | noon | 2.49 | -0.02 | 9.63 | 0.43 | 262 / 28 / 678 / 122 | 3455470 / 173045 / 13021857 / 1258328 |
| forest_edge_eye | noon | 2.65 | 0.04 | 8.62 | 0.38 | 251 / 28 / 297 / 119 | 3309141 / 172253 / 15738472 / 1331008 |
| forest_clearing_eye | noon | 2.30 | 0.02 | 5.35 | 0.40 | 243 / 28 / 151 / 129 | 3462023 / 173014 / 8042590 / 1427057 |
| saved_18km | noon | 3.47 | 0.24 | -0.00 | 0.00 (no change) | 154 / 26 / 0 / 0 | 1438520 / 112440 / 3 / -1 |
| hills_eye | sun_8_set | 2.53 | 0.01 | 0.00 | 0.07 | 293 / 25 / 452 / 118 | 3677886 / 71690 / 10 / 181387 |
| forest_edge_eye | sun_8_set | 1.64 | 0.08 | 10.67 | 0.47 | 263 / 36 / 749 / 119 | 3441663 / 178198 / 15740912 / 1331075 |

##### Far viewport GPU delta (ms)

| pose | time | near terrain | far terrain | trees | grass |
|---|---|---|---|---|---|
| plains_eye | noon | 0.01 | 0.08 | 0.00 | 0.00 |
| hills_eye | noon | 0.01 | 0.02 | 0.00 | -0.00 |
| hills_300m | noon | 0.00 | 0.02 | 0.00 | 0.00 |
| hills_2km | noon | 0.00 | 0.02 | -0.00 | -0.00 |
| coast_eye | noon | 0.00 | 0.11 | -0.00 | -0.00 |
| forest_interior_eye | noon | 0.00 | 0.11 | -0.01 | -0.00 |
| forest_edge_eye | noon | 0.00 | 0.13 | -0.00 | 0.00 |
| forest_clearing_eye | noon | 0.00 | 0.11 | -0.01 | 0.00 |
| saved_18km | noon | 0.00 | 0.16 | 0.00 | 0.00 |
| hills_eye | sun_8_set | 0.00 | 0.02 | 0.00 | 0.00 |
| forest_edge_eye | sun_8_set | -0.00 | 0.13 | -0.00 | 0.00 |

##### GPU compute and bracket markers (median ms over baseline windows; n = windows with samples; a marker absent here did not run in the window, which is not zero)

| pose | time | GPU.TreeCulling | GPU.GrassCulling | GPU.Vegetation | GPU.ProxyHeights | GPU.HeightShadow | GPU.WaterFft | opaque bracket |
|---|---|---|---|---|---|---|---|---|
| plains_eye | noon | n/a (n=0) | 0.03 (n=4) | n/a (n=0) | n/a (n=0) | n/a (n=0) | n/a (n=0) | 3.92 (n=4) |
| hills_eye | noon | n/a (n=0) | 0.03 (n=4) | n/a (n=0) | n/a (n=0) | n/a (n=0) | n/a (n=0) | 4.04 (n=4) |
| hills_300m | noon | n/a (n=0) | 0.01 (n=4) | n/a (n=0) | n/a (n=0) | n/a (n=0) | n/a (n=0) | 3.12 (n=4) |
| hills_2km | noon | n/a (n=0) | n/a (n=0) | n/a (n=0) | n/a (n=0) | n/a (n=0) | n/a (n=0) | 2.55 (n=4) |
| coast_eye | noon | 0.04 (n=4) | 0.03 (n=4) | n/a (n=0) | n/a (n=0) | n/a (n=0) | 0.21 (n=4) | 5.05 (n=4) |
| forest_interior_eye | noon | 0.02 (n=4) | 0.03 (n=4) | n/a (n=0) | n/a (n=0) | n/a (n=0) | n/a (n=0) | 11.20 (n=4) |
| forest_edge_eye | noon | 0.02 (n=4) | 0.03 (n=4) | n/a (n=0) | n/a (n=0) | n/a (n=0) | n/a (n=0) | 10.29 (n=4) |
| forest_clearing_eye | noon | 0.02 (n=4) | 0.03 (n=4) | n/a (n=0) | n/a (n=0) | n/a (n=0) | n/a (n=0) | 7.91 (n=4) |
| saved_18km | noon | n/a (n=0) | n/a (n=0) | n/a (n=0) | n/a (n=0) | n/a (n=0) | n/a (n=0) | 2.51 (n=4) |
| hills_eye | sun_8_set | n/a (n=0) | 0.03 (n=4) | n/a (n=0) | n/a (n=0) | n/a (n=0) | n/a (n=0) | 2.72 (n=4) |
| forest_edge_eye | sun_8_set | 0.02 (n=4) | 0.03 (n=4) | n/a (n=0) | n/a (n=0) | n/a (n=0) | n/a (n=0) | 11.28 (n=4) |

##### Fill (CPU worker scopes from the teleport until builds were idle; wall time includes the 0.2 s teleport wait and the 2 s settle of go_pose)

| pose | time | wall s | Trees build n / med / p95 / sum ms | Trees disk load n / med / p95 / sum ms | Grass build n / med / p95 / sum ms |
|---|---|---|---|---|---|
| plains_eye | noon | 17.2 | 1792 / 8.91 / 12.76 / 17485 | 1792 / 0.05 / 0.09 / 150 | 552 / 2.98 / 8.37 / 2210 |
| hills_eye | noon | 17.8 | 1800 / 9.61 / 12.51 / 18237 | 1800 / 0.05 / 0.09 / 103 | 554 / 2.70 / 7.17 / 1847 |
| hills_300m | noon | 2.5 | 0 / n/a / n/a / 0 | 0 / n/a / n/a / 0 | 0 / n/a / n/a / 0 |
| hills_2km | noon | 2.4 | 0 / n/a / n/a / 0 | 0 / n/a / n/a / 0 | 0 / n/a / n/a / 0 |
| coast_eye | noon | 22.9 | 1797 / 9.65 / 25.78 / 24838 | 1797 / 0.05 / 0.09 / 124 | 553 / 2.08 / 6.83 / 1512 |
| forest_interior_eye | noon | 25.1 | 1792 / 14.76 / 21.51 / 25816 | 1792 / 0.05 / 0.09 / 113 | 551 / 3.62 / 8.69 / 2784 |
| forest_edge_eye | noon | 4.8 | 101 / 14.97 / 23.62 / 1928 | 101 / 0.06 / 0.10 / 6 | 417 / 2.52 / 9.63 / 2094 |
| forest_clearing_eye | noon | 3.5 | 34 / 19.53 / 89.90 / 958 | 34 / 0.06 / 0.11 / 2 | 241 / 2.58 / 15.36 / 1353 |
| saved_18km | noon | 302.2 | 2 / 242.44 / 242.47 / 485 | 2 / 0.07 / 0.09 / 0 | 4 / 21.23 / 29.63 / 89 |
| hills_eye | sun_8_set | 11.4 | 0 / n/a / n/a / 0 | 1800 / 0.13 / 0.20 / 255 | 554 / 1.66 / 6.33 / 1537 |
| forest_edge_eye | sun_8_set | 19.1 | 0 / n/a / n/a / 0 | 1534 / 0.16 / 0.25 / 283 | 553 / 2.66 / 9.61 / 2559 |

##### Workload at the pose (after the fill)

| pose | time | tree records | GPU tree roots | tree batches | grass instances | grass batches | hideable nodes: near / far / trees / grass |
|---|---|---|---|---|---|---|---|
| plains_eye | noon | 12848 | 0 | 0 | 73133 | 130 | 16 / 18 / 122 / 130 |
| hills_eye | noon | 0 | 0 | 0 | 21830 | 123 | 16 / 18 / 122 / 123 |
| hills_300m | noon | 0 | 0 | 0 | 21830 | 31 | 16 / 18 / 122 / 31 |
| hills_2km | noon | 0 | 0 | 0 | 0 | 0 | 16 / 18 / 122 / 0 |
| coast_eye | noon | 52095 | 52095 | 179 | 26698 | 116 | 16 / 18 / 399 / 119 |
| forest_interior_eye | noon | 256856 | 65536 | 73 | 175789 | 125 | 16 / 18 / 234 / 125 |
| forest_edge_eye | noon | 263830 | 65536 | 86 | 182965 | 124 | 16 / 18 / 265 / 124 |
| forest_clearing_eye | noon | 267384 | 65536 | 78 | 161262 | 133 | 16 / 18 / 249 / 133 |
| saved_18km | noon | 267384 | 0 | 0 | 0 | 0 | 16 / 18 / 122 / 0 |
| hills_eye | sun_8_set | 27211 | 0 | 0 | 21830 | 123 | 16 / 18 / 122 / 123 |
| forest_edge_eye | sun_8_set | 248104 | 65536 | 86 | 182375 | 123 | 16 / 18 / 265 / 123 |

##### Power windows

- 132 windows: recorded, non-authoritative (machine: rtx4070 (non-authoritative))


##### LOD switch (tree LOD0 to LOD2)

Tree: Maple variant 4, 24.4 m tall and 19.2 m wide, at around 2,355,131 / across 188,852 (46 m from `forest_edge_eye`), chosen as the nearest tree 8 m or taller ahead with a clear dolly line (2.7 m clearance). The camera is 1.7 m above the ground, looking at 25 % of the tree height (6 m), dollying 15 to 60 m in 0.25 m steps (180 frames), plus a 180-frame control at a fixed 15 m. No wind hook is public, so the control shows the wind and TAA noise floor.

Evidence in `.godot/audit_baseline/rtx4070/t0/lod_switch/2026-11-09_23-56-35/`: `sheet.png` (dolly), `control_sheet.png`, `dolly_crops_22_5_to_47_5m.png` (centre crops at about 22.5, 27.5, 32.5, 37.5, 42.5 and 47.5 m), `flicker.txt`, `frames.json`.

- Control: mean frame-to-frame luma change 0.043, largest spike 0.048 (the scene is essentially frozen).
- Dolly: mean 10.83 (camera motion dominates, so the "2x the control's largest spike" rule is degenerate here); the largest spikes are frames 100-109 (40.0 to 42.5 m) at 11.7-12.0, only 1.1 times the dolly's own mean; nothing stands out between 25 and 40 m (frames 40-100).
- Visual: in the sheet and crops the crown keeps its shape and shrinks smoothly; the leaf clusters get slightly coarser between 30 and 45 m, but there is no pop, flash or silhouette jump. Other trees crossing the view around frames 98-120 show wind sway.
- **Verdict: no visible LOD0-to-LOD2 switch for this tree with the user's settings.** The fade is a 175 m dither (25 to 200 m) and the dolly covered only its first 35 m; 60-200 m was not captured. The plan 7.1 item 8 condition ("visible switch at 25-40 m") does not apply to the user's preferences. With the shipped defaults (25-40 m) the switch would be 15 m wide and may be visible; that was not tested (`--prefs=defaults` on the same tool).

##### A1000 cost run (authoritative)

On the laptop, undocked, P0 / 35 W, memory clock 5,300-5,700 MHz, nothing else on the GPU, with the same working tree and the preference files in the laptop's `user://` (or `--prefs=<dir>`):

```powershell
.\tools\run_tests.ps1 -Label landscape_gpu -Tests landscape_cost_baseline
```

It writes `.godot/audit_baseline/a1000/t0/cost/<stamp>/cost.md` and `cost.json`, stamped with the machine and adapter name. Windows outside P0 / 35 W are marked invalid and the run fails below 80 % valid windows (then split it with `--poses=` through a manual launch). Expect about 30 minutes plus the first-run cache copy. Run `landscape_look_baseline` on the laptop too for its own look references.
