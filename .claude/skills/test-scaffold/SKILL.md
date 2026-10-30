---
name: test-scaffold
description: Create a new Ringworld test - a C# test class in tools/ plus its GDScript SceneTree wrapper - following repo conventions (PASS/FAIL markers, exit codes, isolated fixture paths, test worlds, AutoBind/LoadSavedDefaults fixtures, goldens in tools/fixtures). Use when adding coverage for a feature or bug fix.
---

# Scaffolding a test

## Conventions

- **No dispatcher, no base class, no registration.** Each test is a pair:
  - `tools/<PascalName>.cs`: the C# test (compiled into the game assembly; the csproj excludes only `prototypes/**`)
  - `tools/<snake_name>.gd`: `extends SceneTree`; it `preload`s the `.cs` file, runs it, and calls `quit(code)`
- **Markers:**
  - stdout gets `<TAG>=PASS`
  - failure is `<TAG>=FAIL <reason>`, written to stderr or via `push_error`
  - the wrapper adds `<TAG>_WRAPPER=PASS|FAIL`
  - exit codes: 0 = pass, 1 = fail
  - `=SKIP` + exit 0 only when the test genuinely cannot apply, e.g. GPU-only checks running headless
- Use `namespace RingworldTechDemo.Tools;` (some older smokes have no namespace).
- **Paths:**
  - settings fixtures go under `res://.godot/<name>_<usec>.tres`
  - disposable output goes through `RingGeneratedOutputPaths.File(category, name)` (C#) or `generated_output_paths.gd` `smoke_capture_path()` (GDScript), under `user://generated/`
  - never write to real user settings or real save slots
- **Goldens:** JSON in `tools/fixtures/<name>_golden.json`. A `--capture` flag writes the golden and **refuses to overwrite** an existing one. Store floats as bits for exact comparison (`EmitterSiteBaselineGoldenTest.cs`).
- **GPU tests:**
  - name them `*_gpu_test.gd`
  - put "Run (windowed Vulkan, not --headless; no other Godot process running)" in the header
  - guard with `if RenderingServer.get_rendering_device() == null: fail("requires Vulkan/Forward+")`
- Give every test that loads a world or awaits frames its own timeout:
  `create_timer(300.0).timeout.connect(func(): _fail("timeout"))`.
- Write a header comment with the purpose and the exact run command.

## Template A: logic test (RefCounted, `int Run`)

```csharp
using System; using System.Collections.Generic; using System.Linq; using Godot;
namespace RingworldTechDemo.Tools;

/// <summary>What it proves. Run: Godot --headless --path . --script res://tools/foo_test.gd. Prints FOO=PASS.</summary>
public partial class FooTest : RefCounted
{
    public int Run(bool capture)
    {
        var failures = new List<string>();
        void Check(bool ok, string message) { if (!ok) failures.Add(message); }
        try
        {
            // using RingTerrainSampler s = EmitterSiteTestWorlds.CreateGen17Sites(1337);
            // Node fixtures: add to (Engine.GetMainLoop() as SceneTree).Root, free in finally.
        }
        catch (Exception e) { Console.Error.WriteLine(e.ToString()); return 1; }
        if (failures.Count != 0) { Console.Error.WriteLine("FOO=FAIL " + string.Join(" | ", failures.Take(20))); return 1; }
        Console.WriteLine("FOO=PASS");
        return 0;
    }
}
```

```gdscript
extends SceneTree
# Run: Godot --headless --path . --script res://tools/foo_test.gd [-- --capture]
const TEST := preload("res://tools/FooTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TEST.new()
	var code: int = runner.Run(OS.get_cmdline_user_args().has("--capture"))
	runner = null
	if code == 0:
		print("FOO_WRAPPER=PASS")
	else:
		push_error("FOO_WRAPPER=FAIL exit=%d" % code)
	quit(code)
```

## Template B: settings persistence smoke (Node, `Result`)

- **C#:** `public partial class FooSettingsSmoke : Node` with `string Result = "RUNNING"` and `string IsolatedPath`.
  - `_Ready` wraps `Run(IsolatedPath)` in try/catch and sets `Result = "PASS"` or `"FAIL " + e.Message`.
  - `Require(bool, msg)` throws `InvalidOperationException`.
  - Coverage order: see the `live-tuning-setting` skill, section 3.
- **GDScript:**

```gdscript
extends SceneTree
const SMOKE := preload("res://tools/FooSettingsSmoke.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var path := "res://.godot/foo_settings_smoke_%d.tres" % Time.get_ticks_usec()
	_remove(path)
	var smoke = SMOKE.new()
	smoke.set("IsolatedPath", path)
	root.add_child(smoke)
	await process_frame
	var result := str(smoke.get("Result"))
	smoke.queue_free()
	_remove(path)
	if result == "PASS":
		print("FOO_SETTINGS=PASS")
		quit(0)
	else:
		push_error("FOO_SETTINGS=FAIL " + result)
		quit(1)

func _remove(p: String) -> void:
	var abs := ProjectSettings.globalize_path(p)
	if FileAccess.file_exists(abs):
		DirAccess.remove_absolute(abs)
```

## Fixture helpers to reuse

- **`tools/EmitterSiteTestWorlds.cs`:** `CreateGen17(seed)`, `CreateGen17Sites`, `CreateGen17Climate`, `CreateGen17Landform(seed, climate)`, `CreateGen17WeatherLung`, `CreateGen16(seed)`.
  - All of them return a `RingTerrainSampler` (IDisposable), so use `using`.
  - Constants: `Circumference = 31_415_936L`, `Width = 318_000`.
- **Subsystem nodes** expose `AutoBind = false` and `LoadSavedDefaults = false`, so fixtures avoid scene lookups and user files:
  `new EmitterGridBeamLayer { Name = "EmitterGrid", AutoBind = false, LoadSavedDefaults = false }`.
  The same exports exist on the eclipse, emitter and weather-lung streamers and layers.
- **`tools/AtmosphereVisualFixtureHelper.cs`** places the camera: `Place(explorer, around, vertical, across)`, plus deterministic `Move`.
- **`tools/shadow_harness_common.gd`** provides named GPU poses (`--poses=`), `go_pose()`, `wait_builds_idle`, `hold_world_clocks()` and `machine_info()`.
- **Menu tests:** build a minimal node fixture with the sibling names the drawer expects (`LiveTuningDrawer`, `GraphicsQuality`, `EmitterGrid`, `Terrain3DGameplay`, `StarLight`, `RingDayNightCycle`), and isolate preferences with `MinimapWindContractTest.InstallIsolatedPreferences`.
- **Shader fixtures:** `EmitterGridShaderFixture.cs`, `EclipseHubBeamShaderFixture.cs`.

## Register in the manifest (mandatory)

Add an entry to `tools/test_manifest.json`; `tools/testing/validate_manifest.ps1` fails for a test file without one. Fields:
- `kind`: `test` (auto-selectable) or `tool` (captures, probes; never auto-selected)
- `mode`: `headless`, `gpu`, or `gpu-local` (local RenderingDevice fixture, no world, parallel-safe)
- `tier`: `smoke` | `gate` | `slow` (over 120 s) | `extended` | `manual`
- `tags`: from the tag taxonomy in the manifest; add a tag with source globs when the feature brings new folders
- `marker`: the canonical name; the runner requires the line `^<NAME>=PASS`. Failure is `<NAME>=FAIL`, a skip `<NAME>=SKIP`. Mandatory.
- `expected_s` and `timeout_s`: measured wall seconds and a hard limit
- optional: `world`, `batch`, `args` / `user_args`, `profile`, `parallel_safe`

Copy the nearest existing entry, then check it:

```powershell
pwsh -File tools/testing/validate_manifest.ps1
.\tools\run_tests.ps1 -Tests <name> -DryRun
```

## Headless first (new features)

1. Split model from presentation: lifecycle, timing, geometry, budgets and policy in plain C# with no `Node`, `RenderingServer` or `Viewport`, tested by direct calls (pattern: `storm_lifecycle_code_test`).
2. Every tunable gets a headless settings smoke (`live-tuning-setting` skill).
3. Data contracts (snapshots, buffer layouts, texel packing, CPU references of GPU maps) get headless tests with goldens in `tools/fixtures`.
4. Shader logic is a `gpu-local` fixture compared with a CPU reference, not a world test (pattern: `cloud_density_fastpath_gpu_test`). Add RD shaders to `sunshine_shader_compile_gpu_test`'s list.
5. One world integration case per feature, with no fixed timers (use settled/build counters).
6. Captures and ffmpeg sheets are `manual` tier evidence, not gates.
7. Be deterministic: fixed seeds, paused clocks, no wall-clock frame-rate reliance.
8. Budgets: headless 10 s or less, `gpu-local` 5 s or less, world case body 15 s or less; slower is tier `slow` with a reason.

## After writing

1. Build with `./tools/use_godot_48.ps1 -Build`. Godot generates the `.uid` sidecars on import or launch; commit them along with the files.
2. Run the test via the `godot-test-run` skill (`tools/run_tests.ps1 -Tests <name>`).
3. Prove it **fails** without the fix: temporarily break the assertion or input, then revert. Never use `git stash`.
4. Note the run command in the feature's doc or checklist.
