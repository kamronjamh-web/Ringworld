extends SceneTree

# Grass motion capture: still vs camera yaw vs walking, crossed with grass and cloud ablations, in the real
# Certified gameplay scene with the current user preferences (copied privately by FarRingGameplayCapture.Prepare).
# Windowed only (needs RenderingDevice). Run one Godot process at a time; numbers are only comparable on the
# machine that took them.
#
#   Godot_v4.8-dev7_mono_win64_console.exe --path <repo> --script res://tools/grass_motion_capture.gd -- \
#     --detailed-gpu --warmup-seconds 30 --collection-seconds 8 --settle-seconds 2 \
#     --grass-cases=baseline,grass-off,cull-tiles-off,skip-unchanged-off,interleave-off \
#     --grass-motions=still,yaw45,walk5,circle [--noon] [--fill-timeout=300] \
#     [--pose=<around>,<across>] [--pose-yaw=<deg>] [--circle-yaw-rate=30] [--survey]
#
# --pose relocates from the far-ring pose to a canonical (around, across) point (2 m above the surface, then landed).
# --survey ranks dense-forest candidates near the Certified grove, visits each, prints GRASS_MOTION_SURVEY lines, quits.
# circle walks forward at the explorer's real MoveSpeed while yawing at --circle-yaw-rate deg/s (radius = v / rate).
#
# Output: res://.godot/grass-motion/<label>-<pid>-<time>/report.json, plus one GRASS_MOTION_PHASE line per phase.

const SUPPORTED_CASES := ["baseline", "throttle-off", "grass-off", "trees-off", "water-off", "cull-tiles-off", "skip-unchanged-off", "interleave-off", "interleave-motion-200"]
const SUPPORTED_MOTIONS := ["still", "yaw45", "yaw15", "walk5", "circle"]
const GRASS_COUNTERS := ["GpuCullingCompletedUpdates", "GpuCullingSkippedUnchangedUpdates", "GrassVisibilityScans", "GrassVisibilityCacheHits",
	"GrassGpuRequestScans", "GrassGpuRequestCacheHits", "GrassPresentationScans", "GrassPresentationCacheHits", "GrassBuildNewTileCount",
	"GrassBuildTierUpgradeCount", "GrassBuildTierOtherCount", "GrassTierFilterCount", "GrassTilesEvictedCount", "GrassResetCount", "GrassCullingRelatches"]
const GRASS_GAUGES := ["ResidentGrassTileCount", "PendingGrassTileBuildCount", "PendingGrassUploadCount", "GpuCullingBatchCount",
	"GpuCullingRequestedBatchCount", "GpuCullingDispatchedBatchCount", "GpuCullingDeferredBatchCount", "EffectiveGpuCullingBatchesPerFrame",
	"GrassFrustumRejectedTileCount", "GrassDistanceRejectedTileCount", "AdaptiveGrassActivePresentationCount", "AdaptiveGrassGroupCount",
	"ActiveGrassInteractionSampleCount", "IsCardStyle", "GpuCullingBytes", "ResidentGrassInstanceCount", "MaximumGrassSourceInstances"]

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
# Custom pose (--pose): canonical around/across, landed vertical, and the explorer's real (preference) walk speeds.
var custom_pose := {}
var circle_yaw_rate := 30.0
var real_move_speed := 8.0
var real_sprint_speed := 14.0
var pose_report := {}
var survey_results: Array = []
var path_trace: Array = []
var path_accumulator := 0.0

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
	# --window=maximized|<W>x<H> pins the window so runs are comparable (the OS may otherwise restore a maximized window).
	var window_arg := _arg("--window", "")
	if window_arg == "maximized":
		DisplayServer.window_set_mode(DisplayServer.WINDOW_MODE_MAXIMIZED)
	elif not window_arg.is_empty():
		var dims := window_arg.split("x", false)
		DisplayServer.window_set_mode(DisplayServer.WINDOW_MODE_WINDOWED)
		DisplayServer.window_set_size(Vector2i(int(dims[0]), int(dims[1])))
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
	circle_yaw_rate = _arg("--circle-yaw-rate", "30").to_float()
	var survey := "--survey" in OS.get_cmdline_user_args()
	var pose_text := _arg("--pose", "")
	if not pose_text.is_empty():
		var parts := pose_text.split(",", false)
		if parts.size() != 2:
			_fail("--pose needs <around>,<across>")
			return
		custom_pose = {"around":parts[0].to_float(), "across":parts[1].to_float()}
	var watchdog: float = driver.WarmupSeconds + fill_timeout + phase_count * (driver.CollectionSeconds + driver.SettleSeconds + 45.0) + 120.0
	if survey: watchdog = driver.WarmupSeconds + 12 * 150.0 + 120.0
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
	real_move_speed = float(explorer.get("MoveSpeed"))
	real_sprint_speed = float(explorer.get("SprintSpeed"))
	if survey or not custom_pose.is_empty():
		base_yaw_degrees = _arg("--pose-yaw", str(base_yaw_degrees)).to_float()
	if not custom_pose.is_empty():
		pose_report = probe.PlaceExplorer(explorer, world.get_node("ChunkGridManager"), custom_pose.around, custom_pose.across, 2.0, base_yaw_degrees, base_pitch_degrees)
		pose_report["forest"] = probe.ForestAt(world.get_node("ChunkGridManager"), custom_pose.around, custom_pose.across)
		print("GRASS_MOTION=POSE ", JSON.stringify(pose_report), " moveSpeed=", real_move_speed)
	if "--noon" in OS.get_cmdline_user_args(): _select_noon()
	if survey:
		await _survey(explorer)
		return
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
	if motion == "walk5" or motion == "circle":
		path_accumulator += delta
		if path_accumulator >= .5:
			path_accumulator = 0.0
			var at: Dictionary = probe.ExplorerState(world.get_node("Explorer"))
			path_trace.append([snappedf(float(at.around), .01), snappedf(float(at.across), .01), snappedf(float(at.speed), .01)])
	if motion.begins_with("yaw") or motion == "circle":
		motion_elapsed += delta
		var rate := circle_yaw_rate if motion == "circle" else (45.0 if motion == "yaw45" else 15.0)
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
	if not custom_pose.is_empty():
		custom_pose["vertical"] = float(state.vertical)
		probe.RestoreExplorer(explorer, custom_pose.around, custom_pose.vertical, custom_pose.across, base_yaw_degrees, base_pitch_degrees)
		pose_report["landedVertical"] = custom_pose.vertical
		print("GRASS_MOTION=LANDED custom vertical=", custom_pose.vertical, " altitude=", explorer.CurrentAltitudeAboveTerrainMeters)
		return true
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
	if not custom_pose.is_empty():
		probe.RestoreExplorer(explorer, custom_pose.around, custom_pose.vertical, custom_pose.across, base_yaw_degrees, base_pitch_degrees)
	elif moved > .01 or absf(float(state.vertical) - float(pose_origin.vertical)) > .01:
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
	var trees = world.get_node("BiomeDecorations")
	var start_position: Dictionary = probe.ExplorerState(explorer)
	motion_elapsed = 0.0
	if motion_name == "walk5" or motion_name == "circle":
		var speed := walk_speed if motion_name == "walk5" else real_move_speed
		explorer.set("MoveSpeed", speed)
		explorer.set("SprintSpeed", speed)
		explorer.set_physics_process(true)
		_press_w(true)
		# The circle yaws through the pre-roll so the window opens in steady circular motion.
		if motion_name == "circle": motion = "circle"
		# Accelerate to speed before the window opens.
		await create_timer(1.0, true, false, true).timeout
		start_position = probe.ExplorerState(explorer)
	if motion_name != "circle": motion_elapsed = 0.0
	motion = motion_name
	var trees_before: Dictionary = probe.TreeState(trees)
	path_trace = []
	path_accumulator = 0.0
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
	var trees_after: Dictionary = probe.TreeState(trees)
	motion = "still"
	_press_w(false)
	explorer.set_physics_process(false)
	explorer.set("MoveSpeed", real_move_speed)
	explorer.set("SprintSpeed", real_sprint_speed)
	var deltas := {}
	for key in GRASS_COUNTERS:
		deltas[key] = float(counters_after[key]) - float(counters_before[key])
	for key in gpu_after:
		deltas[key] = float(gpu_after[key]) - float(gpu_before[key])
	var phase := {"case":case_name, "motion":motion_name, "window":root.size, "summary":summary, "grassCounterDeltas":deltas,
		"grassGauges":_grass_values(grass, GRASS_GAUGES), "lastRelatchReason":str(grass.get("GrassCullingLastRelatchReason")),
		"throttle":grass.get("ThrottleGrassCulling"),
		"interleave":{"interleaved":interleave_after.interleaved - interleave_before.interleaved,
			"fullMarch":interleave_after.full - interleave_before.full, "lastReason":interleave_after.reason,
			"lastMotionPixels":interleave_after.motion, "setting":clouds.get("cloud_interleave") if clouds != null else null},
		"water":_water_state(), "trees":trees_after, "treeDispatches":int(trees_after.gpu_tree_dispatches) - int(trees_before.gpu_tree_dispatches),
		"treePlans":int(trees_after.vegetation_plans) - int(trees_before.vegetation_plans),
		"walkSpeed":(walk_speed if motion_name == "walk5" else (real_move_speed if motion_name == "circle" else 0.0)),
		"circleYawRate":(circle_yaw_rate if motion_name == "circle" else 0.0),
		"endSpeed":float(end_position.speed), "startPosition":start_position, "endPosition":end_position, "path":path_trace.duplicate(),
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
	var frames := maxf(1.0, float(s.get("frames", 1)))
	var window := maxf(.001, float(s.get("windowSeconds", 1)))
	return {"case":phase.case, "motion":phase.motion, "fps":snappedf(float(s.get("fps", 0)), .1),
		"frameMean":s.get("frameMs", {}).get("mean"), "frameP99":s.get("frameMs", {}).get("p99"),
		"farGpuMean":s.get("outerFarViewportGpuMs", {}).get("mean"), "regionalFarGpuMean":s.get("regionalFarViewportGpuMs", {}).get("mean"),
		"gcKBPerFrame":snappedf(float(s.get("gc", {}).get("allocatedBytes", 0)) / 1024.0 / frames, .01),
		"relatchPerSecond":snappedf(float(phase.grassCounterDeltas.get("GrassCullingRelatches", 0)) / window, .01),
		"treeDispatchesPerSecond":snappedf(float(phase.treeDispatches) / window, .01), "endSpeed":phase.endSpeed,
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
		"water-off":
			var water = world.get_node("WaterFftController")
			var water_enabled = water.get("Enabled")
			case_restore = func():
				if is_instance_valid(water): water.set("Enabled", water_enabled)
			water.set("Enabled", false)
		"trees-off":
			# Visible alone is re-asserted every _Process, so stop processing (streaming + GPU culling) and hide.
			var trees = world.get_node("BiomeDecorations")
			var trees_processing: bool = trees.is_processing()
			var trees_visible: bool = trees.visible
			case_restore = func():
				if is_instance_valid(trees):
					trees.set_process(trees_processing)
					trees.visible = trees_visible
			trees.set_process(false)
			trees.visible = false
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
		"poseOffsetMeters":pose_offset, "customPose":custom_pose, "pose":pose_report, "survey":survey_results,
		"explorerMoveSpeed":real_move_speed, "explorerSprintSpeed":real_sprint_speed, "note":"Matched pose (far-ring pose dropped and landed); clocks paused after warmup; motion yaw via SetPresentationView, walk via held W at 5 m/s. Per-frame CPU rows = calls x mean / frames (main-thread only when worker=false). GPU rows are delayed timestamp means.",
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

func _survey(explorer) -> void:
	var grid = world.get_node("ChunkGridManager")
	var grass = world.get_node("StandingGrass")
	var trees = world.get_node("BiomeDecorations")
	var candidates: Array = [{"label":"certified-grove", "around":186000.0, "across":141000.0},
		{"label":"certified-woodland", "around":186336.0, "across":140760.0}]
	var started := Time.get_ticks_msec()
	var ranked: Array = probe.RankForestCandidates(grid, 186000.0, 141000.0, int(_arg("--survey-extent", "3000")), 64, int(_arg("--survey-count", "4")), 400)
	print("GRASS_MOTION=SURVEY_RANKED ms=", Time.get_ticks_msec() - started, " ", JSON.stringify(ranked))
	for i in ranked.size():
		candidates.append({"label":"ranked-%d" % i, "around":float(ranked[i].around), "across":float(ranked[i].across), "score":ranked[i].score})
	var dwell := _arg("--survey-dwell", "25").to_float()
	var first := true
	for candidate in candidates:
		var placed: Dictionary = probe.PlaceExplorer(explorer, grid, candidate.around, candidate.across, 2.0, base_yaw_degrees, base_pitch_degrees)
		await create_timer(driver.WarmupSeconds if first else dwell, true, false, true).timeout
		first = false
		if finished: return
		var quiet := await _await_grass_quiescent(60.0)
		probe.RefreshAltitude(explorer)
		await create_timer(2.0, true, false, true).timeout
		var tree_state: Dictionary = probe.TreeState(trees)
		var row := {"label":candidate.label, "around":candidate.around, "across":candidate.across, "rankScore":candidate.get("score", null),
			"forest":probe.ForestAt(grid, candidate.around, candidate.across), "placed":placed, "grassQuiescent":quiet,
			"grassTiles":grass.get("ResidentGrassTileCount"), "grassInstances":grass.get("ResidentGrassInstanceCount"),
			"grassInstanceBudget":grass.get("MaximumGrassSourceInstances"), "nearTrees":tree_state.get("collision_count"),
			"treeRoots":tree_state.get("gpu_resident_root_count"), "treeBatches":tree_state.get("gpu_batch_count"),
			"treeRecords":tree_state.get("canonical_record_count"), "fps":Engine.get_frames_per_second(), "altitude":explorer.CurrentAltitudeAboveTerrainMeters}
		survey_results.append(row)
		print("GRASS_MOTION_SURVEY ", JSON.stringify(row))
		_write_report("RUNNING", "survey")
	_restore_all()
	_write_report("PASS", "survey")
	print("GRASS_MOTION=PASS survey directory=", ProjectSettings.globalize_path(output_directory))
	finished = true
	world.queue_free()
	await process_frame
	await process_frame
	quit(0)

func _water_state() -> Dictionary:
	var water = world.get_node_or_null("WaterFftController")
	if water == null: return {}
	return {"enabled":water.get("Enabled"), "status":str(water.get("WaterDemandStatus")),
		"nearestMeters":water.get("NearestRelevantWaterDistanceMeters"), "areaKm2":water.get("RelevantWaterAreaSquareKilometers"),
		"geometryUps":water.get("TargetGeometryUpdatesPerSecond"), "detailUps":water.get("TargetDetailUpdatesPerSecond"),
		"detailDistance":water.get("DetailDistanceMeters"), "cascades":water.get("ActiveCascadeCount"), "resolution":water.get("ActiveResolution")}
