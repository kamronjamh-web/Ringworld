extends SceneTree

## TEST ONLY (MIST-1 evidence). For each pose: a picture and an A-B-A GPU cost window with the static mist as it was ("old", emulated by tools/MistAbHelper.cs) and as it is now ("new").
## RUN: Godot_console.exe --path . --resolution 1920x1080 --script res://tools/mist_ab_capture.gd -- --prefs=user --preset=keep --poses=coast_eye,forest_edge_eye [--rounds=3 --seconds=6] [--weather=1.0] [--no-cost]
## Aerosol, mist density and mist height are the user's file values (0.35, 1.02, 420) unless given as --aerosol= --mist-density= --mist-height=.
## Output: .godot/mist1/<label>/<pose>__<case>.png and report.json. Prints MIST_AB=PASS.
const BASE := preload("res://tools/landscape_baseline_common.gd")
const H := preload("res://tools/landscape_harness_common.gd")
const COMMON := H.COMMON
const TIMING := preload("res://tools/LandscapeTimingBridge.cs")
const AB := preload("res://tools/MistAbHelper.cs")
const MARKER := "MIST_AB"

var ctx := {}
var finished := false


func _init() -> void:
	call_deferred("_run")


func _run() -> void:
	var watchdog := create_timer(2400.0)
	watchdog.timeout.connect(func() -> void: _fail("watchdog timeout"))
	ctx = await BASE.setup(self, MARKER)
	if ctx.is_empty() or not ctx.has("ok"):
		_fail("setup failed")
		return
	var label := BASE.harness_arg("--label=", "run")
	var out_dir := ProjectSettings.globalize_path("res://.godot/mist1/" + label)
	DirAccess.make_dir_recursive_absolute(out_dir)
	var aerosol := float(BASE.harness_arg("--aerosol=", "0.35"))
	var mist_density := float(BASE.harness_arg("--mist-density=", "1.02"))
	var mist_height := float(BASE.harness_arg("--mist-height=", "420"))
	var weather := float(BASE.harness_arg("--weather=", "0"))
	var rounds := maxi(1, int(BASE.harness_arg("--rounds=", "3")))
	var seconds := maxf(1.0, float(BASE.harness_arg("--seconds=", "6")))
	var cost := not BASE.harness_flag("--no-cost")
	var ab := AB.new()
	# The natural post-rain mist would republish over the A/B states; freeze it.
	for node in BASE._all_nodes(ctx["game"]):
		if node.get_script() != null and str((node.get_script() as Script).resource_path).ends_with("RingPostRainMist.cs"):
			node.process_mode = Node.PROCESS_MODE_DISABLED
	var depth_effect := H.install_depth_effect(ctx["world_environment"])
	BASE._viewports(ctx)
	var timing: RefCounted = TIMING.new()
	timing.call("Begin", true)
	ctx["timing"] = timing
	await COMMON.settle(self, 1.5)
	var report := {"label": label, "weather_mist": weather, "aerosol": aerosol, "mist_density": mist_density, "mist_height": mist_height, "poses": {}}
	var first := true
	for pose in BASE.t0_poses():
		var info: Dictionary = await H.go_pose(self, ctx["game"], pose, "noon", ctx["ground_model"])
		if info.has("error"):
			_fail("%s: %s" % [pose["name"], str(info["error"])])
			return
		if first:
			await BASE._disable_auto_exposure(ctx)
			first = false
		var entry := {"ground_m": float(pose["ground_m"]), "cases": {}}
		report["poses"][str(pose["name"])] = entry
		for case_name in ["new", "old", "new_repeat"]:
			ab.call("SetMist", case_name == "old", aerosol, mist_density, mist_height, weather)
			await COMMON.settle(self, 1.0)
			var record := {"model": ab.call("State")}
			if case_name != "new_repeat":
				var shot: Dictionary = await H.capture(self, depth_effect)
				(shot["image"] as Image).save_png(out_dir.path_join("%s__%s.png" % [pose["name"], case_name]))
			if cost:
				var windows: Array = []
				for r in rounds:
					var w: Dictionary = await BASE.sample_window(ctx, seconds)
					windows.append({"root_gpu_median": float(w["root_gpu_ms"].get("median", NAN)), "all_gpu_median": float(w["all_gpu_ms"].get("median", NAN)), "frame_median": float(w["frame_ms"].get("median", NAN)), "n": int(w["samples"])})
				record["windows"] = windows
			entry["cases"][case_name] = record
			print("%s %s %s %s" % [MARKER, pose["name"], case_name, JSON.stringify(record)])
	ab.call("SetMist", false, aerosol, mist_density, mist_height, 0.0)
	var file := FileAccess.open(out_dir.path_join("report.json"), FileAccess.WRITE)
	file.store_string(JSON.stringify(report, "\t"))
	file.close()
	finished = true
	BASE.cleanup(ctx)
	print("%s=PASS" % MARKER)
	quit(0)


func _fail(message: String) -> void:
	if finished:
		return
	finished = true
	push_error("%s=FAIL %s" % [MARKER, message])
	BASE.cleanup(ctx)
	quit(1)
