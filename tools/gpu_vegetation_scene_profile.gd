extends SceneTree

# Stationary scene comparison; rendering continues while world motion is frozen.
const SCENE := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const DRIVER := preload("res://tools/GrassQualityValidation.cs")
const BASE := "res://.godot/gpu-vegetation-diagnostics/"
const OUTPUT := "res://artifacts/gpu-vegetation-diagnostics/scene-profile.json"
const WARMUP := 90
const SAMPLES := 90
var game: Node
var grass: Node
var trees: Node
var grid: Node
var coordinator: Node
var smooth: Node
var driver: Node
var camera: Camera3D
var failed := false
var started_ms := 0
var total_frames := 0

func _initialize() -> void:
	started_ms = Time.get_ticks_msec()
	call_deferred("boot")

func _process(_delta: float) -> bool:
	total_frames += 1
	if not failed and (total_frames > 4500 or Time.get_ticks_msec() - started_ms > 180000):
		fail("total diagnostic deadline")
	return false

func boot() -> void:
	if RenderingServer.get_rendering_device() == null:
		fail("RenderingDevice required")
		return
	Engine.max_fps = 60
	DisplayServer.window_set_vsync_mode(DisplayServer.VSYNC_DISABLED)
	ProjectSettings.set_setting("ringworld/runtime/active_world_directory", "")
	game = SCENE.instantiate()
	var saves := game.get_node_or_null("WorldSaveManager")
	if saves != null: saves.free()
	var drawer := game.get_node_or_null("LiveTuningDrawer")
	if drawer == null:
		fail("LiveTuningDrawer missing")
		return
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(BASE))
	for item in [
		["InstallGrassDefaults", "gameplay_grass_defaults.tres"],
		["InstallVegetationDefaults", "gameplay_vegetation_defaults.tres"],
		["InstallWaterDefaults", "gameplay_water_defaults.tres"],
		["InstallAntiAliasingDefaults", "gameplay_anti_aliasing_defaults.tres"],
		["InstallShadowDefaults", "gameplay_shadow_defaults.tres"]
	]:
		var saved := "user://" + str(item[1])
		var isolated := BASE + str(item[1])
		if FileAccess.file_exists(saved):
			var copy := FileAccess.open(isolated, FileAccess.WRITE)
			if copy == null:
				fail("isolated settings copy failed")
				return
			copy.store_string(FileAccess.get_file_as_string(saved))
			copy.close()
		elif FileAccess.file_exists(isolated):
			DirAccess.remove_absolute(ProjectSettings.globalize_path(isolated))
		drawer.call(item[0], isolated)
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
	if grass == null or trees == null or grid == null or coordinator == null:
		fail("vegetation scene nodes missing")
		return
	# Root the capture camera in the CPU source before indirect ownership begins.
	grass.set("UseGpuCulling", false)
	trees.set("UseGpuCulling", false)
	for i in range(12): await process_frame
	driver = DRIVER.new()
	game.add_child(driver)
	if not bool(driver.call("VerifyProfileContract")) or not bool(driver.call("FindAndPlaceGrassland", game)):
		fail("certified grassland placement failed")
		return
	for i in range(60): await process_frame
	if not await settle(): return
	camera = driver.call("CreateDedicatedCaptureCamera", game) as Camera3D
	if camera == null:
		fail("capture camera missing")
		return
	_freeze(game)
	Engine.max_fps = 0
	var viewport := root.get_viewport_rid()
	RenderingServer.viewport_set_measure_render_time(viewport, true)
	var report := {
		"status": "RUNNING",
		"purpose": "stationary scene comparison; camera/terrain/weather fixed, grass and tree processing live",
		"renderer": RenderingServer.get_current_rendering_method(),
		"adapter": RenderingServer.get_video_adapter_name(),
		"viewport_size": str(root.get_visible_rect().size),
		"preferences_copied_to": BASE,
		"quality": game.get_node("GraphicsQuality").get("CurrentPresetName"),
		"focus": driver.call("GetFocusDiagnostics", game),
		"windows": []
	}
	if not write_report(report): return
	for phase in [["cpu_both", false, false], ["gpu_grass_only", true, false], ["gpu_trees_only", false, true], ["gpu_both", true, true], ["cpu_both_after", false, false]]:
		grass.set("UseGpuCulling", phase[1])
		trees.set("UseGpuCulling", phase[2])
		var item := await sample_phase(phase[0], phase[1], phase[2])
		if not item.valid: return
		report.windows.append(item)
		if not write_report(report): return
		print("GPU_VEGETATION_SCENE_WINDOW ", JSON.stringify(item))
	RenderingServer.viewport_set_measure_render_time(viewport, false)
	report["status"] = "PASS"
	if not write_report(report): return
	print("GPU_VEGETATION_SCENE_PROFILE=PASS")
	game.queue_free()
	await process_frame
	quit(0)

func settle() -> bool:
	var stable := 0
	for i in range(1200):
		await process_frame
		if failed: return false
		var quiet := int(grass.get("PendingGrassTileBuildCount")) == 0 and int(grass.get("PendingGrassUploadCount")) == 0 \
			and int(coordinator.get("ActiveWorkerCount")) == 0 and int(coordinator.get("PendingUploadCount")) == 0 \
			and int(grid.get("PendingChunkLoadCount")) == 0 and int(grid.get("ActiveVoxelMeshWorkerCount")) == 0 \
			and int(grid.get("PendingChunkRebuildCount")) == 0 and int(grid.get("CompletedVoxelMeshCount")) == 0 \
			and int(trees.get("VegetationPendingTiles")) == 0 and (smooth == null or bool(smooth.get("IsQuiescent")))
		stable = stable + 1 if quiet else 0
		if stable >= 8 and int(grass.get("ResidentGrassInstanceCount")) > 0:
			print("GPU_VEGETATION_SCENE_SETTLED roots=", grass.get("ResidentGrassInstanceCount"), " trees=", trees.get("LastInstalledTransformCount"))
			return true
	fail("settle timeout: grass=%s trees_pending=%s terrain=%s" % [grass.get("PendingGrassTileBuildCount"), trees.get("VegetationPendingTiles"), coordinator.get("ActiveWorkerCount")])
	return false

func sample_phase(label: String, gpu_grass: bool, gpu_trees: bool) -> Dictionary:
	for i in range(WARMUP): await RenderingServer.frame_post_draw
	if failed: return {"valid": false}
	if bool(grass.get("UseGpuCulling")) != gpu_grass or bool(trees.get("UseGpuCulling")) != gpu_trees:
		fail("runtime options were overwritten during " + label)
		return {"valid": false}
	if gpu_trees and not bool(trees.get("GpuTreeCullingActive")):
		fail("GPU trees did not activate")
		return {"valid": false}
	if gpu_grass and int(grass.get("GpuCullingBatchCount")) == 0:
		fail("GPU grass did not activate")
		return {"valid": false}
	var gpu := []
	var cpu := []
	var wall := []
	var calls := []
	var prim := []
	var memory := []
	var epoch_start = trees.get("GpuTreeSourceEpoch")
	var batches_start = trees.get("GpuTreeBatchCount")
	var viewport := root.get_viewport_rid()
	for i in range(SAMPLES):
		var start := Time.get_ticks_usec()
		await RenderingServer.frame_post_draw
		if failed: return {"valid": false}
		wall.append((Time.get_ticks_usec() - start) * .001)
		gpu.append(RenderingServer.viewport_get_measured_render_time_gpu(viewport))
		cpu.append(RenderingServer.viewport_get_measured_render_time_cpu(viewport))
		calls.append(Performance.get_monitor(Performance.Monitor.RENDER_TOTAL_DRAW_CALLS_IN_FRAME))
		prim.append(Performance.get_monitor(Performance.Monitor.RENDER_TOTAL_PRIMITIVES_IN_FRAME))
		memory.append(Performance.get_monitor(Performance.Monitor.RENDER_VIDEO_MEM_USED))
	for value in gpu:
		if not is_finite(value) or value <= 0:
			fail("invalid viewport GPU timing " + label)
			return {"valid": false}
	if trees.get("GpuTreeSourceEpoch") != epoch_start or trees.get("GpuTreeBatchCount") != batches_start:
		fail("tree sources/batches changed during sampling " + label)
		return {"valid": false}
	return {
		"valid": true, "label": label, "warmup_frames": WARMUP, "samples": SAMPLES,
		"viewport_gpu_ms": stats(gpu), "viewport_cpu_ms": stats(cpu), "wall_ms": stats(wall),
		"draw_calls": stats(calls), "primitives": stats(prim), "reported_video_bytes": stats(memory),
		"grass": {"roots": grass.get("ResidentGrassInstanceCount"), "gpu_batches": grass.get("GpuCullingBatchCount"), "status": grass.get("GpuCullingStatus")},
		"trees": {"gpu_batches": trees.get("GpuTreeBatchCount"), "roots": trees.get("GpuTreeResidentRootCount"), "source_epoch": epoch_start, "status": trees.get("GpuTreeCullingStatus"), "installed_transform_count": trees.get("LastInstalledTransformCount"), "installed_transform_count_note": "resident capacity in GPU mode; CPU submitted transforms in CPU mode"}
	}

func stats(values: Array) -> Dictionary:
	var sorted := values.duplicate()
	sorted.sort()
	var sum := 0.0
	for value in sorted: sum += value
	return {"mean": sum / sorted.size(), "median": sorted[int(sorted.size() / 2)], "p95": sorted[mini(sorted.size() - 1, int(ceil(sorted.size() * .95)) - 1)], "min": sorted[0], "max": sorted[-1]}

func write_report(report: Dictionary) -> bool:
	var output := FileAccess.open(OUTPUT, FileAccess.WRITE)
	if output == null:
		fail("profile report write failed: " + OUTPUT)
		return false
	output.store_string(JSON.stringify(report, "\t"))
	output.close()
	return true

func _freeze(node: Node) -> void:
	node.process_mode = Node.PROCESS_MODE_ALWAYS if node == grass or node == trees else Node.PROCESS_MODE_DISABLED
	if node is GPUParticles3D: node.speed_scale = 0.0
	for child in node.get_children(): _freeze(child)

func fail(message: String) -> void:
	if failed: return
	failed = true
	RenderingServer.viewport_set_measure_render_time(root.get_viewport_rid(), false)
	push_error("GPU_VEGETATION_SCENE_PROFILE=FAIL " + message)
	if game != null: game.queue_free()
	quit(1)
