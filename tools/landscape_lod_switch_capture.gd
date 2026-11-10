extends SceneTree

## Landscape T0-7 tree LOD-switch capture (docs/landscape-audit-t0-spec.md WO T0-7): at forest_edge_eye (--pose=<name> overrides) finds the nearest large tree ahead, holds the camera at eye height looking at its trunk and dollies 15 to 60 m in 0.25 m steps (180 frames), then a 180-frame control at a fixed 15 m. Writes frame%08d.png to .godot/captures/landscape/t0/lod_switch/<stamp>/{dolly,control}/ and frames.json; make the sheets and flicker reports with tools/media/ffmpeg_tools.ps1 (see the spec) and delete the frames afterwards.
## RUN (windowed Vulkan, nothing else on the GPU): tools/run_tests.ps1 -Label landscape_gpu -Tests landscape_lod_switch_capture
##   or Godot_console.exe --path . --resolution 1920x1080 --script res://tools/landscape_lod_switch_capture.gd -- [--poses=a,b] [--prefs=user|defaults|<dir>] [--preset=medium|keep] [--allow-blades] [--pose=forest_edge_eye]
## Machine rule: numbers are only authoritative on the A1000 laptop (P0 / 35 W); on any other GPU the output is stamped "non-authoritative" with the machine tag and adapter name.
## Test-only. The harness world directory is deleted at exit. Prints LANDSCAPE_LOD_SWITCH=PASS.
const BASE := preload("res://tools/landscape_baseline_common.gd")
const MARKER := "LANDSCAPE_LOD_SWITCH"

var ctx := {}
var finished := false


func _init() -> void:
	call_deferred("_run")


func _run() -> void:
	var watchdog := create_timer(1150.0)
	watchdog.timeout.connect(func() -> void: _fail("watchdog timeout (1150.0 s)"))
	ctx = await BASE.setup(self, MARKER)
	if ctx.is_empty() or not ctx.has("ok"):
		_finish_failed()
		return
	var report: Dictionary = await BASE.run_lod(ctx)
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
