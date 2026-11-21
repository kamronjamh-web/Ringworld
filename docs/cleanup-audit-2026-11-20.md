# Repo cleanup audit, 2026-11-20

**Outcome (2026-11-21).** The user approved the cleanup, and it was carried out from this audit. The sections below are the audit as written, the before state. Where the cleanup departed from the audit:
- `legacy_terrain_isolation_test` was **fixed**, not deleted. It still guards the production dormant backend: `VoxelRingIntegrationTest.tscn` sets `DormantEditableBackend = true`, and it uses `RingTerrainCoarseSdfPilot`. The parse error was the typed write `grid.TerrainSettings = TERRAIN_SETTINGS.duplicate(true)` ("Cannot assign a new value to a constant"); it now uses `set()`. Because the test is kept, `resources/voxel/voxel_terrain_settings_default.tres` is kept too.
- `sunshine_inside_cloud_seam_test` was **deleted** together with its known-failure entry:
  - as registered (no `--cloud-seam-fixed`) it enforced nothing about the horizon regression it was written for;
  - its mask A/B was diagnostic-only by its own header, and it drove a node (`FarRingBackdrop/FarRingTexture`) that no longer exists;
  - it was unchanged since the initial commit.
- `storm_light_gpu_test` and `compositor_temporal_stability_test` were registered in the manifest.
- `tools/grass-render-matrix.md` was **kept**: it documents the live `--grass-render-matrix` mode of `grass_compute_benchmark`.
- Shorts capture: only the tooling was removed (`record_shorts.ps1`, `VerticalShortCapture.tscn`, `SHORTS_CAPTURE_PLAN.md`). The runtime hooks stay, because they live in the main gameplay scene and HUD and do nothing without `--shorts-*` arguments: `RingShortsCaptureDirector` in `VoxelRingIntegrationTest.tscn`, the capture overlay in `RingPresentationHud.cs`, and `--shorts-max-quality` in `world_size_launcher.gd`. Removed 2026-11-21 (user-approved): the director, HUD overlay and launcher argument are gone.

Read-only audit of branch `terrain-vegetation-revamp` (HEAD f74df15 plus the uncommitted `project.godot` and `world_session.gd` edits). Nothing was deleted, moved or edited. This file is the only file the audit adds. Every row is a candidate for the user to approve.

**Method.** A script indexed all 7,027 tracked files and scanned the 5,453 text files. Comments were stripped from C#, GDScript, shader and PowerShell files first, so a mention in a comment does not count as a use. A file counts as referenced when another code file names its path or basename, its `uid://` (from `.uid`, `.import` or the scene header), a C# type or `class_name` it declares, or, for `.gd`, `.ps1` and `.py` files, its stem as an identifier (this covers manifest keys and `"res://tools/%s.gd"` patterns). Tag globs in `tools/test_manifest.json` were not counted as uses. Docs, `.claude/`, `.codex/`, the memory index and `prototypes/` were reported but never counted as live uses. A reachability pass from `project.godot` (main scene, autoloads, plugins) separated game code from code that only tests use. Every candidate was then checked by hand for string-built paths (`$"..._{n}.glsl"`), directory scans (`DirAccess`, `BIOME_DIR`) and runners. Four false positives found that way are listed under "Keep" so that nobody deletes them later.

**What was not checked.** Unused methods inside live files. Whether manual tools still parse or pass, because no Godot, build or test runs were allowed during the play test.

## Summary

| Category | Delete | Archive | Commit | Ask user | Keep (false positive or still used) |
|---|---|---|---|---|---|
| 1 Unreferenced code | 4 groups (9 files) | - | - | 7 groups | 2 scenes + 4 test-fixture shaders |
| 2 One-off tools and probes | 10 scripts (+`.uid`s) + 6 manifest entries | 1 | - | 6 | 4 groups |
| 3 Obsolete tests | - | - | - | 3 | rest of known-fail list |
| 4 Untracked clutter | 4 items + 1 stale tracked uid | - | 75 `.uid` | - | - |
| 5 Superseded docs | - | 26 (incl. the one from 2c) | - | 16 | the rest |
| 6 Large binaries | 2 groups (100 files) | - | - | 2 | 2 groups + 5 false positives |

| Measure | Low-risk deletes | Plus medium deletes, if approved | Plus the "ask" items, if all approved |
|---|---|---|---|
| Tracked files removed | 107 | +49 | +about 40 |
| Tracked bytes freed | **25.5 MB** (25,526,304 B) | +0.22 MB (220,520 B) | +14.5 MB zips, +0.42 MB cave cluster |
| `-Suite gate` seconds saved | **0 s** | 0 s | 0 s |
| `-Suite full` seconds saved | 0 s | about 61 s | about 61 s |

The gate saving is zero for a structural reason, not because nothing was found. The candidate tests fall into three groups, and `-Suite gate` runs none of them:
- never registered in the manifest;
- `manual` tier;
- `known-fail` or `quarantined`, which `TestRunner.psm1` (lines 696-699) skips unless the suite is `full` or the test's first tag is hit.

Real gate time would come only from removing flaky gate tests. None of them belong to removed features, so none are listed.

Almost all of the reclaimable size is `demo/` (24.9 MB, the vendored Terrain3D demo). `prototypes/` (175 MB of POI source art) is the only bigger block. It is in use as source material and is listed as a question.

---

## 1. Unreferenced code

| Path | Evidence | Risk | Action |
|---|---|---|---|
| `scenes/VoxelPhase1.tscn` | Its 3 `ext_resource` scripts (`res://scripts/voxel_phase1/Runtime/VoxelPhase1Controller.cs`, `Phase1FlyCamera.cs`, `VoxelChunkNode.cs`) do not exist, and they have no uid fallback. The scene cannot load. The only mentions are in `README.md` line 252 and `PHASE1_VOXEL_SETUP_TEMP.md`. | low | delete (also drop the README row) |
| `scripts/Tools/far_regional_transition_contract.gd` (+`.uid`) | An `extends SceneTree` script outside `tools/`, so the manifest validator never sees it. Nothing runs it. It calls `RunStandaloneFarRegionalTransitionContract`, which no longer exists: `RingScaledFarViewport.Tests.cs` only has `RunFarRegionalTransitionContract`. It is broken. | low | delete |
| `scripts/Tools/far_surface_tile_contract.gd` (+`.uid`) | Same pattern. Nothing runs it. It calls `RunStandaloneFarSurfaceTileContract` (`RingScaledFarViewport.SurfaceTiles.Tests.cs:14`), and nothing else calls that method. Last touched 2026-10-12. | low | delete |
| `scenes/FarRegionalTransitionContract.tscn` | Loaded only by the two scripts above. | low | delete together with them |
| (follow-up) `RingScaledFarViewport.SurfaceTiles.Tests.cs` `RunStandaloneFarSurfaceTileContract`, `RingScaledFarViewport.Tests.cs` `RunFarRegionalTransitionContract()` (instance) | These methods become dead once the scripts above go. They sit inside a live partial class. | medium | ask: trim in a later code pass |
| `scenes/VoxelPhase1Test.tscn`, `VoxelPhase2GridTest.tscn`, `VoxelPhase3TerrainTest.tscn`, `VoxelPhase4RingTest.tscn`, `VoxelPhase5SystemsTest.tscn`, `scenes/VoxelDebugAnchoredMarker.tscn` | Nothing loads them (manifest tag globs only). `README.md:252` says "Retained legacy voxel development scenes", which was a deliberate keep at some point. | medium | ask: delete with the 3 controllers below |
| `scripts/Voxel/Phase1/VoxelEditController.cs`, `scripts/Voxel/Phase3/VoxelTestExplorerController.cs`, `scripts/Voxel/Phase5/VoxelPhase5DebugHud.cs` (+`.uid`) | Used only by the legacy phase scenes above. The rest of `Phase1`-`Phase5` is live through `VoxelRingIntegrationTest.tscn` (for example `VoxelChunkGridManager`). Delete only these three. | medium | ask (goes with the scenes) |
| `resources/voxel/voxel_terrain_settings_default.tres` | Used by the phase 3-5 scenes and preloaded by `tools/legacy_terrain_isolation_test.gd` (known-fail, section 3). | medium | delete only if both the scenes and that test go |
| `scenes/GrassMeadowBenchmark.tscn`, `scripts/grass_meadow_benchmark.gd`, `tools/GrassMeadowBenchmarkDriver.cs` (+`.uid`s) | No runner and no manifest entry (tag glob only). `README.md:251` lists it as the "Grass-focused benchmark", and the 2026-10-08 grass checklist ran it by hand. It predates grass cards becoming the default (2026-11-09). | medium | ask |
| `scenes/SmoothConstructionOverlaySmoke.tscn`, `scripts/Terrain/Editable/SmoothConstructionOverlaySmoke.cs` (+`.uid`) | No runner, no manifest entry, no skill. Mentioned only in `CAVE_AND_DEEP_STRUCTURE_IMPLEMENTATION_CHECKLIST.md` and the 2026-10-08 grass checklist. | medium | delete |
| `scripts/ring_day_night_cycle.gd` (+`.uid`) | A GDScript duplicate of `RingDayNightCycle.cs`. The game does not use it. Its only user is `tools/celestial_eclipse_smoke_test.gd:4,100-113` (`_verify_retained_legacy_clock_defaults`), which checks that the retained copy still matches. | medium | ask: delete it and that check together |
| Cave certification cluster: `tools/run_cave_certification.ps1`, `scenes/SmoothTerrainRuntimeSmoke.tscn`, `scripts/Terrain/Editable/SmoothTerrainRuntimeSmoke.cs` (145 KB), `scenes/WaterColumnRegressionSmoke.tscn` + `.cs`, and 16 diagnostics classes used only by `SmoothTerrainRuntimeSmoke.cs` (269 KB): `CaveTopologyFinalDiagnostics`, `DesignedCaveDensityCompositionDiagnostics`, `SmoothTerrainDiagnostics`, `SmoothTerrainTransitionMesherDiagnostics`, `Voxel/Phase3/Cave{CellBoundaryConnection,ChamberAnchor,ChamberGraph,FeatureFamily,FeaturePlacement,GenerationContract,ProvinceDistribution,RegionFeature,TopologyDensityComposer,TopologyPartition,WallNoise}Diagnostics`, `Voxel/Save/RingWorldCaveContractDiagnostics` | About 420 KB that only the certification runner reaches. That runner is not in the manifest, but `.claude/skills/godot-test-run/SKILL.md:87` lists it, and its Godot fallback still names `Godot_v4.7.2`. The voxel-caves feature itself is still live. | high | ask: keep as the cave certification, or retire it and register what is still wanted |
| `scenes/TreeRenderingRegressionSmoke.tscn` + `scripts/Voxel/LOD/TreeRenderingRegressionSmoke.cs` | No runner. However, the open TR-9 order (`docs/work-orders/terrain-vegetation/TR.md`, validation steps) says "TreeRenderingRegressionSmoke still checks the static gates ... add the compact gates to it". An open item points to it. | - | keep (consider registering it) |
| `scenes/CertifiedCloudShadowProjectionSmoke.tscn` + `.cs` | No runner. `docs/shadow-revamp-plan.md:155` names it as a test, and the shadow arc still has open review items (memory). | - | keep (consider registering it) |

Shaders and includes: after comment stripping, no unreferenced `.gdshader`, `.gdshaderinc`, `.glsl` or `.comp` was found under `materials/`. The files that only tests use (`celestial_lighting_diagnostic.gdshader`, `terrain_gpu/fnl_noise_probe.glsl`, `ring_df64_projection_probe.glsl`, `vegetation_gpu/grass_benchmark_cards.gdshader`) are test fixtures: keep.

## 2. One-off tools and probes

### 2a. Test subclasses that never run (not in the manifest)

Each of these `extends "res://tools/<base>.gd"`. `tools/testing/validate_manifest.ps1:79-85` only enforces entries for scripts matching `extends SceneTree`, so these scripts slipped through. Each one appears only as a path in a tag glob, and `git log -S` shows none was ever a manifest test. Nothing has run them since they were written. Their bases are still live tests.

| Path | Last commit | Evidence | Risk | Action |
|---|---|---|---|---|
| `tools/cloud_history_targeted_gameplay_gpu_test.gd` | 2026-10-19 | Only `docs/atmosphere-haze-revamp-plan.md` mentions it. Haze arc closed. | medium | delete |
| `tools/cloud_lightning_history_gpu_test.gd` | 2026-10-08 | No references. | medium | delete |
| `tools/cloud_sky_history_gpu_test.gd` | 2026-10-08 | `docs/cloud-diagnostics-2026-10-16.md` only. | medium | delete |
| `tools/cloud_slowdown_attribution_test.gd` (37 KB) | 2026-09-29 | `docs/cloud-slowdown-investigation.md` only (investigation finished). | medium | delete |
| `tools/cloud_terrain_sun_lighting_gpu_test.gd` | 2026-10-08 | No references. | medium | delete |
| `tools/far_cloud_patch_gpu_test.gd` | 2026-10-08 | No references. | medium | delete |
| `tools/grass_scenario_cost_capture.gd` | 2026-10-08 | No references. | medium | delete |
| `tools/shared_wind_vegetation_gpu_test.gd` | 2026-10-08 | No references. Shared wind was redone on 2026-11-20 (a9b1d4d). | medium | delete |
| `tools/water_shared_wind_gpu_test.gd` | 2026-10-08 | No references. | medium | delete |
| `tools/fsr_jitter_probe.gd` | 2026-10-19 | Mentioned only in a comment in `compositor_temporal_stability_test.gd:4` and in `docs/session-handoff-2026-10-19.md`. | medium | delete |
| `tools/storm_light_gpu_test.gd` | 2026-10-29 | The storm-light WO-3 test, cited by `docs/megastorm-design.md`, `docs/storm-revamp-plan.md` and `docs/storm-revamp-s0-storm-light-design.md`. The feature is live. | medium | ask: register it in the manifest, or delete |
| `tools/compositor_temporal_stability_test.gd` | 2026-10-25 | Not in the manifest, but `.claude/skills/visual-golden-check/SKILL.md` uses it by hand and `CompositorStabilityHelper.cs` serves it. | - | keep (consider registering it) |

The 11 deletable or registrable scripts plus their `.uid`s come to 22 files and 134 KB. When deleting them, also remove their lines from the tag globs in `tools/test_manifest.json` (lines 52-57, 202, 357-358, 375). **Tooling gap:** extend `validate_manifest.ps1` to flag `extends "res://tools/*.gd"` subclasses that declare their own verdict marker, and `extends SceneTree` scripts outside `tools/`.

### 2b. Manifest tool entries for removed things

| Entry | Tier / expected_s | Evidence | Risk | Action |
|---|---|---|---|---|
| `pda_compare_granite_ram`, `pda_compare_granite_vram_split`, `pda_compare_granite_vram_full`, `pda_compare_llama_ram`, `pda_compare_llama_vram_split`, `pda_compare_llama_vram_full` | manual / null, 300, 90 (the same for both models) | `docs/pda-8b-comparison.md:3`: "Granite/Llama comparison weights were removed". The entries pass `-ModelKey granite-3.3-8b-q4` / `llama-3.1-8b-q4` to `tools/run_pda_model_comparison.ps1`, so they cannot run. | low | delete the 6 manifest entries (manifest-only edit; the catalog rows in `PdaModelCatalog.cs` are a separate PDA decision) |
| `megastorm_wo11_real` | manual / null | No reference anywhere besides the manifest. Megastorm merged on 2026-11-05. Shares the `MEGASTORM_REAL` marker with `megastorm_real_path_check`, its successor. | medium | ask |
| `grass_cards_spike_measure`, `grass_cards_spike_sbs` | manual / null | Measured the P0 spike (2026-10-15). Grass cards are now production. The spike scene stays, because `tools/GrassCardAssetsTest.cs:147` compares against `GrassCardsSpikeAssets`. | medium | ask |
| `smooth_runtime_spike_benchmark`, `smooth_runtime_spike_world` | manual / null | 2026-10-15 spike; only the draft manifest and old handoffs mention it. | medium | ask |

The other 107 manual `kind: tool` entries are still live, have a skill reference, or belong to open arcs (PDA, TR, T-phases, perf pass). Keep. The oldest group (13 profiles last touched 2026-09-23, such as `grass_instancing_profile` and `tree_render_production_profile`) is referenced only by the draft manifest. They may no longer run, but that cannot be checked without Godot. Leave them until a sweep can run them.

### 2c. Other tool files

| Path | Evidence | Risk | Action |
|---|---|---|---|
| `tools/grass-render-matrix.md` | Unreferenced how-to for an old grass matrix. | low | archive to `docs/archive/` |
| `tools/record_shorts.ps1` + `scenes/VerticalShortCapture.tscn` + `SHORTS_CAPTURE_PLAN.md` | Shorts capture. Only the plan doc points at the runner, and the plan has 9 of 12 items open. `scripts/ui/RingShortsCapture*` is live game code. | medium | ask |
| `tools/run_sunblocker_demo.ps1`, `Start Sunblocker Demo.cmd`, `scenes/SunblockerDemo.tscn`, `docs/sunblocker-demo.md` | A user-facing demo launcher. Works through the `.cmd`. | - | ask: keep unless the demo is retired |
| `tools/PdaLoadProbe.cs`, `tools/install_pda_test_models.ps1` | Used by the PDA docs. The PDA arc is still open: `docs/pda-handoff-2026-11-06.md` says "slow Gemma response ... unresolved". | - | keep |
| `tools/tr7d_card_thinning_prototype.py` (77 KB) | TR-7d is waiting on user decision D10 (`TR.md:23,57`). | - | keep |
| `tools/keep_awake.ps1`, `tools/manage_generated_data.ps1`, `tools/poi/*.gd`, `tools/media/*.py` | Used by memory, the POI skill, `assets/poi/README.md`, `docs/GENERATED_DATA.md` and the sound project. | - | keep |

## 3. Obsolete tests

No manifest test belongs to a feature that is gone. Every test listed in `tools/test_known_failures.json` still targets live code (Terrain3D, grass clumps, the smooth pilot, biome workshop and so on). The failures are broken tests or fixtures, not dead features. There are no exact duplicates: no two `tools/*.gd` files are identical, and no two runner entries share the same runner and arguments. The rows below are the ones whose failure reason is effectively dead:

| Test | Tier / mode / expected_s | Known-failure record | Risk | Action |
|---|---|---|---|---|
| `legacy_terrain_isolation_test` (+ `tools/LegacyTerrainIsolationTest.cs`) | gate / headless / 1 s | known-fail since 2026-10-29: "GDScript parse error". It guards the "dormant editable backend". | medium | ask: fix the parse error or delete (it is the last user of `voxel_terrain_settings_default.tres`) |
| `sunshine_inside_cloud_seam_test` | gate / gpu / null (quarantine timeout 60 s) | quarantined since 2026-10-29: "stale FarRingBackdrop/FarRingTexture node path; hangs (901 s, 421 s)". `tools/sunshine_inside_cloud_seam_test.gd:87` still asks for `FarRingBackdrop/FarRingTexture`. The far viewport now builds `FarRingBackdropComposite` (`RingScaledFarViewport.cs:2322`). | medium | ask: rewrite against the current node, or delete |
| `celestial_eclipse_smoke_test` (`_verify_retained_legacy_clock_defaults` only) | see the manifest | Checks the legacy GDScript clock (section 1). | medium | trim that check if `ring_day_night_cycle.gd` goes |

Saving: 0 s in `-Suite gate` (both are skipped there); about 61 s in `-Suite full` (1 s plus the 60 s quarantine timeout), plus one Godot launch each.

## 4. Untracked clutter (`git status`)

| Path | Evidence | Risk | Action |
|---|---|---|---|
| 75 untracked `.uid` files (the list in `git status`) | Every one belongs to a tracked script. Their base files were added in 2c6ce9d, 31ca356, 571008e, 270dceb (2026-11-19) and f74df15, b5ba7de, 80aff5b, 26b4722, 0d3d2ac (2026-11-20) without their uids. Every other tracked `.cs`, `.gd`, `.gdshader` and `.gdshaderinc` has a `.uid` on disk. | low | **commit-uid** (all 75; this closes the real gap) |
| `tools/ProxyCountersProbe.cs` + `tools/ProxyCountersProbe.cs.uid` (2026-11-18 16:50) | T4-7 stage 3 "scratch helper for harness scripts". It wraps `RingProxyBuildCounters.Reset()` and `Snapshot()` as JSON. Its only user is `.godot/audit_baseline/common/t4/t4-7/scratch/t4_counters.gd`. Being untracked, it is still compiled into every local build and shows up as dirty in the perf-bench `report.json` files. | low | delete (or commit it as a registered tool if T4-7 harness runs are still wanted) |
| `.cache/acestep/progress_estimates.json` (2026-10-31) | ACE-Step step-time estimates written by the audio studio's music model (`prototypes/audio-studio/README.md:34`). | low | delete, and add `/.cache/` to `.gitignore` |
| `ProjectsRingworld.godottest_runsappdata_t5-7/` (empty, 2026-11-18) | An empty folder at the repo root, made when `C:\Projects\Ringworld\.godot\test_runs\appdata_t5-7` lost its backslashes in a shell. | low | delete |
| `generated/ring_surface_terrain3d/` (empty, 2026-09-24) | Empty, and no `res://generated` reference exists. Disposable output lives under `user://generated` (`tools/generated_output_paths.gd`). | low | delete |

There is also one **stale tracked uid**: `scripts/ring_cloud_layer.cs.uid` (`uid://blc4uoqqdy1le`). Its `.cs` file is gone and nothing references the uid. Risk low; action delete.

## 5. Superseded docs (move to `docs/archive/`, do not delete)

Moving a doc breaks relative links. `README.md` links several of these, so move them with `git mv` and fix the README and doc links in the same commit. Docs that code comments cite (the `code` counts in the audit table: storm, shadow, megastorm, containment, eclipse, emitter specs and so on) are design records. They stay where they are.

### 5a. Archive (low risk: finished, or a snapshot; nothing open points to them)

| Path | Evidence |
|---|---|
| `TEMP_FAR_LOD_TODO.md`, `TEMP_MASTER_PHASE_CHECKLIST.md`, `TEMP_VOXEL_PHASE_TRACKER.md`, `PHASE1_VOXEL_SETUP_TEMP.md` | Root "temporary" trackers from the initial commit (2026-09-23), never updated. Nothing references them. `PHASE1_VOXEL_SETUP_TEMP.md` lists `Scripts/Voxel/Core/*`, which no longer exists. |
| `ARK_FULLY_VOXEL_EDITABLE_CHECKLIST.md` | A "large-scope option" checklist: 301 of 301 items unchecked, never started, no references. |
| `MEGA_RING_SCALE_CHECKLIST.md` | Targets a 150,000,000 km diameter (43 of 44 items open). It was replaced by the certified 5,000 km ring. No references. |
| `docs/session-handoff-2026-10-15.md`, `-10-16.md`, `-10-19.md`, `docs/session-handoff-2026-11-09.md` | Dated handoffs. The 11-09 "Fable 5.1" problem is settled (memory: pin agent model versions). Only other old handoffs link them. |
| `docs/perf-ranking-2026-10-15.md`, `docs/phase-c-p0-measurements.md` | Dated measurement snapshots (8 and 2 inbound doc links, so fix those links). |
| `docs/cloud-diagnostics-2026-10-16.md`, `docs/atmosphere-terrain-diagnostics-2026-09-27.md`, `docs/cloud-slowdown-investigation.md`, `docs/night-horizon-slowdown.md` | Dated investigations, finished. |
| `docs/emitter-milestone1-handoff.md` | `voxel-cave-direction-handoff.md` calls Milestone 1 "complete". |
| `docs/grass-cards-integration-plan.md`, `docs/grass-cards-s6-gpu-plan.md` | Read-only plans; cards became the default on 2026-11-09 and the plan text says so. |
| `docs/shared-wind-implementation-checklist.md` | 47 of 47 checked; replaced by the steady shared wind S1 (a9b1d4d, 2026-11-20). |
| `docs/main-menu-revamp-plan.md` | Marks itself "**Historical (D3)** ... the flyover ... was deleted". |
| `docs/terrain-generation-detail-research.md`, `docs/terrain-lod-transition-integration.md`, `docs/terrain3d-surface-port.md` | September research and port notes, all implemented. Only sibling September docs link them. |
| `docs/test-suite-tooling-manifest.draft.json` (155 KB) | The draft that `tools/test_manifest.json` replaced (test tooling on main since b8b175a, memory). Only `test-suite-tooling-design.md` cites it. |
| `tools/grass-render-matrix.md` | Section 2c. |

That is 26 files, counting `tools/grass-render-matrix.md`.

### 5b. Ask (unchecked items remain, or another doc points at them)

| Path | Why not archive yet |
|---|---|
| `CERTIFIED_RING_ACTION_CHECKLIST.md` | Calls itself "the live implementation checklist"; 23 of 203 items open; not touched since 2026-09-23. |
| `CAVE_AND_DEEP_STRUCTURE_IMPLEMENTATION_CHECKLIST.md` | `docs/underground-mode-spec.md:223` (P6) says to update its Gate 4. |
| `FINAL_RING_SCALE_TARGET.md` | Cited by `docs/unreal-rewrite-master-plan.md:134,202`. |
| `TEMP_TERRAIN_GENERATION_TODO.md` | Cited by `docs/terrain-vegetation-revamp-plan.md:55` (open arc). |
| `SHORTS_CAPTURE_PLAN.md` | 9 of 12 open; goes with the shorts tooling question. |
| `docs/grass-system-implementation-checklist.md` (15 open), `docs/water-system-implementation-checklist.md` (9 open), `docs/weather-system-checklist.md` (35 open; `storm-revamp-plan.md` says it "stays the record") | Long-lived feature records with open items. |
| `docs/terrain3d-runtime-generation-plan.md`, `docs/terrain3d-runtime-generation-checklist.md` | M6 performance criteria still "remain open". |
| `docs/emitter-milestone2-plan.md`, `docs/main-menu-space-checklist.md` (7 open), `docs/phase-c-live-menu-work-orders.md`, `docs/far-ring-adaptive-rendering.md` (2 open), `docs/terrain-generation-cpu-optimization.md`, `docs/terrain-surface-smoothing.md` (+ `docs/benchmarks/terrain-surface-shading-*.png`, 1.6 MB) | Probably finished, but each has unchecked items or no clear close-out line. |

Keep: every doc that the memory index or `.claude/` points to (haze, shadow, storm, megastorm, containment, gpu-offload, load-time, vastness, perf pass, forest, T-phase and TR work orders, sound, sky); the PDA docs (arc open); `docs/unreal-rewrite-master-plan.md` (draft with locked user decisions); `docs/underground-mode-spec.md`; `docs/mountain-terrain-overhaul-plan.md` (deferred, not dropped); and the reference docs (`biome-workshop.md`, `GENERATED_DATA.md`, `performance_diagnostics.md`, `proxy-coverage-culling.md`, `world-generations.md`, `cloud-history-grain-fix.md`, `terrain-rendering-fixes.md`).

## 6. Large tracked binaries

| Path | Size | Evidence | Risk | Action |
|---|---|---|---|---|
| `demo/` (60 files: `Demo.tscn`, `NavigationDemo.tscn`, `CodeGeneratedDemo.tscn`, `data/terrain3d_*.res` 12 MB, `assets/textures/*` 12 MB, models, `src/*.gd`) | 24.9 MB | The Terrain3D upstream demo, added with the Terrain3D migration (2026-09-25). No file outside `demo/` names any `res://demo/` path, any of its file names (`ground037`, `rock023`, `terrain3d_00*`) or any of its `.import` uids. `addons/terrain_3d` does not reference it either. Already in the manifest `ignore_globs`. | low | delete |
| `onedrive_conflict_archive/2026-08-26/` (40 files: `*-DESKTOP-BBJ9N44.*.conflict-backup`, `*.canonical-backup`, `MANIFEST.md`) | 0.66 MB | OneDrive sync-conflict copies from before the repo existed. Nothing references them (manifest `ignore_globs` only). Git history keeps the initial-commit copy. | low | delete |
| `prototypes/ringworld_atmosphere_projector_bundle.zip` (12.4 MB), `prototypes/Ringworld_MainSpire_FarProxy_Rings_Update.zip` (2.2 MB) | 14.5 MB | Delivery archives. Only 10 of the first zip's 20 files match the extracted `prototypes/Ringworld_Atmosphere_Projector/`, and none of the second zip's 16 files match (it contains a patch, validation notes and a far-proxy `.res`). | medium | ask: delete if the extracted folders and the applied patch are the record |
| `prototypes/` (the rest: POI source kits, previews, `pda-voice`, `audio-studio`) | 161 MB | Source art and tools: `tools/poi/verify_*.gd`, `EmitterSiteRecipe.cs`, the audio-studio runners and the PDA code all reference it. About 70 MB of it is preview PNGs and Blender files. | high | ask: keep, or move the art kits out of git (Git LFS or an external store) |
| `assets/poi/beam_emitter/Surface_Base_Facility_Godot.glb` (2.1 MB), `Surface_Beam_Spire_Godot.glb` (0.3 MB) | 2.4 MB | The scenes use the combined `Surface_Spire_and_Facility_Godot.glb`. But `assets/poi/README.md:14-15` documents these split versions, and the POI skill copies import params from `Surface_Base_Facility_Godot.glb.import`. | - | keep (at minimum the `.import`) |
| `assets/poi/reactor/modules/*.glb` (5) | 0.5 MB | README: "five reusable assemblies (imported, not used by the wrapper)". | - | keep |

False positives found during the audit (keep, and do not re-flag):
- `third_party/godot_ocean_waves/compute/fft_compute_{128,256,512,1024}.glsl`: loaded as `$"fft_compute_{n}.glsl"` (`RingWaterFftProvider.cs:415`).
- `resources/audio/weather/**/*.ogg` (24 files): loaded by naming convention (`RingWeatherAudio.cs:77,247`, `RingWeatherAmbience.cs:46-49,1054,1065`, `RingEmitterFieldShotAudio.cs`).
- `resources/biomes/Lush Forest.tres`: `addons/biome_workshop/biome_workshop.gd:5` scans `res://resources/biomes`.
- `assets/poi/underground/CavernShell_*.res`: built as `$"{Directory}/CavernShell_{kind}.res"` (`tools/EmitterUndergroundBaker.cs:19-20`).
- `tools/fixtures/hydrology/d4_baseline/graph.json.gz`: a fixture baseline (`DrainageGraphTest.cs`, `RiverPathMetrics.cs`); off-limits.

## Not audited (off-limits by request)

`addons/` (all four are loaded: SunshineClouds2 and biome_workshop as plugins, clouds_lp by `RingCloudProviderController.cs`, terrain_3d as a GDExtension), `.godot/`, the prune keep-lists, the shadow, golden and fixture baselines, and the A1000 and 4070 goldens.
