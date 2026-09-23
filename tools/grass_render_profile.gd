extends SceneTree

# Static full-scene A/B fixture.  It measures the root viewport after terrain and
# grass streaming settle, then disables the instantiated game subtree so these
# windows describe frozen rendering rather than gameplay simulation performance.
const SCENE := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const DRIVER := preload("res://tools/GrassQualityValidation.cs")
const GRASS_PREFS := "res://.godot/grass_render_profile/grass_defaults.tres"
const WATER_PREFS := "res://.godot/grass_render_profile/water_defaults.tres"
const WARMUP_FRAMES := 32 # Includes enough frames to clear delayed timestamp queries.
const SAMPLE_FRAMES := 108
const SETTLE_TIMEOUT_FRAMES := 1500

var game: Node
var driver: Node
var grass: Node
var grid: Node
var smooth: Node
var coordinator: Node
var camera: Camera3D
var settled_frames := 0

func _initialize() -> void:
	call_deferred("boot")

func boot() -> void:
	if RenderingServer.get_rendering_device() == null:
		fail("Forward+ with a real RenderingDevice is required")
		return
	ProjectSettings.set_setting("ringworld/runtime/active_world_directory", "")
	game = SCENE.instantiate()
	var saves := game.get_node_or_null("WorldSaveManager")
	if saves != null: saves.free()
	var drawer := game.get_node_or_null("LiveTuningDrawer")
	if drawer == null:
		fail("LiveTuningDrawer missing before scene entry")
		return
	# Both defaults stores are redirected before the scene enters the tree.  The
	# fixture neither flushes nor resets them, so personal tuning is untouched.
	drawer.call("InstallGrassDefaults", GRASS_PREFS)
	drawer.call("InstallWaterDefaults", WATER_PREFS)
	var launcher: Node = load("res://scenes/WorldSizeLauncher.tscn").instantiate()
	root.add_child(launcher)
	launcher.call("_on_certified_preset_pressed")
	launcher.call("_configure_game", game, 5000000.0)
	launcher.free()
	root.add_child(game)
	for i in range(12): await process_frame
	driver = DRIVER.new()
	game.add_child(driver)
	if not bool(driver.call("VerifyProfileContract")) or not bool(driver.call("FindAndPlaceGrassland", game)):
		fail("could not establish the validated dry grassland focus")
		return
	grass = game.get_node_or_null("StandingGrass")
	grid = game.get_node_or_null("ChunkGridManager")
	smooth = game.get_node_or_null("SmoothTerrainGrid")
	coordinator = game.get_node_or_null("TerrainLodCoordinator")
	if grass == null or grid == null or coordinator == null:
		fail("StandingGrass, ChunkGridManager, or TerrainLodCoordinator unavailable")
		return
	# FindAndPlaceGrassland has just teleported the real explorer.  Let at least
	# sixty full frames establish the current focused tile before judging queues.
	for i in range(60): await process_frame
	await settle_then_profile()

func settle_then_profile() -> void:
	for frame in range(SETTLE_TIMEOUT_FRAMES):
		await process_frame
		if not _world_quiescent():
			settled_frames = 0
			continue
		settled_frames += 1
		if settled_frames >= 8: break
	if settled_frames < 8:
		fail("streaming did not settle: " + JSON.stringify(_quiescence_diagnostics()))
		return
	if int(grass.get("ResidentGrassInstanceCount")) <= 0:
		fail("settled grassland has no resident roots")
		return
	var focus: Dictionary = driver.call("GetFocusDiagnostics", game)
	if int(focus.get("focus_roots", 0)) <= 0 or int(focus.get("focus_tiles", 0)) <= 0:
		fail("settled residents do not cover the current focused grass tile: " + JSON.stringify(focus))
		return
	camera = driver.call("CreateDedicatedCaptureCamera", game) as Camera3D
	if camera == null:
		fail("dedicated MultiMesh-rooted capture camera unavailable")
		return
	# The camera is created after resident queues drain.  Freeze the entire game
	# subtree; SceneTree frames and the root viewport continue, but no runtime
	# system can re-enable grass or mutate the benchmark world.
	var frozen := _freeze_game_tree(game)
	RenderingServer.viewport_set_measure_render_time(root.get_viewport().get_viewport_rid(), true)
	var report := {
		"status": "PASS",
		"purpose": "frozen full-scene grass render A/B; not gameplay performance",
		"renderer": RenderingServer.get_current_rendering_method(),
		"adapter": RenderingServer.get_video_adapter_name(),
		"viewport_size": root.get_visible_rect().size,
		"isolation": {"grass_preferences": GRASS_PREFS, "water_preferences": WATER_PREFS, "world_save_manager_removed": true},
		"freeze": {"every_game_node_process_disabled": true, "grass_process_disabled": true, "gpu_particles_speed_scale_zero": frozen.gpu_particles, "nodes_disabled": frozen.nodes, "scene_tree_frames_continue": true},
		"settled": _quiescence_diagnostics(), "focus": focus,
		"windows": []
	}
	for label in ["grass_on_before", "grass_off", "grass_on_after"]:
		# Explicitly hide the grass node for OFF while its process remains disabled.
		grass.visible = label != "grass_off"
		var window := await sample_window(label)
		if not bool(window.get("valid", false)): return
		report.windows.append(window)
	RenderingServer.viewport_set_measure_render_time(root.get_viewport().get_viewport_rid(), false)
	report["estimated_grass_cost"] = _cost(report.windows[0], report.windows[1], report.windows[2])
	if not write_report(report): return
	print("GRASS_RENDER_PROFILE=PASS ", JSON.stringify(report["estimated_grass_cost"]))
	game.queue_free()
	quit(0)

func sample_window(label: String) -> Dictionary:
	for i in range(WARMUP_FRAMES): await RenderingServer.frame_post_draw
	var gpu: Array[float] = []
	var render_cpu: Array[float] = []
	var whole_frame_cpu: Array[float] = []
	var draw_calls: Array[float] = []
	var primitives: Array[float] = []
	var video_memory: Array[float] = []
	for i in range(SAMPLE_FRAMES):
		var start := Time.get_ticks_usec()
		await RenderingServer.frame_post_draw
		whole_frame_cpu.append((Time.get_ticks_usec() - start) * .001)
		gpu.append(RenderingServer.viewport_get_measured_render_time_gpu(root.get_viewport().get_viewport_rid()))
		render_cpu.append(RenderingServer.viewport_get_measured_render_time_cpu(root.get_viewport().get_viewport_rid()))
		draw_calls.append(Performance.get_monitor(Performance.Monitor.RENDER_TOTAL_DRAW_CALLS_IN_FRAME))
		primitives.append(Performance.get_monitor(Performance.Monitor.RENDER_TOTAL_PRIMITIVES_IN_FRAME))
		video_memory.append(Performance.get_monitor(Performance.Monitor.RENDER_VIDEO_MEM_USED))
	if not _valid_gpu_samples(gpu):
		fail("GPU render-time query unavailable or invalid during " + label)
		return {"valid": false}
	return {"valid": true, "label": label, "visible": grass.visible, "warmup_frames": WARMUP_FRAMES, "samples": SAMPLE_FRAMES,
		"render_gpu_ms": _stats(gpu), "render_cpu_ms": _stats(render_cpu),
		"frozen_frame_wall_ms": _stats(whole_frame_cpu),
		"draw_calls": _stats(draw_calls), "primitives": _stats(primitives), "video_memory_bytes": _stats(video_memory)}

func _world_quiescent() -> bool:
	return int(grass.get("PendingGrassTileBuildCount")) == 0 and int(grass.get("PendingGrassUploadCount")) == 0 \
		and int(coordinator.get("ActiveWorkerCount")) == 0 and int(coordinator.get("PendingUploadCount")) == 0 \
		and int(grid.get("PendingChunkLoadCount")) == 0 and int(grid.get("PendingChunkRebuildCount")) == 0 \
		and int(grid.get("ActiveVoxelMeshWorkerCount")) == 0 and int(grid.get("CompletedVoxelMeshCount")) == 0 \
		and int(grid.get("DeferredVoxelMeshCount")) == 0 and (smooth == null or bool(smooth.get("IsQuiescent")))

func _quiescence_diagnostics() -> Dictionary:
	return {"resident_roots": grass.get("ResidentGrassInstanceCount"), "resident_tiles": grass.get("ResidentGrassTileCount"),
		"grass_pending_builds": grass.get("PendingGrassTileBuildCount"), "grass_pending_uploads": grass.get("PendingGrassUploadCount"),
		"terrain_lod_active_workers": coordinator.get("ActiveWorkerCount"), "terrain_lod_pending_uploads": coordinator.get("PendingUploadCount"),
		"pending_chunk_loads": grid.get("PendingChunkLoadCount"), "pending_chunk_rebuilds": grid.get("PendingChunkRebuildCount"),
		"active_voxel_workers": grid.get("ActiveVoxelMeshWorkerCount"), "completed_voxel_meshes": grid.get("CompletedVoxelMeshCount"),
		"deferred_voxel_meshes": grid.get("DeferredVoxelMeshCount"), "smooth_terrain_quiescent": smooth == null or bool(smooth.get("IsQuiescent"))}

func _stats(values: Array[float]) -> Dictionary:
	var ordered := values.duplicate()
	ordered.sort()
	var total := 0.0
	for value in ordered: total += value
	var median := _percentile(ordered, .5)
	var deviations: Array[float] = []
	for value in ordered: deviations.append(absf(value - median))
	deviations.sort()
	return {"count": ordered.size(), "mean": total / max(1, ordered.size()), "median": median,
		"mad": _percentile(deviations, .5), "p99": _percentile(ordered, .99), "min": ordered[0], "max": ordered[-1]}

func _percentile(sorted: Array[float], fraction: float) -> float:
	return sorted[clampi(int(round((sorted.size() - 1) * fraction)), 0, sorted.size() - 1)]

func _cost(before: Dictionary, off: Dictionary, after: Dictionary) -> Dictionary:
	var metrics := ["render_gpu_ms", "render_cpu_ms", "frozen_frame_wall_ms", "draw_calls", "primitives", "video_memory_bytes"]
	var result := {"method": "mean(on_before,on_after) - off; negative values are retained and indicate measurement variance or shared-scene behavior"}
	for metric in metrics:
		var on_mean := (float(before[metric].mean) + float(after[metric].mean)) * .5
		result[metric] = {"on_mean": on_mean, "off_mean": float(off[metric].mean), "estimated_delta": on_mean - float(off[metric].mean),
			"on_window_spread": absf(float(before[metric].mean) - float(after[metric].mean))}
	return result

func _valid_gpu_samples(values: Array[float]) -> bool:
	if values.is_empty(): return false
	for value in values:
		if not is_finite(value) or value <= 0.0: return false
	return true

func _freeze_game_tree(node: Node) -> Dictionary:
	var nodes := 1
	var gpu_particles := 0
	node.process_mode = Node.PROCESS_MODE_DISABLED
	if node is GPUParticles3D:
		(node as GPUParticles3D).speed_scale = 0.0
		gpu_particles += 1
	for child in node.get_children():
		var child_freeze := _freeze_game_tree(child)
		nodes += int(child_freeze.nodes)
		gpu_particles += int(child_freeze.gpu_particles)
	return {"nodes": nodes, "gpu_particles": gpu_particles}

func write_report(report: Dictionary) -> bool:
	var path := "res://artifacts/grass_lod/profile.json"
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path("res://artifacts/grass_lod"))
	var output := FileAccess.open(path, FileAccess.WRITE)
	if output == null:
		fail("report write failed: " + path)
		return false
	output.store_string(JSON.stringify(report, "\t"))
	output.close()
	return true

func fail(reason: String) -> void:
	RenderingServer.viewport_set_measure_render_time(root.get_viewport().get_viewport_rid(), false)
	push_error("GRASS_RENDER_PROFILE=FAIL " + reason)
	if game != null: game.queue_free()
	quit(1)
