extends SceneTree

## Landscape T0-7 cost baseline (docs/landscape-audit-t0-spec.md WO T0-7): 11 pose-times (nine t0 poses at noon, hills_eye and forest_edge_eye at sun_8_set), fill CPU scopes then baseline / near_terrain_hidden / far_terrain_hidden / trees_hidden / grass_hidden / baseline_repeat A-B-A windows (second round reversed), root and far viewport GPU ms, CPU ms, draw calls, primitives, compute markers, optional opaque bracket, power log. Writes cost.json, cost.md, power_log.csv and the preferences snapshot to .godot/audit_baseline/<machine>/t0/cost/<stamp>/. PASS needs every case state verified, every hide class effective at some pose, and at least 80 % power-valid windows (always on a non-A1000 GPU).
## RUN (windowed Vulkan, nothing else on the GPU): tools/run_tests.ps1 -Label landscape_gpu -Tests landscape_cost_baseline
##   or Godot_console.exe --path . --resolution 1920x1080 --script res://tools/landscape_cost_baseline.gd -- [--poses=a,b] [--prefs=user|defaults|<dir>] [--preset=medium|keep] [--allow-blades] [--rounds=2] [--seconds=8] [--no-bracket]
##   A1000 (authoritative) run: laptop, undocked, P0 / 35 W, memory clock 5300-5700 MHz; the same command.
## Machine rule: numbers are only authoritative on the A1000 laptop (P0 / 35 W); on any other GPU the output is stamped "non-authoritative" with the machine tag and adapter name.
## Test-only. The harness world directory is deleted at exit. Prints LANDSCAPE_COST=PASS.
const BASE := preload("res://tools/landscape_baseline_common.gd")
const MARKER := "LANDSCAPE_COST"

var ctx := {}
var finished := false


func _init() -> void:
	call_deferred("_run")


func _run() -> void:
	var watchdog := create_timer(3500.0)
	watchdog.timeout.connect(func() -> void: _fail("watchdog timeout (3500.0 s)"))
	ctx = await BASE.setup(self, MARKER)
	if ctx.is_empty() or not ctx.has("ok"):
		_finish_failed()
		return
	var report: Dictionary = await BASE.run_cost(ctx)
	if str(report.get("status", "FAIL")) != "PASS":
		var failures: Array = report.get("failures", [])
		_fail(str(report.get("failure", "; ".join(failures) if not failures.is_empty() else "no result")))
		return
	finished = true
	BASE.cleanup(ctx)
	print("%s=PASS %s" % [MARKER, ctx["machine"]["label"]])
	quit(0)


func _finish_failed() -> void:
	if finished:
		return
	finished = true
	BASE.cleanup(ctx)
	quit(1)


func _fail(message: String) -> void:
	if finished:
		return
	finished = true
	push_error("%s=FAIL %s" % [MARKER, message])
	BASE.cleanup(ctx)
	quit(1)
