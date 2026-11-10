# T0 wrap-up. Landscape audit leftovers and vastness V0: work orders

Parent: docs/terrain-vegetation-revamp-plan.md section 11 (T0). Prerequisite reading: `docs/terrain-vegetation-handoff-2026-11-10.md` ("Open items, in order", items 1-5 and 7); `docs/landscape-audit-t0-spec.md` sections 1, 2, 4 and 8.6; `docs/vastness-plan.md` section 7 (V0); `AGENTS.md` (Testing, Test output, Tools/ffmpeg).
Status: not started
Depends on: T0-1 to T0-7 (done, uncommitted on branch `gpu-offload`, see the handoff)
Freeze: none (no production change; generation 1 only)

Committing is not part of any work order here. The primary agent commits (handoff item 6).

## Phase facts

Verified against the working tree on 2026-11-10 unless marked **unverified**.

**Harness**

1. The T0 GPU tools boot through `tools/landscape_harness_common.gd` `boot()` (line 91). It forces v7 and v8 on (lines 103-104), calls `launcher._configure_game` (line 121), installs the preference files under their real names (`_install_isolated_preferences`, line 153; the list is at lines 15-26), then runs `LandscapeWorldBridge.VerifyGame`.
2. `tools/landscape_baseline_common.gd` `setup()` (line 33):
   - It disables `RingPerformanceMonitor` (line 50) and calls `COMMON.hold_world_clocks(game)` (line 51).
   - `cleanup()` (line 72) ends the timing bridge, stops the power log and `queue_free`s the game. It then deletes the fixture world directory straight away (`H.remove_dir_recursive`, line 83), in the same frame as the `quit()` that follows.
3. `run_look` is at line 131. `look.json` and `reference_set.json` are written at lines 195-196. `run_cost` is at line 485. `run_lod` is at line 796.
4. `tools/shadow_harness_common.gd` `hold_world_clocks` (line 370) pauses `RingDayNightCycle`. It also sets `RingVolumetricClouds.CloudSimulationPaused = true` and `CloudSimulationTimeSeconds = FIXED_CLOUD_TIME` (1234.5, line 17). **Nothing pins weather or storms.**
5. Weather is spatial and deterministic in cloud time.
   - `RingVolumetricCloudLayer.GetNaturalWeatherDiagnosticAtTime(around, across, time)` (`scripts/RingVolumetricCloudLayer.cs:3376`) returns `system_kind`, `influence`, `cloud_coverage`, `precipitation` and `lifecycle_enabled`. `tools/distant_rain_curtains_smoke_test.gd:54` and `:136` show how to call it.
   - `system_kind` is `WeatherSystemKind` (`RingVolumetricCloudLayer.cs:32`): `None` 0, `CumulusCluster` 1, `CloudBank` 2, `MajorFront` 3, `ExceptionalFront` 4, `Megastorm` 6 (scripted, never produced by the natural builder).
   - `NaturalWeatherLifecyclesEnabled` is exported at `RingVolumetricCloudLayer.cs:114`.
   - `RingSunshineCloudBridge.ClearSpawnedWeather()` is at `scripts/RingSunshineCloudBridge.cs:1625`. The scene node is `SunshineCloudBridge`.
   - The scene also holds `RingMegastorm` and `LocalWeatherEffects` (`scenes/VoxelRingIntegrationTest.tscn:639`, `:701`). **Unverified:** whether the natural-weather diagnostic covers the megastorm and storm-arrival systems.
6. The coast look reference in `.godot/audit_baseline/rtx4070/t0/look/2026-11-10_00-01-25/` was captured during a passing storm (handoff, open item 1). Spec section 8.6 cites this folder.
7. The fixture `tools/fixtures/landscape_poses_v8_s1337.json` has 14 poses.
   - `t0` reports: `plains_eye`, `hills_eye`, `hills_300m`, `hills_2km`, `coast_eye`, `forest_interior_eye`, `forest_edge_eye`, `forest_clearing_eye`, `saved_18km`.
   - `v0` reports: `hills_2km`, `coast_eye`, `forest_clearing_eye`, `saved_18km`, `v0_centre_eye`, `v0_wall_lo_eye`, `v0_wall_hi_eye`, `v0_ridge_eye`, `v0_alt_12km`.
   - Times: `noon`, `sun_8_set`, `sun_2_set`, `night_planet`. Headings: `along_pos`, `along_neg`, `wall_hi`, `wall_lo`, `arch_up`.

**Runner and known failures**

8. Run tests with `.\tools\run_tests.ps1 -Label <label> -Tests <names>`. It has no pass-through for tool flags, so flags need a by-hand launch (godot-test-run skill, manual fallback).
9. `tools/test_known_failures.json` records "crash after PASS" as `status: flaky` with `match: "<MARKER>=PASS"`. Examples: `cloud_shadow_crossfade_test`, `megastorm_capture`.
10. `quality_preset_contract_test` is already listed as flaky, but with `match: "C6 .*p=0"`, so a crash after PASS does not match it.

**Exit code -1073741819 (0xC0000005, access violation) after a PASS marker**

11. The run history in `.godot/test_runs/*/results.json` shows when this started:
    - `imported_tree_prepared_fallback_test` (headless) passed in every run up to `final1/20261104-224336`. It crashed after PASS in `gpu3/20261106-001701`, `grass-cards-default/20261109-213040` and `grass-cards-default2/20261109-214358`, and passed in `gpu3r` and `gpu3v` (11-06).
    - `quality_preset_contract_test` crashed after PASS in `gpu3r/20261106-005012` and `grass-cards-default/20261109-213040`.
    - `landscape_cost_baseline` crashed after PASS in `landscape_gpu/20261110-000700`.
    - The GPU-offload commits landed on 2026-11-06 (`d88987a`, `2c713b0`, `fa94a08`, `e470adc`).
    - **Hypothesis (unverified):** the three tests share one exit-time cause introduced by the GPU-offload work, for example RenderingDevice compute resources or worker jobs outliving the scene at quit. The timestamp hooks the handoff suspects may not be the cause.

**Other pre-existing failures**

12. `ui_scaling_smoke_test` (gpu, gate, `world: start-menu`) fails with "Button visual-center hover failed at (1280, 720)":
    - failed: `megastorm/20261030-141229` onward, `arch_60fps/20261102-205241`, `grass-cards-default*`;
    - passed: `megastorm6c/20261030-172711`, `nightly/20261031-030003`, `final1/20261104-224336`.
    So it is intermittent and predates this work.
13. `grave_menu_interaction_test` (gpu, gate) fails with "gameplay setting AtmospherePreset is outside a collapsible section", followed by an exit crash.
    - It passed up to `final1/20261104-224336` and failed in both `grass-cards-default*` runs (2026-11-09).
    - So it is a regression between 2026-11-04 and 2026-11-09. Commits in that window touch the drawer, and the uncommitted working tree has `scripts/ui/RingLiveTuningDrawer.Grass.cs` modified (the Cards default).
    - The `AtmospherePreset` row is created at `scripts/ui/RingLiveTuningDrawer.Atmosphere.cs:132`.

**Shadow harness preference names**

14. The shadow harness installs preference files that do not exist. `tools/shadow_harness_common.gd:272` copies `user://surface_tuning.tres` and `user://atmosphere_defaults.tres`. The real files are:
    - `gameplay_surface_terrain_defaults.tres` (`scripts/ui/GameplaySurfaceTerrainDefaults.cs:10`);
    - `atmosphere_visual_defaults.tres` (`scripts/ui/AtmosphereVisualDefaults.cs:12`).

    The same wrong list is copied in `tools/night_horizon_attribution_test.gd:388` and `tools/terrain_render_attribution_test.gd:652`. Callers of `shadow_harness_common.gd` are `contact_shadow_capture`, `forest_dive_collision_test`, `height_shadow_budget_test`, `height_shadow_gameplay_smoke`, `quality_preset_capture`, `quality_preset_contract_test`, `shadow_attribution_test`, `shadow_quality_capture` and `weather_lung_world_bind_test`. `landscape_harness_common` uses only its helpers, not its preference list.

    Several harnesses pass `surface_tuning.tres` only as an isolated destination (`eclipse_*`, `emitter_*`, `terrain3d_gameplay_*`). They copy nothing from `user://`, so they are not affected.

**Baselines**

15. Durable GPU baselines that `tools/prune_test_output.ps1` keeps (the `$keep` list, lines 31-36) are `shadow_baseline`, `audit_baseline`, `s5_before`, `shadow_compose_identity_head_clouds` and `shadow_compose_identity_storm_revamp_4070`. **Unverified:** which of them were captured with Blades grass in view. `wet_world_gpu_test.gd` and `terrain_render_attribution_test.gd` hold no stored image golden (grep finds none). The handoff's "may need re-baselining" list is therefore a hypothesis to check, not a list of known breakages.

**V0**

16. V0 seams:
    - `tools/rim_wall_check.gd` has the `top_along` view (line 259).
    - `RingTerrainShadowGeometry.RayHitsStructuralWall` is at `scripts/Voxel/LOD/RingTerrainShadowGeometry.cs:41`.
    - The registries are `scripts/Terrain/Sites/EmitterSiteRegistry.cs`, `scripts/poi/Projectors/AtmosphereProjectorSites.cs` (`Build`, line 43), `scripts/Terrain/Sites/ClimateMachineRegistry.cs` and `scripts/Voxel/LOD/RingScaleAnchorLayer.cs`.
    - `RingScaleAnchorLayer` exposes only its scatter parameters (`AnchorCellSizeMeters` at line 52, `SpawnChance` at line 58, the tower heights at lines 66-68). There is no positions accessor, so the spec's "reproduce the scatter and verify 10 positions" path applies.
    - `tools/landscape_audit_bands.gd` has `compute` (line 54), `band_image` (line 105) and `adjacent_steps` (line 117).
    - No `tools/Vastness*` or `tools/vastness_*` file exists yet. The manifest already has the `vastness` tag.
17. The V0 view count is wrong in spec section 4. That section estimates about 50 views. Counting the poses gives 9 `v0` poses x 5 headings at `noon` (45), plus 3 poses x 5 headings x 3 further times (45), plus the wall-top view: about 91 views. At about 10 s per view (the T0-7 look run took about 10 s per view), that is still about 15 GPU minutes.

## Order and parallelism

| WO | Role | Size | Depends on | Can run alongside |
|---|---|---|---|---|
| T0W-1 Pin weather; re-capture 4070 look references | sol_implementer | S | - | T0W-4 (headless edit), T0W-5 (analysis part) |
| T0W-2 Crash after PASS at exit (cost run + two gate tests) | sol_lead | M | - | T0W-4, T0W-5 analysis; GPU runs serial |
| T0W-3 A1000 cost and look run (user checklist) | user | S | T0W-1; the primary's T0 commit; T0W-2 preferred | anything on the desktop |
| T0W-4 Shadow-harness preference names (**needs user approval before execution**) | sol_implementer | S | user decision | T0W-1, T0W-2 |
| T0W-5 Triage `ui_scaling_smoke_test`, `grave_menu_interaction_test`; Cards-grass baseline inventory | sol_lead | S-M | - | T0W-1, T0W-2 (GPU runs serial) |
| T0W-6 Re-baseline captures that changed only because of Cards grass | sol_implementer | M | T0W-5, the primary's approval of the list | headless work only |
| T0W-7 Vastness V0-1: views, analytic distance, band and ladder tables | sol_implementer | M | T0W-1 | headless WOs |
| T0W-8 Vastness V0-2: fly-out and vanish distances | sol_implementer | M | T0W-7 | headless WOs |

**T0W-1 blocks work outside this phase.** Its weather pin is a prerequisite for every later comparison capture taken through the landscape harness: T7-13, T8-9, the T1 phase-acceptance heatmap, T1-9's `--generation=1` compare, and each freeze's look goldens. Without it, `FIXED_CLOUD_TIME` puts a storm over `coast_eye`. Schedule it first.

GPU work is exclusive. Every GPU run goes through the runner's queue on the `landscape_gpu` profile (one run at a time), and nobody kills another agent's Godot. The four failing gate tests from handoff item 5 are split by cause: the two crash-after-PASS tests go to T0W-2, the two genuine failures to T0W-5.

## Work orders

### T0W-1. Pin weather in the landscape harness and re-capture the 4070 look references

ROLE: sol_implementer
SIZE: S
DEPENDS ON: -
BLOCKS: T0W-3, T0W-7, T7-13, T8-9, and every later look golden or comparison capture taken through the landscape harness

OBJECTIVE
After this WO, every landscape GPU tool (smoke, look, cost, LOD switch, and later V0) runs under one recorded, clear weather state, so storms cannot appear in a capture. The 4070 look references are re-captured under it and replace the storm-affected set.

CONTEXT / ROOT CAUSE
`hold_world_clocks` pins cloud time but not weather (Phase facts 4-6). Natural weather is a deterministic function of position and cloud time. `FIXED_CLOUD_TIME` 1234.5 happens to put a storm cell over `coast_eye`. Handoff open item 1.

FILES/SYMBOLS
- `tools/landscape_harness_common.gd`: new `static func pin_weather(game: Node, poses: Array) -> Dictionary` and `static func weather_at(game: Node, around: float, across: float) -> Dictionary`.
- `tools/landscape_baseline_common.gd`: `setup()` (call `pin_weather` after `COMMON.hold_world_clocks`, line 51); `_common_record`; `run_look`, `run_cost` and `run_lod` (record the weather per view).
- Read only: `RingVolumetricCloudLayer.cs` (`GetNaturalWeatherDiagnosticAtTime`, `NaturalWeatherLifecyclesEnabled`), `RingSunshineCloudBridge.cs` (`ClearSpawnedWeather`), `tools/distant_rain_curtains_smoke_test.gd` (call shape).

REQUIRED CHANGES
1. `pin_weather(game, poses)`:
   1. Call `SunshineCloudBridge.ClearSpawnedWeather()` when the node exists.
   2. Search a deterministic list of cloud times for one global time `T`: `1234.5 + 600 * k` for k = 0..199. Accept the first `T` at which every pose in `poses` has `precipitation <= 0.02` and a `system_kind` below `MajorFront` (3). `MajorFront`, `ExceptionalFront` and `Megastorm` count as storms (Phase fact 5). Cite `RingVolumetricCloudLayer.cs:32` in a comment.
   3. Set `RingVolumetricClouds.CloudSimulationTimeSeconds = T` (paused stays true).
   4. Return `{"cloud_time": T, "searched": k + 1, "per_pose": {...diagnostic...}, "spawned_cleared": bool, "megastorm": <state or "unknown">}`.
   5. Find a read-only way to tell whether `RingMegastorm` is active near a pose (a public property or diagnostic). If none exists, record `"megastorm": "unknown"` and say so in the report. Do not change production code.
2. Flag `--weather=clear|natural`. The default is `clear`, which runs step 1. `natural` keeps `FIXED_CLOUD_TIME` and only records the weather, which reproduces the old behaviour.
3. Record a `weather` block (the diagnostic at the view's around and across, at `T`) in every view of `look.json` and `cost.json`, and in `frames.json` of the LOD tool. In `clear` mode the look tool fails a view whose `precipitation > 0.02`, with reason `weather not clear`.
4. Pose sets for the search: T0 tools pass the nine `t0` poses. V0 (T0W-7) will pass the `v0` poses. The function takes the list, so one `T` per tool is fine.
5. Re-capture the 4070 look references: `.\tools\run_tests.ps1 -Label landscape_gpu -Tests landscape_look_baseline`. The new folder replaces the old one.
   - In `docs/landscape-audit-t0-spec.md` section 8.6 "Look references", update the output path, the weather line (`cloud_time`) and the band summary table of the five reference views.
   - Delete the superseded folder `.godot/audit_baseline/rtx4070/t0/look/2026-11-10_00-01-25/` only after the new run passes.
   - Do not re-run the cost or LOD parts on the 4070. Note in 8.6 that the 4070 cost `coast_eye` row predates the weather pin.

DO-NOT-CHANGE CONSTRAINTS
- Generation 1 worlds must stay identical: `world_generation_fingerprint_test` must pass unchanged.
- No edits under `scripts/`, `scenes/`, `materials/`, `resources/` or `addons/`. No edit to `tools/shadow_harness_common.gd`.
- Pose fixture, band table, prefs handling and `VerifyGame` are unchanged.
- No grave-menu settings.

VALIDATION STEPS
1. Build with `./tools/use_godot_48.ps1 -Build`.
2. Quick by-hand pass (godot-test-run manual fallback, APPDATA = the `landscape_gpu` runner profile): `landscape_look_baseline.gd -- --poses=coast_eye,plains_eye`. Check that `look.json` has the `weather` block and that `coast_eye__noon.png` shows no rain.
3. Prove the check can fail: run the same with `--weather=natural`. The coast view's recorded `precipitation` must be above 0.02. That is the old storm.
4. `.\tools\run_tests.ps1 -Label landscape_gpu -Tests landscape_look_baseline -DryRun`. The expected selection is exactly `landscape_look_baseline`. Then the real run. Report the `results.md` summary line.
5. `.\tools\run_tests.ps1 -Label t0w_weather -Cache none -Tests landscape_world_test,world_generation_fingerprint_test`: both pass.
6. Pictures for the user, in chat:
   - old and new `coast_eye__noon` side by side, caption "storm removed by the weather pin";
   - the new `plains_eye__noon`;
   - the new `forest_edge_eye__noon`.
7. Run `.\tools\prune_test_output.ps1 -Apply`.

ESCALATION CONDITIONS
- No `T` in the 200 candidates clears all nine poses. Report the best `T` and its worst pose. The fallback (`NaturalWeatherLifecyclesEnabled = false`, or a per-pose time) changes what the clouds look like and is a lead decision.
- The diagnostic says clear but the image shows rain or a storm, which would mean the diagnostic does not cover the megastorm or storm arrival.
- Clearing spawned weather or setting the cloud time changes anything other than weather in the references (exposure, sky brightness), seen by comparing `plains_eye__noon` old and new with `tools/media/ffmpeg_tools.ps1 compare`. Keep that heatmap as evidence.

### T0W-2. Crash after PASS at exit: `landscape_cost_baseline`, `imported_tree_prepared_fallback_test`, `quality_preset_contract_test`

ROLE: sol_lead (investigation; may implement a tools-only fix)
SIZE: M
DEPENDS ON: -

OBJECTIVE
Each of the three tests exits 0 after PASS, or is recorded in `tools/test_known_failures.json` as a known exit-time engine issue backed by a stated root cause or a documented bisection.

CONTEXT / ROOT CAUSE
All three print PASS and then exit with -1073741819 (Phase facts 2, 11). The onset dates point at the GPU-offload work of 2026-11-06, not at the T0 timestamp hooks. `imported_tree_prepared_fallback_test` is headless, so timestamps cannot explain it. Handoff items 3 and 5.

FILES/SYMBOLS
- `tools/landscape_cost_baseline.gd`
- `tools/landscape_baseline_common.gd` (`cleanup`, line 72; bracket detach around line 574)
- `tools/LandscapeTimingBridge.cs`
- `tools/landscape_pass_timestamp_effect.gd`
- `tools/imported_tree_prepared_fallback_test.gd` and its C# class
- `tools/quality_preset_contract_test.gd`
- `tools/test_known_failures.json`
- Read only: GPU-offload owners of RenderingDevice resources (`scripts/Terrain/Generation/Gpu/*`, `RingTerrainProxySector.GpuHeights.cs`), `scripts/world_session.gd` `_drain_worker_pool_for_exit` (line 270)

REQUIRED CHANGES
1. Reproduce each crash at least 3 times. Use the `log-triage` skill on every crash log: look for the faulting thread or module, any `ERROR:` lines in the last 50 lines, and leak reports.
   - Cost: by hand with `-- --poses=plains_eye --rounds=1 --seconds=4` on the `landscape_gpu` profile.
   - The other two: `.\tools\run_tests.ps1 -Label t0w_exit -Tests imported_tree_prepared_fallback_test,quality_preset_contract_test`, repeated 3 times.
2. Bisect the cost tool by hand, one change at a time, 3 runs each:
   - `--no-bracket` (already exists);
   - no `LandscapeTimingBridge` (add a hidden `--no-timing` flag);
   - no power log (`--no-power-log`);
   - `RingPerformanceMonitor` left enabled;
   - cleanup that waits 30 process frames after `queue_free(game)` and deletes the fixture directory after that wait instead of before `quit()`.
3. Run `imported_tree_prepared_fallback_test` with `--proxy-heights=cpu` and `--proxy-install=mesh` (the GPU-offload opt-outs) if it accepts them, 5 runs. If the crash disappears, the cause is in GPU offload.
4. Decide:
   - (a) The cause is in a tool (cleanup order, a hook): fix it in `tools/` and prove 5 clean exits.
   - (b) The cause is in production code: stop. Write a bounded fix WO for the primary (file, symbol, the change, a test) and do not implement it.
   - (c) It is engine-level and cannot be fixed in tools: add `flaky` entries to `tools/test_known_failures.json` with `match` = the test's `<MARKER>=PASS`, an `evidence` line citing your runs, and `since: 2026-11-06`. For `quality_preset_contract_test` add the PASS case without deleting the existing `C6 .*p=0` meaning. If the format allows only one match per test, use a regex alternation and say so in `evidence`.
5. Record the outcome in spec section 8.6 (cost-run row) and in this WO's result note.

DO-NOT-CHANGE CONSTRAINTS
- Generation 1 worlds must stay identical: `world_generation_fingerprint_test` must pass unchanged.
- No production code changes (outcome (b) stops).
- The cost tool's measurements must not change. Changes to its cleanup run after the outputs are written.
- Never kill another agent's Godot process. Profiles: `landscape_gpu` (GPU) and `t0w_exit` (headless).

VALIDATION STEPS
1. `.\tools\run_tests.ps1 -Label t0w_exit -Tests imported_tree_prepared_fallback_test,quality_preset_contract_test -DryRun`: exactly those two.
2. After a fix: 5 consecutive clean runs of each (exit 0, `pass`). For the cost tool, 3 short by-hand runs, then one full `.\tools\run_tests.ps1 -Label landscape_gpu -Tests landscape_cost_baseline`. The expected summary is "1 selected: 1 pass".
3. After a known-failures entry: the runner shows the row as `flaky-pass` or `known-fail` and not `crash`.
4. Run `pwsh -File tools/testing/validate_manifest.ps1` if the manifest was touched.
5. Run `.\tools\prune_test_output.ps1 -Apply`.

ESCALATION CONDITIONS
- The cause is in production code (decision (b)).
- The crash cannot be reproduced in 5 tries. Record it as flaky with the evidence and stop.
- The bisection takes more than about 3 hours without isolating a factor. Hand back to the primary with the matrix; `sol_deep` is the next step only if the primary agrees.

### T0W-3. A1000 cost and look run (checklist for the user, on the laptop)

ROLE: user (the primary agent hands this checklist over; no agent can run it)
SIZE: S (about 50 minutes of machine time)
DEPENDS ON: T0W-1; the T0 work committed by the primary and available on the laptop (handoff item 6); T0W-2 preferred (without it the cost run reports "crash after PASS", but its outputs are still complete)

OBJECTIVE
The authoritative A1000 cost baseline and the A1000 look references exist under `.godot/audit_baseline/a1000/t0/`, and their tables are in spec section 8.6.

CONTEXT / ROOT CAUSE
Plan section 12 budgets are A1000 numbers. The 4070 cost run is non-authoritative, and goldens are machine-specific (spec section 1, item 10; spec section 7, decision 1). Handoff item 2.

FILES/SYMBOLS
Outputs only: `.godot/audit_baseline/a1000/t0/cost/<stamp>/` (`cost.md`, `cost.json`, `power_log.csv`, `prefs_snapshot/`) and `.godot/audit_baseline/a1000/t0/look/<stamp>/`.

REQUIRED CHANGES (the checklist)
1. On the laptop: `git fetch`, check out the branch the primary named, and check that `git log -1` matches the commit the primary gave you.
2. Undock and unplug external monitors. Plug in the charger. Use the power plan you normally use for performance work. Close games, browsers with video, and any other GPU apps.
3. Run `nvidia-smi --query-gpu=name,pstate,enforced.power.limit,clocks.mem --format=csv`. Expect `RTX A1000`, enforced limit 35 W. The P-state is low at idle; the tool checks P0 under load itself.
4. Run `.\tools\gpu_queue.ps1 -Status`. Nothing should be running. Start `.\tools\keep_awake.ps1` in a separate window.
5. Run `.\tools\run_tests.ps1 -Label landscape_gpu -Tests landscape_cost_baseline -DryRun`. It should list only `landscape_cost_baseline`. Then run it without `-DryRun`. It takes about 30 minutes; the first run also copies about 2.5 GB of caches.
6. Open `.godot/audit_baseline/a1000/t0/cost/<stamp>/cost.md` and check the "Power windows" line:
   - 80 % or more valid: done.
   - Less: tell the primary. The run is then split by poses with a manual launch.
7. Run `.\tools\run_tests.ps1 -Label landscape_gpu -Tests landscape_look_baseline` (about 4 minutes).
8. Send the primary agent:
   - the two `results.md` summary lines;
   - `cost.md`;
   - `look.json`, plus the five reference PNGs named in `reference_set.json`;
   - the `sha256` lines from each `prefs_snapshot/` (your laptop's grave-key settings may differ from the desktop's; that is fine, but the primary must know).
   A zip of the two stamp folders also works.
9. Run `.\tools\prune_test_output.ps1 -Apply` and stop `keep_awake`.

DO-NOT-CHANGE CONSTRAINTS
Do not change grave-key settings between the two runs. Do not run other GPU work at the same time.

VALIDATION STEPS
The primary agent pastes `cost.md` into spec 8.6 "A1000 cost run (authoritative)". It then compares the tree, near-terrain and grass hide deltas with the plan section 12 budgets and flags any phase gate that the measured tree cost makes infeasible (handoff: "revisit the T4-T6 budgets"). It sends the user the A1000 `forest_edge_eye__noon` and `coast_eye__noon` pictures.

ESCALATION CONDITIONS
The tool fails the world check, more than 20 % of power windows are invalid, or the laptop drops to P5 during the run. Report back; do not retry more than once.

### T0W-4. Shadow-harness preference file names (needs user approval before execution)

ROLE: sol_implementer
SIZE: S
DEPENDS ON: none. **User answer (2026-11-10): approved, option A** (fix all three copies; existing shadow baselines become historical).

OBJECTIVE
Shadow and attribution harnesses that claim to use the user's preferences really install the user's atmosphere and surface-terrain files (option A), or can opt in to doing so (option B).

CONTEXT / ROOT CAUSE
Phase fact 14. The shadow S0 baselines ran with default haze and default surface tuning. Fixing the names changes what future runs of nine tools measure (spec section 1, item 4; handoff item 4).

Options for the user (recommendation: A):
- **A. Fix the names everywhere.** Correct the list in all three copies. From now on every caller measures the user's real haze and surface settings, like the landscape harness. The existing `.godot/shadow_baseline` and `s5_before` captures stay as historical "default haze" records. The next shadow comparison re-captures its own "before" set instead of comparing against them.
- **B. Opt-in flag.** Keep today's behaviour as the default and add `--prefs=user` (the same meaning as in `landscape_harness_common.gd`), which installs the corrected list. No existing baseline changes meaning, but the default stays misleading.
- C. Leave it and record the behaviour in `docs/shadow-revamp-plan.md` (not recommended).

FILES/SYMBOLS
- `tools/shadow_harness_common.gd` `_install_isolated_preferences` (line 268, list at 272)
- `tools/night_horizon_attribution_test.gd:388`
- `tools/terrain_render_attribution_test.gd:652` (`_install_isolated_preferences`, line 647)
- `docs/shadow-revamp-plan.md` (one note)

REQUIRED CHANGES
1. The source file names become the real `UserPath` names:
   - `["InstallSurfaceTerrainDefaults", "gameplay_surface_terrain_defaults.tres"]`;
   - `["InstallAtmosphereDefaults", "atmosphere_visual_defaults.tres"]`.
   Keep the other five entries. Option A changes this in all three files. Option B adds the corrected list behind `--prefs=user` in `shadow_harness_common.gd` only (the other two files take the same flag) and keeps the old list as the default.
2. Each copy prints one line `PREFS_INSTALLED <name>=<copied|absent>` per file, so a run shows what it used.
3. Add one dated note to `docs/shadow-revamp-plan.md`: what changed, and from which date the baselines include the user's haze.

DO-NOT-CHANGE CONSTRAINTS
- Generation 1 worlds must stay identical: `world_generation_fingerprint_test` must pass unchanged.
- No production code changes. Do not re-capture or delete any baseline in the prune keep list.

VALIDATION STEPS
1. `.\tools\run_tests.ps1 -Label t0w_prefs -Tests height_shadow_gameplay_smoke -DryRun`, then the run (GPU, extended tier, about 5 minutes).
   - Option A: the log shows `PREFS_INSTALLED atmosphere_visual_defaults.tres=copied`.
   - Option B: run it once without the flag (`absent` names as before) and once by hand with `--prefs=user` (`copied`).
2. Prove the line can fail: run by hand with an isolated profile that has no `atmosphere_visual_defaults.tres`. The line must say `absent`.
3. Run `.\tools\prune_test_output.ps1 -Apply`.

ESCALATION CONDITIONS
Installing the real atmosphere file makes any of the nine callers fail or time out. Report which one; do not tune around it.

### T0W-5. Triage two failing gate tests and inventory the baselines Cards grass may have moved

ROLE: sol_lead (investigation)
SIZE: S-M
DEPENDS ON: -

OBJECTIVE
`ui_scaling_smoke_test` and `grave_menu_interaction_test` each have a root cause and an outcome: a known-failures entry, or a bounded fix WO. Separately, there is a checked list of stored baselines and goldens whose difference is caused only by the Cards default.

CONTEXT / ROOT CAUSE
Phase facts 12, 13 and 15. Handoff item 5. The other two tests from that item are crash-after-PASS cases and belong to T0W-2.

FILES/SYMBOLS
- `tools/ui_scaling_smoke_test.gd`
- `tools/grave_menu_interaction_test.gd`
- `scripts/ui/RingLiveTuningDrawer*.cs` (read; `RingLiveTuningDrawer.Atmosphere.cs:132`)
- `tools/test_known_failures.json`
- the keep-list baselines (Phase fact 15) and `tools/fixtures/`

REQUIRED CHANGES
1. `ui_scaling_smoke_test`:
   1. Run it 5 times: `.\tools\run_tests.ps1 -Label t0w_triage -Tests ui_scaling_smoke_test`.
   2. Read the test's hover logic and find why the centre hover misses at 2560x1440 scale (window focus, the mouse position before the window is ready, DPI).
   3. If it is a harness timing issue: write a bounded fix WO for the test (no production change).
   4. If the failure is real UI behaviour: write a fix WO for the primary.
   5. In the meantime add a `flaky` entry (match `Button visual-center hover failed`, `since: 2026-10-30`, with the run evidence above).
2. `grave_menu_interaction_test`:
   1. Find the change that moved the `AtmospherePreset` row outside a collapsible section. Use `git log --since=2026-11-04 --format="%h %ad %s" -- scripts/ui/` and `git diff -- scripts/ui/` (uncommitted work, including the Cards default edit to `RingLiveTuningDrawer.Grass.cs`).
   2. Name the commit or the uncommitted file and the owning session.
   3. Outcome: a fix WO (move the row into its section, or fix the drawer section builder), or "caused by another session's uncommitted work; report to the primary". Do not add a known-failure entry for a regression with a known cause.
   4. Also say whether the exit crash that follows is the T0W-2 pattern.
3. Cards inventory:
   1. List every test or tool that compares against a stored image or number (the keep-list baselines, `tools/fixtures/*` image or number goldens, `compare` references).
   2. For each one whose view contains grass, run it once on the 4070.
   3. If it fails, prove the cause is the grass style only:
      - re-run with Blades forced through the test's existing preference path (`--prefs=` or its grass defaults install);
      - or show with `ffmpeg_tools.ps1 compare` that the heatmap differs only on grass pixels.
   4. Output a table: test, baseline path, status (passes / fails because of Cards only / fails for another reason), evidence path.
4. Write the results into this file under a "T0W-5 result" heading after the WO list. Include the table and any fix WOs, in this template's work order layout.

DO-NOT-CHANGE CONSTRAINTS
- Generation 1 worlds must stay identical: `world_generation_fingerprint_test` must pass unchanged.
- No production code changes. No golden or baseline re-capture in this WO (that is T0W-6).
- Never revert or edit another session's uncommitted files.

VALIDATION STEPS
1. Every `-Tests` run is preceded by `-DryRun`, which must show exactly the named tests.
2. Known-failures edits: the next run of the test shows `flaky-pass` or `known-fail`.
3. Keep only cited evidence (heatmaps, the table). Run `.\tools\prune_test_output.ps1 -Apply`.

ESCALATION CONDITIONS
A baseline differs for a reason other than grass style (a real regression on generation 1). Stop and report: per plan 2.2, an existing golden must not be re-captured to hide a change.

### T0W-6. Re-baseline the captures that changed only because of Cards grass

ROLE: sol_implementer
SIZE: M
DEPENDS ON: T0W-5 (its table), the primary agent's approval of the exact list

OBJECTIVE
Every baseline in T0W-5's "fails because of Cards only" rows is re-captured on the 4070 with the Cards default, and the matching tests pass again.

CONTEXT / ROOT CAUSE
The Cards default is the user's explicit global exception to "new worlds only" (plan 13.1, question 7; handoff). Baselines captured with Blades in view encode the old default. This is the only WO in either phase that may re-capture an existing golden, and only for the approved rows.

FILES/SYMBOLS
The baselines and tests named in the approved T0W-5 rows, re-captured through each test's own capture flag or tool.

REQUIRED CHANGES
1. For each approved row, re-capture through the test's documented capture path on the 4070 (`desktop_mid_high`). Never edit a golden by hand. Keep the old capture until the new one passes, then delete it, unless it is in the prune keep list. A keep-list baseline is replaced in place, and its old copy is moved to the scratchpad until the primary accepts the change.
2. A1000 goldens (if any row is machine-scoped to the A1000) are not touched here. List them for the next laptop session (T0W-3 style).
3. Add a one-line note per baseline to the doc that owns it (for example `docs/shadow-revamp-plan.md`): "re-captured 2026-11-xx for the Cards default".

DO-NOT-CHANGE CONSTRAINTS
- Generation 1 worlds must stay identical: `world_generation_fingerprint_test` must pass unchanged.
- Only the approved rows. No threshold changes. No production code.

VALIDATION STEPS
1. For each row: `.\tools\run_tests.ps1 -Label t0w_rebaseline -Tests <test> -DryRun`, then the run: `pass`.
2. Prove the new baseline can fail: re-run one test with Blades forced through its preference path. It must fail, the mirror image of T0W-5's proof.
3. Pictures for the user: one before/after pair (the largest change) with its heatmap, captioned with the test name.
4. Delete the raw frames and the intermediate files. Run `.\tools\prune_test_output.ps1 -Apply`.

ESCALATION CONDITIONS
A re-captured baseline still fails, or differs in non-grass pixels. Stop: the difference is not only the Cards default.

### T0W-7. Vastness V0-1: views, analytic distance, band and ladder tables

ROLE: sol_implementer
SIZE: M
DEPENDS ON: T0W-1 (the weather pin; V0 views must not contain storms)

OBJECTIVE
`vastness_geometry_test` (headless gate) and the `vastness_audit` GPU tool exist and produce the V0 view set's captures, distance-band tables, tier metric, ladder table and empty-band list. This is plan V0 outputs 1-3.

CONTEXT / ROOT CAUSE
`docs/landscape-audit-t0-spec.md` section 4, WO V0-1, is the full specification. Execute it as written, with these corrections and verified facts:
- Phase facts 16-17: the view count is about 91, not about 50.
- The scale-anchor scatter must be reproduced (there is no positions accessor).
- Call `pin_weather(game, v0_poses)` from T0W-1 right after boot.
- The user may remove the scale-anchor towers later (vastness plan section 11, user answer 2). Audit them as they are today.

FILES/SYMBOLS
As spec 4 V0-1 "FILES":
- `tools/VastnessGeometry.cs`
- `tools/VastnessGeometryTest.cs` and `tools/vastness_geometry_test.gd`
- `tools/VastnessLadder.cs`
- `tools/vastness_audit_common.gd`
- `tools/vastness_audit.gd`
- two manifest entries

REQUIRED CHANGES
1. Spec 4 V0-1 "REQUIRED BEHAVIOUR" items 1-5, unchanged. The wall geometry comes from `RingTerrainShadowGeometry` (`RayHitsStructuralWall`, line 41). No hard-coded 10 km.
2. The boot reuses `landscape_harness_common.gd` `boot()` and `landscape_baseline_common.gd` `setup()` (prefs, preset, world verification, clocks, weather pin). Do not copy them.
3. Wrappers never name a `.cs` file. C# is reached only through `vastness_audit_common.gd` (spec 1, item 9).
4. Output goes to `.godot/audit_baseline/<machine>/v0/views/<stamp>/`. Paste the three tables and the empty-band list for the 4070 into a new "Audit baseline (rtx4070)" section of `docs/vastness-plan.md`, marked non-authoritative.

DO-NOT-CHANGE CONSTRAINTS
- Generation 1 worlds must stay identical: `world_generation_fingerprint_test` must pass unchanged.
- Spec section 3's shared constraints apply: no edits under `scripts/`, `scenes/`, `materials/`, `resources/` or `addons/`; no edit to `tools/shadow_harness_common.gd`; no edit to the T0 fixture.

VALIDATION STEPS
1. Manifest entries (spec 4), inserted with a small targeted edit:
   - `"vastness_geometry_test": {"kind": "test", "mode": "headless", "tier": "gate", "tags": ["vastness"], "marker": "VASTNESS_GEOMETRY", "expected_s": 5, "timeout_s": 60}`
   - `"vastness_audit": {"kind": "tool", "mode": "gpu", "tier": "manual", "tags": ["vastness", "atmosphere-haze"], "marker": "VASTNESS_AUDIT", "expected_s": 1200, "timeout_s": 2400}`
   Then `pwsh -File tools/testing/validate_manifest.ps1`.
2. `.\tools\run_tests.ps1 -Label v0_geometry -Cache none -Tests vastness_geometry_test -DryRun`: exactly that test. Then the run.
3. Prove it can fail: flip the sign of `dv` in a scratch build. The straight-down case must fail. Then revert.
4. `.\tools\run_tests.ps1 -Label landscape_gpu -Tests vastness_audit -DryRun`, then the run (about 15 minutes). The tool's own 10 % depth agreement check (spec V0-1, item 5) must pass.
5. Pictures for the user:
   - `v0_centre_eye` along the ring at noon with its band map;
   - `v0_wall_lo_eye` toward the wall at `sun_8_set`;
   - `saved_18km` at `night_planet`;
   each captioned with the empty bands found.
6. Run `.\tools\prune_test_output.ps1 -Apply`.

ESCALATION CONDITIONS
The analytic distance disagrees with depth by more than 10 % on flat ground, a registry seam does not exist as spec 4 describes, or the weather pin finds no clear time for the `v0` poses.

### T0W-8. Vastness V0-2: fly-out and vanish distances

ROLE: sol_implementer
SIZE: M
DEPENDS ON: T0W-7

OBJECTIVE
`vastness_flyout` measures, per ladder object type, the distance at which the object stops being visible, against the theoretical 1.5 px distance, and validates T0W-7's detection heuristic.

CONTEXT / ROOT CAUSE
Spec section 4, WO V0-2, is the full specification. Execute it as written. Plan V0 "Fly-out".

FILES/SYMBOLS
`tools/vastness_flyout.gd` (reaches `VastnessLadder` only through `tools/vastness_audit_common.gd`); one manifest entry.

REQUIRED CHANGES
1. Spec 4 V0-2 "REQUIRED BEHAVIOUR" items 1-6, unchanged.
2. Boot as in T0W-7, including the weather pin.
3. The "wall cue line" object is the rim-wall course nearest the pose. If no such object exists in the registries, record the row as "absent" instead of inventing one.
4. Use ffmpeg only through `tools/media/ffmpeg_tools.ps1 sheet`. Copy the sheets into `.godot/audit_baseline/<machine>/v0/flyout/<stamp>/`, then delete `.godot/captures/vastness/v0/flyout/`.
5. Paste the `results.md` table into the vastness plan's audit section (from T0W-7).

DO-NOT-CHANGE CONSTRAINTS
- Generation 1 worlds must stay identical: `world_generation_fingerprint_test` must pass unchanged.
- The spec section 3 shared constraints, as in T0W-7.

VALIDATION STEPS
1. Manifest: `"vastness_flyout": {"kind": "tool", "mode": "gpu", "tier": "manual", "tags": ["vastness"], "marker": "VASTNESS_FLYOUT", "expected_s": 1200, "timeout_s": 2400}`. Then `validate_manifest.ps1`.
2. `.\tools\run_tests.ps1 -Label landscape_gpu -Tests vastness_flyout -DryRun`, then the run.
3. Sanity check that can fail: for the tree type, the measured vanish distance must lie within 0.3x-3x of the theoretical one. A ratio outside that range fails the row with the reason "vanish ratio out of range" rather than passing silently.
4. Pictures for the user: the tree and emitter-spire sheets, captioned with measured versus theoretical distance.
5. Delete the frames, then run `.\tools\prune_test_output.ps1 -Apply`.

ESCALATION CONDITIONS
- The hidden/visible A/B toggle cannot hide an object class without production changes.
- TAA never settles within 24 frames (the diff never falls below 3/255 even at 300 km).

## Phase acceptance

The primary agent checks:

1. **T0W-1.** New 4070 look references, all 18 views PASS with a `weather` block, and the coast shows no rain. Spec 8.6 is updated. The pictures have been sent.
2. **T0W-2.** The three tests exit 0, or each has a known-failures entry with evidence. Any production-code fix WO has been raised separately.
3. **T0W-3.** The A1000 `cost.md` and look references are pasted into spec 8.6, and the budget implications are noted against plan section 12. This may stay open while the laptop run is outstanding; T1 does not depend on it.
4. **T0W-4.** The user's option is recorded and executed, or the WO is marked "declined".
5. **T0W-5 and T0W-6.** Both failing gate tests have an outcome. The Cards inventory table exists, and every approved re-baseline passes.
6. **T0W-7 and T0W-8.** `vastness_geometry_test` passes in gate. The V0 tables and sheets are in `docs/vastness-plan.md`.
7. **Regression checks:**
   - `.\tools\run_tests.ps1 -Label t0w_final -Cache none -Tests landscape_world_test,world_generation_fingerprint_test,landscape_inland_water_audit_test,landscape_tree_pack_audit_test,vastness_geometry_test` all pass, with no golden changes.
   - `pwsh -File tools/testing/validate_manifest.ps1` passes.
   - The primary runs `-Suite gate` at the commit point.
8. **Cleanup.** `.\tools\prune_test_output.ps1 -Apply` has been run, and only the cited evidence remains under `.godot/audit_baseline/`.
