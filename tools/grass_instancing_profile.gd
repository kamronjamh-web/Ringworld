extends SceneTree

# Production grass streaming in a rendered world with unrelated simulation
# frozen. Discrete moves force rebasing; this is not a gameplay FPS test.
const SCENE := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const QUALITY := preload("res://tools/GrassQualityValidation.cs")
const BRIDGE := preload("res://tools/GrassGenerationProfileDriver.cs")
var output_path := "res://artifacts/grass-registration-fix/instancing-profile.json"
var without_frustum := false
var generation_prototype := false
const BASE := "res://.godot/grass-instancing-profile/"
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
var baseline_audit: Array = []
var expected_gpu_batches := 0
var report := {"status":"RUNNING", "comparison":"GPU culling and indirect draws versus existing CPU LOD MultiMesh instancing", "limitation":"fixed scene with unrelated simulation frozen; camera pans rotate without walking or terrain streaming", "cases":[]}

func _initialize() -> void:
	without_frustum = "--without-frustum" in OS.get_cmdline_user_args()
	generation_prototype = "--generation-prototype" in OS.get_cmdline_user_args()
	if without_frustum: output_path = "res://artifacts/grass-registration-fix/instancing-no-frustum-profile.json"
	if generation_prototype: output_path = "res://artifacts/grass-gpu-generation-prototype/gameplay-profile.json"
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
	if without_frustum: grass.set("GpuFrustumCulling", false)
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
	grass.call("BeginGrassStreamingDiagnostics", true, 4096)
	var cold_begin := Time.get_ticks_usec()
	print("GRASS_INSTANCING_COLD_START")
	if not await settle_mode(): return
	report["cold_population_ms"] = (Time.get_ticks_usec() - cold_begin) * .001
	report["cold_trace"] = grass.call("CaptureGrassStreamingDiagnostics")
	grass.call("EndGrassStreamingDiagnostics")
	baseline_audit = grass.call("CaptureGrassStreamingDiagnosticResidents")
	report["resident_audit"] = baseline_audit
	for tile in baseline_audit:
		if int(tile.instance_count) > 0:
			expected_gpu_batches += 2 if bool(baseline_settings.distance_lod) else 1
	if not verify_state(true): return
	print("GRASS_INSTANCING_COLD_READY roots=", grass.get("ResidentGrassInstanceCount"), " batches=", expected_gpu_batches)
	if not write_report(): return
	if generation_prototype:
		await sample_generation_prototype()
		return
	for config in [["cpu_static", false, false], ["gpu_static", true, false], ["cpu_pan", false, true], ["gpu_pan", true, true], ["cpu_static_repeat", false, false], ["gpu_static_repeat", true, false]]:
		var item := await sample(str(config[0]), bool(config[1]), bool(config[2]))
		if done: return
		report.cases.append(item)
		if not write_report(): return
		var summary := item.duplicate()
		summary.erase("timeline")
		print("GRASS_INSTANCING_WINDOW ", JSON.stringify(summary))
	finish("PASS")

func sample_generation_prototype() -> void:
	report.comparison = "Diagnostic hybrid GPU grass generation versus CPU baseline and same-cache CPU resolution"
	report["prototype_runs"] = []
	var builds_before := int(grass.get("TotalGrassBuildCount"))
	var uploads_before := int(grass.get("TotalGrassUploadCount"))
	grass.set("GpuGenerationPrototypeTileCount", 4)
	for repetition in range(3):
		if not bool(grass.call("StartGpuGenerationPrototype")): fail("prototype did not start"); return
		if bool(grass.call("StartGpuGenerationPrototype")): fail("prototype allowed concurrent starts"); return
		var deadline := Time.get_ticks_msec() + 120000
		while bool(grass.get("GpuGenerationPrototypeRunning")):
			await RenderingServer.frame_post_draw
			if Time.get_ticks_msec() > deadline: fail("prototype timed out"); return
		var result: Dictionary = grass.call("CaptureGpuGenerationPrototypeDiagnostics")
		report.prototype_runs.append(result)
		if not write_report(): return
		print("GPU_GRASS_GENERATION_GAMEPLAY ", JSON.stringify(result))
		if not str(result.get("status", "")).begins_with("Completed") or result.get("tiles", []).size() != 4:
			fail("prototype did not complete all four tiles"); return
		for tile in result.tiles:
			if not bool(tile.supported) or not bool(tile.parity) or int(tile.queries) < 100 or int(tile.cpu_instances) <= 0:
				fail("prototype parity or meaningful sample check failed"); return
		if not verify_state(true): return
		if int(grass.get("TotalGrassBuildCount")) != builds_before or int(grass.get("TotalGrassUploadCount")) != uploads_before:
			fail("generation prototype changed live grass"); return
		for i in range(30): await RenderingServer.frame_post_draw
	finish("PASS")

func settle_mode() -> bool:
	var deadline := Time.get_ticks_msec() + LIMIT_CASE_MS
	var quiet := 0
	var next_progress := Time.get_ticks_msec() + 10000
	while Time.get_ticks_msec() < deadline and not done:
		await RenderingServer.frame_post_draw
		quiet = quiet + 1 if bool(grass.get("IsGrassStreamingDiagnosticsSettled")) else 0
		if quiet >= 4: return true
		if Time.get_ticks_msec() >= next_progress:
			print("GRASS_INSTANCING_SETTLE roots=", grass.get("ResidentGrassInstanceCount"), " builds=", grass.get("PendingGrassTileBuildCount"), " batches=", grass.get("GpuCullingBatchCount"))
			next_progress += 10000
	report["failure_trace"] = grass.call("CaptureGrassStreamingDiagnostics")
	fail("grass CPU/GPU settlement timeout")
	return false

func verify_state(gpu: bool) -> bool:
	var state: Dictionary = grass.call("CaptureGrassStreamingDiagnostics")
	var expected := baseline_settings.duplicate(true)
	expected.gpu_culling = gpu
	var audit: Array = grass.call("CaptureGrassStreamingDiagnosticResidents")
	if not bool(grass.get("visible")) or state.settings != expected or runtime_context() != baseline_runtime or audit != baseline_audit:
		fail("runtime settings, visibility, or grass population changed"); return false
	if int(grass.get("PendingGrassTileBuildCount")) != 0 or int(grass.get("PendingGrassUploadCount")) != 0:
		fail("grass regenerated during a renderer comparison"); return false
	var batches := int(grass.get("GpuCullingBatchCount"))
	var bytes := int(grass.get("GpuCullingBytes"))
	if gpu:
		if batches != expected_gpu_batches or bytes <= 0 or not bool(grass.get("IsGrassStreamingDiagnosticsSettled")):
			fail("GPU grass handoff incomplete"); return false
	elif batches != 0 or bytes != 0:
		fail("CPU mode retained GPU grass batches"); return false
	return true

func sample(label: String, gpu_enabled: bool, pan: bool) -> Dictionary:
	var explorer := game.get_node("Explorer")
	grass.set("UseGpuCulling", gpu_enabled)
	explorer.call("SetPresentationView", 0.0, -10.0)
	if not await settle_mode(): return {}
	var warm_yaw := -35.0 if pan else 0.0
	explorer.call("SetPresentationView", warm_yaw, -10.0)
	for i in range(60): await RenderingServer.frame_post_draw
	if not verify_state(gpu_enabled): return {}
	var before := int(grass.get("GpuCullingCompletedUpdates"))
	var builds_before := int(grass.get("TotalGrassBuildCount"))
	var uploads_before := int(grass.get("TotalGrassUploadCount"))
	var wall := []; var gpu := []; var cpu := []; var draws := []; var video := []
	var poses := []
	var begin := Time.get_ticks_usec()
	var previous := begin
	for i in range(180):
		var yaw := lerpf(-35.0, 35.0, float(i) / 179.0) if pan else 0.0
		explorer.call("SetPresentationView", yaw, -10.0)
		poses.append(yaw)
		await RenderingServer.frame_post_draw
		var tick := Time.get_ticks_usec()
		wall.append((tick-previous)*.001)
		previous = tick
		var gpu_ms := RenderingServer.viewport_get_measured_render_time_gpu(root.get_viewport_rid())
		var cpu_ms := RenderingServer.viewport_get_measured_render_time_cpu(root.get_viewport_rid())
		if not is_finite(gpu_ms) or gpu_ms <= 0 or not is_finite(cpu_ms) or cpu_ms < 0:
			fail("invalid render timing"); return {}
		gpu.append(gpu_ms); cpu.append(cpu_ms)
		draws.append(Performance.get_monitor(Performance.Monitor.RENDER_TOTAL_DRAW_CALLS_IN_FRAME))
		video.append(Performance.get_monitor(Performance.Monitor.RENDER_VIDEO_MEM_USED))
	var updates := int(grass.get("GpuCullingCompletedUpdates")) - before
	if int(grass.get("TotalGrassBuildCount")) != builds_before or int(grass.get("TotalGrassUploadCount")) != uploads_before:
		fail("grass rebuilt or reinstalled during sampling"); return {}
	if gpu_enabled and not pan and bool(baseline_runtime.skip_unchanged) and updates != 0:
		fail("stationary GPU culling did not stay cached"); return {}
	if gpu_enabled and pan and bool(baseline_runtime.gpu_frustum) and updates <= 0:
		fail("camera pan did not refresh GPU culling"); return {}
	if not verify_state(gpu_enabled): return {}
	var result := {"label":label, "gpu_culling":gpu_enabled, "camera_pan":pan, "samples":180, "warmup_frames":60, "elapsed_ms":(Time.get_ticks_usec()-begin)*.001, "wall_ms":stats(wall), "viewport_gpu_ms":stats(gpu), "viewport_cpu_ms":stats(cpu), "draw_calls":stats(draws), "video_bytes":stats(video), "gpu_updates":updates, "gpu_batches":grass.get("GpuCullingBatchCount"), "gpu_grass_bytes":grass.get("GpuCullingBytes"), "grass_roots":grass.get("ResidentGrassInstanceCount"), "audit_matches":true, "timeline":{"wall_ms":wall,"gpu_ms":gpu,"cpu_ms":cpu,"yaw_degrees":poses}}
	explorer.call("SetPresentationView", 0.0, -10.0)
	return result

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
	return {"adapter":RenderingServer.get_video_adapter_name(), "renderer":RenderingServer.get_current_rendering_method(), "window_size":str(root.get_visible_rect().size), "quality":graphics.get("CurrentPresetName"), "atlas":graphics.get("EffectiveDirectionalShadowAtlasSize"), "depth_bits":graphics.get("EffectiveDirectionalShadowDepthBits"), "shadow_filter":graphics.get("EffectiveDirectionalShadowFilter"), "aa":aa, "tree_roots":trees.get("GpuTreeResidentRootCount"), "tree_batches":trees.get("GpuTreeBatchCount"), "skip_unchanged":grass.get("SkipUnchangedGpuCulling"), "gpu_frustum":grass.get("GpuFrustumCulling")}

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
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path("res://artifacts/grass-registration-fix/"))
	if generation_prototype: DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path("res://artifacts/grass-gpu-generation-prototype/"))
	var file := FileAccess.open(output_path, FileAccess.WRITE)
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
	print("GRASS_INSTANCING_PROFILE=", status)
	if game != null: game.queue_free()
	await process_frame
	quit(0 if status == "PASS" else 1)

func fail(message: String) -> void:
	if done: return
	report.error = message
	push_error("GRASS_INSTANCING_PROFILE=FAIL " + message)
	finish("FAIL")
