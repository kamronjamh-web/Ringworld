---
name: log-triage
description: Summarise a Godot run log (test console.log, godot.log, ad-hoc .godot/*.log) into device, PASS/FAIL/SKIP verdicts, grouped errors (shader, Vulkan, script exceptions, leaks, crashes) and the tail - without pasting the whole log into context. Also covers warm-stall reports. Use after any Godot run, and whenever asked why a run failed or hitched.
---

# Godot log triage

## 0. Runner results first

For a `tools/run_tests.ps1` run, start from `.godot/test_runs/<label>/<run id>/results.md` (summary line and table) or `results.json` (per-test status, reason, GPU, wall seconds). Read logs only for `fail`, `timeout`, `crash` and `error`; skip `known-fail` rows (see `tools/test_known_failures.json`). Per-test logs are in `.godot/test_runs/<label>/<run id>/logs/<test>.log`; triage one with the script below.

## 1. Find the log

| Run type | Log |
|---|---|
| `tools/run_tests.ps1` | `.godot\test_runs\<label>\<run id>\logs\<test>.log` |
| phase runners (`tools/*.ps1`) | `.godot\<base>\<Label>\console.log` or `console_phase<N>.log` (+ `.err`) |
| emitter capture route | `<Out>\godot.log` (stderr appended) |
| ad-hoc `--script` run | wherever you redirected `*>`; convention is `.godot\<task>_<name>.log` |
| Godot's own file log | `<APPDATA>\Godot\app_userdata\RingworldTechDemo\generated\logs\godot.log` (keeps 3) |

- Under an isolated profile, `APPDATA` is the `.godot\...\appdata_<Label>` folder.
- `-Cache copy` also copies the real profile's old logs into the isolated profile. Check timestamps before blaming a log.

## 2. Summarise

```powershell
.\.claude\skills\log-triage\triage.ps1 <log> [-Top 8] [-Tail 15] [-IncludeNoise]
```

The script prints:
- the `Using Device` line
- the verdict markers
- problems grouped by kind (SHADER, VULKAN, SCRIPT, ERROR, WARNING, LEAK, CRASH), with repeated lines collapsed and counted
- the tail of the log

It also reads `<log>.err` if one exists. Read more of the raw log only around the specific line you need (`Select-String -Context 5,15`).

## 3. Interpret

- **Verdict first.** `<NAME>=PASS` with exit 0 means pass. `=FAIL <reason>` usually comes with a matching `ERROR:` line. `=SKIP` is not a pass: report it.
- **Expected noise:**
  - Errors about a missing `ring_voxel_mesher.gdextension` at startup (filtered out by default).
  - Hangs or segfaults on quit after a PASS: the Godot 4.8-dev7 WorkerThreadPool hang, and `sunshine_shadow_toggle_smoke`.
- **Shader errors:** take the file and line from the first SHADER entry, then see the `shader-debug` skill. A compile failure in a `.gdshaderinc` shows up under every shader that includes it, so fix the first one.
- **C# exceptions:** the `   at ` stack lines are grouped under SCRIPT. The first non-Godot frame is usually the culprit.
- **Leaks at exit:** usually a fixture that a test never freed. Mention them, but they are not a failure unless the test asserts on them.
- **No verdict and a truncated tail:** the run hung or crashed. Check CRASH, then whether a watchdog line or `process wall seconds` exists.

## 4. Hitches and stalls (warm-load, menu)

Use `menu_warm_stall_probe.ps1 -Label <x> [-Trace]` (see the `godot-test-run` skill), then:

```powershell
python tools/warm_stall_report.py .godot/phase_c_live/<Label> --min-ms 100 --top 4 --depth 12
```

- Inputs: `stall_detail.txt`, `stall_frames.csv`, the `STALL_PROBE_ALIGN` lines in `console.log`, and optionally `trace.speedscope.json` (needs `-Trace`).
- Output (stdout only): per-frame probe buckets, top marker gaps, hot managed call paths, and a menu-to-warm timeline with GC totals.

## 5. Report

Give the user:
- the verdict and the device
- the top 1-3 real problems, each with a `file:line` when the log names one
- whether each problem is new or known noise

Do not paste raw logs longer than about 20 lines.
