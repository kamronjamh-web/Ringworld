extends SceneTree

# Controlled production A/B for the compact tree LOD/render changes.  It keeps
# one land forest and camera fixed, alternates case order, and only samples once
# tree compute and streaming work have quiesced.
const SCENE := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const DRIVER := preload("res://tools/TreeCpuProfileDriver.cs")
const QUALITY := preload("res://tools/GrassQualityValidation.cs")
const BRIDGE := preload("res://tools/TreeRenderTimingBridge.cs")
const ARTIFACTS := "res://artifacts/tree-render-optimization/production/"
const WARMUP := 60
const FRAMES := 120
const REPS := 3
const SETTLE_LIMIT_MS := 120000
const LOD_READBACK_LIMIT_MS := 30000

var game: Node
var trees: Node
var grass: Node
var grid: Node
var coordinator: Node
var smooth: Node
var driver: Node
var bridge: Node
var baseline := {}
var failed := false
var writing := false
var report := {"status":"RUNNING", "purpose":"controlled production A/B for compact tree LOD, batching, distant shading, and low-detail shadows", "cases":[]}
var raw := {"cases":[]}

func _initialize() -> void:
	call_deferred("boot")

func boot() -> void:
	if RenderingServer.get_rendering_device() == null:
		fail("RenderingDevice required; headless is invalid")
		return
	Engine.max_fps = 0
	DisplayServer.window_set_vsync_mode(DisplayServer.VSYNC_DISABLED)
	DisplayServer.window_set_size(Vector2i(1280, 720))
	game = SCENE.instantiate()
	var fixture_grass := game.get_node_or_null("StandingGrass")
	if fixture_grass != null: fixture_grass.set("Enabled", false)
	var saves := game.get_node_or_null("WorldSaveManager")
	if saves != null: saves.free()
	var drawer := game.get_node_or_null("LiveTuningDrawer")
	if drawer != null: drawer.free()
	var launcher: Node = load("res://scenes/WorldSizeLauncher.tscn").instantiate()
	root.add_child(launcher)
	launcher.call("_on_certified_preset_pressed")
	launcher.call("_configure_game", game, 5000000.0)
	launcher.free()
	root.add_child(game)
	trees = game.get_node_or_null("BiomeDecorations")
	grass = game.get_node_or_null("StandingGrass")
	grid = game.get_node_or_null("ChunkGridManager")
	coordinator = game.get_node_or_null("TerrainLodCoordinator")
	smooth = game.get_node_or_null("SmoothTerrainGrid")
	if trees == null or grass == null or grid == null or coordinator == null:
		fail("required production nodes missing")
		return
	configure_common()
	var monitor := game.get_node_or_null("RingPerformanceMonitor")
	if monitor != null:
		monitor.set("Enabled", false)
		monitor.process_mode = Node.PROCESS_MODE_DISABLED
	driver = DRIVER.new()
	bridge = BRIDGE.new()
	game.add_child(driver)
	game.add_child(bridge)
	if not bool(driver.call("Prepare", game)):
		fail("tree render driver preparation failed")
		return
	var quality := QUALITY.new()
	game.add_child(quality)
	if not bool(quality.call("VerifyProfileContract")) or not bool(quality.call("FindAndPlaceGrassland", game)):
		fail("certified land anchor failed")
		return
	var forest: Dictionary = driver.call("FindAndPlaceForestNearLandFocus")
	if not bool(forest.get("valid", false)):
		fail("forest placement failed: " + str(forest))
		return
	if not await settle("forest focus"):
		return
	baseline = bridge.call("Capture", trees)
	if not valid_population(baseline):
		fail("invalid settled production tree population: " + str(baseline))
		return
	freeze_all()
	RenderingServer.viewport_set_measure_render_time(root.get_viewport_rid(), true)
	report["runtime"] = {"renderer":RenderingServer.get_current_rendering_method(), "adapter":RenderingServer.get_video_adapter_name(), "viewport":str(root.get_visible_rect().size), "appdata":OS.get_environment("APPDATA"), "forest":forest, "baseline":baseline, "settings":{"gpu_root_cap":65536, "gpu_batch_cap":1024, "shadow_batch_cap":128, "shadow_distance_m":480.0, "merge_far":true, "shared_far":true, "optimize_dispatches":true}}
	write_outputs()
	var modes := modes()
	for rep in range(REPS):
		for offset in range(modes.size()):
			var item: Dictionary = await capture_mode(modes[(offset + rep) % modes.size()], rep + 1)
			if failed: return
			report.cases.append(compact(item))
			raw.cases.append(item)
			write_outputs()
			print("TREE_RENDER_OPTIMIZATION_WINDOW ", JSON.stringify(summary(item)))
	report.status = "PASS"
	write_outputs()
	RenderingServer.viewport_set_measure_render_time(root.get_viewport_rid(), false)
	print("TREE_RENDER_OPTIMIZATION=PASS")
	game.queue_free()
	await process_frame
	quit(0)

func configure_common() -> void:
	trees.set("UseGpuCulling", true)
	trees.set("MaximumGpuTreeRoots", 65536)
	trees.set("MaximumGpuTreeBatches", 1024)
	trees.set("MergeFarGpuTreeBatches", true)
	trees.set("UseSharedFarTreeImpostors", true)
	trees.set("OptimizeGpuTreeDispatches", true)
	trees.set("TreeSunShadowDistanceMeters", 480.0)
	trees.set("MaximumTreeSunShadowBatches", 128)
	trees.set("ImportedForestCollisionRadiusMeters", 110.0)
	trees.set("MaximumImportedForestColliders", 192)
	grass.set("Enabled", false)

func modes() -> Array:
	return [
		{"name":"current_before", "UseCompactTreeLodProfile":false, "MergeMidGpuTreeBatches":false, "PruneEmptyGpuTreeBatches":false, "SimplifyDistantTreeMaterials":false, "UseLowDetailTreeShadows":false},
		{"name":"profile_only", "UseCompactTreeLodProfile":true, "MergeMidGpuTreeBatches":false, "PruneEmptyGpuTreeBatches":false, "SimplifyDistantTreeMaterials":false, "UseLowDetailTreeShadows":false},
		{"name":"batching", "UseCompactTreeLodProfile":true, "MergeMidGpuTreeBatches":true, "PruneEmptyGpuTreeBatches":true, "SimplifyDistantTreeMaterials":false, "UseLowDetailTreeShadows":false},
		{"name":"simple", "UseCompactTreeLodProfile":true, "MergeMidGpuTreeBatches":true, "PruneEmptyGpuTreeBatches":true, "SimplifyDistantTreeMaterials":true, "UseLowDetailTreeShadows":false},
		{"name":"all_on", "UseCompactTreeLodProfile":true, "MergeMidGpuTreeBatches":true, "PruneEmptyGpuTreeBatches":true, "SimplifyDistantTreeMaterials":true, "UseLowDetailTreeShadows":true}
	]

func capture_mode(mode: Dictionary, rep: int) -> Dictionary:
	trees.process_mode = Node.PROCESS_MODE_ALWAYS
	trees.visible = true
	for key in mode:
		if key != "name": trees.set(key, mode[key])
	for frame in range(4): await RenderingServer.frame_post_draw
	if not await settle("case " + str(mode.name)):
		return {}
	if not await settle_shadow_mirrors(mode):
		return {}
	var settled: Dictionary = bridge.call("Capture", trees)
	if not same_population(settled):
		fail("tree population changed while applying " + str(mode.name) + ": " + str(settled))
		return {}
	var shadow_policy := capture_shadow_policy()
	if not valid_shadow_policy(mode, shadow_policy):
		fail("invalid GPU shadow policy for " + str(mode.name) + ": " + str(shadow_policy))
		return {}
	freeze_all()
	var lod_diagnostic := await capture_lod_diagnostic()
	if lod_diagnostic.is_empty():
		fail("LOD diagnostic did not complete for " + str(mode.name))
		return {}
	for lod in lod_diagnostic:
		if int(lod_diagnostic[lod].get("unreadable_batches", 0)) != 0:
			fail("LOD readback was incomplete for " + str(mode.name))
			return {}
	if int(trees.get("GpuTreeBatchCount")) == 0 or total_visible(lod_diagnostic) == 0:
		fail("case has no installed or visible tree outputs " + str(mode.name))
		return {}
	for frame in range(WARMUP): await RenderingServer.frame_post_draw
	var gpu := []
	var cpu := []
	var wall := []
	var draws := []
	var primitives := []
	var completed_before := int(trees.get("GpuTreeCompletedUpdates"))
	var source_epoch := int(trees.get("GpuTreeSourceEpoch"))
	var last := Time.get_ticks_usec()
	for frame in range(FRAMES):
		await RenderingServer.frame_post_draw
		var now := Time.get_ticks_usec()
		wall.append((now - last) * .001)
		last = now
		gpu.append(RenderingServer.viewport_get_measured_render_time_gpu(root.get_viewport_rid()))
		cpu.append(RenderingServer.viewport_get_measured_render_time_cpu(root.get_viewport_rid()))
		draws.append(Performance.get_monitor(Performance.Monitor.RENDER_TOTAL_DRAW_CALLS_IN_FRAME))
		primitives.append(Performance.get_monitor(Performance.Monitor.RENDER_TOTAL_PRIMITIVES_IN_FRAME))
	if int(trees.get("GpuTreeCompletedUpdates")) != completed_before:
		fail("tree compute updated during frozen timed window " + str(mode.name))
		return {}
	var final_state: Dictionary = bridge.call("Capture", trees)
	if not same_population(final_state) or int(final_state.get("gpu_source_epoch", -1)) != source_epoch:
		fail("tree sources changed during timed window " + str(mode.name))
		return {}
	var final_shadow_policy := capture_shadow_policy()
	if not valid_shadow_policy(mode, final_shadow_policy):
		fail("GPU shadow policy changed during timed window " + str(mode.name) + ": " + str(final_shadow_policy))
		return {}
	if rep == 1: await screenshot(str(mode.name))
	return {"label":mode.name, "repetition":rep, "warmup_frames":WARMUP, "frames":FRAMES, "mode":mode, "settled":settled, "final":final_state, "shadow_policy":shadow_policy, "final_shadow_policy":final_shadow_policy, "lod_diagnostic":lod_diagnostic, "viewport_gpu_ms":stats(gpu), "viewport_cpu_ms":stats(cpu), "wall_ms":stats(wall), "draw_calls":stats(draws), "primitives":stats(primitives), "timeline":{"gpu_ms":gpu, "cpu_ms":cpu, "wall_ms":wall, "draw_calls":draws, "primitives":primitives}}

func capture_shadow_policy() -> Dictionary:
	return {"configured_batch_cap":int(trees.get("MaximumTreeSunShadowBatches")), "configured_distance_m":float(trees.get("TreeSunShadowDistanceMeters")), "logical_renderer_count":int(trees.get("ActiveGpuTreeSunShadowRendererCount")), "low_detail_proxy_count":int(trees.get("ActiveGpuTreeShadowProxyCount")), "pending_low_detail_proxy_count":int(trees.get("PendingGpuTreeShadowProxyCount"))}

func valid_shadow_policy(mode: Dictionary, policy: Dictionary) -> bool:
	var count := int(policy.get("logical_renderer_count", 0))
	if int(policy.get("configured_batch_cap", -1)) != 128 or not is_equal_approx(float(policy.get("configured_distance_m", -1.0)), 480.0): return false
	# The old layout should retain the production's 128 selected batches. Compact
	# LOD merging legitimately leaves fewer physical outputs eligible to cast.
	if str(mode.get("name", "")) == "current_before":
		return count == 128 and int(policy.get("low_detail_proxy_count", 0)) == 0 and int(policy.get("pending_low_detail_proxy_count", 0)) == 0
	if count <= 0 or count > 128: return false
	# Only the all-on configuration creates LOD2 shadow mirrors; require those
	# mirrors to finish copying before its timing window starts.
	if bool(mode.get("UseLowDetailTreeShadows", false)) and int(policy.get("pending_low_detail_proxy_count", 0)) != 0: return false
	return true

func settle_shadow_mirrors(mode: Dictionary) -> bool:
	if not bool(mode.get("UseLowDetailTreeShadows", false)): return true
	var deadline := Time.get_ticks_msec() + LOD_READBACK_LIMIT_MS
	while Time.get_ticks_msec() < deadline:
		if int(trees.get("PendingGpuTreeShadowProxyCount")) == 0: return true
		await RenderingServer.frame_post_draw
	fail("low-detail GPU tree shadow mirrors did not settle for " + str(mode.name))
	return false

func capture_lod_diagnostic() -> Dictionary:
	var received: Array[Dictionary] = []
	var callback := func(counts: Dictionary) -> void: received.append(counts)
	bridge.connect("GpuLodCountsReady", callback, CONNECT_ONE_SHOT)
	bridge.call("CaptureGpuLodCounts", trees)
	var deadline := Time.get_ticks_msec() + LOD_READBACK_LIMIT_MS
	while received.is_empty() and Time.get_ticks_msec() < deadline:
		await RenderingServer.frame_post_draw
	if received.is_empty():
		if bridge.is_connected("GpuLodCountsReady", callback): bridge.disconnect("GpuLodCountsReady", callback)
		return {}
	return received[0]

func total_visible(diagnostic: Dictionary) -> int:
	var total := 0
	for lod in diagnostic: total += int(diagnostic[lod].get("visible_instances", 0))
	return total

func settle(label: String) -> bool:
	var deadline := Time.get_ticks_msec() + SETTLE_LIMIT_MS
	var quiet := 0
	var progress := Time.get_ticks_msec()
	while Time.get_ticks_msec() < deadline:
		await RenderingServer.frame_post_draw
		var idle := int(coordinator.get("ActiveWorkerCount")) == 0 and int(coordinator.get("PendingUploadCount")) == 0 and int(grid.get("PendingChunkLoadCount")) == 0 and int(grid.get("ActiveVoxelMeshWorkerCount")) == 0 and int(grid.get("PendingChunkRebuildCount")) == 0 and int(grid.get("CompletedVoxelMeshCount")) == 0 and int(trees.get("VegetationPendingTiles")) == 0 and bool(trees.get("GpuTreeCullingSettled")) and not bool(trees.get("TreePreparationPending")) and int(trees.get("GpuTreeFallbackBatchCount")) == 0 and (smooth == null or bool(smooth.get("IsQuiescent")))
		quiet = quiet + 1 if idle else 0
		if Time.get_ticks_msec() - progress >= 5000:
			print("TREE_RENDER_OPTIMIZATION_PROGRESS ", label, " quiet=", quiet, " state=", bridge.call("Capture", trees))
			progress = Time.get_ticks_msec()
		if quiet >= 8: return true
	fail("settlement timeout " + label)
	return false

func valid_population(state: Dictionary) -> bool:
	return int(state.get("canonical_record_count", 0)) > 0 and int(state.get("gpu_root_count", 0)) > 0 and int(state.get("gpu_batch_count", 0)) > 0

func same_population(state: Dictionary) -> bool:
	return valid_population(state) and int(state.get("canonical_record_count", 0)) == int(baseline.get("canonical_record_count", -1)) and int(state.get("gpu_root_count", 0)) == int(baseline.get("gpu_root_count", -1))

func freeze_all() -> void:
	freeze_node(game)

func freeze_node(node: Node) -> void:
	node.process_mode = Node.PROCESS_MODE_DISABLED
	if node is GPUParticles3D: node.speed_scale = 0.0
	for child in node.get_children(): freeze_node(child)

func screenshot(label: String) -> void:
	var image := root.get_texture().get_image()
	if image == null or image.is_empty() or image.save_png(ARTIFACTS + label + ".png") != OK:
		fail("screenshot failed " + label)

func stats(values: Array) -> Dictionary:
	if values.is_empty(): return {}
	var sorted := values.duplicate()
	sorted.sort()
	var total := 0.0
	for value in sorted: total += float(value)
	return {"count":sorted.size(), "mean":total / sorted.size(), "median":sorted[int(sorted.size() / 2)], "p95":sorted[mini(sorted.size() - 1, int(ceil(sorted.size() * .95)) - 1)], "min":sorted[0], "max":sorted[-1]}

func compact(item: Dictionary) -> Dictionary:
	var result := item.duplicate(true)
	result.erase("timeline")
	return result

func summary(item: Dictionary) -> Dictionary:
	return {"label":item.get("label", ""), "repetition":item.get("repetition", 0), "gpu_mean_ms":item.get("viewport_gpu_ms", {}).get("mean", null), "draw_mean":item.get("draw_calls", {}).get("mean", null), "shadows":item.get("settled", {}).get("shadow_renderer_count", 0)}

func write_outputs() -> void:
	if writing: return
	writing = true
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(ARTIFACTS))
	for entry in [["report.json", report], ["rawdata.json", raw]]:
		var file := FileAccess.open(ARTIFACTS + str(entry[0]), FileAccess.WRITE)
		if file == null:
			push_error("TREE_RENDER_OPTIMIZATION artifact write failed")
			writing = false
			return
		file.store_string(JSON.stringify(entry[1], "\t"))
		file.close()
	writing = false

func fail(message: String) -> void:
	if failed: return
	failed = true
	report.status = "FAIL"
	report.error = message
	write_outputs()
	push_error("TREE_RENDER_OPTIMIZATION=FAIL " + message)
	if game != null: game.queue_free()
	quit(1)
