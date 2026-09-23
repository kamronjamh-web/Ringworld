extends SceneTree

# Isolated stationary comparison for the optimized vegetation backend. This is
# intentionally separate from gpu_vegetation_scene_profile.gd and its artifacts.
const SCENE := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const DRIVER := preload("res://tools/GrassQualityValidation.cs")
const BASE := "res://.godot/gpu-vegetation-optimization-scene-profile/"
const OUTPUT := "res://artifacts/gpu-vegetation-optimization/after-hierarchical-coverage.json"
const WARMUP := 240
const SAMPLES := 90
var game: Node
var grass: Node
var trees: Node
var grid: Node
var coordinator: Node
var smooth: Node
var explorer: Node
var camera: Camera3D
var driver: Node
var failed := false
var started_ms := 0
var total_frames := 0

func _initialize() -> void:
	started_ms = Time.get_ticks_msec()
	call_deferred("boot")

func _process(_delta: float) -> bool:
	total_frames += 1
	if not failed and (total_frames > 5400 or Time.get_ticks_msec() - started_ms > 240000): fail("total diagnostic deadline")
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
	for item in [["InstallGrassDefaults", "gameplay_grass_defaults.tres"], ["InstallVegetationDefaults", "gameplay_vegetation_defaults.tres"], ["InstallWaterDefaults", "gameplay_water_defaults.tres"], ["InstallAntiAliasingDefaults", "gameplay_anti_aliasing_defaults.tres"], ["InstallShadowDefaults", "gameplay_shadow_defaults.tres"]]:
		var saved := "user://" + str(item[1])
		var isolated := BASE + str(item[1])
		if FileAccess.file_exists(saved):
			var copy := FileAccess.open(isolated, FileAccess.WRITE)
			if copy == null: fail("isolated settings copy failed"); return
			copy.store_string(FileAccess.get_file_as_string(saved)); copy.close()
		elif FileAccess.file_exists(isolated): DirAccess.remove_absolute(ProjectSettings.globalize_path(isolated))
		drawer.call(item[0], isolated)
	var launcher: Node = load("res://scenes/WorldSizeLauncher.tscn").instantiate()
	root.add_child(launcher); launcher.call("_on_certified_preset_pressed"); launcher.call("_configure_game", game, 5000000.0); launcher.free()
	root.add_child(game)
	grass = game.get_node_or_null("StandingGrass")
	trees = game.get_node_or_null("BiomeDecorations")
	grid = game.get_node_or_null("ChunkGridManager")
	coordinator = game.get_node_or_null("TerrainLodCoordinator")
	smooth = game.get_node_or_null("SmoothTerrainGrid")
	explorer = game.find_child("Explorer", true, false)
	if grass == null or trees == null or grid == null or coordinator == null or explorer == null: fail("vegetation scene nodes missing"); return
	grass.set("UseGpuCulling", false); trees.set("UseGpuCulling", false)
	for i in range(12): await process_frame
	driver = DRIVER.new(); game.add_child(driver)
	if not bool(driver.call("VerifyProfileContract")) or not bool(driver.call("FindAndPlaceGrassland", game)): fail("certified grassland placement failed"); return
	for i in range(60): await process_frame
	if not await settle(): return
	camera = driver.call("CreateDedicatedCaptureCamera", game) as Camera3D
	if camera == null: fail("dedicated capture camera missing"); return
	_freeze(game)
	Engine.max_fps = 0
	RenderingServer.viewport_set_measure_render_time(root.get_viewport_rid(), true)
	var report := {"status":"RUNNING", "purpose":"stationary optimized scene comparison; camera, terrain, and weather fixed", "renderer":RenderingServer.get_current_rendering_method(), "adapter":RenderingServer.get_video_adapter_name(), "viewport_size":str(root.get_visible_rect().size), "preferences_copied_to":BASE, "quality":game.get_node("GraphicsQuality").get("CurrentPresetName"), "focus":driver.call("GetFocusDiagnostics", game), "windows":[]}
	if not write_report(report): return
	for phase in [["cpu_both", false, false], ["gpu_both_unmerged", true, false], ["gpu_both_merged", true, true]]:
		var item := await profile_phase(phase[0], "dedicated_closeup", phase[1], phase[2])
		if not item.valid: return
		report.windows.append(item)
		print("GPU_VEGETATION_OPTIMIZED_SCENE_WINDOW ", JSON.stringify(item))
		if not write_report(report): return
	# Find the gameplay camera recursively; FindAndPlaceGrassland already restored
	# canonical horizontal orientation. Keep the explorer disabled after setting view.
	camera = find_camera(explorer)
	if camera == null: fail("Explorer Camera3D missing"); return
	camera.make_current(); explorer.call("SetPresentationView", 0.0, -10.0); explorer.process_mode = Node.PROCESS_MODE_DISABLED
	for phase in [["cpu_both_ground", false, false], ["gpu_both_optimized_ground", true, true]]:
		var item := await profile_phase(phase[0], "explorer_ground", phase[1], phase[2])
		if not item.valid: return
		report.windows.append(item)
		print("GPU_VEGETATION_OPTIMIZED_SCENE_WINDOW ", JSON.stringify(item))
		if not write_report(report): return
	RenderingServer.viewport_set_measure_render_time(root.get_viewport_rid(), false)
	report.status = "PASS"
	if not write_report(report): return
	print("GPU_VEGETATION_OPTIMIZED_SCENE_PROFILE=PASS")
	game.queue_free(); await process_frame; quit(0)

func settle() -> bool:
	var stable := 0
	for i in range(1200):
		await process_frame
		if failed: return false
		var quiet := int(grass.get("PendingGrassTileBuildCount")) == 0 and int(grass.get("PendingGrassUploadCount")) == 0 and int(coordinator.get("ActiveWorkerCount")) == 0 and int(coordinator.get("PendingUploadCount")) == 0 and int(grid.get("PendingChunkLoadCount")) == 0 and int(grid.get("ActiveVoxelMeshWorkerCount")) == 0 and int(grid.get("PendingChunkRebuildCount")) == 0 and int(grid.get("CompletedVoxelMeshCount")) == 0 and int(trees.get("VegetationPendingTiles")) == 0 and (smooth == null or bool(smooth.get("IsQuiescent")))
		stable = stable + 1 if quiet else 0
		if stable >= 8 and int(grass.get("ResidentGrassInstanceCount")) > 0: return true
	fail("settle timeout")
	return false

func profile_phase(label: String, view_label: String, gpu: bool, merge: bool) -> Dictionary:
	grass.set("UseGpuCulling", gpu); grass.set("SkipUnchangedGpuCulling", true)
	trees.set("UseGpuCulling", gpu); trees.set("SkipUnchangedGpuCulling", true); trees.set("MergeFarGpuTreeBatches", merge)
	for i in range(WARMUP): await RenderingServer.frame_post_draw
	if failed: return {"valid":false}
	if bool(grass.get("UseGpuCulling")) != gpu or bool(trees.get("UseGpuCulling")) != gpu or bool(trees.get("MergeFarGpuTreeBatches")) != merge: fail("runtime options overwritten during " + label); return {"valid":false}
	# Allocated indirect buffers alone do not establish a stable window. The tree
	# layer owns its request-level skip, so its settled signal is the authority.
	if gpu and (int(grass.get("GpuCullingBatchCount")) == 0 or int(grass.get("GpuCullingCompletedUpdates")) == 0 or int(grass.get("GpuCullingSkippedUnchangedUpdates")) == 0 or not bool(trees.get("GpuTreeCullingActive")) or not bool(trees.get("GpuTreeCullingSettled")) or int(trees.get("GpuTreeCompletedUpdates")) == 0): fail("GPU vegetation was not fully stable during " + label); return {"valid":false}
	var grass_start := int(grass.get("GpuCullingCompletedUpdates"))
	var tree_start := int(trees.get("GpuTreeCompletedUpdates"))
	var gpu_ms := []; var cpu_ms := []; var wall_ms := []; var calls := []; var primitives := []; var memory := []
	var viewport := root.get_viewport_rid()
	var epoch_start = trees.get("GpuTreeSourceEpoch"); var batches_start = trees.get("GpuTreeBatchCount")
	for i in range(SAMPLES):
		var start := Time.get_ticks_usec(); await RenderingServer.frame_post_draw
		if failed: return {"valid":false}
		wall_ms.append((Time.get_ticks_usec() - start) * .001); gpu_ms.append(RenderingServer.viewport_get_measured_render_time_gpu(viewport)); cpu_ms.append(RenderingServer.viewport_get_measured_render_time_cpu(viewport)); calls.append(Performance.get_monitor(Performance.Monitor.RENDER_TOTAL_DRAW_CALLS_IN_FRAME)); primitives.append(Performance.get_monitor(Performance.Monitor.RENDER_TOTAL_PRIMITIVES_IN_FRAME)); memory.append(Performance.get_monitor(Performance.Monitor.RENDER_VIDEO_MEM_USED))
	for value in gpu_ms:
		if not is_finite(value) or value <= 0: fail("invalid viewport GPU timing " + label); return {"valid":false}
	if trees.get("GpuTreeSourceEpoch") != epoch_start or trees.get("GpuTreeBatchCount") != batches_start: fail("tree sources/batches changed during sampling " + label); return {"valid":false}
	var grass_delta := int(grass.get("GpuCullingCompletedUpdates")) - grass_start
	var tree_delta := int(trees.get("GpuTreeCompletedUpdates")) - tree_start
	if gpu and (grass_delta != 0 or tree_delta != 0): fail("steady GPU compute updates during " + label + ": grass=%s trees=%s" % [grass_delta, tree_delta]); return {"valid":false}
	return {"valid":true, "label":label, "view_label":view_label, "warmup_frames":WARMUP, "samples":SAMPLES, "actual_flags":{"grass_gpu_culling":grass.get("UseGpuCulling"), "tree_gpu_culling":trees.get("UseGpuCulling"), "grass_skip_unchanged":grass.get("SkipUnchangedGpuCulling"), "tree_skip_unchanged":trees.get("SkipUnchangedGpuCulling"), "merge_far_gpu_tree_batches":trees.get("MergeFarGpuTreeBatches")}, "viewport_gpu_ms":stats(gpu_ms), "viewport_cpu_ms":stats(cpu_ms), "wall_ms":stats(wall_ms), "draw_calls":stats(calls), "primitives":stats(primitives), "reported_video_bytes":stats(memory), "steady_compute_updates":{"grass":grass_delta, "trees":tree_delta}, "grass":{"roots":grass.get("ResidentGrassInstanceCount"), "near_roots":grass.get("ResidentGrassNearRootCount"), "mid_roots":grass.get("ResidentGrassMidRootCount"), "far_roots":grass.get("ResidentGrassFarRootCount"), "resident_tiles":grass.get("ResidentGrassTileCount"), "gpu_batches":grass.get("GpuCullingBatchCount"), "requested_batches":grass.get("GpuCullingRequestedBatchCount"), "dispatched_batches":grass.get("GpuCullingDispatchedBatchCount"), "deferred_batches":grass.get("GpuCullingDeferredBatchCount"), "completed_updates":grass.get("GpuCullingCompletedUpdates"), "skipped_unchanged_updates":grass.get("GpuCullingSkippedUnchangedUpdates"), "status":grass.get("GpuCullingStatus")}, "trees":{"gpu_batches":trees.get("GpuTreeBatchCount"), "roots":trees.get("GpuTreeResidentRootCount"), "sun_shadow_batches":trees.get("ActiveGpuTreeSunShadowRendererCount"), "sun_shadow_batch_cap":trees.get("MaximumTreeSunShadowBatches"), "sun_shadow_distance_m":trees.get("TreeSunShadowDistanceMeters"), "sun_shadows_suppressed":trees.get("TreeSunShadowsSuppressed"), "source_epoch":epoch_start, "completed_updates":trees.get("GpuTreeCompletedUpdates"), "skipped_unchanged_updates":trees.get("GpuTreeSkippedUnchangedUpdates"), "status":trees.get("GpuTreeCullingStatus")}}

func find_camera(node: Node) -> Camera3D:
	if node is Camera3D: return node
	for child in node.get_children():
		var found := find_camera(child)
		if found != null: return found
	return null

func stats(values: Array) -> Dictionary:
	var sorted := values.duplicate(); sorted.sort(); var sum := 0.0
	for value in sorted: sum += value
	return {"mean":sum / sorted.size(), "median":sorted[int(sorted.size() / 2)], "p95":sorted[mini(sorted.size() - 1, int(ceil(sorted.size() * .95)) - 1)], "min":sorted[0], "max":sorted[-1]}

func write_report(report: Dictionary) -> bool:
	var output := FileAccess.open(OUTPUT, FileAccess.WRITE)
	if output == null: fail("profile report write failed: " + OUTPUT); return false
	output.store_string(JSON.stringify(report, "\t")); output.close(); return true

func _freeze(node: Node) -> void:
	node.process_mode = Node.PROCESS_MODE_ALWAYS if node == grass or node == trees else Node.PROCESS_MODE_DISABLED
	if node is GPUParticles3D: node.speed_scale = 0.0
	for child in node.get_children(): _freeze(child)

func fail(message: String) -> void:
	if failed: return
	failed = true; RenderingServer.viewport_set_measure_render_time(root.get_viewport_rid(), false)
	push_error("GPU_VEGETATION_OPTIMIZED_SCENE_PROFILE=FAIL " + message)
	if game != null: game.queue_free()
	quit(1)
