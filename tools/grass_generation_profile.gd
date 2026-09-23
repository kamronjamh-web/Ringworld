extends SceneTree

# Production grass streaming in a rendered world with unrelated simulation
# frozen. Discrete moves force rebasing; this is not a gameplay FPS test.
const SCENE := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const QUALITY := preload("res://tools/GrassQualityValidation.cs")
const BRIDGE := preload("res://tools/GrassGenerationProfileDriver.cs")
const OUT := "res://artifacts/grass-generation-diagnostics/profile.json"
const BASE := "res://.godot/grass-generation-diagnostics/"
const LIMIT_CASE_MS := 120000
const LIMIT_TOTAL_MS := 600000
var game: Node
var grass: Node
var trees: Node
var grid: Node
var coordinator: Node
var smooth: Node
var graphics: Node
var driver: Node
var started := 0
var done := false
var baseline_settings := {}
var baseline_runtime := {}
var surface_cache_override_mb := -1.0
var baseline_only := false
var report := {"status":"RUNNING", "purpose":"grass generation versus upload latency", "limitation":"unrelated simulation frozen; discrete 48m moves force canonical rebasing; GPU readiness is observed dispatch handoff, not a hardware completion timestamp", "cases":[]}

func _initialize() -> void:
	for argument in OS.get_cmdline_user_args():
		if argument.begins_with("--surface-cache-mb="):
			surface_cache_override_mb = float(argument.trim_prefix("--surface-cache-mb="))
		elif argument == "--baseline-only":
			baseline_only = true
	started = Time.get_ticks_msec()
	call_deferred("boot")

func _process(_delta: float) -> bool:
	if not done and Time.get_ticks_msec() - started > LIMIT_TOTAL_MS: fail("overall diagnostic deadline")
	return false

func boot() -> void:
	if RenderingServer.get_rendering_device() == null: fail("RenderingDevice required"); return
	Engine.max_fps = 60
	DisplayServer.window_set_vsync_mode(DisplayServer.VSYNC_DISABLED)
	ProjectSettings.set_setting("ringworld/runtime/active_world_directory", "")
	game = SCENE.instantiate()
	var saves := game.get_node_or_null("WorldSaveManager")
	if saves != null: saves.free()
	var drawer := game.get_node_or_null("LiveTuningDrawer")
	if drawer == null: fail("LiveTuningDrawer missing"); return
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(BASE))
	for item in [["InstallGrassDefaults","gameplay_grass_defaults.tres"], ["InstallVegetationDefaults","gameplay_vegetation_defaults.tres"], ["InstallWaterDefaults","gameplay_water_defaults.tres"], ["InstallAntiAliasingDefaults","gameplay_anti_aliasing_defaults.tres"], ["InstallShadowDefaults","gameplay_shadow_defaults.tres"]]:
		var src := "user://" + str(item[1])
		var dst := BASE + str(item[1])
		if FileAccess.file_exists(src):
			var copy := FileAccess.open(dst, FileAccess.WRITE)
			if copy == null: fail("preference copy failed"); return
			copy.store_string(FileAccess.get_file_as_string(src)); copy.close()
		elif FileAccess.file_exists(dst): DirAccess.remove_absolute(ProjectSettings.globalize_path(dst))
		drawer.call(item[0], dst)
	var launcher: Node = load("res://scenes/WorldSizeLauncher.tscn").instantiate()
	root.add_child(launcher)
	launcher.call("_on_certified_preset_pressed")
	launcher.call("_configure_game", game, 5000000.0)
	launcher.free()
	root.add_child(game)
	grass = game.get_node_or_null("StandingGrass")
	trees = game.get_node_or_null("BiomeDecorations")
	grid = game.get_node_or_null("ChunkGridManager")
	coordinator = game.get_node_or_null("TerrainLodCoordinator")
	smooth = game.get_node_or_null("SmoothTerrainGrid")
	graphics = game.get_node_or_null("GraphicsQuality")
	if grass == null or trees == null or grid == null or coordinator == null or graphics == null:
		fail("required scene feature missing"); return
	if surface_cache_override_mb >= 0.0:
		grass.set("SurfaceCacheMegabytes", surface_cache_override_mb)
		report["surface_cache_override_megabytes"] = surface_cache_override_mb
	grass.set("Enabled", false)
	var tree_gpu_requested := bool(trees.get("UseGpuCulling"))
	trees.set("UseGpuCulling", false)
	for i in range(12): await process_frame
	var quality := QUALITY.new()
	game.add_child(quality)
	if not bool(quality.call("VerifyProfileContract")) or not bool(quality.call("FindAndPlaceGrassland", game)):
		fail("grassland placement failed"); return
	driver = BRIDGE.new()
	game.add_child(driver)
	if not bool(driver.call("Prepare", game)): fail("benchmark bridge unavailable"); return
	for i in range(60): await process_frame
	if not await settle_world(): return
	freeze_except(game, [grass, trees])
	trees.set("UseGpuCulling", tree_gpu_requested)
	if tree_gpu_requested:
		var gpu_deadline := Time.get_ticks_msec() + 15000
		while not bool(trees.get("GpuTreeCullingSettled")) or int(trees.get("GpuTreeCompletedUpdates")) == 0:
			await process_frame
			if Time.get_ticks_msec() > gpu_deadline: fail("tree GPU handoff timeout"); return
	freeze_except(game, [grass])
	grass.set("Enabled", true)
	var initial_state: Dictionary = grass.call("CaptureGrassStreamingDiagnostics")
	baseline_settings = initial_state.settings.duplicate(true)
	baseline_runtime = runtime_context()
	if not bool(baseline_settings.gpu_culling): fail("this diagnostic requires the saved GPU grass path"); return
	report["runtime"] = baseline_runtime
	report["initial_focus"] = driver.call("CaptureFocus")
	report["preferences_copied_to"] = BASE
	if not write_report(): return
	RenderingServer.viewport_set_measure_render_time(root.get_viewport_rid(), true)
	Engine.max_fps = 0
	grass.set("Enabled", false)
	for i in range(8): await RenderingServer.frame_post_draw
	grass.set("Enabled", true)
	var workers := int(baseline_settings.maximum_builds_in_flight)
	var uploads := int(baseline_settings.maximum_uploads_per_frame)
	var alternative_workers := mini(8, workers + 2) if workers < 8 else 6
	var cases := [
		{"name":"baseline", "offset":-1, "workers":workers, "uploads":uploads},
		{"name":"move_48m", "offset":48, "workers":workers, "uploads":uploads},
		{"name":"move_96m", "offset":96, "workers":workers, "uploads":uploads},
		{"name":"move_144m", "offset":144, "workers":workers, "uploads":uploads},
		{"name":"workers_" + str(alternative_workers), "offset":-1, "workers":alternative_workers, "uploads":uploads},
		{"name":"uploads_16", "offset":-1, "workers":workers, "uploads":16},
		{"name":"baseline_repeat", "offset":-1, "workers":workers, "uploads":uploads}]
	if baseline_only:
		cases.resize(1)
	for config in cases:
		var sample := await run_case(config)
		report.cases.append(sample)
		if not write_report(): return
		print("GRASS_GENERATION_CASE ", sample.case, " ", sample.status, " elapsed_ms=", sample.elapsed_ms, " roots=", sample.resident_roots, " traces=", sample.record_count)
		if sample.status != "PASS" and sample.status != "SETTLED_AT_ROOT_BUDGET" and sample.status != "GPU_HANDOFF_INCOMPLETE":
			fail("case failed: " + str(sample.case)); return
	report["gpu_handoff_incomplete"] = report.cases.any(func(item: Dictionary) -> bool: return item.status == "GPU_HANDOFF_INCOMPLETE")
	finish("COMPLETE")

func run_case(config: Dictionary) -> Dictionary:
	grass.set("MaximumTileBuildsInFlight", int(config.workers))
	grass.set("MaximumTileUploadsPerFrame", int(config.uploads))
	var begin := Time.get_ticks_usec()
	grass.call("BeginGrassStreamingDiagnostics", int(config.offset) < 0, 4096)
	if int(config.offset) < 0: driver.call("Restore")
	else: driver.call("MoveDiscrete", float(config.offset))
	var frames := []
	var quiet := 0
	var status := "TIMEOUT"
	var previous_tick := begin
	var cpu_completion_tick := 0
	var last_gpu_progress_tick := begin
	var last_gpu_batches := -1
	var progress_log_tick := begin
	print("GRASS_GENERATION_START ", config.name, " workers=", config.workers, " uploads=", config.uploads)
	while Time.get_ticks_usec() - begin < LIMIT_CASE_MS * 1000 and not done:
		await RenderingServer.frame_post_draw
		var tick := Time.get_ticks_usec()
		var gpu := RenderingServer.viewport_get_measured_render_time_gpu(root.get_viewport_rid())
		var cpu := RenderingServer.viewport_get_measured_render_time_cpu(root.get_viewport_rid())
		frames.append({"elapsed_ms":(tick-begin)*.001, "wall_ms":(tick-previous_tick)*.001, "gpu_ms":gpu, "cpu_ms":cpu, "pending_builds":grass.get("PendingGrassTileBuildCount"), "pending_uploads":grass.get("PendingGrassUploadCount"), "resident_roots":grass.get("ResidentGrassInstanceCount")})
		previous_tick = tick
		var gpu_batches := int(grass.get("GpuCullingBatchCount"))
		if gpu_batches != last_gpu_batches:
			last_gpu_batches = gpu_batches
			last_gpu_progress_tick = tick
		var cpu_idle := int(grass.get("PendingGrassTileBuildCount")) == 0 and int(grass.get("PendingGrassUploadCount")) == 0 and int(grass.get("ResidentGrassTileCount")) > 0
		if not cpu_idle: cpu_completion_tick = 0
		elif cpu_completion_tick == 0: cpu_completion_tick = tick
		if tick - progress_log_tick > 10000000:
			print("GRASS_GENERATION_PROGRESS ", config.name, " roots=", grass.get("ResidentGrassInstanceCount"), " pending=", grass.get("PendingGrassTileBuildCount"), " gpu_batches=", gpu_batches)
			progress_log_tick = tick
		if not is_finite(gpu) or gpu <= 0 or not is_finite(cpu) or cpu < 0:
			status = "INVALID_RENDER_TIMING"; break
		quiet = quiet + 1 if bool(grass.get("IsGrassStreamingDiagnosticsSettled")) else 0
		if quiet >= 4: status = "PASS"; break
		# Record incomplete GPU handoff explicitly and continue the independent
		# CPU and installation comparisons after one second without progress.
		if cpu_completion_tick > 0 and tick - maxi(cpu_completion_tick, last_gpu_progress_tick) >= 1000000:
			status = "GPU_HANDOFF_INCOMPLETE"; break
	var data: Dictionary = grass.call("CaptureGrassStreamingDiagnostics")
	if status == "PASS" and bool(data.idle_at_root_budget): status = "SETTLED_AT_ROOT_BUDGET"
	var expected := baseline_settings.duplicate(true)
	expected.maximum_builds_in_flight = int(config.workers)
	expected.maximum_uploads_per_frame = int(config.uploads)
	if data.settings != expected or runtime_context() != baseline_runtime: status = "RUNTIME_SETTINGS_CHANGED"
	data["case"] = config.name
	data["status"] = status
	data["elapsed_ms"] = (Time.get_ticks_usec() - begin) * .001
	data["cpu_pipeline_complete_ms"] = (cpu_completion_tick - begin) * .001 if cpu_completion_tick > 0 else -1.0
	data["focus"] = driver.call("CaptureFocus")
	data["resident_audit"] = grass.call("CaptureGrassStreamingDiagnosticResidents")
	data["frame_timeline"] = frames
	data["frame_statistics"] = timing_stats(frames)
	data["gpu_status"] = grass.get("GpuCullingStatus")
	grass.call("EndGrassStreamingDiagnostics")
	return data

func settle_world() -> bool:
	var deadline := Time.get_ticks_msec() + 90000
	var quiet := 0
	while Time.get_ticks_msec() < deadline and not done:
		await process_frame
		var idle := int(coordinator.get("ActiveWorkerCount")) == 0 and int(coordinator.get("PendingUploadCount")) == 0 and int(grid.get("PendingChunkLoadCount")) == 0 and int(grid.get("ActiveVoxelMeshWorkerCount")) == 0 and int(grid.get("PendingChunkRebuildCount")) == 0 and int(grid.get("CompletedVoxelMeshCount")) == 0 and int(trees.get("VegetationPendingTiles")) == 0 and (smooth == null or bool(smooth.get("IsQuiescent"))) and int(grass.get("PendingGrassTileBuildCount")) == 0
		quiet = quiet + 1 if idle else 0
		if quiet >= 8: return true
	fail("terrain/tree settlement timeout"); return false

func runtime_context() -> Dictionary:
	var aa := {}
	for key in ["use_taa", "msaa_3d", "screen_space_aa", "scaling_3d_mode", "scaling_3d_scale"]: aa[key] = root.get(key)
	return {"adapter":RenderingServer.get_video_adapter_name(), "renderer":RenderingServer.get_current_rendering_method(), "window_size":str(root.get_visible_rect().size), "quality":graphics.get("CurrentPresetName"), "atlas":graphics.get("EffectiveDirectionalShadowAtlasSize"), "depth_bits":graphics.get("EffectiveDirectionalShadowDepthBits"), "shadow_filter":graphics.get("EffectiveDirectionalShadowFilter"), "aa":aa, "tree_roots":trees.get("GpuTreeResidentRootCount"), "tree_batches":trees.get("GpuTreeBatchCount")}

func freeze_except(node: Node, active: Array) -> void:
	node.process_mode = Node.PROCESS_MODE_ALWAYS if node in active else Node.PROCESS_MODE_DISABLED
	if node is GPUParticles3D: node.speed_scale = 0.0
	for child in node.get_children(): freeze_except(child, active)

func timing_stats(values: Array) -> Dictionary:
	var wall := []; var gpu := []; var cpu := []
	for item in values: wall.append(item.wall_ms); gpu.append(item.gpu_ms); cpu.append(item.cpu_ms)
	return {"wall_ms":stats(wall), "gpu_ms":stats(gpu), "cpu_ms":stats(cpu)}

func stats(values: Array) -> Dictionary:
	if values.is_empty(): return {}
	var a := values.duplicate(); a.sort(); var sum := 0.0
	for value in a: sum += value
	return {"count":a.size(), "mean":sum/a.size(), "median":a[int(a.size()/2)], "p95":a[mini(a.size()-1, int(ceil(a.size()*.95))-1)], "min":a[0], "max":a[-1]}

func write_report() -> bool:
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path("res://artifacts/grass-generation-diagnostics/"))
	var file := FileAccess.open(OUT, FileAccess.WRITE)
	if file == null:
		done = true; push_error("GRASS_GENERATION_PROFILE=FAIL report write"); quit(1); return false
	file.store_string(JSON.stringify(report, "\t")); file.close()
	return true

func finish(status: String) -> void:
	if done: return
	done = true
	report.status = status
	if not write_report(): return
	RenderingServer.viewport_set_measure_render_time(root.get_viewport_rid(), false)
	print("GRASS_GENERATION_PROFILE=", status)
	if game != null: game.queue_free()
	await process_frame
	quit(0 if status == "COMPLETE" else 1)

func fail(message: String) -> void:
	if done: return
	report.error = message
	push_error("GRASS_GENERATION_PROFILE=FAIL " + message)
	finish("FAIL")
