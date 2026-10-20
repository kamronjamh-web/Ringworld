extends SceneTree

## Shadow revamp S4c budget report (docs/shadow-revamp-plan.md S4): cost of the terrain height shadow producer in the real scene.
## Per sun elevation window (noon, 30, 10, 5, 3 degrees by default; the day/night clock RUNS so the sun really moves and the producer rebuilds every sun step):
##   GPU ms per full build (sum of the render thread's own timestamp pairs around every slice), GPU ms per frame averaged over the window (all builds / frames),
##   worst single slice, frames per build, rows per frame chosen by the adaptive slicing rule, main-thread ms per frame, render-callback CPU ms, builds per window.
## Plus the CPU fill cost (first full fill and the recentre strips; Debug C# build unless you run a Release build) and the first-publish latency.
## MACHINE RULE: authoritative baseline is the A1000 (P0 / 35 W). Everything else is stamped `machine: <tag> (non-authoritative)`; the 4070 numbers only show the shape.
## RUN (windowed Vulkan, no other Godot process, editor closed):  Godot_console.exe --path . --script res://tools/height_shadow_budget_test.gd [-- flags]
## Flags: --seconds=S per elevation window (default 60 for noon and the 3-5 degree windows, 20 for the others; --seconds overrides all), --elevations=noon,30,10,5,3,
##   --rows=N (RowsPerFrame, default 64), --budget=MS (default 0.15), --out=<dir>, --res=WxH (default 1920x1080), --no-vsync-off.
## Output: <out>/height_shadow/<stamp>/report.json and the console table. Default out is .godot/shadow_baseline/<machine tag>/.

const COMMON := preload("res://tools/shadow_harness_common.gd")

var game: Node
var node: Node
var graphics: Node
var cycle: Node
var report := {}


func _init() -> void:
	call_deferred("_run")


func _fail(message: String) -> void:
	push_error("HEIGHT_SHADOW_BUDGET=FAIL " + message)
	quit(1)


func _diag() -> Dictionary:
	return node.call("GetDiagnostics")


func _run() -> void:
	if RenderingServer.get_rendering_device() == null:
		_fail("RenderingDevice is required (windowed Vulkan)")
		return
	var machine: Dictionary = COMMON.machine_info()
	print("HEIGHT_SHADOW_BUDGET ", machine["label"], " adapter=", machine["adapter"])
	if not COMMON.has_flag("--no-vsync-off"):
		DisplayServer.window_set_vsync_mode(DisplayServer.VSYNC_DISABLED)
		Engine.max_fps = 0
	var res := COMMON.arg_value("--res=", "1920x1080").split("x")
	if res.size() == 2:
		root.size = Vector2i(int(res[0]), int(res[1]))
	var stamp := Time.get_datetime_string_from_system(false).replace(":", "-").replace("T", "_")
	var out_dir: String = COMMON.output_dir(machine, "height_shadow/" + stamp)
	var elevations: PackedStringArray = COMMON.csv_arg("--elevations=")
	if elevations.is_empty():
		elevations = PackedStringArray(["noon", "30", "10", "5", "3"])
	game = await COMMON.create_game(self, "res://.godot/height-shadow-budget/%d" % Time.get_ticks_usec())
	if game == null:
		_fail("game boot failed")
		return
	node = game.get_node_or_null("TerrainHeightShadow")
	graphics = game.get_node_or_null("GraphicsQuality")
	cycle = game.get_node_or_null("RingDayNightCycle")
	var surface := game.get_node_or_null("Terrain3DGameplay")
	var explorer := game.get_node_or_null("Explorer")
	if node == null or graphics == null or cycle == null or surface == null or explorer == null:
		_fail("required nodes missing")
		return
	var monitor := game.get_node_or_null("RingPerformanceMonitor")
	if monitor != null:
		monitor.process_mode = Node.PROCESS_MODE_DISABLED
	COMMON.hold_world_clocks(game)
	var atmosphere := game.get_node_or_null("DaySideAtmosphere")
	if atmosphere != null:
		atmosphere.set("storm_auto_exposure_enabled", false)
	var spawn_height := float(surface.call("SampleHeight", COMMON.FIXTURE_AROUND, COMMON.FIXTURE_ACROSS)) + 1.1
	explorer.call("SetCanonicalSpawnMeters", COMMON.FIXTURE_AROUND, spawn_height, COMMON.FIXTURE_ACROSS)
	explorer.call("SetPresentationView", 0.0, -10.0)
	await COMMON.wait_builds_idle(self, game, 180.0, 60)
	var base: Dictionary = COMMON.pose_now(game)

	report = {"tool": "height_shadow_budget_test", "machine": machine["label"], "machine_info": machine, "authoritative": machine["authoritative"], "godot": machine["godot"],
		"csharp_build": "Debug unless the project was built -c Release", "args": OS.get_cmdline_user_args(), "windows": []}
	graphics.call("SetShadowTuningOverride", "HeightShadowRowsPerFrame", int(COMMON.arg_value("--rows=", "64")))
	graphics.call("SetShadowTuningOverride", "HeightShadowBudgetMs", float(COMMON.arg_value("--budget=", "0.15")))
	report["settings"] = {"rows_per_frame": int(COMMON.arg_value("--rows=", "64")), "budget_ms": float(COMMON.arg_value("--budget=", "0.15"))}

	# First pose, producer on: measure the first full fill and the latency to the first publish.
	var first_pose := "forest_noon" if elevations[0] == "noon" else "dawn_5deg"
	var pose: Dictionary = await COMMON.go_pose(self, game, base, first_pose, 2.0, 120.0)
	if pose.has("error"):
		_fail("pose: " + str(pose["error"]))
		return
	var t_enable := Time.get_ticks_msec()
	graphics.call("SetShadowTuningOverride", "HeightShadowEnabled", true)
	var deadline := t_enable + 300000
	while int(_diag()["publishes"]) < 1 and Time.get_ticks_msec() < deadline:
		await process_frame
	var d := _diag()
	if int(d["publishes"]) < 1:
		_fail("no publish within 300 s: " + str(d["status"]))
		return
	report["first_publish"] = {"seconds_since_enable": (Time.get_ticks_msec() - t_enable) / 1000.0, "fill_seconds": d["fill_seconds"], "fill_texels": d["fill_texels"], "fill_rects": d["fill_rects"],
		"build_frames": d["build_frames"], "main_thread_ms_avg": d["main_thread_ms_avg"]}
	print("HEIGHT_SHADOW_BUDGET first publish %.1f s after enabling; first fill %.2f s for %s texels" % [report["first_publish"]["seconds_since_enable"], float(d["fill_seconds"]), d["fill_texels"]])

	var window_default := float(COMMON.arg_value("--seconds=", "0"))
	for name in elevations:
		var spec: Dictionary = {"mode": "noon"} if name == "noon" else {"mode": "elevation", "value": float(name), "rising": true}
		var pose_name := "forest_noon" if name == "noon" else ("dawn_%ddeg" % int(float(name)) if COMMON.POSES.has("dawn_%ddeg" % int(float(name))) else "")
		if pose_name != "":
			var info: Dictionary = await COMMON.go_pose(self, game, base, pose_name, 1.0, 120.0)
			if info.has("error"):
				print("HEIGHT_SHADOW_BUDGET window %s skipped: %s" % [name, str(info["error"])])
				continue
		else:
			var applied: Dictionary = COMMON.apply_time(game, spec)
			if applied.has("error"):
				print("HEIGHT_SHADOW_BUDGET window %s skipped: %s" % [name, str(applied["error"])])
				continue
		# Settle: wait for the pending job to publish and for the crossfade to end, then run the clock.
		var settle_end := Time.get_ticks_msec() + 120000
		await process_frame
		while (str(_diag()["phase"]) != "Idle" or float(_diag()["crossfade"]) < 1.0) and Time.get_ticks_msec() < settle_end:
			await process_frame
		var seconds := window_default if window_default > 0.0 else (60.0 if name in ["noon", "5", "3"] else 20.0)
		var elevation_start := float(cycle.get("SunElevationDegrees"))
		var before := _diag()
		cycle.set("Paused", false)
		var frames := 0
		var t0 := Time.get_ticks_msec()
		var frame_ms: Array = []
		var last := Time.get_ticks_usec()
		while (Time.get_ticks_msec() - t0) < int(seconds * 1000.0):
			await process_frame
			frames += 1
			var now := Time.get_ticks_usec()
			frame_ms.append(float(now - last) / 1000.0)
			last = now
		cycle.set("Paused", true)
		var elevation_end := float(cycle.get("SunElevationDegrees"))
		await process_frame
		await process_frame
		var after := _diag()
		var builds := int(after["builds"]) - int(before["builds"])
		var gpu_total := float(after["gpu_ms_total"]) - float(before["gpu_ms_total"])
		var window := {"elevation_start_deg": elevation_start, "elevation_end_deg": elevation_end, "label": name, "seconds": seconds, "frames": frames, "builds": builds, "publishes": int(after["publishes"]) - int(before["publishes"]),
			"gpu_ms_total": gpu_total, "gpu_ms_per_frame_avg": gpu_total / maxf(1.0, float(frames)), "gpu_ms_per_build_avg": gpu_total / maxf(1.0, float(builds)),
			"gpu_ms_last_build": after["gpu_ms_last_build"], "gpu_ms_max_slice": after["gpu_ms_max_slice"], "build_frames_last": after["build_frames"], "rows_per_frame_last": after["rows_per_frame"],
			"gpu_ms_per_row_l0": after["gpu_ms_per_row_l0"], "gpu_ms_per_row_l1": after["gpu_ms_per_row_l1"], "render_callback_cpu_ms_max": after["render_callback_cpu_ms_max"],
			"main_thread_ms_avg": after["main_thread_ms_avg"], "frame_ms_median": COMMON.stats(frame_ms).get("median", 0.0), "fills_during": int(after["fills"]) - int(before["fills"]), "fill_seconds_last": after["fill_seconds"]}
		report["windows"].append(window)
		print("HEIGHT_SHADOW_BUDGET window %-5s sun %.1f->%.1f deg: %d builds, GPU %.4f ms/frame avg (%.3f ms/build, worst slice %.4f ms), %s rows/frame, %s frames/build, main %.3f ms, frame %.2f ms" % [
			name, elevation_start, elevation_end, builds, window["gpu_ms_per_frame_avg"], window["gpu_ms_per_build_avg"], float(after["gpu_ms_max_slice"]), after["rows_per_frame"], after["build_frames"], float(after["main_thread_ms_avg"]), window["frame_ms_median"]])

	var file := FileAccess.open(out_dir.path_join("report.json"), FileAccess.WRITE)
	if file != null:
		file.store_string(JSON.stringify(report, "\t"))
		file.close()
	print("HEIGHT_SHADOW_BUDGET report: ", out_dir.path_join("report.json"))
	print("HEIGHT_SHADOW_BUDGET=DONE ", machine["label"])
	quit(0)
