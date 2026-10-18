extends SceneTree

# Grass motion capture: still vs camera yaw vs walking, crossed with grass and cloud ablations, in the real
# Certified gameplay scene with the current user preferences (copied privately by FarRingGameplayCapture.Prepare).
# Windowed only (needs RenderingDevice). Run one Godot process at a time; numbers are only comparable on the
# machine that took them.
#
#   Godot_v4.8-dev7_mono_win64_console.exe --path <repo> --script res://tools/grass_motion_capture.gd -- \
#     --detailed-gpu --warmup-seconds 30 --collection-seconds 8 --settle-seconds 2 \
#     --grass-cases=baseline,grass-off,cull-tiles-off,skip-unchanged-off,interleave-off \
#     --grass-motions=still,yaw45,walk5 [--noon] [--fill-timeout=300]
#
# Output: res://.godot/grass-motion/<label>-<pid>-<time>/report.json, plus one GRASS_MOTION_PHASE line per phase.

const SUPPORTED_CASES := ["baseline", "throttle-off", "grass-off", "cull-tiles-off", "skip-unchanged-off", "interleave-off", "interleave-motion-200"]
const SUPPORTED_MOTIONS := ["still", "yaw45", "yaw15", "walk5"]
const GRASS_COUNTERS := ["GpuCullingCompletedUpdates", "GpuCullingSkippedUnchangedUpdates", "GrassVisibilityScans", "GrassVisibilityCacheHits",
	"GrassGpuRequestScans", "GrassGpuRequestCacheHits", "GrassPresentationScans", "GrassPresentationCacheHits", "GrassBuildNewTileCount",
	"GrassBuildTierUpgradeCount", "GrassBuildTierOtherCount", "GrassTierFilterCount", "GrassTilesEvictedCount", "GrassResetCount", "GrassCullingRelatches"]
const GRASS_GAUGES := ["ResidentGrassTileCount", "PendingGrassTileBuildCount", "PendingGrassUploadCount", "GpuCullingBatchCount",
	"GpuCullingRequestedBatchCount", "GpuCullingDispatchedBatchCount", "GpuCullingDeferredBatchCount", "EffectiveGpuCullingBatchesPerFrame",
	"GrassFrustumRejectedTileCount", "GrassDistanceRejectedTileCount", "AdaptiveGrassActivePresentationCount", "AdaptiveGrassGroupCount",
	"ActiveGrassInteractionSampleCount", "IsCardStyle", "GpuCullingBytes"]

var world: Node
var driver: RefCounted
var probe: RefCounted
var finished := false
var clock_restore: Callable
var case_restore: Callable
var output_directory := ""
var phases: Array = []
var motion := "still"
var motion_elapsed := 0.0
var base_yaw_degrees := 0.0
var base_pitch_degrees := 0.0
var walk_speed := 5.0
# The far-ring capture pose hovers ~350 m above terrain (no grass). Drop 340 m (as grass_cost_gameplay_capture does),
# then let physics land the explorer so the walk starts on the ground; this is the offset every reset restores.
const POSE_DROP_METERS := -340.0
var pose_offset := POSE_DROP_METERS
var vsync_restore: Callable
var capture_vsync := -1

func _initialize() -> void:
	call_deferred("_run")

func _arg(name: String, fallback: String) -> String:
	for argument in OS.get_cmdline_user_args():
		if argument.begins_with(name + "="): return argument.trim_prefix(name + "=")
	return fallback

func _run() -> void:
	if DisplayServer.get_name().to_lower().contains("headless") or RenderingServer.get_rendering_device() == null:
		_fail("Forward+ display/RenderingDevice required")
		return
	driver = load("res://tools/FarRingGameplayCapture.cs").new()
	probe = load("res://tools/GrassMotionCaptureProbe.cs").new()
	if not driver.ConfigureArguments():
		_fail(driver.ErrorText)
		return
	var cases := _arg("--grass-cases", "baseline,grass-off,cull-tiles-off,skip-unchanged-off,interleave-off").split(",", false)
	var motions := _arg("--grass-motions", "still,yaw45,walk5").split(",", false)
	for name in cases:
		if not SUPPORTED_CASES.has(name):
			_fail("Unsupported case " + name)
			return
	for name in motions:
		if not SUPPORTED_MOTIONS.has(name):
			_fail("Unsupported motion " + name)
			return
	var fill_timeout := _arg("--fill-timeout", "300").to_float()
	var phase_count := cases.size() * motions.size() + 1
	var watchdog: float = driver.WarmupSeconds + fill_timeout + phase_count * (driver.CollectionSeconds + driver.SettleSeconds + 45.0) + 120.0
	create_timer(watchdog, true, false, true).timeout.connect(func(): _fail("watchdog expired"))
	output_directory = "res://.godot/grass-motion/%s-%d-%d" % [driver.ArtifactLabel, OS.get_process_id(), Time.get_unix_time_from_system()]
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(output_directory))
	world = load("res://scenes/VoxelRingIntegrationTest.tscn").instantiate()
	var launcher = load("res://scenes/WorldSizeLauncher.tscn").instantiate()
	root.add_child(launcher)
	launcher.call("_on_certified_preset_pressed")
	launcher.call("_configure_game", world, 5000000.0)
	launcher.free()
	# Prepare refuses directories outside .godot/far-ring-gameplay; its private preference copies live there.
	var prepare_directory := "res://.godot/far-ring-gameplay/grass-motion-%d-%d" % [OS.get_process_id(), Time.get_unix_time_from_system()]
	if not driver.Prepare(world, prepare_directory):
		_fail(driver.ErrorText)
		return
	root.add_child(world)
	await process_frame
	if not driver.SetPose(world):
		_fail(driver.ErrorText)
		return
	Input.mouse_mode = Input.MOUSE_MODE_VISIBLE
	if not driver.SetPoseAltitudeOffset(world, POSE_DROP_METERS):
		_fail(driver.ErrorText)
		return
	# Uncapped frame rate so fps/frame-time differences are visible (pass --vsync to keep the user's mode).
	if not "--vsync" in OS.get_cmdline_user_args():
		var previous_vsync := DisplayServer.window_get_vsync_mode()
		var previous_max_fps := Engine.max_fps
		vsync_restore = func():
			DisplayServer.window_set_vsync_mode(previous_vsync)
			Engine.max_fps = previous_max_fps
		DisplayServer.window_set_vsync_mode(DisplayServer.VSYNC_DISABLED)
		Engine.max_fps = 0
	capture_vsync = DisplayServer.window_get_vsync_mode()
	var explorer = world.get_node("Explorer")
	base_yaw_degrees = rad_to_deg(float(explorer.SavedYaw))
	base_pitch_degrees = float(explorer.SavedPitch)
	if "--noon" in OS.get_cmdline_user_args(): _select_noon()
	var grass = world.get_node("StandingGrass")
	print("GRASS_MOTION=WARMUP seconds=", driver.WarmupSeconds, " cards=", grass.get("IsCardStyle"), " altitude=", explorer.CurrentAltitudeAboveTerrainMeters)
	await create_timer(maxf(.001, driver.WarmupSeconds), true, false, true).timeout
	if finished: return
	# Land after warmup, once terrain collision around the pose has streamed in.
	if not await _land_explorer(explorer): return
	var fill_started := Time.get_ticks_msec()
	if not await _await_grass_quiescent(fill_timeout):
		_fail("grass streaming did not settle within %s s" % fill_timeout)
		return
	print("GRASS_MOTION=FILLED seconds=", (Time.get_ticks_msec() - fill_started) / 1000.0, " resident=", grass.get("ResidentGrassTileCount"))
	if int(grass.get("ResidentGrassTileCount")) <= 0:
		_fail("no resident grass at the capture pose (altitude %s m)" % explorer.CurrentAltitudeAboveTerrainMeters)
		return
	var weather = world.get_node("RingVolumetricClouds")
	var celestial = world.get_node("RingDayNightCycle")
	var weather_paused = weather.CloudSimulationPaused
	var celestial_paused = celestial.Paused
	weather.CloudSimulationPaused = true
	celestial.Paused = true
	clock_restore = func():
		if is_instance_valid(weather): weather.CloudSimulationPaused = weather_paused
		if is_instance_valid(celestial): celestial.Paused = celestial_paused
	# Unrecorded priming window: landing and the fill leave tree/terrain installs and JIT/GC warm-up that otherwise
	# inflate whichever phase runs first.
	await create_timer(driver.CollectionSeconds, true, false, true).timeout
	if finished: return
	for case_name in cases:
		if not _apply_case(case_name): return
		for motion_name in motions:
			if not await _capture(case_name, motion_name): return
		_restore_case()
	# Drift check: the first phase repeated last.
	if not await _capture(cases[0] + "-repeat", motions[0]): return
	_restore_all()
	_write_report("PASS", "")
	print("GRASS_MOTION=PASS directory=", ProjectSettings.globalize_path(output_directory))
	finished = true
	world.queue_free()
	await process_frame
	await process_frame
	quit(0)

func _process(delta: float) -> bool:
	if finished or not is_instance_valid(world) or not world.is_inside_tree(): return false
	if motion.begins_with("yaw"):
		motion_elapsed += delta
		var rate := 45.0 if motion == "yaw45" else 15.0
		world.get_node("Explorer").SetPresentationView(base_yaw_degrees + rate * motion_elapsed, base_pitch_degrees)
	return false

func _select_noon() -> void:
	var clock = world.get_node("RingDayNightCycle")
	var best := 0.0
	var highest := -INF
	for step in 48:
		var candidate := float(step) / 48.0
		clock.call("SetCycleFraction", candidate)
		var elevation := float(clock.get("SunElevationDegrees"))
		if elevation > highest:
			highest = elevation
			best = candidate
	clock.call("SetCycleFraction", best)
	print("GRASS_MOTION=NOON fraction=", best, " elevation=", highest)

func _land_explorer(explorer) -> bool:
	# Physics on with no input: gravity lands the explorer; stable height for 0.5 s counts as landed.
	explorer.set_physics_process(true)
	var started := Time.get_ticks_msec()
	var last := float(probe.ExplorerState(explorer).vertical)
	var stable_since := Time.get_ticks_msec()
	while not finished:
		await physics_frame
		var height := float(probe.ExplorerState(explorer).vertical)
		var now := Time.get_ticks_msec()
		if absf(height - last) > .001: stable_since = now
		last = height
		if now - stable_since >= 500 and now - started >= 1000: break
		if now - started > 15000:
			_fail("explorer did not land within 15 s")
			return false
	explorer.set_physics_process(false)
	var state: Dictionary = probe.ExplorerState(explorer)
	pose_offset = float(state.vertical) - 397.3149763485417
	if not driver.SetPoseAltitudeOffset(world, pose_offset):
		_fail(driver.ErrorText)
		return false
	print("GRASS_MOTION=LANDED offset=", pose_offset, " altitude=", explorer.CurrentAltitudeAboveTerrainMeters)
	return true

func _await_grass_quiescent(timeout_seconds: float) -> bool:
	var grass = world.get_node("StandingGrass")
	var started := Time.get_ticks_msec()
	var stable_since := -1
	while not finished:
		var idle: bool = int(grass.get("PendingGrassTileBuildCount")) == 0 and int(grass.get("PendingGrassUploadCount")) == 0
		var now := Time.get_ticks_msec()
		if not idle: stable_since = -1
		elif stable_since < 0: stable_since = now
		elif now - stable_since >= 1500: return true
		if now - started > timeout_seconds * 1000.0: return false
		await process_frame
	return false

var pose_origin := {}

func _reset_pose() -> bool:
	var explorer = world.get_node("Explorer")
	explorer.set_physics_process(false)
	var state: Dictionary = probe.ExplorerState(explorer)
	if pose_origin.is_empty():
		pose_origin = state
	# Only a walk moves the explorer; restoring an unchanged pose would be a needless teleport.
	var moved := Vector2(float(state.around) - float(pose_origin.around), float(state.across) - float(pose_origin.across)).length()
	if moved > .01 or absf(float(state.vertical) - float(pose_origin.vertical)) > .01:
		if not driver.SetPoseAltitudeOffset(world, pose_offset):
			_fail(driver.ErrorText)
			return false
	explorer.SetPresentationView(base_yaw_degrees, base_pitch_degrees)
	return true

func _press_w(down: bool) -> void:
	var event := InputEventKey.new()
	event.keycode = KEY_W
	event.physical_keycode = KEY_W
	event.pressed = down
	Input.parse_input_event(event)

func _capture(case_name: String, motion_name: String) -> bool:
	motion = "still"
	_press_w(false)
	if not _reset_pose(): return false
	await create_timer(driver.SettleSeconds, true, false, true).timeout
	if finished: return false
	if world.get_node("StandingGrass").is_processing() and not await _await_grass_quiescent(40.0):
		print("GRASS_MOTION=WARN grass not quiescent before ", case_name, "/", motion_name)
	var explorer = world.get_node("Explorer")
	var grass = world.get_node("StandingGrass")
	var monitor = world.get_node("RingPerformanceMonitor")
	var clouds = world.get_node("SunshineCloudsDriver").get("clouds_resource")
	var start_position: Dictionary = probe.ExplorerState(explorer)
	if motion_name == "walk5":
		explorer.set("MoveSpeed", walk_speed)
		explorer.set("SprintSpeed", walk_speed)
		explorer.set_physics_process(true)
		_press_w(true)
		# Accelerate to speed before the window opens.
		await create_timer(1.0, true, false, true).timeout
	motion_elapsed = 0.0
	motion = motion_name
	var counters_before := _grass_values(grass, GRASS_COUNTERS)
	var gpu_before: Dictionary = probe.GrassGpuCounters(grass)
	var interleave_before := _interleave_counts(clouds)
	monitor.ClearHistory()
	probe.MarkPhaseStart()
	await create_timer(driver.CollectionSeconds, true, false, true).timeout
	if finished: return false
	var summary = JSON.parse_string(probe.Summarize(monitor, driver.CollectionSeconds))
	var counters_after := _grass_values(grass, GRASS_COUNTERS)
	var gpu_after: Dictionary = probe.GrassGpuCounters(grass)
	var interleave_after := _interleave_counts(clouds)
	var end_position: Dictionary = probe.ExplorerState(explorer)
	motion = "still"
	_press_w(false)
	explorer.set_physics_process(false)
	var deltas := {}
	for key in GRASS_COUNTERS:
		deltas[key] = float(counters_after[key]) - float(counters_before[key])
	for key in gpu_after:
		deltas[key] = float(gpu_after[key]) - float(gpu_before[key])
	var phase := {"case":case_name, "motion":motion_name, "summary":summary, "grassCounterDeltas":deltas,
		"grassGauges":_grass_values(grass, GRASS_GAUGES), "lastRelatchReason":str(grass.get("GrassCullingLastRelatchReason")),
		"throttle":grass.get("ThrottleGrassCulling"),
		"interleave":{"interleaved":interleave_after.interleaved - interleave_before.interleaved,
			"fullMarch":interleave_after.full - interleave_before.full, "lastReason":interleave_after.reason,
			"lastMotionPixels":interleave_after.motion, "setting":clouds.get("cloud_interleave") if clouds != null else null},
		"movedMeters":Vector2(float(end_position.around) - float(start_position.around), float(end_position.across) - float(start_position.across)).length(),
		"altitudeAboveTerrain":explorer.CurrentAltitudeAboveTerrainMeters}
	phases.append(phase)
	print("GRASS_MOTION_PHASE ", JSON.stringify(_brief(phase)))
	_write_report("RUNNING", "")
	return true

func _brief(phase: Dictionary) -> Dictionary:
	var s: Dictionary = phase.summary if phase.summary is Dictionary else {}
	var grass_rows := {}
	for row in s.get("rows", []):
		var name := str(row.name)
		if name.begins_with("Grass") or name.begins_with("GPU.Grass") or name.begins_with("GPU.Vegetation") or name.begins_with("GPU.Cloud"):
			grass_rows[name] = snappedf(float(row.perFrameMs if not row.gpu else row.meanMs), .001)
	return {"case":phase.case, "motion":phase.motion, "fps":snappedf(float(s.get("fps", 0)), .1),
		"frameP50":s.get("frameMs", {}).get("p50"), "frameP95":s.get("frameMs", {}).get("p95"), "processMean":s.get("processMs", {}).get("mean"),
		"setupMean":s.get("renderSetupMs", {}).get("mean"), "viewportCpuMean":s.get("mainViewportCpuMs", {}).get("mean"),
		"viewportGpuMean":s.get("mainViewportGpuMs", {}).get("mean"), "gcAllocMB":float(s.get("gc", {}).get("allocatedBytes", 0)) / 1048576.0,
		"moved":snappedf(float(phase.movedMeters), .1), "interleave":phase.interleave, "counters":phase.grassCounterDeltas, "rows":grass_rows}

func _grass_values(grass: Node, keys: Array) -> Dictionary:
	var result := {}
	for key in keys: result[key] = grass.get(key)
	return result

func _interleave_counts(clouds) -> Dictionary:
	if clouds == null: return {"interleaved":0, "full":0, "reason":"", "motion":0.0}
	return {"interleaved":int(clouds.get("interleave_interleaved_callbacks")), "full":int(clouds.get("interleave_full_march_callbacks")),
		"reason":str(clouds.get("interleave_last_fallback_reason")), "motion":float(clouds.get("interleave_last_motion_pixels"))}

func _apply_case(name: String) -> bool:
	_restore_case()
	var grass = world.get_node("StandingGrass")
	var bridge = world.get_node("SunshineCloudBridge")
	var clouds = world.get_node("SunshineCloudsDriver").get("clouds_resource")
	match name:
		"baseline":
			pass
		"grass-off":
			var processing: bool = grass.is_processing()
			var visible: bool = grass.visible
			case_restore = func():
				if is_instance_valid(grass):
					grass.set_process(processing)
					grass.visible = visible
			grass.set_process(false)
			grass.visible = false
		"cull-tiles-off", "skip-unchanged-off", "throttle-off":
			var property := "CullGrassTiles" if name == "cull-tiles-off" else ("ThrottleGrassCulling" if name == "throttle-off" else "SkipUnchangedGpuCulling")
			var previous = grass.get(property)
			case_restore = func():
				if is_instance_valid(grass): grass.set(property, previous)
			grass.set(property, false)
			if grass.get(property) != false:
				_fail("Case did not apply: " + name)
				return false
		"interleave-off", "interleave-motion-200":
			var bridge_property := "CloudInterleave" if name == "interleave-off" else "CloudInterleaveMaxPixelMotion"
			var resource_property := "cloud_interleave" if name == "interleave-off" else "cloud_interleave_max_pixel_motion"
			var value = 0 if name == "interleave-off" else 200.0
			var previous_bridge = bridge.get(bridge_property)
			var previous_resource = clouds.get(resource_property)
			case_restore = func():
				if is_instance_valid(bridge): bridge.set(bridge_property, previous_bridge)
				if clouds != null: clouds.set(resource_property, previous_resource)
			bridge.set(bridge_property, value)
			clouds.set(resource_property, value)
		_:
			_fail("Unsupported case " + name)
			return false
	return true

func _restore_case() -> void:
	if case_restore.is_valid(): case_restore.call()
	case_restore = Callable()

func _restore_all() -> void:
	_restore_case()
	_press_w(false)
	if clock_restore.is_valid(): clock_restore.call()
	clock_restore = Callable()
	if vsync_restore.is_valid(): vsync_restore.call()
	vsync_restore = Callable()

func _write_report(status: String, reason: String) -> void:
	if output_directory.is_empty(): return
	var file := FileAccess.open(ProjectSettings.globalize_path(output_directory.path_join("report.json")), FileAccess.WRITE)
	if file == null: return
	file.store_string(JSON.stringify({"schemaVersion":1, "status":status, "reason":reason,
		"hardware":{"gpu":RenderingServer.get_video_adapter_name(), "cpu":OS.get_processor_name()},
		"window":root.size, "vsync":capture_vsync, "maxFps":Engine.max_fps,
		"poseOffsetMeters":pose_offset, "note":"Matched pose (far-ring pose dropped and landed); clocks paused after warmup; motion yaw via SetPresentationView, walk via held W at 5 m/s. Per-frame CPU rows = calls x mean / frames (main-thread only when worker=false). GPU rows are delayed timestamp means.",
		"phases":phases}, "  "))
	file.close()

func _fail(reason) -> void:
	if finished: return
	finished = true
	printerr("GRASS_MOTION=FAIL ", reason)
	_write_report("FAILED", str(reason))
	_restore_all()
	if is_instance_valid(world):
		if world.is_inside_tree(): world.queue_free()
		else: world.free()
	await process_frame
	quit(1)
