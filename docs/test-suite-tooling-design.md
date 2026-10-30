# Test-suite tooling design

**Status:** design for review (2026-10-29, `main` acd9b74). Nothing here is implemented yet; the work orders in section 9 build it.
**Draft manifest:** `docs/test-suite-tooling-manifest.draft.json` (generated; 450 entries, tag globs, smoke set, known failures).
**Measurements:** RTX 4070 desktop, warm profile, `.godot/testtooling_timing/*.tlog` (per-line wall-clock timestamps).

**User decisions (2026-10-29), building paused by the user until resumed:**
1. **Full pre-merge runs** are scheduled overnight on the RTX 4070 desktop, which is the reference machine for the full run and the known-failures list. This needs a Windows scheduled task; pick a time and confirm it with the user when it is built.
2. **Batch host (WO7/WO8):** deferred. Build WO1-WO6 and WO9, plus the GPU queue in `tools/` (WO3), and stop after "await settled".
3. **The 97 tests never in the gate list** stay opt-in (tier `extended`/`manual`). WO4 does not triage or promote them.

## 1. Summary

The approved goals are tag-based selection, batched GPU launches, uniform reporting, headless-first features, agent integration and cost controls. The measurements change the priority order of the GPU work:

- **Windowed Godot startup is cheap.** An empty windowed script reaches `_initialize` at 1.5-1.7 s and exits at 1.8-2.0 s (headless: 0.23 s and 0.36 s). The close-out's median GPU test took 10 s, and 37 of the 73 took 10 s or less. "About a minute each" is not the typical cost.
- **The GPU hour went to four things:**
  - a known hang: `sunshine_inside_cloud_seam_test` took 901 s, plus 421 s in a rerun;
  - three long tests: `rain_sound_field_behavior_test` 284 s, `quality_preset_contract_test` 256 s and `local_mist_diagnostic_test` 103 s;
  - about 30 min of reruns to re-triage pre-existing failures;
  - 16 GPU tests also run headless. They produced 16 false failures, including a 421 s timeout.
- **World tests are 65-85% setup.** In a test that loads the certified world, the assertions start after startup, a 10-20 s world load and a fixed 20 s settle timer. `cloud_layers_gpu_test` runs 36.8 s, its first assertion is at 31.1 s, and its body takes about 5 s. Inside one process, a state change re-settles in 0.8-2.2 s. Batching world tests in one process plus an "await settled" helper is therefore worth doing, but it is the third-largest saving, not the first.

**Expected effect** (details in section 10):

| | Today | Proposed |
|---|---|---|
| Typical single-file step (median, simulated over 367 source files from the last 45 commits) | hand-picked, or the full GPU gate (about 30 min) | 10 GPU tests, about 2.7 min, plus 25 headless tests, about 25 s wall |
| Feature-sized commit (median of 45 commits) | full gate | 17 GPU tests, about 4 min, plus about 1.5 min headless |
| Full pre-merge (headless + GPU, unattended) | about 80 min, including reruns and triage | about 35 min in phase 1, about 28 min once the world-test work lands; an agent reads only the deltas |

## 2. Current state inventory

### 2.1 Counts

| | Count | Notes |
|---|---|---|
| `tools/*.gd` | 471 | 449 `extends SceneTree`; 22 helpers (effects, shared harness code) |
| Scanner classification of the 449 | 389 tests (219 headless, 157 GPU, 13 GPU local-RD) + 60 tools (captures, benchmarks, probes) | `kind`/`mode` per entry in the draft manifest |
| `tools/*.ps1` phase runners | 22 | 4 are gate-relevant: boot check, continue transition, warm lifecycle, altitude LOD flight |
| Close-out headless list | 237 wrappers, sum 2005 s, 3-way parallel, about 5 min wall | `.godot/batch_close/headless/results.tsv` |
| Close-out GPU list | 73 tests, sum 2560 s (42.7 min) sequential | `gpu_run1/results.tsv` |
| Close-out GPU reruns | `gpu` (37 tests, about 18 min), `main_gpu2` (13 tests, 11 min), `head_gpu`, the `main_baseline_*` folders | triage of failures that already existed |
| Real tests in neither close-out list | 97 (93 GPU, 3 of them local-RD) | "extended" tier: water FFT visuals, height shadow, grass/tree GPU and others; status unknown |

### 2.2 Marker coverage (gate, smoke and slow tiers)

| Marker form | Tests | Example |
|---|---|---|
| canonical `NAME=PASS` / `NAME=FAIL <reason>` | most (about 325 of 388 test entries) | `CLOUD_LAYERS=PASS` |
| suffix `NAME_PASS` | 24 | `ANTI_ALIASING_SETTINGS_SMOKE_TEST_PASS` |
| free text or none | 33 | `Sunshine entry-skip GPU fixture: PASS (4096 cases)`, `Cloud shadow crossfade test passed`, `UI scaling smoke test passed at all window sizes.` |
| capture only, no assertion | `sunshine_arc_smoke_test`, `sunshine_water_order_test` | print `... complete: <png>` |

The draft manifest lists the 57 non-canonical tests under `marker_fix`.

**The close-out runner has two bugs** (scratchpad `run_batch.ps1`):
- `Start-Process -NoNewWindow -Redirect*` returned a null `ExitCode` on the headless pass, so all 193 headless passes were reported as `PASS_NONZERO_EXIT`.
- Its verdict regex accepts any `\bPASS\b` line. A test that prints sub-check `PASS` lines and then crashes counts as a pass.

### 2.3 Slowest 15 (close-out, wall seconds)

| # | Test | Mode run | s | Verdict | Note |
|---|---|---|---|---|---|
| 1 | sunshine_inside_cloud_seam_test | GPU | 901 | TIMEOUT | known hang (stale node path) |
| 2 | sunshine_water_order_test | headless | 421 | TIMEOUT | a GPU capture run in the wrong mode |
| 3 | rain_sound_field_behavior_test | GPU | 284 | PASS | slow tier |
| 4 | quality_preset_contract_test | GPU | 256 | FAIL | flake (C6 p=0); reloads the world per preset |
| 5 | landform_terrain_v7_test | headless | 224 | PASS | headless critical path |
| 6 | grass_streaming_hysteresis_test | headless | 108 | PASS | |
| 7 | local_mist_diagnostic_test | GPU | 103 | PASS | |
| 8 | terrain3d_gameplay_movement_test | GPU | 68 | FAIL | known (21.5 s vs 10 s) |
| 9 | eclipse_darkness_parity_test | headless | 64 | PASS | |
| 10 | grass_card_placement_test | headless | 59 | PASS | |
| 11 | water_rendering_smoke_test | GPU | 58 | PASS | fixed 20 s wait between bindings |
| 12 | rain_sound_field_continuity_test | headless | 56 | PASS | |
| 13 | grass_card_style_test | headless | 56 | PASS | |
| 14 | cloud_shadow_crossfade_test | GPU | 54 | no marker | |
| 15 | cloud_layers_gpu_test | GPU | 46 | PASS | 31 s of setup |

## 3. Measured timing breakdown

All measurements are on the RTX 4070 with the warm copied profile `appdata_testtooling`. Times are wall seconds from process start, taken from stdout timestamps by a .NET event reader (prototype: scratchpad `tt/run_timed.ps1`).

| Run | Vulkan device | Script start | Test-specific milestones | Verdict | Exit |
|---|---|---|---|---|---|
| empty script, headless | n/a | 0.23 | | 0.23 | 0.36 |
| empty script, windowed (x2) | 0.24-0.28 | 1.51-1.73 | | | 1.76-2.01 |
| `atmosphere_settings_smoke_test` (headless) | | | | 0.33 | 0.46 |
| `sunshine_entry_skip_gpu_test` (local RD) | 0.25 | | | about 1.9 | 2.01 |
| `cloud_density_fastpath_gpu_test` (local RD) | 0.25 | | | about 2.1 | 2.25 |
| `sunshine_shader_compile_gpu_test` (local RD) | 0.24 | | 10 compiles 4.05-4.65 | | 4.93 |
| 4 local-RD fixtures **in parallel** | | | all PASS | | **5.8 wall** (11.6 sequential) |
| world probe (certified world, `cloud_layers` load path; x2) | 0.24 | 1.68 / 1.74 | instantiate 3.1 / 1.1 s, Prepare 0.2 s, `add_child`/_Ready 4.4 s, first frame 2.2 s, long frames (>100 ms) end at **19.4 / 11.4 s** | | quit 1.2 / 0.8 s |
| `cloud_layers_gpu_test` | 0.25 | | first assertion 31.1; scenario re-settles 0.8, 1.0, 2.2 s | 35.96 | 36.81 |
| `far_cloud_detail_gpu_test` | 0.30 | | deltas at 14.1 | 14.56 | 15.95 |
| `water_rendering_smoke_test` | 0.25 | | 1st FFT binding 26.4, 2nd 46.7 (fixed wait) | 54.34 | 55.5 |
| `cloud_haze_gameplay_test` | 0.24 | | | 16.81 | 17.76 |
| `gameplay_cloud_tuning_smoke_test` | 0.26 | | | 12.05 | 12.86 |
| `rain_streaks_gpu_test` (960x540, x2) | 0.25 | | sub-checks from 9.5 | 18.4 / 18.2 | 19.7 (**FAIL** `paused_clock_identical` mean delta 0.0014) / 19.1 PASS |

Where the time goes in a world test:
- engine + .NET + autoloads: about 1.7 s;
- world instantiate + _Ready + first frame: 7.8-9.8 s;
- streaming settle: done at 11-19 s from start;
- **test-owned fixed timers**: 20 s in `cloud_layers*`, `storm_profile_map`, `water_rendering`, `create_timer(90/120/240)` watchdogs (harmless) and settle sleeps;
- body: 2-8 s;
- quit: 0.8-1.2 s.

Nothing in startup is worth batching for local-RD fixtures or headless tests. They need parallelism instead.

## 4. Selection by tags

### 4.1 Where tags live: one manifest

`tools/test_manifest.json`, one entry per runnable test, promoted from the draft. Reasons:
- **One reviewable diff.** Retagging 300 headers is noisy and needs a parser for every header style. Header comments are free text today (see the marker zoo above).
- **Data that is not the test's business.** Expected duration, timeout, tier, batch group, the profile needed and known-failure status are suite policy. Tests should not carry them.
- **Validation.** A validator (WO1) checks that every `tools/*.gd` SceneTree script has an entry, that tags exist, globs compile and smoke tests exist. A new test that is not registered fails the smoke suite, so drift is caught.
- **Cross-runtime.** Codex and Claude read the same JSON. PowerShell's `ConvertFrom-Json` reads it without dependencies.
- **Known failures** live in a sibling file, `tools/test_known_failures.json`. It changes on a different cadence and carries evidence text.

The cost is that a test author must also edit the manifest. The test-scaffold skill and the validator make that one step.

### 4.2 Entry schema

```json
"cloud_layers_gpu_test": {
  "kind": "test",                 // test | tool (captures, benchmarks, probes: never auto-selected)
  "mode": "gpu",                  // headless | gpu | gpu-local (local RenderingDevice fixture, no world, parallel-safe)
  "tier": "gate",                 // smoke | gate | slow | extended | manual
  "tags": ["clouds", "sunshine-compute", "weather"],
  "world": "certified",           // certified | integration | start-menu | biome-preview | scene | (absent: none)
  "batch": "certified@1280x720",  // batch group, or null with batch_note saying why it must own its process
  "marker": "CLOUD_LAYERS",       // the runner requires the line ^CLOUD_LAYERS=PASS
  "args": "--resolution 1280x720",// engine args; "user_args" goes after "--"
  "expected_s": 46, "timeout_s": 140,
  "profile": "shared",            // shared (default) | fresh (save-slot-only profile, recreated) | own-runner
  "runner": null,                 // tools/*.ps1 when the test has its own phase runner
  "parallel_safe": true
}
```

Tiers:
- `smoke` always runs.
- `gate` is selected by tags.
- `slow` (over 120 s) runs pre-merge, or when the test file itself or its first tag is touched.
- `extended` holds real tests not in the curated gate. They run only with `-Suite extended` or when their own files change, until triaged (open question 3).
- `manual` holds captures and benchmarks.

### 4.3 Tag taxonomy and source globs

Globs are case-sensitive and `**` spans directories. The repository uses descriptive PascalCase and snake_case names, so `*Rain*` matches `RingRainHaze.cs` but not `Terrain`. A file may map to several tags. When in doubt the globs overlap, which favours recall over precision. The full glob lists are in the draft manifest under `tags`.

| Tag | Main globs (abridged) | Tests (gate GPU) |
|---|---|---|
| clouds | `scripts/clouds/**`, `scripts/**/*Cloud*`, `materials/*cloud*`, `addons/SunshineClouds2/**`, `addons/clouds_lp/**` | 52 (35) |
| sunshine-compute | `addons/SunshineClouds2/**`, `scripts/RingSunshineCloudBridge*`, `materials/ring_terrain_cloud_sun*` | 16 (13) |
| cloud-history | `**/*History*`, Sunshine post/interleave passes | 9 (5) |
| far-viewport | `scripts/Voxel/LOD/RingScaledFarViewport*`, `**/*FarRing*`, `**/*Far[A-Z]*`, `materials/ring_far_*` | 10 (3) |
| terrain | `scripts/Terrain/**`, LOD/proxy/surface files in `scripts/Voxel/LOD`, `addons/terrain_3d/**`, `materials/ring_terrain*`, `native/**` | 90 (8) |
| voxel-caves | `scripts/Voxel/Core|Phase*|Save/**`, `scripts/Underground/**`, `scripts/Terrain/Editable/**` | 5 |
| weather | `scripts/Weather/**`, `scripts/RingVolumetricCloudLayer*`, `**/*Storm*`, `**/*Climate*` | 30 (7) |
| rain | `**/*Rain*`, `**/*Precip*`, `scripts/RingLocalWeatherEffects*`, `materials/ring_rain*` | 17 (8) |
| wet-world | `**/*Wet*`, `materials/*wet*`, `materials/ring_surface*` | 5 (1) |
| lightning | `scripts/Lightning/**`, `**/*Lightning*`, `**/*Thunder*`, `**/*Sprite*` | 14 (1) |
| audio | `**/*Audio*`, `**/*Sound*`, `**/*Ambience*`, `resources/audio/**`, `default_bus_layout.tres` | 10 (1) |
| ui-menu | `scripts/ui/*Menu*|*Drawer*|*Hud*|*Popup*|*.gd`, `scenes/StartMenu.tscn`, `materials/menu_*`, `resources/menu/**` | 16 (4) |
| settings | `scripts/ui/*Defaults.cs`, `*Settings.cs`, `*Drawer*`, `*Tuning*` | 28 (2) |
| poi | `scripts/poi/**`, `scenes/poi/**`, `assets/poi/**`, `RingPoiHaze*`, `materials/ring_poi*|ring_emitter*|ring_eclipse_hub*` | 26 (1) |
| vegetation | `scripts/Vegetation/**`, `**/*Grass*`, `**/*Tree*`, `**/*BiomeDecoration*`, `materials/*grass*|*tree*|vegetation*`, `resources/grass|trees/**` | 77 (8) |
| water | `**/*Water*`, `**/*Ocean*`, `**/*Fft*`, `**/*Shore*`, `materials/ring_water*|ring_ocean*` | 27 (4) |
| atmosphere-haze | `scripts/Atmosphere/**`, `**/*Atmosphere*`, `**/*Haze*`, `materials/atmosphere/**` | 18 (8) |
| shadows | `scripts/Shadows/**`, `**/*Shadow*`, `materials/shadows/**` | 32 (8) |
| celestial | `scripts/Celestial/**`, `*DayNight*`, `ring_day_night_cycle.gd`, `materials/celestial*|ring_eclipse*` | 26 (5) |
| wind | `**/*Wind*`, `**/*Gust*`, `materials/ring_wind*|ring_gust*` | 10 (1) |
| loading | `scripts/Loading/**`, `world_session.gd`, `scripts/Voxel/Save/**`, `**/*Warm*`, `**/*Startup*` | 10 (+ 2 ps1 runners) |
| quality-presets | `**/RingGraphicsQualityController*`, `**/*Quality*`, `scripts/ui/*Preset*` | 5 (1) |
| render-core | `scripts/render/**`, `scripts/Rendering/**`, `**/*AntiAliasing*|*Resolution*|*Fsr*|*Compositor*` | 8 |
| perf-telemetry | `scripts/diagnostics/**`, `**/*Performance*` | 10 (1) |
| tooling-policy | `tools/*.ps1`, `RingGeneratedOutputPaths*` | 1 |

Special globs:
- **core**: `project.godot`, `*.csproj`, `*.sln`, `scenes/VoxelRingIntegrationTest.tscn`, `scenes/Main.tscn`, `scenes/RingWorld.tscn`, `scripts/Voxel/Integration/**`, `RingVoxelExplorerController.cs`, the engine folder. A core change selects the **full headless suite** (about 4 min) plus the smoke set and every `gate` GPU test in the `integration@*` and `certified@*` batch groups.
- **ignore**: `docs/**`, `**/*.md`, `.claude/**`, `.codex/**`, `captures/**`, `prototypes/**`, `*.uid`, `*.import`.
- **tools files**: a changed `tools/<name>.gd` selects that test. A changed `tools/<Helper>.cs` selects every wrapper that `preload`s or `load`s it (resolved by the runner from the wrapper source).
- **Unmapped file** (no tag, not core, not ignored): the run selects the full headless suite plus the GPU smoke set, and `results.md` lists the file so the manifest gets a glob. In the 45-commit simulation 2% of source files were unmapped after one refinement pass.

**Initial assignment.** All 450 entries are in the draft manifest:
- Tags are derived from the test name, which gives 1-3 precise tags. The header-derived extras are in `suggest_tags` for the WO1 reviewer (for example, `cloud_layers_gpu_test` gets `clouds` and suggests `sunshine-compute, weather`).
- Over-tagging only costs time; under-tagging misses regressions. The reviewer adds a suggested tag when the test would catch a regression in that system.

### 4.4 Smoke set (always runs, about 25 s plus the C# build)

- Build: `tools/use_godot_48.ps1 -Build` (the runner skips it with `-NoBuild` when no `.cs` file changed).
- Headless (all under 3 s, all passed at close-out):
  - `quality_preset_ownership_test`
  - `visual_settings_ui_smoke_test`
  - `world_loading_startup_contract`
  - `cloud_layer_column_test`
  - `performance_diagnostics_contract_test`
  - `terrain_generation_shared_math_test`
  - `variant_equality_test`
  - `storm_lifecycle_code_test`
  - the manifest validator
- GPU:
  - `sunshine_shader_compile_gpu_test` (5 s; catches GLSL compile errors and stale imports, see the batch-close testing note);
  - `gameplay_cloud_tuning_smoke_test` (13 s; boots the integration world with clouds, wind and tuning).

### 4.5 Selection algorithm

1. Get the changed files:
   - default: `git diff --name-only <base>...HEAD` plus `git diff --name-only` (unstaged) plus `git ls-files --others --exclude-standard` (new);
   - `-Base` defaults to `main`;
   - `-Since <rev>` runs the files changed since the last green run of this label (stored in `.godot/test_runs/<label>/last_green.json`).
2. Map each file to its tags, core, ignore, a direct test, a helper's dependants, or unmapped.
3. Select:
   - smoke;
   - every `gate` test with a tag in the set;
   - every direct test;
   - `slow` tests only when direct, when their first tag is in the set, or with `-Suite full`;
   - known-fail tests only with `-Suite full`, or when direct or when their first tag is in the set, so a fix gets noticed.
4. `-DryRun` prints each selected test with its reason (`tag clouds <- scripts/clouds/RingCloudLayers.cs`) and an estimate built from `expected_s`.

## 5. Batched GPU runner

### 5.1 Three layers, in order of payoff

1. **Scheduling with no test changes.**
   - Headless tests run 6 at a time, longest first; the critical path is `landform_terrain_v7_test` at 224 s.
   - `gpu-local` fixtures run 3 at a time (measured: 4 at once, 5.8 s wall against 11.6 s sequential, all PASS).
   - World GPU tests run one at a time.
   - Each test has its own manifest timeout instead of 900 s.
   - Quarantined tests are skipped.
   - No GPU test runs headless.
2. **Await settled instead of fixed timers** (WO6). Explicit fixed settles in the wrappers are rare: `create_timer(20.0)` in the three `cloud_layers`/`storm_profile_map` tests and `create_timer(30)` in `shared_wind_integration_test`. The other world tests wait in C# helpers or by frame counts, and WO6 audits those case by case. A shared helper, `RingTestWorld.AwaitSettled(world, maxSeconds)`, returns when:
   - 30 consecutive frames take under 50 ms;
   - the terrain streaming queue is idle;
   - the Sunshine macro map has published at least two builds.

   It replaces the fixed `create_timer(20.0)` settles. Measured settle point: 11-19 s, against the 30 s that the fixed timers reach.
3. **One process, several cases** (WO7 pilot, WO8 rollout). A batch host loads a world once and runs compatible cases in sequence.

### 5.2 Batch host design

- **Files.**
  - `tools/testing/batch_host.gd` (SceneTree).
  - `tools/testing/RingTestWorld.cs`: world bootstrap shared with the standalone wrappers; the `FarRingGameplayCapture.Prepare` path or the integration path; state snapshot, reset and fingerprint.
  - `tools/cases/<test>_case.gd`: a `RefCounted` with `func run(host) -> void` that awaits frames and calls `host.check(name, ok, detail)` / `host.pass_case()` / `host.fail_case(reason)`.
  - Each existing `tools/<test>.gd` becomes a thin wrapper: boot the world through `RingTestWorld`, run the single case, print the marker, quit. Standalone and batched runs execute the same code, and the canonical marker does not change.
- **Protocol on stdout:**
  - `BATCH_HOST_READY world=<group> t=<s>`
  - `CASE_BEGIN <name>`
  - the case's own lines and `<MARKER>=PASS|FAIL|SKIP`
  - `CASE_END <name> <pass|fail|skip> <secs>`
  - `BATCH_HOST_DONE`

  The outer runner reads stdout live (the `TimedRun` pattern from the prototype) and keeps a per-case timer.
- **Groups.** The world kind plus resolution make the group key (`certified@1280x720`, `certified@default`, `integration@default`, ...). The window size is fixed per launch, because tests assert pixel counts at a set resolution. In the draft: `certified@1280x720` has 3 tests, `certified@default` 7, `integration@default` 12, and `integration@1280x720` and `integration@960x540` 1 each (run standalone until they have company).

### 5.3 Isolation between cases

Before each case the host runs `RingTestWorld.Reset()`, then checks a fingerprint against the post-boot baseline:

| Area | Reset action | Fingerprint item |
|---|---|---|
| Settings | Re-apply the isolated preference snapshot that `Prepare` wrote (every `Gameplay*Defaults` resource), through the same apply path as the live menu; restore `ProjectSettings` keys that the case declared it changes | hash of the applied settings resources |
| Weather | Clear manual systems (`ClearWeather`), rain off, lightning queue and history cleared, storm light reset, natural-lifecycle clock back to the boot epoch with a fixed seed | manual system count, weather epoch, rain intensity |
| Time | Day-night clock to the boot time; paused or running as the case declares | time of day, pause flags |
| Camera | Explorer to `SetCanonicalSpawnMeters(...)` and its rotation; far viewport resynced; wait for the macro map to republish (2 builds) | camera canonical position, far anchor |
| Debug | `CameraIndependentDebugMask = 0`, debug views off, compositor effect list equal to the snapshot | compositor chain signature |
| Scene tree | Nodes the case added under `root` are freed; leftovers are logged as `CASE_LEAK` | root child set |
| RNG | `seed(hash(case name))` before the case | n/a |

If the fingerprint mismatches after a reset, the host stops. The remaining cases go to a fresh host process, the case that ran last is marked `isolation-suspect`, and the runner reruns it standalone. A case that fails batched but passes standalone is reported as `fail-batch-only`: an isolation bug in the case or in the reset, never silently counted as a pass. Pre-merge runs use `-Shuffle <seed>` to expose order dependence.

**Which tests cannot share a launch** (manifest `batch: null` + `batch_note`):
- Tests of loading, boot or quit: `world_loading_*`, `world_save_exit_*`, `menu_*` runners, warm-stall probes, startup-flash checks, quit-crash checks.
- Tests that reload or rebuild the world: `quality_preset_contract_test` (one world per preset), `terrain_cloud_sun_visibility_state_test`.
- Tests that mutate the world in ways reset cannot undo: `rain_sound_field_behavior_test` (geometry edits), voxel or terrain edits, `terrain3d_gameplay_movement_test` (long traversal, streaming state).
- Tests that need different engine arguments, audio driver, renderer or profile (`fresh` profile, `--audio-driver Dummy`, a different `--resolution` with no group partner), or a different world scene (StartMenu, BiomeAuthoringPreview).
- Tests whose subject is cold-start behaviour: first build, empty history (`menu_cloud_history_gpu_test`, entry and first-frame checks).
- Tests that drive real input or the UI tree (`grave_menu_interaction_test`), and tests that advance the weather clock by epochs (`weather_lifecycle_visual_smoke_test`) until their reset is proven.
- Quarantined tests, perf captures, and anything in the `manual` tier.
- `gpu-local` fixtures need no world, so they never batch. They run in parallel instead.

### 5.4 Crash and timeout containment, resume

- **Per-case timeout** comes from the manifest. If `CASE_END` does not arrive in time, the runner kills the host's process tree (`taskkill /T /F`, only the PID it started), marks the case `timeout`, and relaunches a host with the cases after it.
- **Crash:** the process exits with a `CASE_BEGIN` that has no `CASE_END`. The case is marked `crash` and the runner relaunches with the rest.
- **Relaunch limit:** 2 relaunches per group. After that, the remaining cases run standalone, so one bad case costs at most one world load per incident.
- **Quit crashes** (the known 4.8-dev7 `0xC0000005` on quit) after `BATCH_HOST_DONE` or after a standalone PASS marker do not change verdicts. They are reported as `exit_anomaly: true`.
- **Results are written after every test** (`results.json` is rewritten atomically). `-Resume <run dir>` reruns only the tests that are `not-run`, `timeout`, `crash` or `error`; `-RetryFailed` adds `fail`. Reruns go to the same run directory, with `attempts` incremented.

### 5.5 PASS/FAIL collection

The verdict comes from the canonical marker the manifest names. Exit codes are read with `System.Diagnostics.Process` (the prototype gets them right where `Start-Process -NoNewWindow` did not).

| Observed | Status |
|---|---|
| `^<MARKER>=FAIL` (stdout or stderr, `ERROR:` prefix allowed) | `fail`, or `known-fail` if it matches the known-failures `match` regex |
| `^<MARKER>=PASS`, exit 0 | `pass` (`fixed` if the test is listed as known-fail) |
| `^<MARKER>=PASS`, exit nonzero or crash after the marker | `pass` + `exit_anomaly` |
| `^<MARKER>=SKIP` | `skip` (never counted as a pass) |
| no marker, exit 0 | `error: no-marker` |
| no marker, nonzero exit | `crash` (0xC0000005 and similar) or `fail` |
| timer expired | `timeout` |
| listed as `quarantined` | `quarantined` (not launched; with `-Suite full` it is launched with the quarantine timeout to detect a fix) |
| listed as `flaky` and failed | retried `retries` times; a later pass gives `flaky-pass` |

### 5.6 Estimated saving of the batch layers (full GPU gate)

The full gate in the draft has 68 world/GPU entries plus 10 `gpu-local` and 2 phase runners. The close-out sum, excluding the hang, is 1768 s (29.5 min), of which the 2 slow tests take 540 s.

| Step | Saves | Basis |
|---|---|---|
| Hang quarantined (timeout 60 s with `-Suite full`) | 14-15 min against the close-out | 901 s measured |
| GPU tests no longer run headless | 7 min of headless lane + 16 false failures | 421 s timeout + 15 failures |
| `gpu-local` 3-way parallel | about 0.5 min | 10 fixtures, 50 s → about 20 s |
| Await settled | about 1-2 min | 4 tests with explicit 20-30 s timers save about 10 s each (settle measured at 11-19 s); the helper-side waits of the other world tests are unaudited, so no saving is counted for them |
| Batch host for 22 tests in 3 groups | about 4 min | per case: startup 1.7 + world 8-10 + quit 1 s, minus about 2 s reset ≈ 10 s; 3 world loads remain |
| No triage reruns of known failures | about 30 min against the close-out | the `gpu`, `main_gpu2`, `head_gpu` and `main_baseline*` folders |

The full GPU phase goes from about 29.5 min (phase 1, scheduling only) to about 23-24 min (after WO6-WO8).

## 6. Reporting

Each run writes `.godot/test_runs/<label>/<yyyyMMdd-HHmmss>/` with `results.json`, `results.md` and `logs/<test>.log` (stdout and stderr merged, timestamped).

`results.json`:

```json
{
  "run_id": "20261029-221500", "label": "megastorm", "suite": "changed", "base": "main", "head": "acd9b74", "dirty": true,
  "machine": { "gpu": "NVIDIA GeForce RTX 4070", "hardware_profile": "desktop_mid_high" },
  "selection": { "changed_files": 4, "tags": ["clouds", "weather"], "unmapped": [], "core": false },
  "wall_s": 212.4,
  "summary": { "pass": 31, "fail": 1, "known_fail": 2, "fixed": 0, "flaky_pass": 0, "skip": 0, "timeout": 0, "crash": 0, "quarantined": 1, "error": 0, "not_run": 0 },
  "tests": [
    { "name": "cloud_layers_gpu_test", "mode": "gpu", "batch": "certified@1280x720", "status": "pass",
      "marker": "CLOUD_LAYERS=PASS", "exit_code": 0, "exit_anomaly": false, "duration_s": 8.1, "attempts": 1,
      "reason": "tag clouds <- scripts/clouds/RingCloudLayers.cs", "detail": "", "log": "logs/cloud_layers_gpu_test.log" }
  ]
}
```

`results.md` is short so an agent can read it with few tokens:

```
# Test run megastorm 20261029-221500  (changed vs main, RTX 4070)
35 selected: 31 pass, 1 FAIL, 2 known-fail, 1 quarantined. Wall 3m32s. Exit 1.

| Status | Test | s | First failure line | Log |
|---|---|---|---|---|
| FAIL | storm_profile_map_gpu_test | 9.4 | STORM_PROFILE_MAP=FAIL deck top 18400 > 18000 | logs/storm_profile_map_gpu_test.log |
| known-fail | rain_cloud_compositing_gpu_test | 16.2 | (matches known: frozen-scene cloud source drift) | logs/... |
| known-fail | distant_rain_curtains_gpu_test | 17.0 | (matches known) | logs/... |
| quarantined | sunshine_inside_cloud_seam_test | - | stale FarRingBackdrop path; hangs | - |
Passes: 31 (list with -Verbose). Unmapped files: none.
```

The rows that need attention come first. `fixed` gets its own banner line ("known failure now passes: update tools/test_known_failures.json"). Runner exit code: 0 when there is no `fail`, `timeout`, `crash`, `error` or `fail-batch-only`; 1 otherwise; 2 when the runner itself fails.

### 6.1 Known-failures list (draft, `known_failures` in the draft manifest; becomes `tools/test_known_failures.json`)

Each entry has `status`, a `match` regex that a failure must match to count as known (a different failure on a known-failing test is a new `fail`), `evidence`, and `since`. Entries are per machine where it matters: an optional `"machines": ["desktop_mid_high"]`.

| Test | Status | Evidence (abridged) |
|---|---|---|
| generated_output_policy_test | known-fail | 30 `res://artifacts` literals in untouched files; same on main 3110b7e |
| grass_clump_validation_test | known-fail | near clump triangle contract; main_baseline_clean |
| grass_streaming_contract_smoke | known-fail | dense buffered root height diverged; main |
| grass_quality_gameplay_test | known-fail | grass did not settle; main |
| distant_rain_curtains_smoke_test | known-fail | time discontinuity never repopulated rain; main |
| legacy_terrain_isolation_test | known-fail | GDScript parse error; main |
| rain_surface_column_smoke_test | known-fail | edit rejected: Concrete; main |
| biome_authoring_preview_test | known-fail | preset name editor missing/focus; main (headless and GPU) |
| world_loading_smoke_test | known-fail | `activation` diagnostics entry (425871c) has no `ready` key; main |
| terrain3d_gameplay_movement_test | known-fail | reverse after handoff 21.5 s vs 10 s |
| smooth_terrain_pilot_smoke_test | known-fail | skill list; pilot did not settle |
| rain_cloud_compositing_gpu_test | known-fail | frozen-scene cloud source drift on main and e2485b7 |
| distant_rain_curtains_gpu_test | known-fail | no stable rain contribution; main |
| water_fft_demand_integration_test | known-fail | main |
| cloud_lighting_smoke_test | known-fail | main |
| sunshine_gpu_shadow_smoke_test | known-fail | main |
| sunshine_storm_persistence_test | known-fail | main |
| sunshine_menu_smoke_test | known-fail | main; skill list |
| opposite_ring_lightning_visual_smoke_test | known-fail | localized-contrast check fails on e2485b7 |
| sunshine_inside_cloud_seam_test | quarantined | stale `FarRingBackdrop/FarRingTexture` path; hangs |
| quality_preset_contract_test | flaky (1 retry) | C6 p=0 once; 150/150 on reruns |
| rain_streaks_gpu_test | flaky (1 retry) | **new 2026-10-29**: `paused_clock_identical` failed 1 of 2 runs (mean delta 0.0014) |
| sunshine_shadow_slice_equivalence_test | unverified | 0xC0000005 before any verdict at close-out; not in the batch-close record |
| sunshine_night_shell_exit_smoke_test | unverified | same |

`unverified` entries run normally and report `fail`. WO4 runs them once on `main` to decide between `known-fail` and a real defect. The close-out also showed these, which the manifest fixes rather than lists:
- `smooth_terrain_overlap_ownership_test` passes solo (`smooth_overlap.log`), so it gets `parallel_safe: false`.
- `world_save_exit_lifecycle_test` passes with a fresh profile (`save_exit_fresh.log`), so it gets `profile: fresh`.
- `showcase_world_definition_test` refuses a non-showcase profile, so it gets `runner: tools/showcase_world_definition_test.ps1`.
- `terrain_generation_performance_test` needs `--baseline` or `--incremental`, so it moves to the `manual` tier.

### 6.2 Tests without a canonical PASS marker

WO5 normalises 57 gate-tier tests to `^<NAME>=PASS` / `=FAIL <reason>` / `=SKIP <reason>`. It keeps every existing check and adds or renames only the final line (suffix forms `X_PASS` become `X=PASS`). Named cases:
- `sunshine_shader_compile_gpu_test`: `SUNSHINE_SHADER_COMPILE=PASS files=10` after the loop; failures already go through `_fail`.
- `cloud_shadow_crossfade_test`: `CLOUD_SHADOW_CROSSFADE=PASS` where it prints "Cloud shadow crossfade test passed".
- `ui_scaling_smoke_test`: `UI_SCALING_SMOKE=PASS sizes=<n>`.
- Capture-only tools (`sunshine_arc_smoke_test`, `sunshine_water_order_test` and the `*_capture*` family): `kind: tool`, `tier: manual`, and a final `<NAME>=CAPTURED <path>`. The runner reports them as `captured`, never as `pass`.

The rest of the list is `marker_fix` in the draft manifest.

## 7. Headless first: guidance for new features (megastorm, containment field)

Each windowed world check costs about 10-15 s even after WO6-WO8 and needs the GPU queue. A headless check costs under 1 s and runs 6 at a time. Design for that from the first commit.

Checklist for a new feature (copied into the test-scaffold skill):

1. **Split model from presentation.**
   - Lifecycle, timing, geometry, budgets and policy go in plain C# types with no `Node`, `RenderingServer` or `Viewport` dependency. Test them headless with direct calls (pattern: `storm_lifecycle_code_test`, `storm_arrival_math_test`, `precipitation_altitude_test`).
   - The megastorm's lifecycle phases, footprint, wall radius vs time, wind field and lightning rate belong here.
   - So do the containment field's boundary math, field strength vs distance, the inside/outside query, the transition rules and its interaction with weather (does rain stop at the boundary).
2. **Settings.** Every tunable gets a headless settings smoke from the `live-tuning-setting` skill (defaults, sanitize, persistence, reset).
3. **Data contracts headless.** Snapshots, uniform-buffer layouts, map texel packing and CPU references of GPU maps get headless contract tests with goldens in `tools/fixtures`. Pattern: `cloud_history_frame_contract_test`, `wet_map_frame_test`.
4. **Shader logic as a `gpu-local` fixture, not a world test.** Compile the shader and compare it with a CPU reference on a local `RenderingDevice` (pattern: `cloud_density_fastpath_gpu_test`, `far_cloud_density_parity_gpu_test`, 2-3 s, parallel-safe). Register the shader in `sunshine_shader_compile_gpu_test`'s list when it is an RD shader.
5. **One world integration case per feature**, written as a batch case (`tools/cases/<name>_case.gd`) in an existing group (`certified@default`). It proves the feature appears, composites in the right order, stays within budget, and gets haze, shadows and lighting like every other surface. No fixed timers: use `AwaitSettled` and explicit build counters.
6. **Visual review.** Captures and ffmpeg sheets go in the `manual` tier (`<NAME>=CAPTURED`). They are evidence for review, not gates.
7. **Register in the manifest** with tags, mode, tier, marker, expected seconds and batch group.
   - Add new tag globs when the feature brings new folders, for example `containment-field: ["scripts/Containment/**", "materials/*containment*"]`.
   - The megastorm most likely reuses `weather`, `clouds`, `lightning` and `rain`; add a `megastorm` tag only if it gets its own folder.
8. **Determinism.** Fixed seeds, paused clocks where possible, and no reliance on wall-clock frame rate.
9. **Budgets.** Headless test 10 s or less; `gpu-local` 5 s or less; world case body 15 s or less. Anything slower is tier `slow` and needs a reason in the manifest.

## 8. Agent integration and cost controls

### 8.1 What agents are told

- **AGENTS.md**, a new short "Tests" section under Tools:
  - Run tests through `tools/run_tests.ps1`.
  - During a step: `-Changed` (default). At a batch or commit point: `-Suite gate`. Before a merge: `-Suite full`, unattended.
  - Read `results.md`, not the logs. Open a log only for `fail`, `timeout`, `crash` or `error`, with the log-triage skill.
  - Never re-triage `known-fail`. Report `fixed` so the list gets updated.
  - New tests are registered in `tools/test_manifest.json` (the validator enforces it).
  - The GPU queue is `tools/gpu_queue.ps1`; the runner takes it itself.
- **`godot-test-run` skill:** sections 3-5 are rewritten around `run_tests.ps1` (examples: `-Changed`, `-Tests a,b`, `-Tags clouds`, `-DryRun`, `-Resume`, `-Label`). The raw invocation stays as a fallback for one-off debugging. The stale known-failures list is replaced by a pointer to the JSON, and the `gpu.lock` text by the queue.
- **`test-scaffold` skill:** the canonical marker is mandatory; add the manifest entry; the section 7 checklist; the batch-case template.
- **`log-triage` skill:** start from `results.json` to choose which logs to read.
- **Agent definitions:** one identical bullet in `.claude/agents/{implementer,claude-lead,claude-deep}.md` and `.codex/agents/{sol-implementer,sol-lead,sol-deep}.toml`, kept in sync per AGENTS.md: "Run tests with `tools/run_tests.ps1` (godot-test-run skill): `-Changed -Label <task>` during a step; report the `results.md` summary line and any non-pass rows; do not re-triage known failures; register new tests in `tools/test_manifest.json`." The lead and deep definitions add: "Work orders name the runner command and the expected selection (`-DryRun` output)."

### 8.2 Cost controls

- **Profiles.**
  - The runner keeps one profile per label: `.godot/test_runs/appdata_<label>`, with `-Cache copy|reuse|none` and the same semantics as `menu_warm_stall_probe.ps1`. The default is `reuse`; the first run copies the warm caches.
  - `profile: fresh` tests get `appdata_<label>_fresh` (save slots only), recreated per run.
  - The runner calls `prune_test_output.ps1 -Exclude appdata_<label>` before each run, like the other runners.
  - Run directories older than 7 days, and all but the 10 newest runs per label, are pruned. The prune script learns `test_runs/` (WO2).
- **GPU queue: move it into `tools/`.** Recommended. Today's queue lives in one session's scratchpad (`<scratchpad>/gpu_queue/`), so a second Claude session or a Codex agent has a different queue and the FIFO breaks. Move it as follows:
  - Script to `tools/gpu_queue.ps1`.
  - Ticket directory to `.godot/gpu_queue/` (repo-local, shared by every session and runtime; added to the prune keep list).
  - Same CLI (`-Acquire/-Heartbeat/-Release/-Status -Label`).
  - New `-Wrap { ... }` mode: acquire, heartbeat every 5 min in a background job, release in `finally`.
  - `run_tests.ps1` acquires once per GPU phase (not per test). With `-YieldMinutes 10`, a full run releases between batch groups when another ticket is waiting, and re-queues after the 60 s fairness gap.
  - Headless phases never take the queue. The solo kill-Godot permission stays as it is: the runner kills only PIDs it started.
- **Machine awareness.** `results.json` records the GPU and hardware profile. Known failures can be scoped by machine, and byte-exact golden tests are tagged with their capture machine and report `skip` on other machines (goldens are machine-specific; see the visual-golden-check skill).

## 9. Work orders

Each work order is sized for the implementer: bounded files, no gameplay or engine changes. Acceptance checks are listed with each.

**WO1: Manifest promotion and validator.**
- Files: `tools/test_manifest.json`, `tools/test_known_failures.json`, `tools/testing/validate_manifest.ps1`.
- Changes:
  - Promote the draft and drop the `closeout` blocks.
  - Review tags against `suggest_tags`, adding a tag when the test would catch a regression in that system.
  - Fix the mode of the 16 dual-run tests and the forced-mode list.
  - Split out the known failures.
  - Write the validator: every SceneTree `tools/*.gd` has an entry; tags exist; globs parse; smoke and runner files exist; `batch` groups have a world; markers are non-empty for gate tiers.
- Do not change any test file.
- Acceptance:
  - the validator passes;
  - adding a dummy `tools/zz_test.gd` makes it fail with the file named;
  - it runs in under 2 s.

**WO2: Runner core.**
- Files: `tools/run_tests.ps1` and `tools/testing/TestRunner.psm1` (PowerShell 5.1 and 7 compatible, no Python).
- Changes:
  - Selection (section 4.5: `-Changed`/`-Base`/`-Since`, `-Tags`, `-Tests`, `-Suite smoke|gate|full|extended`, `-DryRun` with reasons and an estimate).
  - Launch through `System.Diagnostics.Process` with timestamped merged logs.
  - Headless lanes (`-Parallel 6`, longest first, honouring `parallel_safe`), `gpu-local` lanes (3) and sequential GPU.
  - Manifest timeouts with tree kill of own PIDs; verdicts per section 5.5; incremental `results.json` and `results.md`; `-Resume`/`-RetryFailed`; profile handling and prune per section 8.2; `-NoBuild` (otherwise build when `.cs` files changed).
  - `.ps1`-runner entries run through their runner with `-Label`/`-Cache reuse`.
- Acceptance:
  - `-DryRun` on 3 sample diffs (a Sunshine shader, `scripts/ui/GameplayGrassDefaults.cs`, `project.godot`) prints the expected tags and selections;
  - `-Suite smoke` passes;
  - the headless exit codes are real (no `PASS_NONZERO_EXIT`);
  - killing the runner mid-run and `-Resume` finishes only the remainder;
  - a test with a 5 s injected sleep past a 3 s timeout reports `timeout` and the suite continues.

**WO3: GPU queue in `tools/`.**
- Files: `tools/gpu_queue.ps1`, `tools/prune_test_output.ps1` (keep `.godot/gpu_queue` and `test_runs` handling).
- Changes:
  - Move the script, with the ticket dir `.godot/gpu_queue/`.
  - Add `-Wrap`.
  - Integrate it with the runner (one acquire per GPU phase, background heartbeat, `-YieldMinutes`).
- Acceptance:
  - two concurrent `-Suite smoke` runs with different labels serialize their GPU phases (log timestamps);
  - a ticket that is not refreshed for 15 min is removed;
  - `-Status` lists both;
  - the scratchpad copy is no longer referenced by docs or skills.

**WO4: Known failures and reporting polish.**
- Files: runner module, `tools/test_known_failures.json`.
- Changes:
  - The `known-fail`/`flaky`/`quarantined`/`unverified` handling, `fixed` detection, and `match` regex checks (a different failure is a new `fail`).
  - Per-machine scoping.
  - Run the 2 `unverified` tests and `rain_streaks_gpu_test` x5 on `main` and record the outcome in the JSON.
- Acceptance:
  - rerunning the close-out failure set gives only `known-fail`/`quarantined` and runner exit 0;
  - temporarily editing one `match` to a wrong pattern turns that test into `fail`;
  - a known-fail test forced to pass (fixture flag) is reported `fixed` with the banner.

**WO5: Marker normalisation.**
- Files: the 57 `marker_fix` tests in `tools/` (the `.gd` wrappers, or their C# `Console.WriteLine` lines), and the manifest `marker` fields.
- Changes:
  - Add or rename only the final verdict line to the canonical form.
  - Move capture-only entries to the `manual` tier with `=CAPTURED`.
- Do not change checks, thresholds or exit codes.
- Acceptance:
  - the validator's `marker_fix` list is empty;
  - each changed test's verdict on one run matches its close-out verdict;
  - the C# build is clean.

**WO6: AwaitSettled helper.**
- Files: `tools/testing/RingTestWorld.cs` (new: `AwaitSettled`, world boot shared by the certified and integration paths), and the world GPU tests in the `certified@*` groups.
- Changes:
  - Replace their fixed settle timers (`create_timer(20.0)` and similar waits that are not watchdogs) with `AwaitSettled(maxSeconds: 40)`, keeping the explicit build or readback waits.
  - Audit the C# helper waits of the other `certified@*`/`integration@*` tests and list which ones are fixed sleeps.
  - Measure before and after with the runner.
- Acceptance:
  - each test's verdict is unchanged over 2 runs;
  - the summed duration of the tests with explicit fixed timers drops by at least 20%;
  - no test loses a check.

**WO7: Batch host pilot (`certified@1280x720`: `cloud_layers_gpu_test`, `cloud_layers_map_gpu_test`, `storm_profile_map_gpu_test`).**
- Files: `tools/testing/batch_host.gd`, `RingTestWorld` reset and fingerprint, `tools/cases/*_case.gd` for the 3 tests; the 3 wrappers become thin; runner support for `batch` groups (protocol, per-case timers, relaunch, standalone confirmation, `-Shuffle`).
- Acceptance:
  - batched verdicts equal standalone verdicts over 3 shuffled orders;
  - an injected hang in the 2nd case makes it `timeout` and the 3rd case still runs in a relaunched host;
  - an injected crash is contained the same way;
  - the group's wall time is under 60% of the standalone sum (expected about 50 s against 121 s);
  - each standalone wrapper still prints its original marker.

**WO8: Batch rollout.** After the pilot is reviewed, roll out to the `integration@default` (12 tests) and `certified@default` (7 tests) groups, test by test, with the WO7 acceptance per group. Escalate any test whose reset needs new production hooks to the lead.

**WO9: Agent integration.** Make the documentation edits in section 8.1: AGENTS.md, the `godot-test-run`, `test-scaffold` and `log-triage` skills, and the 6 agent definitions in both runtimes. Acceptance: a reviewer diff shows identical bullets across `.claude` and `.codex`, and every command example in the skills runs (dry run).

Order: WO1 → WO2 → WO3 and WO4 (in parallel) → WO5 → WO9 → WO6 → WO7 → WO8. WO1-WO5 and WO9 deliver about 80% of the saving with no change to test logic.

## 10. Expected time saving

| Scenario | Today | After WO1-WO5 | After WO6-WO8 |
|---|---|---|---|
| Single-file step (median of 367 files from 45 commits, simulated) | the agent picks tests by hand or runs the full gate (about 30 min GPU) | smoke + 10 GPU tests, median 2.7 min (p75 3.9 min), + 25 headless, 25 s wall | about 2 min |
| Feature-sized commit (median of 45 commits) | full gate (about 30 min GPU + 5 min headless) | 17 GPU tests, about 4 min, + about 1.5 min headless | about 2.5-3 min |
| Full pre-merge, unattended | about 80 min: 43 min GPU pass (15 min of it one hang), about 30 min of failure reruns, 2 headless passes; plus agent triage tokens | headless about 4 min (6 lanes; critical path 224 s) + GPU about 30 min, with 0 triage reruns | headless about 4 min + GPU about 23-24 min |

Basis:
- Close-out durations from `.godot/batch_close/*/results.tsv`.
- The timing breakdown in section 3.
- Selection simulated with the draft globs over `git log -45`. Slow-tier and quarantined tests are excluded from the per-step figures.

Per-step figures depend on tag precision: the WO1 review moves them in either direction.

## 11. Open questions for the user

1. **The pre-merge reference machine and trigger.** Is the RTX 4070 desktop the reference machine for `-Suite full` and the known-failures list (A1000-only goldens would then be `skip` there)? Should the full run be started by the primary agent at merge time (about 25-35 min, unattended), or by a scheduled overnight run?
2. **Batch-host scope.** WO7 and WO8 refactor about 22 GPU world tests into a case file plus a thin wrapper. They save about 4 min per full run and roughly 10 s per batched world test in a step. Proceed with the pilot, or stop after WO6 (await settled only, no structural change to tests)?
3. **The extended tier.** 97 real tests (90 GPU) were not in the close-out gate list, including the water FFT visuals, height shadow, and grass/tree GPU tests. Should WO4 triage them once and promote the passing ones to `gate`? That adds an estimated 10-20 min to the full run. Or should they stay opt-in?
