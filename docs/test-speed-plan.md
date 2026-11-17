# Test and measurement speed plan (2026-11-17)

Status: **Phase A, analysis and design only.** No runner, queue, harness or manifest file was changed. Phase B (section 5) starts when the other lanes have stopped using the runners.

Goal: make testing and measurement faster without lowering quality. The user approved the item list; the numbering (1) to (5) below is the user's.

## 1. Where the time went

Source: every `results.json` and log timestamp under `.godot/test_runs/`, every `console.log` under `.godot/perf_bench/` (the run id is the enqueue time, the file's creation time is the Godot launch, its last write is the exit) and the `tv_*` folders of `tools/tree_visibility_run.ps1`. The queue keeps no history, so waits are reconstructed from these. Analysis scripts (temporary, in the session scratchpad): `C:\Users\Kamron\AppData\Local\Temp\claude\cfbed90965d6c41b\286353fc-04af-4709-9ccd-0682bc5380c9\scratchpad\test_speed_analysis\` (`runs.py`, `jobs.py`, `sim.py`).

Window studied: 2026-11-17 07:19 to 13:57 (398 min), 4 to 5 agents, one RTX 4070. The queue was never empty.

| Where the 398 min of GPU ticket time went | min | share |
|---|---|---|
| Godot running, timing runs (20 `perf_bench`, 1 cadence probe) | 145 | 36 % |
| Godot running, correctness and picture runs (18) | 103 | 26 % |
| of which the looping `landscape_look_baseline` (parse error at 2.8 s, held until the 2300 s script watchdog) | 38 | 10 % |
| `perf_bench` and tree-visibility preparation **inside** the ticket, no Godot running | about 72 | 18 % |
| Not attributable (ad-hoc `gpu_queue -Wrap` jobs without a recorded folder, 15 s polls, instant failures such as `tp_g4`) | about 78 | 20 % |

Waiting: the 32 jobs with a known enqueue time waited **759 min in total** (mean 23.7 min, longest 65 min) for 248 min of Godot time. Useful Godot time was about half of the window.

Not located in the logs, so no time is attributed to them: the empty log from `$ErrorActionPreference = 'Stop'`, the `perf_bench` chain that failed at once (`tp_g4` has a folder and no console log) and the run whose `-ScenarioFile` was ignored. Five `t43` and `tocc` reports record the default scenario file as `args.scenario_file`, which is how that name clash shows.

Findings that change the plan:

1. **The largest avoidable cost is preparation done while holding the ticket.** When a `run_tests` job took over the GPU, Godot started 0.3 min (median) after the previous job ended. When a `perf_bench` took over it was 3.0 min (median of 23 handoffs). Measured parts:
   - the "is the optimized assembly stale" check, `Get-ChildItem $project -Recurse -Include *.cs`, walks all of `.godot`: **143 s**. `git ls-files '*.cs'` answers the same question in 0.05 s (`TestRunner.psm1` already does this);
   - `prune_test_output.ps1`: 9.6 s, twice per run;
   - delete and copy the 529 MB world (5,671 files): 2.4 s + 8.7 s. The copy is not the problem it looked like.
   `tools/tree_visibility_run.ps1` has the same walk and the same order.
2. **The day was dominated by timing runs, which can never share.** 21 of 39 jobs were exclusive by nature. Correctness jobs waited behind chains of `perf_bench` runs, not behind each other. Two queue classes alone would have changed total waiting by -9 % (at a 1.3x slowdown when two jobs share) to +18 % (at 1.6x). They pay off after the exclusive demand is cut.
3. **20 of the 23 `perf_bench` launches were 6 experiments** (`nt4_still` on/off/on2, `nt4_ms`, `vo`, `vo2`, `t43` twice). Each launch paid 3.0 min preparation, 31 s boot and about 18 s of placement and streaming per pose.
4. **Windows are longer than the medians need.** Between the two 12 s rounds of a standing scenario, frame p50 differed by 0.00 to 0.04 ms (reference run `full_a785e4b`, 9 standing scenarios) against a 0.3 ms decision floor. Flights differ more (up to 0.51 ms) and cover different ground over time.
5. **VRAM limits sharing to two world jobs.** One game instance at 1600 x 900 High takes about 4.0 GB on the card (6.4 GB used at peak with 2.3 GB already in use by the desktop). Two fit in 12 GB, three do not.
6. **A parse check works and is cheap.** `Godot --headless --check-only --script <entry.gd>` reported the exact error class of the 38-minute loop, including an error in a preloaded dependency, in 0.5 s on a scratch script and 4 s on `landscape_look_baseline.gd`. It exits 0 either way, so the check must read the output.
7. **`-Files` selection.** A file no glob maps selects every headless test: 283 tests for one new shader include or one `tools/fixtures/*.json` (10 min at 6 lanes, measured on `overnight_gate`). 250 of 3,926 tracked files are unmapped today. Any proxy terrain file selects the whole `terrain` tag: 85 headless and 15 GPU tests, where the agent's hand-picked set for the same change was 13.
8. **Known failures.** Since 2026-11-15 (150 runs): `grass_live_tuning_smoke_test` failed 7 times, `shadow_compose_identity_gpu_test` 5 times, `proxy_river_true_width_test` twice, all reported as new `FAIL` because none has a known-failures entry. `terrain_near_lod_parity_gpu_test` has an entry, but the script error leaves the scene tree running, so it ends as a 300 s `TIMEOUT` (10 min of GPU, twice) and the entry never matches.

## 2. Recommendation

Do the approved items, in this order. The change from the approved order: **ticket hygiene and the pre-launch check come first, then in-session A/B, then the two queue classes.** The first three are smaller changes with the largest measured saving, and the queue classes only pay off once they are in.

| Order | Item | Basis | Saving on the 2026-11-17 window |
|---|---|---|---|
| B0 | Preparation outside the ticket; `git ls-files` staleness check; mirror restore of the world | measured | about 70 min of ticket time (18 %); replayed waiting -45 % |
| B1 | (5) Pre-launch check, shared launcher, stall watchdog | measured | 38 min loop + 10 min of script-error timeouts; replayed waiting -37 % for the loop alone |
| B2 | (2) In-session A/B | estimated from logs | 20 launches become 6; Godot time 137 to about 92 min; 42 min of preparation gone before B0, 4 min after |
| B3 | (1) Two queue classes | replay | waiting of correctness jobs -25 to -40 % once B0 to B2 are in; little before |
| B4 | (4) `-Files` mapping | measured | up to 10 min of 6-lane headless load per unmapped file; 100 to about 15 tests for a proxy change |
| B5 | Known failures | measured | 16 re-triaged rows and 10 min of GPU in three days |
| B6 | Adaptive windows (standing scenarios) | estimated | 15 to 25 % of Godot time of a timing run |
| B7 | (3) Deterministic captures | estimated | one launch and one capture per pose fewer in every picture comparison; exact verdicts in place of "noise-limited" |

Replay of the same 39 jobs with B0 to B3 and B6: mean wait from enqueue to launch 12.4 to about 1.4 min, launches 39 to 25. The replay keeps the observed arrival times and does not know the unattributed holders, so it under-predicts absolute waits (485 min replayed against 759 observed). Read it as a ratio.

Not recommended:

- **Hardlink or junction for the bench world.** The game autosaves into the profile's world, so a link would let it write into the frozen snapshot. `robocopy /MIR` from the snapshot restores only what the game changed: 0.1 s measured against 11 s, same result, snapshot untouched.
- **Three concurrent world jobs.** VRAM (finding 5). Three is fine for `gpu-local` fixtures, which already run three wide inside one ticket.
- **Short-jobs-first ordering.** Replayed: under 7 % and it adds starvation rules. Strict FIFO stays.
- **Making the cloud compute bit-exact.** `rain_cloud_compositing_gpu_test` has shown since October that a frozen scene's cloud source is not bitwise stable. Hold the outputs instead (B7).

## 3. Design

### 3.1 Ticket hygiene (B0)

Order of work in `perf_bench.ps1` and `tree_visibility_run.ps1`: user-playing check, argument checks, staleness check with `git ls-files` (tracked plus untracked `*.cs`), optimized build, prune, pre-launch check (3.2), **then** acquire. Inside the ticket: world and settings restore by `robocopy /MIR` (0.1 s; it stays inside because the profile is shared by every run of that runner), assembly swap, launch, assembly restore. Report building, the still comparison and the second prune run after release.

The builds are serialised by a small lock file (`.godot/mono/temp/opt_build.lock`), because two agents may now prepare at once. Each runner keeps its one profile and its "another run of mine is alive" refusal, so two `perf_bench` runs never overlap, whatever their queue class.

`tree_visibility_run.ps1` still takes the queue under a fixed label. It gets the per-run label `perf_bench.ps1` already has.

Add `.godot/gpu_queue/history.jsonl` (enqueue, acquire, release, class, label, pid, reason). The next analysis reads waits instead of reconstructing them.

### 3.2 Pre-launch check, launcher, watchdog (B1)

One module, `tools/testing/GpuJob.psm1`, used by `run_tests.ps1`, `perf_bench.ps1`, `tree_visibility_run.ps1` and `gpu_queue.ps1 -Wrap`, plus `tools/run_godot.ps1` for ad-hoc launches.

Pre-launch check, before acquiring, all headless:

1. Parse check of the entry script with `--headless --check-only --script`, isolated `APPDATA`. Fail on `Parse Error`, `Compile Error` or `Failed to load script` in the output (the exit code is 0 either way). 0.5 to 4 s per entry point.
2. C# build is current (existing `Test-NeedsBuild`).
3. Arguments: every named file exists; unknown scenario, pose or variant names are rejected; the resolved plan is printed (scenario file, scenario names, windows, variants). `perf_bench.gd` then fails the run if the scenario list it executed differs from the plan the runner printed. That is the check that catches an ignored `-ScenarioFile`.
4. Optional `--plan` mode of a harness: parse its own arguments and fixtures, print the plan, quit. Headless, a few seconds. Start with `perf_bench.gd` and the landscape harness.

Launcher: the existing `RingTestProcV2` class (merged, timestamped, flushed log; real exit code). No `& exe *> log`, so `$ErrorActionPreference = 'Stop'` cannot empty a log. The manual fallback in the `godot-test-run` skill currently prescribes the pattern that fails; it changes to `tools/run_godot.ps1`.

Watchdog, in the launcher:

- **Entry script did not load:** the log shows `Failed to load script "res://tools/<entry>.gd"`. Kill the own process tree at once. Nothing can print a verdict after that line.
- **Stall:** no new log line for `stall_s` (default 300 s, per manifest entry or `-StallMinutes`). Kill the own tree, write `STALLED` into the log, release the ticket. Long silent harness phases print a heartbeat line every 30 s through one helper in `shadow_harness_common.gd`.
- **Hard timeout:** as today.
- The ticket heartbeat is tied to the job: it refreshes only while the own Godot is alive and its log advanced within the stall limit.
- Known-failure matching also applies to `timeout` and `stalled` rows, so an entry like `terrain_near_lod_parity_gpu_test` matches.

Harness side: failure paths call `quit()` before any cleanup (`landscape_look_baseline.gd` was patched this way today). One shared `_fail` helper replaces the per-script copies as scripts are touched.

### 3.3 In-session A/B (B2)

`perf_bench`: a scenario file may carry `variants`, or `-Variants <json>` adds them: a name plus what to apply, using the parsers that exist for `PERF_BENCH_TREE_PROPS` and `PERF_BENCH_NODE_PROPS` (node property, method call) and a settings overlay applied through the drawer's `Install*Defaults` methods, the path a player's change takes. Per standing scenario: place once, then per round A, B, (C,) A. Each switch is followed by a settle (frames, `wait_builds_idle`, a per-variant `settle_s`) and a check that the switch took (read back the property). The report gives paired deltas per pose with the same-session A-A spread as the noise floor. That floor is 0.00 to 0.04 ms within a session against up to 0.25 ms between launches, so smaller effects become decidable.

Stills follow the same loop: `<pose>_<variant>.png` plus an A2 repeat, compared in the run.

Landscape harness: `run_cost` already has same-boot A-B-A cases (`--extra-cases`, `--feature-ab`). The look run gets the same `--feature-ab`: every pose captured with the feature on, off and on again in one boot, in place of three `--feature-off` launches.

Limit: a code or shader source change cannot be toggled in a session. Those still take two launches; B0 and B6 make each cheaper. Where a comparison will be repeated, expose the feature as a debug property, as the tree lane does.

### 3.4 Two queue classes (B3)

`gpu_queue.ps1 -Class exclusive|shared [-Weight world|light]`; default `exclusive`, so old callers behave as today. The class and weight are part of the ticket file name. The rule needs only the sorted ticket list, like today:

- An exclusive ticket holds when it is the oldest.
- A shared ticket holds when every older ticket is shared and there is room: at most 2 `world` jobs and at most 3 holders in total (`light` = a fixture job without a world).

So strict FIFO is kept: shared jobs never overtake a waiting exclusive job, and an exclusive job waits only for the shared jobs that were ahead of it. A shared job also checks free VRAM (`nvidia-smi`, at least 4.5 GB) before launching and waits if the desktop is using more than usual.

Who is what:

- `perf_bench` with timing windows, cadence probes, `landscape_cost_baseline`, attribution and benchmark tools: exclusive.
- `perf_bench` runs that only take stills, tree identity and oracle runs, look captures: shared.
- Manifest tests: new field `gpu_class`, default `shared`. `exclusive` for the `perf-telemetry` tag and for tests that assert on wall-clock time. `run_tests` takes a shared ticket for the shared group and a separate exclusive ticket for the rest.

Quality guard: a shared job runs slower, and some tests fail on settle timeouts ("world never settled: timeout after 40 s" hit four tests in three days on an exclusive GPU). Before the default flips, run the GPU gate alone and as two concurrent runs; verdicts must match. Tests that differ become `exclusive` or get their wall-clock limits scaled by an environment factor the runner sets for shared jobs. The measured slowdown decides whether the cap stays at 2.

Not changed by this: the optimized assembly is swapped into the shared Debug slot during a `perf_bench`, and headless runs of other agents are outside the queue. Both are as today.

### 3.5 `-Files` mapping (B4)

- Unmapped file: warn, list it in `results.md`, select smoke only. `-UnmappedPolicy full` restores today's behaviour. The gate at the commit point still runs everything.
- `validate_manifest.ps1` lists tracked files no glob maps and fails when the count rises above the recorded number, so the map is fixed when a file is added, not discovered at run time. The 250 existing ones are mapped or ignored once (`demo/**`, `third_party/**`, `tools/media/**`, `tools/poi/**`, `scripts/Tools/**`).
- Shader includes map through their includers: a `.gdshaderinc` or `.glsl` include selects the tags of the shaders that `#include` it.
- `tools/fixtures/<name>` selects the tests whose wrapper or C# class names the fixture (the existing `Get-WrapperDependants` search).
- Split `terrain`: `terrain-proxy` (`scripts/Voxel/LOD/*Proxy*`, `materials/ring_terrain_proxy*`, `materials/terrain_gpu/ring_proxy*`), `terrain-near` (Terrain3D surface, `materials/terrain3d*`), `terrain-generation`. Each of the 89 terrain tests keeps only the sub-tags it exercises. Target: a proxy-only change selects about 15 tests.

### 3.6 Known failures (B5)

- `terrain_near_lod_parity_gpu_test`: remove the wet/dry section that reads the retired `surface_wetness` (trivial); drop the entry when it passes.
- `shadow_compose_identity_gpu_test`: the 1-LSB difference against the golden. A tolerance or a new golden is a decision for the user (question 4).
- `proxy_river_true_width_test`, `grass_live_tuning_smoke_test`: real asserts in lanes that are being worked on. Add `known-fail` entries with the exact message as `match` and the date; the owning lanes fix them.
- `results.md` prints one line for known failures ("3 known-fail, see list") in place of rows, so they stop drawing triage.

### 3.7 Adaptive windows (B6)

Standing scenarios only. Sample in 2 s blocks and stop when all hold: at least 4 s and 300 frames; the 95 % confidence half-width of the frame median is at most the larger of 0.05 ms and 0.5 %; first and last block medians agree within that width (no drift). The cap is today's window length, so a window is never longer or looser than now. Flights and sweeps keep fixed windows, because the path is the workload. Tail metrics (p99, max, hitches) from a window that stopped early are marked informational; `-Tails` forces full windows. The report records the actual window length and why it stopped.

### 3.8 Deterministic captures (B7)

One capture mode in debug tooling (no grave-key entry), built on `tree_visibility_world.gd` (`freeze_world`, `freeze_noise`, `hold_wind`), which today has to hide the clouds, grass and gust debris to get comparable stills:

- D1, certain: gust-front debris, grass wind and the shared wind time read a held simulation time while the mode is on. `RingGustFrontDebris._Process` advances on frame delta today. The harness no longer re-asserts wind every frame.
- D2: temporal AA off and a fixed dither and jitter index.
- D3, uncertain: clouds and the far weather atlas. Hold the **outputs**: after N warm frames, stop dispatching the compute and keep presenting the last resolved targets. The camera is still during a capture, so this is valid, and the picture keeps its clouds.
- Self-check in every capture: two consecutive frames must be byte-identical before the still is taken. If not, the record names the layer that still moves (hide one at a time) and the run falls back to today's noise-pair logic, clearly marked.

D3 needs an investigation by the lead role before any implementation estimate.

## 4. Risks

- **Shared jobs change verdicts** (timeouts under contention). Mitigation in 3.4; the default stays exclusive until the two-run comparison passes.
- **The stall watchdog kills a healthy, silent job.** Heartbeat helper plus a per-entry `stall_s`; the first week logs "would have killed" without killing.
- **Unmapped-file policy hides a regression until the gate.** The validator check keeps the unmapped set from growing; the gate cadence in `AGENTS.md` is unchanged.
- **In-session switches that do not fully apply** (a setting read only at boot). The read-back check fails the variant; such settings stay two-launch.
- **Rollout while agents are running.** The ticket name format changes in B3. Do it when the queue is empty, and have the new script treat old-format tickets as exclusive.

## 5. Phase B work orders

Each is one bounded change for `implementer`, reviewed by the lead. B0, B1, B2 and B6 touch `perf_bench.ps1` / `perf_bench.gd` and run in that order in one lane. B4 and B5 touch the manifest and the selection code and can run beside them. B3 follows B1.

| WO | Goal | Touches | Verification | Depends on |
|---|---|---|---|---|
| B0 | Preparation before acquire; `git ls-files` staleness; `robocopy /MIR` world restore; per-run label in tree-visibility; queue history log | `perf_bench.ps1`, `tree_visibility_run.ps1`, `gpu_queue.ps1` (history only) | Previous-exit-to-launch gap at most 0.5 min in `history.jsonl`; a quick run matches `baseline_a785e4b` within noise; snapshot hashes unchanged after a run | none |
| B1a | `GpuJob.psm1`: pre-launch check, launcher, stall and load-failure kill; `run_godot.ps1` | new module, `TestRunner.psm1`, the two own-runners, `gpu_queue.ps1 -Wrap` | Scratch entry script with a parse error in a preload: rejected in under 10 s, no ticket taken. A script that never quits: killed at the stall limit, ticket released. `-Suite smoke` unchanged | B0 |
| B1b | `--plan` mode and plan echo check in `perf_bench.gd` and the landscape harness; `_fail` quits first everywhere | `perf_bench.gd`, `landscape_*_common.gd`, tool scripts | A wrong `-ScenarioFile` or unknown variant fails before the queue | B1a |
| B2a | `perf_bench` variants: A-B-A per pose, read-back, paired report, stills per variant | `perf_bench.gd`, `perf_bench.ps1`, `docs/perf-bench.md`, `perf-capture` skill | An A-A-A run of one variant reports zero deltas beyond the floor; the `vo2` four-way comparison reproduced in one launch agrees with today's four reports within their noise | B0 |
| B2b | Look run `--feature-ab` | `landscape_baseline_common.gd`, `landscape_look_baseline.gd` | One boot gives on, off, on2 per pose; on against on2 is the stated noise | B1b |
| B3 | Queue classes, weights, VRAM check; manifest `gpu_class`; `run_tests` split | `gpu_queue.ps1`, `TestRunner.psm1`, `test_manifest.json`, `validate_manifest.ps1`, own-runners | Scripted ticket tests of the rule (S+S hold, S+S+S waits, X never beside S, FIFO kept); GPU gate alone against two concurrent runs gives the same verdicts; slowdown recorded here | B1a |
| B4 | Unmapped policy, include and fixture mapping, validator coverage, `terrain` split | `TestRunner.psm1`, `test_manifest.json`, `validate_manifest.ps1` | Dry runs: new include selects its includers' tags; a fixture selects its tests; a proxy file selects about 15; validator passes | none |
| B5 | Known failures as in 3.6 | `terrain_near_lod_parity_gpu_test.gd`, `test_known_failures.json`, report format | The parity test passes or is listed; the three others show as known | none (the timeout matching part needs B1a) |
| B6 | Adaptive windows | `perf_bench.gd`, report, `docs/perf-bench.md` | Adaptive and fixed runs of the quick set agree within the floor on every median; windows recorded | B2a |
| B7a | Investigate capture determinism (which layers move, D3 feasibility) | read-only, `claude-lead` | A list of moving layers per pose with evidence | none |
| B7b | Capture mode D1, D2, then D3 | `RingGustFrontDebris.cs`, wind owner, cloud bridge, `tree_visibility_world.gd` | Self-check passes at the perf-bench still poses with clouds visible; identity tests report byte-identical without noise pairs | B7a |

Afterwards: update `AGENTS.md` (testing and tools sections, shared with Codex), the `godot-test-run` and `perf-capture` skills and `docs/perf-bench.md`.

Success check, from `history.jsonl` on the next multi-agent day: handoff gap at most 0.5 min for every runner; no ticket held past the stall limit without log progress; mean enqueue-to-launch under 5 min; one launch per A/B experiment.

## 6. Open questions for the user

1. **Shared cap of 2 world jobs** on the 4070 (VRAM, finding 5), in place of "2 to 3". Three only for fixture jobs. Agreed?
2. **Mirror restore in place of a hardlink or junction** for the bench world (protects the frozen snapshot, same saving). Agreed?
3. **Unmapped files select smoke plus a warning**, with the validator stopping the unmapped set from growing. The alternative is to fail the run until the file is mapped, which is stricter and slower.
4. **`shadow_compose_identity` 1-LSB difference:** accept a 1-LSB tolerance, re-capture the golden on the 4070, or leave it listed as known?
5. **Deterministic clouds by holding the last resolved targets** (the capture shows clouds but does not exercise the cloud compute during the still). Agreed, pending the B7a investigation?
