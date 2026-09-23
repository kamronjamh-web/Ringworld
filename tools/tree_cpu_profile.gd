extends SceneTree

# Opt-in production CPU capture for imported-forest streaming and collision work.
# This fixture never writes user preferences; launch it with an isolated APPDATA.
const SCENE := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const DRIVER := preload("res://tools/TreeCpuProfileDriver.cs")
const QUALITY := preload("res://tools/GrassQualityValidation.cs")
const ARTIFACTS := "res://artifacts/tree-cpu-profile/"
const WARMUP := 60
const FRAMES := 180
const REPS := 2
const SETTLE_LIMIT_MS := 120000
const TOTAL_LIMIT_MS := 1200000
var game: Node
var trees: Node
var grass: Node
var grid: Node
var coordinator: Node
var smooth: Node
var explorer: Node
var driver: Node
var failed := false
var started := 0
var last_progress := 0
var report := {"status":"RUNNING", "purpose":"production imported forest CPU profile", "note":"collision-off is diagnostic A/B only and changes GPU protected-root handling; timed paths use fixed 60 Hz simulation steps at 8 m/s", "cases":[]}
var raw := {"cases":[]}
var writing_outputs := false

func _initialize() -> void:
	started = Time.get_ticks_msec()
	call_deferred("boot")

func _process(_delta: float) -> bool:
	if not failed and Time.get_ticks_msec() - started > TOTAL_LIMIT_MS: fail("overall twenty-minute deadline")
	return false

func boot() -> void:
	if RenderingServer.get_rendering_device() == null: fail("RenderingDevice required; headless is invalid"); return
	Engine.max_fps = 60
	DisplayServer.window_set_vsync_mode(DisplayServer.VSYNC_DISABLED)
	game = SCENE.instantiate()
	var fixture_grass := game.get_node_or_null("StandingGrass")
	if fixture_grass != null: fixture_grass.set("Enabled", false)
	var saves := game.get_node_or_null("WorldSaveManager")
	if saves != null: saves.free()
	var drawer := game.get_node_or_null("LiveTuningDrawer")
	if drawer != null: drawer.free()
	var launcher: Node = load("res://scenes/WorldSizeLauncher.tscn").instantiate()
	root.add_child(launcher); launcher.call("_on_certified_preset_pressed"); launcher.call("_configure_game", game, 5000000.0); launcher.free(); root.add_child(game)
	trees = game.get_node_or_null("BiomeDecorations"); grass = game.get_node_or_null("StandingGrass"); grid = game.get_node_or_null("ChunkGridManager"); coordinator = game.get_node_or_null("TerrainLodCoordinator"); smooth = game.get_node_or_null("SmoothTerrainGrid"); explorer = game.get_node_or_null("Explorer")
	if trees == null or grass == null or grid == null or coordinator == null or explorer == null: fail("required production nodes missing"); return
	# Explicitly reproduce current production tree defaults on this disposable scene instance.
	trees.set("UseGpuCulling", true); trees.set("MaximumGpuTreeRoots", 65536); trees.set("MaximumGpuTreeBatches", 1024); trees.set("MergeFarGpuTreeBatches", true)
	trees.set("TreeSunShadowDistanceMeters", 480.0); trees.set("MaximumTreeSunShadowBatches", 128); trees.set("ImportedForestCollisionRadiusMeters", 110.0); trees.set("MaximumImportedForestColliders", 192)
	grass.set("Enabled", false)
	var monitor := game.get_node_or_null("RingPerformanceMonitor")
	if monitor != null: monitor.set("Enabled", false); monitor.process_mode = Node.PROCESS_MODE_DISABLED
	driver = DRIVER.new(); game.add_child(driver)
	if not bool(driver.call("Prepare", game)): fail("tree CPU driver preparation failed"); return
	# Reuse the production-certified dry-land anchor before selecting the nearby forest by sampler weights.
	var quality := QUALITY.new(); game.add_child(quality)
	if not bool(quality.call("VerifyProfileContract")) or not bool(quality.call("FindAndPlaceGrassland", game)): fail("certified land anchor placement failed"); return
	var forest: Dictionary = driver.call("FindAndPlaceForestNearLandFocus")
	if not bool(forest.get("valid", false)): fail("direct terrain sampler could not find forest near certified land anchor: " + str(forest)); return
	print("TREE_CPU_PROFILE_FOREST_FOCUS ", JSON.stringify(forest))
	if not await settle_world("forest focus settlement"): return
	if not await verify_population("forest focus"): return
	freeze_except(game, [trees])
	RenderingServer.viewport_set_measure_render_time(root.get_viewport_rid(), true)
	# The explorer stays disabled for input/physics, while the driver writes deterministic canonical positions.
	report["runtime"] = {"renderer":RenderingServer.get_current_rendering_method(), "adapter":RenderingServer.get_video_adapter_name(), "appdata":OS.get_environment("APPDATA"), "engine_max_fps":Engine.max_fps, "focus":driver.call("CaptureFocus"), "settings":actual_settings()}
	write_outputs()
	for repetition in range(REPS):
		var collision_order := [true, false] if repetition % 2 == 0 else [false, true]
		for collisions_on in collision_order:
			for path in ["stationary", "walking"]:
				var item := await capture_window(collisions_on, path, repetition + 1)
				if failed: return
				report.cases.append(compact_case(item)); raw.cases.append(item); write_outputs()
				print("TREE_CPU_PROFILE_WINDOW ", JSON.stringify(stdout_case(item)))
	report.status = "PASS"; write_outputs(); RenderingServer.viewport_set_measure_render_time(root.get_viewport_rid(), false); print("TREE_CPU_PROFILE=PASS")
	game.queue_free(); await process_frame; quit(0)

func capture_window(collisions_on: bool, path: String, repetition: int) -> Dictionary:
	trees.set("EnableImportedForestCollisions", collisions_on)
	driver.call("MoveRoute", 0.0, 0.0)
	if not await settle_world("case setup %s %s" % [str(collisions_on), path]): return {}
	if not await verify_population("case setup"): return {}
	for frame in range(WARMUP):
		if path == "walking": driver.call("MoveRoute", float(frame) / 60.0 * 8.0, 8.0)
		await RenderingServer.frame_post_draw
	driver.call("BeginTelemetry")
	var wall := []; var viewport_cpu := []; var frame_times := []; var poses := []
	var last := Time.get_ticks_usec(); var begin_gc: Dictionary = driver.call("CaptureGc")
	for frame in range(FRAMES):
		var offset := 0.0 if path == "stationary" else float(frame + WARMUP) / 60.0 * 8.0
		driver.call("MoveRoute", offset, 8.0 if path == "walking" else 0.0); poses.append(offset)
		await RenderingServer.frame_post_draw
		var now := Time.get_ticks_usec(); var ms := (now - last) * .001; last = now
		wall.append(ms); frame_times.append(ms); viewport_cpu.append(RenderingServer.viewport_get_measured_render_time_cpu(root.get_viewport_rid()))
		driver.call("SampleTelemetry")
	trees.process_mode = Node.PROCESS_MODE_DISABLED
	for tail in range(8): await RenderingServer.frame_post_draw; driver.call("SampleTelemetry")
	trees.process_mode = Node.PROCESS_MODE_ALWAYS
	var telemetry: Dictionary = driver.call("FinishTelemetry"); var end_gc: Dictionary = driver.call("CaptureGc")
	var diagnostics: Dictionary = trees.call("GetImportedForestRuntimeDiagnostics")
	if collisions_on and int(diagnostics.get("collision_count", 0)) <= 0: fail("collision-on window has no installed forest colliders"); return {}
	var scopes: Array = telemetry.get("scopes", [])
	if collisions_on and path == "walking" and not scope_seen(scopes, "Trees protected-metadata refresh"): fail("collision-on walking window did not trigger protected-metadata refresh"); return {}
	if collisions_on and path == "stationary" and scope_count(scopes, "Trees collision scan/sort") > 2: fail("settled stationary collision window repeatedly scanned forest roots"); return {}
	return {"label":("collisions_on" if collisions_on else "collisions_off") + "_" + path, "path":"fixed 60 Hz simulation steps at 8 m/s" if path == "walking" else "stationary", "repetition":repetition, "warmup_frames":WARMUP, "frames":FRAMES, "actual_settings":actual_settings(), "population":diagnostics, "settlement":"settled", "wall_ms":stats(wall), "frame_ms":stats(frame_times), "viewport_cpu_ms":stats(viewport_cpu), "allocation_begin":begin_gc, "allocation_end":end_gc, "allocation_delta_bytes":int(end_gc.get("allocated_bytes", 0)) - int(begin_gc.get("allocated_bytes", 0)), "telemetry":telemetry, "timeline":{"offset_meters":poses, "wall_ms":wall, "viewport_cpu_ms":viewport_cpu}}

func verify_population(stage: String) -> bool:
	var d: Dictionary = trees.call("GetImportedForestRuntimeDiagnostics")
	if int(d.get("canonical_record_count", 0)) <= 0 or int(d.get("gpu_resident_root_count", 0)) <= 0 or int(d.get("gpu_batch_count", 0)) <= 0 or not bool(d.get("gpu_active", false)):
		fail("invalid or GPU-unready forest population at " + stage + ": " + str(d)); return false
	return true

func settle_world(label: String) -> bool:
	var deadline := Time.get_ticks_msec() + SETTLE_LIMIT_MS; var quiet := 0; last_progress = Time.get_ticks_msec()
	while Time.get_ticks_msec() < deadline:
		await RenderingServer.frame_post_draw
		var idle := int(coordinator.get("ActiveWorkerCount")) == 0 and int(coordinator.get("PendingUploadCount")) == 0 and int(grid.get("PendingChunkLoadCount")) == 0 and int(grid.get("ActiveVoxelMeshWorkerCount")) == 0 and int(grid.get("PendingChunkRebuildCount")) == 0 and int(grid.get("CompletedVoxelMeshCount")) == 0 and int(trees.get("VegetationPendingTiles")) == 0 and bool(trees.get("GpuTreeCullingSettled")) and not bool(trees.get("TreePreparationPending")) and int(trees.get("GpuTreeFallbackBatchCount")) == 0 and (smooth == null or bool(smooth.get("IsQuiescent")))
		quiet = quiet + 1 if idle else 0
		if Time.get_ticks_msec() - last_progress >= 5000: print("TREE_CPU_PROFILE_PROGRESS ", label, " quiet=", quiet, " diagnostics=", trees.call("GetImportedForestRuntimeDiagnostics")); last_progress = Time.get_ticks_msec()
		if quiet >= 8: return true
	fail("settlement timeout: " + label); return false

func actual_settings() -> Dictionary:
	var result := {}
	for key in ["UseGpuCulling","MaximumGpuTreeRoots","MaximumGpuTreeBatches","MergeFarGpuTreeBatches","UseSharedFarTreeImpostors","OptimizeGpuTreeDispatches","TreeCullingMovementThresholdMeters","TreeCullingMaximumIntervalSeconds","TreeSunShadowDistanceMeters","MaximumTreeSunShadowBatches","EnableImportedForestCollisions","ImportedForestCollisionRadiusMeters","MaximumImportedForestColliders"]: result[key] = trees.get(key)
	return result

func scope_seen(scopes: Array, name: String) -> bool:
	for scope in scopes:
		if str(scope.get("name", "")) == name: return true
	return false

func scope_count(scopes: Array, name: String) -> int:
	var count := 0
	for scope in scopes:
		if str(scope.get("name", "")) == name: count += 1
	return count

func stats(values: Array) -> Dictionary:
	if values.is_empty(): return {}
	var sorted := values.duplicate(); sorted.sort(); var total := 0.0
	for value in sorted: total += float(value)
	return {"count":sorted.size(), "mean":total / sorted.size(), "median":sorted[int(sorted.size() / 2)], "p95":sorted[mini(sorted.size() - 1, int(ceil(sorted.size() * .95)) - 1)], "min":sorted[0], "max":sorted[-1]}

func compact_case(item: Dictionary) -> Dictionary:
	var result := item.duplicate(true); result.erase("timeline"); return result

func stdout_case(item: Dictionary) -> Dictionary:
	return {"label":item.get("label", ""), "repetition":item.get("repetition", 0), "wall_mean_ms":item.get("wall_ms", {}).get("mean", null), "frame_p95_ms":item.get("frame_ms", {}).get("p95", null), "scopes":item.get("telemetry", {}).get("scopes", []).size(), "population":item.get("population", {})}

func write_outputs() -> void:
	if writing_outputs: return
	writing_outputs = true
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(ARTIFACTS))
	for entry in [["report.json", report], ["rawdata.json", raw]]:
		var file := FileAccess.open(ARTIFACTS + str(entry[0]), FileAccess.WRITE)
		if file == null: push_error("TREE_CPU_PROFILE artifact write failed " + str(entry[0])); writing_outputs = false; return
		file.store_string(JSON.stringify(entry[1], "\t")); file.close()
	writing_outputs = false

func freeze_except(node: Node, active: Array) -> void:
	node.process_mode = Node.PROCESS_MODE_ALWAYS if node in active else Node.PROCESS_MODE_DISABLED
	if node in active: return # Preserve the active tree layer's physics bodies and inherited child processing.
	if node is GPUParticles3D: node.speed_scale = 0.0
	for child in node.get_children(): freeze_except(child, active)

func fail(message: String) -> void:
	if failed: return
	failed = true; report.status = "FAIL"; report.error = message; write_outputs(); push_error("TREE_CPU_PROFILE=FAIL " + message)
	if game != null: game.queue_free()
	quit(1)
