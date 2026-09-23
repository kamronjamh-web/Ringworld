extends SceneTree

# Opt-in, full-production render attribution for settled imported-tree visibility and shadow modes.
const SCENE := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const DRIVER := preload("res://tools/TreeCpuProfileDriver.cs")
const QUALITY := preload("res://tools/GrassQualityValidation.cs")
const BRIDGE := preload("res://tools/TreeRenderTimingBridge.cs")
const ARTIFACTS := "res://artifacts/tree-render-production/"
const SHARED_ARTIFACTS := "res://artifacts/tree-shared-far-production/"
const WARMUP := 60
const FRAMES := 120
const REPS := 3
const SETTLE_LIMIT_MS := 120000
var game: Node
var trees: Node
var grass: Node
var grid: Node
var coordinator: Node
var smooth: Node
var driver: Node
var bridge: Node
var failed := false
var writing := false
var baseline := {}
var report := {"status":"RUNNING", "purpose":"settled production imported-tree render attribution", "cases":[]}
var raw := {"cases":[]}
var shared_far_ablation := false
var artifacts := ARTIFACTS

func _initialize() -> void: call_deferred("boot")

func boot() -> void:
	if RenderingServer.get_rendering_device() == null: fail("RenderingDevice required; headless is invalid"); return
	Engine.max_fps = 0; DisplayServer.window_set_vsync_mode(DisplayServer.VSYNC_DISABLED); DisplayServer.window_set_size(Vector2i(1280, 720))
	game = SCENE.instantiate()
	var fixture_grass := game.get_node_or_null("StandingGrass")
	if fixture_grass != null: fixture_grass.set("Enabled", false)
	var saves := game.get_node_or_null("WorldSaveManager")
	if saves != null: saves.free()
	var drawer := game.get_node_or_null("LiveTuningDrawer")
	if drawer != null: drawer.free()
	var launcher: Node = load("res://scenes/WorldSizeLauncher.tscn").instantiate()
	root.add_child(launcher); launcher.call("_on_certified_preset_pressed"); launcher.call("_configure_game", game, 5000000.0); launcher.free(); root.add_child(game)
	trees = game.get_node_or_null("BiomeDecorations"); grass = game.get_node_or_null("StandingGrass"); grid = game.get_node_or_null("ChunkGridManager"); coordinator = game.get_node_or_null("TerrainLodCoordinator"); smooth = game.get_node_or_null("SmoothTerrainGrid")
	if trees == null or grass == null or grid == null or coordinator == null: fail("required production nodes missing"); return
	shared_far_ablation = OS.get_cmdline_user_args().has("--shared-far-ablation")
	if shared_far_ablation: artifacts = SHARED_ARTIFACTS; report.purpose = "settled production shared far-tree impostor A/B attribution"
	trees.set("UseGpuCulling", true); trees.set("MaximumGpuTreeRoots", 65536); trees.set("MaximumGpuTreeBatches", 1024); trees.set("MergeFarGpuTreeBatches", true)
	trees.set("TreeSunShadowDistanceMeters", 480.0); trees.set("MaximumTreeSunShadowBatches", 128); trees.set("ImportedForestCollisionRadiusMeters", 110.0); trees.set("MaximumImportedForestColliders", 192)
	grass.set("Enabled", false)
	var monitor := game.get_node_or_null("RingPerformanceMonitor")
	if monitor != null: monitor.set("Enabled", false); monitor.process_mode = Node.PROCESS_MODE_DISABLED
	driver = DRIVER.new(); bridge = BRIDGE.new(); game.add_child(driver); game.add_child(bridge)
	if not bool(driver.call("Prepare", game)): fail("tree render driver preparation failed"); return
	var quality := QUALITY.new(); game.add_child(quality)
	if not bool(quality.call("VerifyProfileContract")) or not bool(quality.call("FindAndPlaceGrassland", game)): fail("certified land anchor failed"); return
	var forest: Dictionary = driver.call("FindAndPlaceForestNearLandFocus")
	if not bool(forest.get("valid", false)): fail("forest placement failed: " + str(forest)); return
	if not await settle("forest focus"): return
	baseline = bridge.call("Capture", trees)
	if not valid_population(baseline): fail("invalid settled production tree population: " + str(baseline)); return
	freeze_all()
	RenderingServer.viewport_set_measure_render_time(root.get_viewport_rid(), true)
	report["runtime"] = {"renderer":RenderingServer.get_current_rendering_method(), "adapter":RenderingServer.get_video_adapter_name(), "viewport":str(root.get_visible_rect().size), "appdata":OS.get_environment("APPDATA"), "forest":forest, "baseline":baseline, "shared_far_ablation":shared_far_ablation, "settings":{"gpu_root_cap":65536, "gpu_batch_cap":1024, "shadow_batch_cap":128, "shadow_distance_m":480.0, "merge_far":true, "shared_far":bool(trees.get("UseSharedFarTreeImpostors"))}}
	write_outputs()
	if shared_far_ablation:
		await run_shared_far_ablation()
		if failed: return
		report.status = "PASS"; write_outputs(); RenderingServer.viewport_set_measure_render_time(root.get_viewport_rid(), false); print("TREE_RENDER_PRODUCTION=PASS shared far ablation")
		game.queue_free(); await process_frame; quit(0); return
	var modes := [{"name":"trees_128shadow_480m", "visible":true, "batches":128, "distance":480.0}, {"name":"trees_64shadow_480m", "visible":true, "batches":64, "distance":480.0}, {"name":"trees_128shadow_240m", "visible":true, "batches":128, "distance":240.0}, {"name":"trees_shadows0", "visible":true, "batches":0, "distance":480.0}, {"name":"trees_hidden", "visible":false, "batches":128, "distance":480.0}]
	for rep in range(REPS):
		for index in range(modes.size()):
			var mode: Dictionary = modes[(index + rep) % modes.size()]
			var item := await capture_mode(mode, rep + 1)
			if failed: return
			report.cases.append(compact(item)); raw.cases.append(item); write_outputs(); print("TREE_RENDER_PRODUCTION_WINDOW ", JSON.stringify(summary(item)))
	report.status = "PASS"; write_outputs(); RenderingServer.viewport_set_measure_render_time(root.get_viewport_rid(), false); print("TREE_RENDER_PRODUCTION=PASS")
	game.queue_free(); await process_frame; quit(0)

func capture_mode(mode: Dictionary, rep: int) -> Dictionary:
	# Let only the production tree layer apply changed exported shadow settings at the fixed camera pose.
	trees.process_mode = Node.PROCESS_MODE_ALWAYS; trees.visible = true
	trees.set("TreeSunShadowDistanceMeters", mode.distance); trees.set("MaximumTreeSunShadowBatches", mode.batches)
	for frame in range(4): await RenderingServer.frame_post_draw
	if not await settle("shadow policy " + mode.name): return {}
	var settled: Dictionary = bridge.call("Capture", trees)
	if not same_sources(settled): fail("tree sources changed while applying shadow mode " + mode.name + ": " + str(settled)); return {}
	freeze_all(); trees.visible = bool(mode.visible)
	for frame in range(WARMUP): await RenderingServer.frame_post_draw
	var gpu := []; var cpu := []; var wall := []; var draws := []; var primitives := []
	var completed_before := int(trees.get("GpuTreeCompletedUpdates")); var last := Time.get_ticks_usec()
	for frame in range(FRAMES):
		await RenderingServer.frame_post_draw
		var now := Time.get_ticks_usec(); wall.append((now - last) * .001); last = now
		gpu.append(RenderingServer.viewport_get_measured_render_time_gpu(root.get_viewport_rid())); cpu.append(RenderingServer.viewport_get_measured_render_time_cpu(root.get_viewport_rid()))
		draws.append(Performance.get_monitor(Performance.Monitor.RENDER_TOTAL_DRAW_CALLS_IN_FRAME)); primitives.append(Performance.get_monitor(Performance.Monitor.RENDER_TOTAL_PRIMITIVES_IN_FRAME))
	var completed_after := int(trees.get("GpuTreeCompletedUpdates"))
	if completed_after != completed_before: fail("tree compute updated during frozen timed window " + mode.name); return {}
	var final_state: Dictionary = bridge.call("Capture", trees)
	if not same_sources(final_state): fail("tree sources changed during frozen timed window " + mode.name); return {}
	if rep == 1: await screenshot(str(mode.name))
	return {"label":mode.name, "repetition":rep, "warmup_frames":WARMUP, "frames":FRAMES, "mode":mode, "settled":settled, "final":final_state, "viewport_gpu_ms":stats(gpu), "viewport_cpu_ms":stats(cpu), "wall_ms":stats(wall), "draw_calls":stats(draws), "primitives":stats(primitives), "timeline":{"gpu_ms":gpu, "cpu_ms":cpu, "wall_ms":wall, "draw_calls":draws, "primitives":primitives}}

func run_shared_far_ablation() -> void:
	# Alternating modes avoids attributing thermal or frame-order drift to either format.
	var modes := [false, true]
	for rep in range(REPS):
		for offset in range(modes.size()):
			var shared: bool = bool(modes[(offset + rep) % modes.size()])
			var item := await capture_shared_far_mode(shared, rep + 1)
			if failed: return
			report.cases.append(compact(item)); raw.cases.append(item); write_outputs(); print("TREE_SHARED_FAR_PRODUCTION_WINDOW ", JSON.stringify(summary(item)))
	report["lod_visible_instance_comparison"] = shared_far_lod_comparison()

func shared_far_lod_comparison() -> Dictionary:
	var totals := {"false":{}, "true":{}}
	var samples := {"false":0, "true":0}
	for item in raw.cases:
		var key := str(bool(item.get("mode", {}).get("shared_far", false)))
		samples[key] = int(samples[key]) + 1
		var lods: Dictionary = item.get("lod_diagnostic", {})
		for lod in lods:
			var entry: Dictionary = lods[lod]
			var prior: Dictionary = totals[key].get(str(lod), {"batches":0, "visible_instances":0})
			prior.batches = int(prior.batches) + int(entry.get("batches", 0)); prior.visible_instances = int(prior.visible_instances) + int(entry.get("visible_instances", 0))
			totals[key][str(lod)] = prior
	for key in totals:
		for lod in totals[key]:
			totals[key][lod].batches = float(totals[key][lod].batches) / maxi(1, int(samples[key]))
			totals[key][lod].visible_instances = float(totals[key][lod].visible_instances) / maxi(1, int(samples[key]))
	return {"original_shared_false":totals["false"], "shared_true":totals["true"], "repetitions":{"false":samples["false"], "true":samples["true"]}}

func capture_shared_far_mode(shared: bool, rep: int) -> Dictionary:
	trees.process_mode = Node.PROCESS_MODE_ALWAYS; trees.visible = true
	trees.set("UseSharedFarTreeImpostors", shared)
	for frame in range(4): await RenderingServer.frame_post_draw
	if not await settle("shared far " + str(shared)): return {}
	var settled: Dictionary = bridge.call("Capture", trees)
	if not same_population(settled): fail("tree population changed while applying shared far=" + str(shared) + ": " + str(settled)); return {}
	freeze_all()
	var diagnostic: Dictionary = await capture_lod_diagnostic()
	if diagnostic.is_empty(): fail("shared far LOD diagnostic did not complete"); return {}
	for lod in diagnostic:
		if int(diagnostic[lod].get("unreadable_batches", 0)) != 0: fail("shared far LOD readback was incomplete"); return {}
	freeze_all(); trees.visible = true
	for frame in range(WARMUP): await RenderingServer.frame_post_draw
	var gpu := []; var cpu := []; var wall := []; var draws := []; var primitives := []
	var source_epoch := int(trees.get("GpuTreeSourceEpoch")); var completed_before := int(trees.get("GpuTreeCompletedUpdates")); var last := Time.get_ticks_usec()
	for frame in range(FRAMES):
		await RenderingServer.frame_post_draw
		var now := Time.get_ticks_usec(); wall.append((now - last) * .001); last = now
		gpu.append(RenderingServer.viewport_get_measured_render_time_gpu(root.get_viewport_rid())); cpu.append(RenderingServer.viewport_get_measured_render_time_cpu(root.get_viewport_rid()))
		draws.append(Performance.get_monitor(Performance.Monitor.RENDER_TOTAL_DRAW_CALLS_IN_FRAME)); primitives.append(Performance.get_monitor(Performance.Monitor.RENDER_TOTAL_PRIMITIVES_IN_FRAME))
	if int(trees.get("GpuTreeCompletedUpdates")) != completed_before: fail("tree compute updated during shared far timed window"); return {}
	var final_state: Dictionary = bridge.call("Capture", trees)
	if not same_population(final_state) or int(final_state.get("gpu_source_epoch", -1)) != source_epoch: fail("tree sources changed during shared far timed window"); return {}
	if rep == 1: await screenshot("shared_far_" + str(shared))
	return {"label":"shared_far_" + str(shared), "repetition":rep, "warmup_frames":WARMUP, "frames":FRAMES, "mode":{"shared_far":shared, "visible":true, "batches":128, "distance":480.0}, "settled":settled, "final":final_state, "lod_diagnostic":diagnostic, "viewport_gpu_ms":stats(gpu), "viewport_cpu_ms":stats(cpu), "wall_ms":stats(wall), "draw_calls":stats(draws), "primitives":stats(primitives), "timeline":{"gpu_ms":gpu, "cpu_ms":cpu, "wall_ms":wall, "draw_calls":draws, "primitives":primitives}}

func capture_lod_diagnostic() -> Dictionary:
	var received: Array[Dictionary] = []
	var callback := func(counts: Dictionary) -> void: received.append(counts)
	bridge.connect("GpuLodCountsReady", callback, CONNECT_ONE_SHOT)
	bridge.call("CaptureGpuLodCounts", trees)
	var deadline := Time.get_ticks_msec() + 30000
	while received.is_empty() and Time.get_ticks_msec() < deadline:
		await RenderingServer.frame_post_draw
	if received.is_empty():
		if bridge.is_connected("GpuLodCountsReady", callback): bridge.disconnect("GpuLodCountsReady", callback)
		return {}
	return received[0]

func settle(label: String) -> bool:
	var deadline := Time.get_ticks_msec() + SETTLE_LIMIT_MS; var quiet := 0; var progress := Time.get_ticks_msec()
	while Time.get_ticks_msec() < deadline:
		await RenderingServer.frame_post_draw
		var idle := int(coordinator.get("ActiveWorkerCount")) == 0 and int(coordinator.get("PendingUploadCount")) == 0 and int(grid.get("PendingChunkLoadCount")) == 0 and int(grid.get("ActiveVoxelMeshWorkerCount")) == 0 and int(grid.get("PendingChunkRebuildCount")) == 0 and int(grid.get("CompletedVoxelMeshCount")) == 0 and int(trees.get("VegetationPendingTiles")) == 0 and bool(trees.get("GpuTreeCullingSettled")) and not bool(trees.get("TreePreparationPending")) and int(trees.get("GpuTreeFallbackBatchCount")) == 0 and (smooth == null or bool(smooth.get("IsQuiescent")))
		quiet = quiet + 1 if idle else 0
		if Time.get_ticks_msec() - progress >= 5000: print("TREE_RENDER_PRODUCTION_PROGRESS ", label, " quiet=", quiet, " state=", bridge.call("Capture", trees)); progress = Time.get_ticks_msec()
		if quiet >= 8: return true
	fail("settlement timeout " + label); return false

func valid_population(state: Dictionary) -> bool:
	return int(state.get("canonical_record_count", 0)) > 0 and int(state.get("gpu_root_count", 0)) > 0 and int(state.get("gpu_batch_count", 0)) > 0

func same_sources(state: Dictionary) -> bool:
	return valid_population(state) and int(state.get("canonical_record_count", 0)) == int(baseline.get("canonical_record_count", -1)) and int(state.get("gpu_root_count", 0)) == int(baseline.get("gpu_root_count", -1)) and int(state.get("gpu_batch_count", 0)) == int(baseline.get("gpu_batch_count", -1)) and int(state.get("gpu_source_epoch", 0)) == int(baseline.get("gpu_source_epoch", -1))

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
	if image == null or image.is_empty() or image.save_png(artifacts + label + ".png") != OK: fail("screenshot failed " + label)

func stats(values: Array) -> Dictionary:
	if values.is_empty(): return {}
	var sorted := values.duplicate(); sorted.sort(); var total := 0.0
	for value in sorted: total += float(value)
	return {"count":sorted.size(), "mean":total / sorted.size(), "median":sorted[int(sorted.size() / 2)], "p95":sorted[mini(sorted.size() - 1, int(ceil(sorted.size() * .95)) - 1)], "min":sorted[0], "max":sorted[-1]}

func compact(item: Dictionary) -> Dictionary:
	var result := item.duplicate(true); result.erase("timeline"); return result

func summary(item: Dictionary) -> Dictionary:
	return {"label":item.get("label", ""), "repetition":item.get("repetition", 0), "gpu_mean_ms":item.get("viewport_gpu_ms", {}).get("mean", null), "draw_mean":item.get("draw_calls", {}).get("mean", null), "shadows":item.get("settled", {}).get("shadow_renderer_count", 0)}

func write_outputs() -> void:
	if writing: return
	writing = true; DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(artifacts))
	for entry in [["report.json", report], ["rawdata.json", raw]]:
		var file := FileAccess.open(artifacts + str(entry[0]), FileAccess.WRITE)
		if file == null: push_error("TREE_RENDER_PRODUCTION artifact write failed"); writing = false; return
		file.store_string(JSON.stringify(entry[1], "\t")); file.close()
	writing = false

func fail(message: String) -> void:
	if failed: return
	failed = true; report.status = "FAIL"; report.error = message; write_outputs(); push_error("TREE_RENDER_PRODUCTION=FAIL " + message)
	if game != null: game.queue_free()
	quit(1)
