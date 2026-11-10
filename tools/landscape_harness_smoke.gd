extends SceneTree

## Landscape audit T0-1 GPU smoke (docs/landscape-audit-t0-spec.md WO T0-1): boots the pinned generation 1 world through tools/landscape_harness_common.gd,
## verifies it (recipe + 64 sample points against the in-game sampler), goes to plains_eye at noon, captures, computes the distance-band table and writes
## smoke.png, smoke_bands.png and smoke.json to .godot/captures/landscape/t0/smoke/<stamp>/.
## RUN (windowed Vulkan, nothing else on the GPU): tools/run_tests.ps1 -Label landscape_gpu -Tests landscape_harness_smoke
##   or Godot_console.exe --path . --resolution 1920x1080 --script res://tools/landscape_harness_smoke.gd -- [--prefs=user|defaults|<dir>] [--preset=medium|keep] [--allow-blades]
## PASS needs: world verified, ground check holds, >= 3 non-empty distance bands, mean linear luma > 0.02, streaming idle. Prints LANDSCAPE_HARNESS_SMOKE=PASS.
## Test-only. The harness world directory is deleted at exit.
const HARNESS := preload("res://tools/landscape_harness_common.gd")
const BANDS := preload("res://tools/landscape_audit_bands.gd")
const MARKER := "LANDSCAPE_HARNESS_SMOKE"

var booted := {}
var finished := false


func _init() -> void:
	call_deferred("_run")


func _run() -> void:
	var watchdog := create_timer(900.0)
	watchdog.timeout.connect(func() -> void: _fail("watchdog timeout (900 s)"))
	if RenderingServer.get_rendering_device() == null:
		_fail("RenderingDevice is required")
		return
	DisplayServer.window_set_vsync_mode(DisplayServer.VSYNC_DISABLED)
	Engine.max_fps = 0
	var res := HARNESS.COMMON.arg_value("--res=", "1920x1080").split("x")
	if res.size() == 2:
		root.size = Vector2i(int(res[0]), int(res[1]))
	var machine: Dictionary = HARNESS.COMMON.machine_info()
	print("%s %s adapter=%s" % [MARKER, machine["label"], machine["adapter"]])

	booted = await HARNESS.boot(self, MARKER)
	if booted.is_empty():
		_finish_failed()
		return
	var game: Node = booted["game"]
	var world: Dictionary = booted["world"]
	print("%s world verified: %s" % [MARKER, str(world.get("message", ""))])
	print("%s prefs: %s" % [MARKER, str(booted["prefs"])])
	var monitor := game.get_node_or_null("RingPerformanceMonitor")
	if monitor != null:
		monitor.process_mode = Node.PROCESS_MODE_DISABLED
	HARNESS.COMMON.hold_world_clocks(game)
	var world_environment := game.get_node_or_null("WorldEnvironment") as WorldEnvironment
	if world_environment == null:
		_fail("WorldEnvironment missing")
		return
	var preset: Dictionary = await HARNESS.apply_preset(self, game)
	if preset.has("error"):
		_fail(str(preset["error"]))
		return
	var depth_effect := HARNESS.install_depth_effect(world_environment)

	var pose := HARNESS.pose_by_name("plains_eye")
	if pose.is_empty():
		_fail("fixture pose plains_eye missing")
		return
	var info: Dictionary = await HARNESS.go_pose(self, game, pose, "noon")
	if info.has("error"):
		_fail(str(info["error"]))
		return
	var attributes := world_environment.camera_attributes as CameraAttributesPractical
	if attributes != null and attributes.auto_exposure_enabled:
		attributes.auto_exposure_enabled = false
		await HARNESS.COMMON.settle(self, 0.5)
	var shot: Dictionary = await HARNESS.capture(self, depth_effect)
	var image: Image = shot["image"]
	var w := image.get_width()
	var h := image.get_height()
	var distance := BANDS.distance_from_depth(shot["depth"], w, h)
	var table := BANDS.compute(image, distance, w, h, 2)
	var out_dir := HARNESS.transient_dir("smoke")
	image.save_png(out_dir.path_join("smoke.png"))
	BANDS.band_image(distance, w, h).save_png(out_dir.path_join("smoke_bands.png"))

	var total_luma := 0.0
	var weight := 0.0
	for row in table:
		if int(row["pixels"]) > 0:
			total_luma += float(row["mean_luma"]) * float(row["share"])
			weight += float(row["share"])
	var mean_luma := total_luma / maxf(weight, 0.000001)
	var non_empty := BANDS.non_empty_bands(table)
	var report := {"marker": MARKER, "machine": machine["label"], "adapter": machine["adapter"], "world": world, "prefs": booted["prefs"], "preset": preset, "pose": info,
		"depth_available": not (shot["depth"] as Dictionary).is_empty(), "size": [w, h], "band_table": table, "adjacent_steps": BANDS.adjacent_steps(table),
		"non_empty_bands": non_empty, "mean_luma": mean_luma, "streaming_idle": bool(info.get("streaming_idle", false)) and bool(info.get("streaming_idle_after", false)),
		"status": "RUNNING"}
	var failure := ""
	if not bool(info["ground"]["ok"]):
		failure = "ground check failed"
	elif non_empty < 3:
		failure = "only %d non-empty distance bands (depth available: %s)" % [non_empty, str(report["depth_available"])]
	elif mean_luma <= 0.02:
		failure = "mean linear luma %.4f is not above 0.02" % mean_luma
	elif not bool(report["streaming_idle"]):
		failure = "streaming did not go idle"
	report["status"] = "FAIL" if failure != "" else "PASS"
	if failure != "":
		report["failure"] = failure
	var file := FileAccess.open(out_dir.path_join("smoke.json"), FileAccess.WRITE)
	if file != null:
		file.store_string(JSON.stringify(report, "\t"))
		file.close()
	print("%s output: %s" % [MARKER, out_dir])
	print("%s bands non-empty=%d mean_luma=%.4f sun_elevation=%.2f ground=%s" % [MARKER, non_empty, mean_luma, float(info.get("sun_elevation_deg", NAN)), str(info["ground"])])
	if failure != "":
		_fail(failure)
		return
	_cleanup()
	finished = true
	print("%s=PASS %s" % [MARKER, machine["label"]])
	quit(0)


func _cleanup() -> void:
	if booted.has("game") and is_instance_valid(booted["game"]):
		(booted["game"] as Node).queue_free()
	if booted.has("fixture_root"):
		HARNESS.remove_dir_recursive(ProjectSettings.globalize_path(str(booted["fixture_root"])))


func _finish_failed() -> void:
	_cleanup()
	quit(1)


func _fail(message: String) -> void:
	if finished:
		return
	finished = true
	push_error("%s=FAIL %s" % [MARKER, message])
	_cleanup()
	quit(1)
