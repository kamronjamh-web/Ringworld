---
name: godot-test-run
description: Run Ringworld tests with tools/run_tests.ps1 (tag/changed-file selection, GPU queue, results.md), or by hand as a fallback (headless C#/GDScript check, windowed GPU test, or tools/*.ps1 phase runner) the correct way - build, isolated APPDATA profile, GPU coordination, PASS/FAIL detection, log capture and test-output pruning. Use before launching Godot for any test or capture.
---

# Running a Ringworld test

## Start here: tools/run_tests.ps1

The runner selects tests from `tools/test_manifest.json`, builds C#, keeps one isolated profile per label, takes the GPU queue (`tools/gpu_queue.ps1`) for its GPU phase, prunes old output and writes the verdicts. Use it for everything that has a manifest entry; the manual path below is a fallback for one-off debugging.

```powershell
.\tools\run_tests.ps1 -Label <task> -DryRun                  # selection, reasons, time estimate; runs nothing
.\tools\run_tests.ps1 -Label <task>                          # -Changed is the default: tests for files changed vs main
.\tools\run_tests.ps1 -Label <task> -Tags clouds,weather
.\tools\run_tests.ps1 -Label <task> -Tests cloud_layers_gpu_test,storm_lifecycle_code_test
.\tools\run_tests.ps1 -Label <task> -Suite smoke             # quick check; also: gate | full | extended
.\tools\run_tests.ps1 -Label <task> -Resume latest           # finish tests that did not complete (add -RetryFailed for fails)
.\tools\run_tests.ps1 -Files scripts/ui/GameplayGrassDefaults.cs -DryRun   # explicit paths instead of git
```

- Cadence: `-Changed` mid-step; `-Suite gate` at a batch or commit point; `-Suite full` only before a merge or overnight (unattended).
- Selection flags: `-Changed [-Base main]`, `-Since <rev|green>` (green = last green run of the label), `-Files`, `-Tags`, `-Tests`, `-Suite`. Smoke always runs with `-Changed` and `-Suite`.
- Other flags: `-NoBuild` (skip the C# build), `-Cache copy|reuse|none` (default `reuse`; the first run copies the warm caches), `-Parallel` / `-LocalLanes` (lane counts, 0 = default), `-YieldMinutes` (release the GPU between groups when another agent waits).
- Output: `.godot/test_runs/<label>/<run id>/results.md`, `results.json` and `logs/<test>.log`. Exit 0 = no fail/timeout/crash/error, 1 = failures, 2 = runner error.
- Read the `results.md` summary line and non-pass rows only. Open a log for `fail`, `timeout`, `crash` or `error` with the `log-triage` skill. Do not re-triage `known-fail` rows (list: `tools/test_known_failures.json`); report `fixed` rows so the list is updated.
- Do not wrap the runner in `gpu_queue.ps1 -Wrap`; it takes the queue itself.
- Use a distinct `-Label` per task; do not run two runs with the same label at once.
- After adding or editing a manifest entry, `pwsh -File tools/testing/validate_manifest.ps1` must pass.

## Manual fallback

Everything below is for tests without a manifest entry, one-off debugging, or when the runner itself is suspect.

## 0. Pick the engine and build

- Engine (console build, so stdout is captured):
  `artifacts\engine-upgrade\Godot_v4.8-dev7_mono_win64\Godot_v4.8-dev7_mono_win64_console.exe`
  The phase runners hard-code the identical copy under
  `%USERPROFILE%\Downloads\Godot_v4.8-dev7_mono_win64\Godot_v4.8-dev7_mono_win64\`.
- Build C# before any run that touches `.cs` changes:
  `./tools/use_godot_48.ps1 -Build` (pinned .NET in `artifacts/engine-upgrade/dotnet`).
- `./tools/use_godot_48.ps1 <engine args>` runs the engine with `--path <repo>` already added.
- After any `--import`, check `git status addons/terrain_3d/bin/`; an import can delete
  `~libterrain.ringworld.windows.debug.x86_64.dll`. Restore with `git show HEAD:<path> > <path>`.

## 1. Headless or GPU?

Read the test's header comment; the filename is not reliable.
- Headless covers config, data and scene wiring. Most `*_test.gd`, `*_smoke.gd` and settings smokes.
- GPU (windowed Vulkan) is required when the header says "not --headless", or the script checks
  `RenderingServer.get_rendering_device() == null` / `DisplayServer.get_name() == "headless"`.
  This includes all `*_gpu_test.gd`, `*_capture*.gd`, `*_parity_test.gd` and `*_visual*`.

## 2. GPU coordination (windowed runs only)

- **Solo agent:** you may kill all Godot processes once before the GPU batch, including the user's editor:
  `Get-Process Godot* -ErrorAction SilentlyContinue | Stop-Process -Force`.
  Never do this for builds or headless checks.
- **Parallel agents:** do not kill. List Godot processes and kill only processes you started.
  Take the shared FIFO queue (tickets in `.godot\gpu_queue\`):
  `.\tools\gpu_queue.ps1 -Acquire -Label <task>` (blocks until you hold the GPU), `-Heartbeat` at least every 10 min,
  `-Release` when done, `-Status` to see the queue. A ticket not refreshed for 15 min is stale.
  Or run the command inside it: `.\tools\gpu_queue.ps1 -Label <task> -Wrap { <command> }`.
- Note the `Using Device` line in the log; results are only valid on that GPU.
  RTX A1000 laptop = goldens and baselines; RTX 4070 desktop = `desktop_mid_high`.
  See the `visual-golden-check` skill.

## 3. Invocation

**A test with a `tools/*.ps1` runner: use the runner.**

| Runner | Key params | Exit code? |
|---|---|---|
| `terrain_altitude_lod_flight_test.ps1` | `-Label -Preset <0-3> (ALWAYS pass) -Slot -Cache` | yes |
| `menu_altitude_perf_capture.ps1` | `-Label -Slot -Cache` | **no**: grep log |
| `menu_warm_world_lifecycle_test.ps1` | `-Label -Primary -Secondary -Headless -Phase -Sequence -Cache` | yes |
| `menu_warm_stall_probe.ps1` | `-Label -Cache -Trace -NoWarm -EnvVars` | yes |
| `run_emitter_capture_route.ps1` | `-Out -Sites -Quick` (windowed only) | yes |
| `run_cave_certification.ps1` | `-Godot <exe>` (**required**, not on PATH) `-SkipBuild` | throws on fail |
| `menu_main_scene_boot_check.ps1` | `-AppData <isolated dir>` (mandatory) `-Seconds` | yes |

Profile flags:
- Always pass a fixed `-Label` for the task and `-Cache reuse` after the first run.
  - `reuse` keeps `.godot\<base>\appdata_<Label>`, and rebuilds it only if it is missing.
  - `copy` clones the shader, Vulkan, ring and generated caches (up to ~2.5 GB). Use it only for the first run.
  - `none` is a fresh profile with only the save slots.
- Phase runners already isolate `$env:APPDATA` and call `prune_test_output.ps1` before running.

**Anything else: one `tools/<name>.gd` SceneTree wrapper per C# test.** There is no dispatcher.

```powershell
$exe = "artifacts\engine-upgrade\Godot_v4.8-dev7_mono_win64\Godot_v4.8-dev7_mono_win64_console.exe"
$log = ".godot\<task_label>_<name>.log"
& $exe --headless --path . --script res://tools/<name>.gd -- <user args> *> $log   # drop --headless for GPU
"exit=$LASTEXITCODE"
```

- Some tests are scenes: `--headless --path . res://scenes/<X>.tscn -- --flag`.
- **Isolate the profile** for anything that loads, saves or writes `user://`:
  1. Set `$env:APPDATA = "<repo>\.godot\adhoc_<task_label>"`.
  2. Copy in only the `worlds\slot_N` you need.
  3. Restore `APPDATA` afterwards.
- `world_loading_smoke_test` uses the **real** slot 1 unless you isolate it.

## 4. Pass/fail

- Stdout prints a `<NAME>=PASS` marker. `<NAME>=FAIL <reason>` goes through `push_error`, so stderr shows it as `ERROR:`.
  Always capture both streams (`*>`).
- Exit codes: 0 = pass; 1 = fail; 2/3 = guard refusal or watchdog (lifecycle test). `=SKIP` exits 0. Report SKIP as SKIP, not as a pass.
- A PASS marker with a non-zero exit, or a hang on quit, is a known Godot 4.8-dev7 exit issue
  (WorkerThreadPool hang; `sunshine_shadow_toggle_smoke` segfaults on quit).
  Note it and do not count it as a failure. For long windowed runs, poll the log for the verdict and kill after a grace period.
- Known failures that predate current work are listed in `tools/test_known_failures.json` (status, match pattern, evidence).
  Agents never `git stash`/`reset`/`checkout` to prove something predates a change; reason from the diff, or ask the user.

  Startup errors about `ring_voxel_mesher.gdextension` are expected noise.
- Summarise the log with the `log-triage` skill; do not paste the whole log into context.

## 5. Cadence

- Mid-step: `run_tests.ps1 -Changed` (covers the touched files).
- At the commit or batch point: `run_tests.ps1 -Suite gate` once.
- Before a merge: `run_tests.ps1 -Suite full`, unattended.
- Skip the darkness capture and the storm check unless lighting or beam code changed.

## 6. Clean up (always)

```powershell
.\tools\prune_test_output.ps1          # dry run: review
.\tools\prune_test_output.ps1 -Apply   # at task end
```

- Never delete the protected caches:
  - `imported`, `mono`, `editor`, `shader_cache`, `uid_cache.bin`
  - `shadow_baseline`, `s5_before`, `shadow_compose_identity_head_clouds`, `shadow_compose_identity_storm_revamp_4070`
  - `terrain3d-native-*`
- Big manual deletions need the user's go-ahead.

## Subagent prompts

When delegating a test run, state explicitly:
- headless or GPU
- whether the agent may kill Godot (solo) or must use the GPU queue (parallel; `run_tests.ps1` does this itself)
- the runner command and the expected selection (`-DryRun` output)
- the profile label to reuse
- that it must prune at the end
