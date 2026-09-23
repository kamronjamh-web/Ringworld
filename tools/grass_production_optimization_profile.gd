extends SceneTree

# Full-production A/B capture for the three grass execution controls.  It is a
# rendered viewport measurement: GPU scopes are separate partial compute intervals.
const SCENE := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const QUALITY := preload("res://tools/GrassQualityValidation.cs")
const BRIDGE := preload("res://tools/GrassProductionTimingBridge.cs")
const FOCUS_DRIVER := preload("res://tools/GrassGenerationProfileDriver.cs")
const BASE := "res://.godot/grass-production-profile/"
const ARTIFACTS := "res://artifacts/grass-production-optimization/"
const WARMUP := 60
const FRAMES := 180
const REPS := 3
const TOTAL_LIMIT_MS := 600000
const SETTLE_LIMIT_MS := 90000
var game: Node
var grass: Node
var trees: Node
var grid: Node
var coordinator: Node
var smooth: Node
var explorer: Node
var graphics: Node
var bridge: Node
var started := 0
var failed := false
var invariant_settings := {}
var tree_gpu_requested := false
var report := {"status":"RUNNING", "purpose":"full production grass compute optimization A/B", "comparison":"baseline disables grouping, tile culling, and clumps; optimized enables all three", "limitations":"viewport timing is main viewport CPU/GPU interval; GPU.GrassCulling is an asynchronous partial compute interval and is not frame time", "cases":[]}
var raw := {"cases":[]}

func _initialize() -> void:
	started = Time.get_ticks_msec()
	call_deferred("boot")

func _process(_delta: float) -> bool:
	if not failed and Time.get_ticks_msec() - started > TOTAL_LIMIT_MS: fail("overall ten-minute deadline")
	return false

func boot() -> void:
	if RenderingServer.get_rendering_device() == null: fail("RenderingDevice required; headless is not valid"); return
	Engine.max_fps = 60
	DisplayServer.window_set_vsync_mode(DisplayServer.VSYNC_DISABLED)
	ProjectSettings.set_setting("ringworld/runtime/active_world_directory", "")
	game = SCENE.instantiate()
	var saves := game.get_node_or_null("WorldSaveManager")
	if saves != null: saves.free()
	var drawer := game.get_node_or_null("LiveTuningDrawer")
	if drawer == null: fail("LiveTuningDrawer missing"); return
	if not install_fixture_defaults(drawer): return
	var launcher: Node = load("res://scenes/WorldSizeLauncher.tscn").instantiate()
	root.add_child(launcher); launcher.call("_on_certified_preset_pressed"); launcher.call("_configure_game", game, 5000000.0); launcher.free(); root.add_child(game)
	grass = game.get_node_or_null("StandingGrass"); trees = game.get_node_or_null("BiomeDecorations"); grid = game.get_node_or_null("ChunkGridManager"); coordinator = game.get_node_or_null("TerrainLodCoordinator"); smooth = game.get_node_or_null("SmoothTerrainGrid"); explorer = game.get_node_or_null("Explorer"); graphics = game.get_node_or_null("GraphicsQuality")
	if grass == null or trees == null or grid == null or coordinator == null or explorer == null or graphics == null: fail("required production nodes missing"); return
	grass.set("Enabled", false)
	tree_gpu_requested = bool(trees.get("UseGpuCulling")); trees.set("UseGpuCulling", false)
	for i in range(12): await process_frame
	var quality := QUALITY.new(); game.add_child(quality)
	if not bool(quality.call("VerifyProfileContract")) or not bool(quality.call("FindAndPlaceGrassland", game)): fail("grassland placement failed"); return
	for i in range(60): await process_frame
	if not await settle_world(): return
	var focus_driver := FOCUS_DRIVER.new(); game.add_child(focus_driver)
	if not bool(focus_driver.call("Prepare", game)): fail("production focus driver unavailable"); return
	freeze_except(game, [grass, trees])
	trees.set("UseGpuCulling", tree_gpu_requested)
	if tree_gpu_requested and not await settle_tree_gpu(): return
	freeze_except(game, [grass])
	grass.set("Enabled", true); grass.set("UseGpuCulling", true); grass.set("GpuFrustumCulling", true)
	invariant_settings = fixed_grass_settings()
	bridge = BRIDGE.new(); game.add_child(bridge)
	# The scene monitor remains frozen and cannot save or compete for telemetry.
	var monitor := game.get_node_or_null("RingPerformanceMonitor")
	if monitor != null: monitor.set("Enabled", false); monitor.process_mode = Node.PROCESS_MODE_DISABLED
	RenderingServer.viewport_set_measure_render_time(root.get_viewport_rid(), true)
	Engine.max_fps = 0
	report["runtime"] = runtime_context()
	report["preferences_copied_to"] = BASE
	report["focus"] = focus_driver.call("CaptureFocus")
	report["invariant_settings"] = invariant_settings
	write_outputs()
	for rep in range(REPS):
		var order := ["baseline", "optimized"] if rep % 2 == 0 else ["optimized", "baseline"]
		for mode in order:
			var item := await populate_and_sample(mode, rep + 1)
			if failed: return
			report.cases.append(compact_case(item)); raw.cases.append(item)
			write_outputs()
			print("GRASS_PRODUCTION_OPTIMIZATION_WINDOW ", JSON.stringify(stdout_window(item)))
			await screenshot(mode, rep + 1)
			if failed: return
	report.status = "PASS"
	write_outputs()
	RenderingServer.viewport_set_measure_render_time(root.get_viewport_rid(), false)
	print("GRASS_PRODUCTION_OPTIMIZATION_PROFILE=PASS")
	game.queue_free(); await process_frame; quit(0)

func install_fixture_defaults(drawer: Node) -> bool:
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(BASE))
	for item in [["InstallGrassDefaults","gameplay_grass_defaults.tres"], ["InstallVegetationDefaults","gameplay_vegetation_defaults.tres"], ["InstallWaterDefaults","gameplay_water_defaults.tres"], ["InstallAntiAliasingDefaults","gameplay_anti_aliasing_defaults.tres"], ["InstallShadowDefaults","gameplay_shadow_defaults.tres"]]:
		var src := "user://" + str(item[1]); var dst := BASE + str(item[1])
		if FileAccess.file_exists(src):
			var copy := FileAccess.open(dst, FileAccess.WRITE)
			if copy == null: fail("preference fixture copy failed"); return false
			copy.store_string(FileAccess.get_file_as_string(src)); copy.close()
		elif FileAccess.file_exists(dst): DirAccess.remove_absolute(ProjectSettings.globalize_path(dst))
		drawer.call(item[0], dst)
	return true

func populate_and_sample(mode: String, repetition: int) -> Dictionary:
	var enabled := mode == "optimized"
	grass.set("GroupGrassComputePhases", enabled); grass.set("CullGrassTiles", enabled); grass.set("UseGrassClumps", enabled)
	# Clump changes alter source capacity and require a fresh, fully settled population.
	grass.call("BeginGrassStreamingDiagnostics", true, 4096)
	if not await settle_grass(): return {}
	grass.call("EndGrassStreamingDiagnostics")
	explorer.call("SetPresentationView", -35.0, -10.0)
	for i in range(WARMUP): await RenderingServer.frame_post_draw
	if not verify_mode(enabled): return {}
	var builds := int(grass.get("TotalGrassBuildCount")); var uploads := int(grass.get("TotalGrassUploadCount"))
	var gpu := []; var cpu := []; var wall := []; var calls := []; var requested := []; var dispatched := []; var deferred := []; var poses := []
	bridge.call("BeginTimedWindow")
	var last := Time.get_ticks_usec(); var begin := last
	for frame in range(FRAMES):
		var yaw := lerpf(-35.0, 35.0, float(frame) / float(FRAMES - 1)); explorer.call("SetPresentationView", yaw, -10.0); poses.append(yaw)
		await RenderingServer.frame_post_draw
		var now := Time.get_ticks_usec(); wall.append((now - last) * .001); last = now
		gpu.append(RenderingServer.viewport_get_measured_render_time_gpu(root.get_viewport_rid())); cpu.append(RenderingServer.viewport_get_measured_render_time_cpu(root.get_viewport_rid())); calls.append(Performance.get_monitor(Performance.Monitor.RENDER_TOTAL_DRAW_CALLS_IN_FRAME))
		requested.append(grass.get("GpuCullingRequestedBatchCount")); dispatched.append(grass.get("GpuCullingDispatchedBatchCount")); deferred.append(grass.get("GpuCullingDeferredBatchCount"))
		bridge.call("SampleFrame")
	var timed_end := Time.get_ticks_usec()
	# Let delayed timestamps arrive, while no camera or grass process can create extra grass timestamps.
	grass.process_mode = Node.PROCESS_MODE_DISABLED
	for i in range(8): await RenderingServer.frame_post_draw; bridge.call("SampleFrame")
	var grass_timing: Dictionary = bridge.call("FinishTimedWindow")
	grass.process_mode = Node.PROCESS_MODE_ALWAYS
	if int(grass.get("TotalGrassBuildCount")) != builds or int(grass.get("TotalGrassUploadCount")) != uploads: fail("build/upload occurred during timed window"); return {}
	if not verify_mode(enabled): return {}
	for value in gpu:
		if not is_finite(value) or value <= 0: fail("invalid viewport GPU time"); return {}
	return {"label":mode, "repetition":repetition, "warmup_frames":WARMUP, "frames":FRAMES, "elapsed_ms":(timed_end-begin)*.001, "wall_ms":stats(wall), "viewport_gpu_ms":stats(gpu), "viewport_cpu_ms":stats(cpu), "draw_calls":stats(calls), "gpu_requested_batches":stats(requested), "gpu_dispatched_batches":stats(dispatched), "gpu_deferred_batches":stats(deferred), "grass_culling_summary":grass_timing_summary(grass_timing), "grass_culling_timestamps":grass_timing, "actual_settings":actual_settings(), "grass_counts":grass.call("CaptureGrassStreamingDiagnostics"), "timeline":{"yaw_degrees":poses,"wall_ms":wall,"viewport_gpu_ms":gpu,"viewport_cpu_ms":cpu,"draw_calls":calls,"gpu_requested_batches":requested,"gpu_dispatched_batches":dispatched,"gpu_deferred_batches":deferred}}

func settle_world() -> bool:
	var deadline := Time.get_ticks_msec() + SETTLE_LIMIT_MS; var quiet := 0
	while Time.get_ticks_msec() < deadline:
		await process_frame
		var idle := int(coordinator.get("ActiveWorkerCount")) == 0 and int(coordinator.get("PendingUploadCount")) == 0 and int(grid.get("PendingChunkLoadCount")) == 0 and int(grid.get("ActiveVoxelMeshWorkerCount")) == 0 and int(grid.get("PendingChunkRebuildCount")) == 0 and int(grid.get("CompletedVoxelMeshCount")) == 0 and int(trees.get("VegetationPendingTiles")) == 0 and (smooth == null or bool(smooth.get("IsQuiescent")))
		quiet = quiet + 1 if idle else 0
		if quiet >= 8: return true
	fail("terrain/tree world settlement timeout"); return false

func settle_grass() -> bool:
	var deadline := Time.get_ticks_msec() + SETTLE_LIMIT_MS; var quiet := 0
	while Time.get_ticks_msec() < deadline:
		await RenderingServer.frame_post_draw
		quiet = quiet + 1 if bool(grass.get("IsGrassStreamingDiagnosticsSettled")) else 0
		if quiet >= 4 and int(grass.get("ResidentGrassInstanceCount")) > 0: return true
	fail("grass population settlement timeout"); return false

func settle_tree_gpu() -> bool:
	var deadline := Time.get_ticks_msec() + 15000
	while Time.get_ticks_msec() < deadline:
		await RenderingServer.frame_post_draw
		if bool(trees.get("GpuTreeCullingSettled")) and int(trees.get("GpuTreeCompletedUpdates")) > 0: return true
	fail("tree GPU handoff timeout"); return false

func verify_mode(optimized: bool) -> bool:
	if bool(grass.get("UseGpuCulling")) != true or bool(grass.get("GpuFrustumCulling")) != true or bool(grass.get("GroupGrassComputePhases")) != optimized or bool(grass.get("CullGrassTiles")) != optimized or bool(grass.get("UseGrassClumps")) != optimized or not bool(grass.get("IsGrassStreamingDiagnosticsSettled")):
		fail("actual grass settings or settled state mismatch"); return false
	if fixed_grass_settings() != invariant_settings:
		fail("a density, visibility, budget, shadow, AA, or resolution invariant changed"); return false
	return true

func actual_settings() -> Dictionary:
	var result := {}
	for key in ["UseGpuCulling","GpuFrustumCulling","GroupGrassComputePhases","CullGrassTiles","UseGrassClumps","GrassBladesPerSource","MaximumGrassSourceInstances","MaximumInstances","MaximumInstancesPerTile","BladesPerSquareMeter","VisibilityRadiusMeters","MaximumGpuCullingBatchesPerFrame"]: result[key] = grass.get(key)
	return result

func fixed_grass_settings() -> Dictionary:
	var result := {}
	for key in ["BladesPerSquareMeter","VisibilityRadiusMeters","LodNearDistanceMeters","LodMidDistanceMeters","LodFarDistanceMeters","LodMidSourceDensity","LodFarSourceDensity","LodTransitionWidthMeters","LodFarDensity","MaximumInstances","MaximumInstancesPerTile","MaximumResidentTiles","MaximumTileBuildsInFlight","MaximumTileUploadsPerFrame","MaximumTileUploadMilliseconds","MaximumGpuCullingBatchesPerFrame"]: result[key] = grass.get(key)
	result["shadow_atlas"] = graphics.get("EffectiveDirectionalShadowAtlasSize")
	result["shadow_filter"] = graphics.get("EffectiveDirectionalShadowFilter")
	for key in ["use_taa","msaa_3d","screen_space_aa","scaling_3d_mode","scaling_3d_scale"]: result["aa_" + key] = root.get(key)
	result["viewport"] = str(root.get_visible_rect().size)
	for key in ["GpuTreeResidentRootCount","GpuTreeBatchCount","GpuTreeFallbackBatchCount","GpuTreeResidentBytes"]: result["tree_" + key] = trees.get(key)
	return result

func runtime_context() -> Dictionary:
	return {"renderer":RenderingServer.get_current_rendering_method(), "adapter":RenderingServer.get_video_adapter_name(), "viewport_size":str(root.get_visible_rect().size), "quality":graphics.get("CurrentPresetName"), "shadow_atlas":graphics.get("EffectiveDirectionalShadowAtlasSize"), "shadow_filter":graphics.get("EffectiveDirectionalShadowFilter")}

func screenshot(mode: String, repetition: int) -> void:
	explorer.call("SetPresentationView", 0.0, -10.0)
	if not await settle_canonical_grass(): return
	var image := root.get_texture().get_image()
	if image == null: fail("canonical screenshot unavailable"); return
	var name := "canonical_%s_rep%d.png" % [mode, repetition]
	if image.save_png(ARTIFACTS + name) != OK: fail("canonical screenshot write failed")

func compact_case(item: Dictionary) -> Dictionary:
	var result := item.duplicate(true); result.erase("timeline"); result.erase("grass_culling_timestamps"); return result

func stdout_window(item: Dictionary) -> Dictionary:
	var timestamps: Dictionary = item.get("grass_culling_summary", {})
	return {"label":item.get("label", ""), "repetition":item.get("repetition", 0), "compute_ms_median":timestamps.get("milliseconds", {}).get("median", null), "timestamp_sample_count":timestamps.get("sample_count", 0), "viewport_gpu_ms_median":item.get("viewport_gpu_ms", {}).get("median", null), "wall_ms_median":item.get("wall_ms", {}).get("median", null), "draw_calls_median":item.get("draw_calls", {}).get("median", null), "resident_roots":item.get("grass_counts", {}).get("resident_roots", 0)}

func grass_timing_summary(timing: Dictionary) -> Dictionary:
	var milliseconds := []; var source_frames := []; var distinct := []
	for item in timing.get("grass_culling", []):
		milliseconds.append(float(item.get("milliseconds", 0.0)))
		var frame := int(item.get("source_frame", -1)); source_frames.append(frame)
		if frame >= 0 and not distinct.has(frame): distinct.append(frame)
	return {"sample_count":milliseconds.size(), "source_frame_count":distinct.size(), "source_frames":source_frames, "milliseconds":stats(milliseconds), "dropped":timing.get("dropped", 0), "status":timing.get("status", "unavailable")}

func settle_canonical_grass() -> bool:
	var budget := maxi(1, int(grass.get("MaximumGpuCullingBatchesPerFrame")))
	var batches := maxi(1, int(grass.get("GpuCullingBatchCount")))
	var requested := maxi(batches, int(grass.get("GpuCullingRequestedBatchCount")))
	var required_frames := maxi(8, int(ceil(float(requested) / float(budget))) + 8)
	var skipped_before := int(grass.get("GpuCullingSkippedUnchangedUpdates"))
	for i in range(required_frames):
		await RenderingServer.frame_post_draw
	if not bool(grass.get("IsGrassStreamingDiagnosticsSettled")):
		fail("canonical grass population lost readiness"); return false
	if bool(grass.get("SkipUnchangedGpuCulling")) and int(grass.get("GpuCullingSkippedUnchangedUpdates")) <= skipped_before:
		fail("canonical stationary grass did not use unchanged-update cache"); return false
	return true

func stats(values: Array) -> Dictionary:
	if values.is_empty(): return {}
	var sorted := values.duplicate(); sorted.sort(); var total := 0.0
	for value in sorted: total += float(value)
	return {"count":sorted.size(), "mean":total/sorted.size(), "median":sorted[int(sorted.size()/2)], "p95":sorted[mini(sorted.size()-1, int(ceil(sorted.size()*.95))-1)], "min":sorted[0], "max":sorted[-1]}

func write_outputs() -> void:
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(ARTIFACTS))
	for entry in [["report.json",report],["rawdata.json",raw]]:
		var file := FileAccess.open(ARTIFACTS + str(entry[0]), FileAccess.WRITE)
		if file == null: fail("artifact write failed " + str(entry[0])); return
		file.store_string(JSON.stringify(entry[1], "\t")); file.close()

func freeze_except(node: Node, active: Array) -> void:
	node.process_mode = Node.PROCESS_MODE_ALWAYS if node in active else Node.PROCESS_MODE_DISABLED
	if node is GPUParticles3D: node.speed_scale = 0.0
	for child in node.get_children(): freeze_except(child, active)

func fail(message: String) -> void:
	if failed: return
	failed = true; report.status = "FAIL"; report.error = message; write_outputs(); push_error("GRASS_PRODUCTION_OPTIMIZATION_PROFILE=FAIL " + message)
	if game != null: game.queue_free()
	quit(1)
