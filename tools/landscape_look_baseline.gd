extends SceneTree

## Landscape T0-7 look references (docs/landscape-audit-t0-spec.md WO T0-7): the nine t0 poses at noon and sun_8_set, keeping AA as configured. Writes <pose>__<time>.png, _bands.png, look.json, reference_set.json and the preferences snapshot to .godot/audit_baseline/<machine>/t0/look/<stamp>/. PASS needs every view non-black, the world verified, sun_8_set within 0.5 degrees of 8, streaming idle.
## RUN (windowed Vulkan, nothing else on the GPU): tools/run_tests.ps1 -Label landscape_gpu -Tests landscape_look_baseline
##   or Godot_console.exe --path . --resolution 1920x1080 --script res://tools/landscape_look_baseline.gd -- [--poses=a,b] [--prefs=user|defaults|<dir>] [--preset=medium|keep] [--allow-blades] [--times=noon,sun_8_set]
##   Add --generation=N [--review-features=a,b] after -- to select a v9 world.
##   T4-2: --poses= also takes the dressing poses (steep_eye, scree_eye, cliff_eye, breaks_300m, mountain_2km, lowland_eye from tools/fixtures/landscape_poses_dressing_s1337.json, and river_bank_eye in a
##   --generation=1 --review-features=water.rivers world); a run that names one pins the weather over the T0 poses plus all dressing poses. --quiet hides clouds, grass and gust debris for pixel-comparable stills.
##   --feature-off=<feature id|bit:N> runs the whole look with that world feature's bit cleared in the shader mask (same boot; needs a review world that has the feature).
## Machine rule: numbers are only authoritative on the A1000 laptop (P0 / 35 W); on any other GPU the output is stamped "non-authoritative" with the machine tag and adapter name.
## Test-only. The harness world directory is deleted at exit. Prints LANDSCAPE_LOOK=PASS.
const BASE := preload("res://tools/landscape_baseline_common.gd")
const MARKER := "LANDSCAPE_LOOK"

var ctx := {}
var finished := false


func _init() -> void:
	call_deferred("_run")


func _run() -> void:
	var watchdog := create_timer(2300.0)
	watchdog.timeout.connect(func() -> void: _fail("watchdog timeout (2300.0 s)"))
	ctx = await BASE.setup(self, MARKER)
	if ctx.is_empty() or not ctx.has("ok"):
		_finish_failed()
		return
	var report: Dictionary = await BASE.run_look(ctx)
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
	quit(1) # quit first: it only takes effect at the end of the frame, so a failing cleanup cannot leave the run hanging
	BASE.cleanup(ctx)

