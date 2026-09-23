extends SceneTree

# Stationary raster attribution. Simulation and streaming are frozen before
# sampling, so this cannot attribute their CPU cost (including FFT updates).
const SCENE := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const DRIVER := preload("res://tools/GrassQualityValidation.cs")
const BASE := "res://.godot/render-feature-cost-profile/"
const OUTPUT := "res://artifacts/render-feature-cost/profile.json"
const WARMUP := 60
const SAMPLES := 90
var game: Node
var grass: Node
var trees: Node
var grid: Node
var coordinator: Node
var smooth: Node
var explorer: Node
var graphics: Node
var sun: DirectionalLight3D
var environment: Environment
var compositor: Compositor
var baseline := {}
var failed := false
var frames := 0
var started := 0

func _initialize() -> void:
	started = Time.get_ticks_msec()
	call_deferred("boot")

func _process(_delta: float) -> bool:
	frames += 1
	if not failed and (frames > 12000 or Time.get_ticks_msec() - started > 240000): fail("diagnostic deadline")
	return false

func boot() -> void:
	if RenderingServer.get_rendering_device() == null: fail("RenderingDevice required"); return
	Engine.max_fps = 60; DisplayServer.window_set_vsync_mode(DisplayServer.VSYNC_DISABLED)
	ProjectSettings.set_setting("ringworld/runtime/active_world_directory", "")
	game = SCENE.instantiate()
	var saves := game.get_node_or_null("WorldSaveManager")
	if saves != null: saves.free()
	var drawer := game.get_node_or_null("LiveTuningDrawer")
	if drawer == null: fail("LiveTuningDrawer missing"); return
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(BASE))
	for item in [["InstallGrassDefaults","gameplay_grass_defaults.tres"], ["InstallVegetationDefaults","gameplay_vegetation_defaults.tres"], ["InstallWaterDefaults","gameplay_water_defaults.tres"], ["InstallAntiAliasingDefaults","gameplay_anti_aliasing_defaults.tres"], ["InstallShadowDefaults","gameplay_shadow_defaults.tres"]]:
		var source := "user://" + str(item[1]); var isolated := BASE + str(item[1])
		if FileAccess.file_exists(source):
			var copy := FileAccess.open(isolated, FileAccess.WRITE)
			if copy == null: fail("isolated preferences copy failed"); return
			copy.store_string(FileAccess.get_file_as_string(source)); copy.close()
		elif FileAccess.file_exists(isolated): DirAccess.remove_absolute(ProjectSettings.globalize_path(isolated))
		drawer.call(item[0], isolated)
	var launcher: Node = load("res://scenes/WorldSizeLauncher.tscn").instantiate()
	root.add_child(launcher); launcher.call("_on_certified_preset_pressed"); launcher.call("_configure_game", game, 5000000.0); launcher.free(); root.add_child(game)
	grass = game.get_node_or_null("StandingGrass"); trees = game.get_node_or_null("BiomeDecorations"); grid = game.get_node_or_null("ChunkGridManager"); coordinator = game.get_node_or_null("TerrainLodCoordinator"); smooth = game.get_node_or_null("SmoothTerrainGrid"); explorer = game.find_child("Explorer", true, false); graphics = game.get_node_or_null("GraphicsQuality"); sun = game.get_node_or_null("StarLight") as DirectionalLight3D
	var world := game.get_node_or_null("WorldEnvironment") as WorldEnvironment
	if grass == null or trees == null or grid == null or coordinator == null or explorer == null or graphics == null or sun == null or world == null or world.environment == null: fail("required world feature missing"); return
	grass.set("UseGpuCulling", false); trees.set("UseGpuCulling", false)
	for i in range(12): await process_frame
	var driver := DRIVER.new(); game.add_child(driver)
	if not bool(driver.call("VerifyProfileContract")) or not bool(driver.call("FindAndPlaceGrassland", game)): fail("certified grassland placement failed"); return
	for i in range(60): await process_frame
	if not await settle(1200): return
	environment = world.environment; compositor = world.compositor
	if compositor == null: fail("main WorldEnvironment compositor unavailable after scene startup"); return
	var camera := find_camera(explorer)
	if camera == null: fail("Explorer Camera3D missing"); return
	camera.make_current(); explorer.call("SetPresentationView", 0.0, -10.0); explorer.process_mode = Node.PROCESS_MODE_DISABLED
	_freeze_except_layers(game)
	grass.set("UseGpuCulling", true); grass.set("SkipUnchangedGpuCulling", true); trees.set("UseGpuCulling", true); trees.set("SkipUnchangedGpuCulling", true); trees.set("MergeFarGpuTreeBatches", true)
	if not await settle_gpu(180): return
	_freeze_all(game)
	Engine.max_fps = 0
	RenderingServer.viewport_set_measure_render_time(root.get_viewport_rid(), true)
	baseline = capture_baseline()
	var report := {"status":"RUNNING", "purpose":"stationary raster/effect attribution after all simulation processing is frozen", "limitation":"streaming, FFT, and other simulation CPU work are frozen and cannot be attributed", "renderer":RenderingServer.get_current_rendering_method(), "adapter":RenderingServer.get_video_adapter_name(), "viewport_size":str(root.get_visible_rect().size), "quality":graphics.get("CurrentPresetName"), "focus":driver.call("GetFocusDiagnostics", game), "preferences_copied_to":BASE, "windows":[]}
	if not write_report(report): return
	for phase in ["baseline", "sun_shadows_off", "atlas_16384", "atlas_8192", "shadow_filter_0", "trees_hidden", "grass_hidden", "compositors_off", "environment_effects_off", "baseline_repeat"]:
		restore_baseline()
		if not apply_phase(phase) or not verify_phase(phase): return
		var item := await sample(phase)
		if not item.valid or not verify_phase(phase): return
		report.windows.append(item); print("RENDER_FEATURE_COST_WINDOW ", JSON.stringify(item))
		if not write_report(report): return
	restore_baseline(); RenderingServer.viewport_set_measure_render_time(root.get_viewport_rid(), false)
	report.status = "PASS"
	if not write_report(report): return
	print("RENDER_FEATURE_COST_PROFILE=PASS")
	game.queue_free(); await process_frame; quit(0)

func settle(limit: int) -> bool:
	var quiet := 0
	for i in range(limit):
		await process_frame
		var idle := int(grass.get("PendingGrassTileBuildCount")) == 0 and int(grass.get("PendingGrassUploadCount")) == 0 and int(coordinator.get("ActiveWorkerCount")) == 0 and int(coordinator.get("PendingUploadCount")) == 0 and int(grid.get("PendingChunkLoadCount")) == 0 and int(grid.get("ActiveVoxelMeshWorkerCount")) == 0 and int(grid.get("PendingChunkRebuildCount")) == 0 and int(grid.get("CompletedVoxelMeshCount")) == 0 and int(trees.get("VegetationPendingTiles")) == 0 and (smooth == null or bool(smooth.get("IsQuiescent")))
		quiet = quiet + 1 if idle else 0
		if quiet >= 8 and int(grass.get("ResidentGrassInstanceCount")) > 0: return true
	fail("world settle timeout"); return false

func settle_gpu(limit: int) -> bool:
	for i in range(limit):
		await process_frame
		if int(grass.get("GpuCullingBatchCount")) > 0 and int(grass.get("GpuCullingCompletedUpdates")) > 0 and bool(trees.get("GpuTreeCullingSettled")) and int(trees.get("GpuTreeCompletedUpdates")) > 0: return true
	fail("GPU vegetation settle timeout"); return false

func capture_baseline() -> Dictionary:
	var effects := []
	for effect in compositor.compositor_effects: effects.append({"effect":effect, "enabled":effect.enabled, "script_path":effect.get_script().resource_path if effect.get_script() != null else ""})
	var screen := {}
	for key in ["ssao_enabled","ssil_enabled","ssr_enabled","sdfgi_enabled","volumetric_fog_enabled"]: screen[key] = environment.get(key)
	var aa := {}
	for key in ["use_taa", "msaa_3d", "screen_space_aa", "scaling_3d_mode", "scaling_3d_scale"]: aa[key] = root.get(key)
	return {"sun_shadow":sun.shadow_enabled, "trees_visible":trees.visible, "grass_visible":grass.visible, "effects":effects, "screen":screen, "aa":aa, "atlas":graphics.get("EffectiveDirectionalShadowAtlasSize"), "filter":graphics.get("EffectiveDirectionalShadowFilter")}

func restore_baseline() -> void:
	graphics.call("SetShadowTuningOverride", "AtlasSize", int(baseline.atlas)); graphics.call("SetShadowTuningOverride", "Filter", int(baseline.filter))
	sun.shadow_enabled = baseline.sun_shadow; trees.visible = baseline.trees_visible; grass.visible = baseline.grass_visible
	for item in baseline.effects: item.effect.enabled = item.enabled
	for key in baseline.screen: environment.set(key, baseline.screen[key])

func apply_phase(phase: String) -> bool:
	match phase:
		"baseline", "baseline_repeat": pass
		"sun_shadows_off": sun.shadow_enabled = false
		"atlas_16384": graphics.call("SetShadowTuningOverride", "AtlasSize", 16384)
		"atlas_8192": graphics.call("SetShadowTuningOverride", "AtlasSize", 8192)
		"shadow_filter_0": graphics.call("SetShadowTuningOverride", "Filter", 0)
		"trees_hidden": trees.visible = false
		"grass_hidden": grass.visible = false
		"compositors_off":
			for item in baseline.effects: item.effect.enabled = false
		"environment_effects_off":
			for key in baseline.screen: environment.set(key, false)
		_: fail("unknown phase " + phase); return false
	return true

func verify_phase(phase: String) -> bool:
	var expected_sun := false if phase == "sun_shadows_off" else bool(baseline.sun_shadow)
	var expected_atlas := 16384 if phase == "atlas_16384" else 8192 if phase == "atlas_8192" else int(baseline.atlas)
	var expected_filter := 0 if phase == "shadow_filter_0" else int(baseline.filter)
	var expected_trees := false if phase == "trees_hidden" else bool(baseline.trees_visible)
	var expected_grass := false if phase == "grass_hidden" else bool(baseline.grass_visible)
	if sun.shadow_enabled != expected_sun or int(graphics.get("EffectiveDirectionalShadowAtlasSize")) != expected_atlas or int(graphics.get("EffectiveDirectionalShadowFilter")) != expected_filter or trees.visible != expected_trees or grass.visible != expected_grass:
		fail("runtime feature state mismatch " + phase); return false
	for item in baseline.effects:
		if item.effect.enabled != (false if phase == "compositors_off" else bool(item.enabled)): fail("compositor state mismatch " + phase); return false
	for key in baseline.screen:
		if bool(environment.get(key)) != (false if phase == "environment_effects_off" else bool(baseline.screen[key])): fail("environment effect state mismatch " + phase); return false
	for key in baseline.aa:
		if root.get(key) != baseline.aa[key]: fail("AA state changed " + phase); return false
	return true

func sample(label: String) -> Dictionary:
	for i in range(WARMUP): await RenderingServer.frame_post_draw
	if failed or not verify_phase(label): return {"valid":false}
	var gpu := []; var cpu := []; var wall := []; var calls := []; var video := []; var viewport := root.get_viewport_rid()
	var grass_updates := int(grass.get("GpuCullingCompletedUpdates")); var tree_updates := int(trees.get("GpuTreeCompletedUpdates")); var source_epoch: Variant = trees.get("GpuTreeSourceEpoch")
	for i in range(SAMPLES):
		var start := Time.get_ticks_usec(); await RenderingServer.frame_post_draw
		wall.append((Time.get_ticks_usec()-start)*.001); gpu.append(RenderingServer.viewport_get_measured_render_time_gpu(viewport)); cpu.append(RenderingServer.viewport_get_measured_render_time_cpu(viewport)); calls.append(Performance.get_monitor(Performance.Monitor.RENDER_TOTAL_DRAW_CALLS_IN_FRAME)); video.append(Performance.get_monitor(Performance.Monitor.RENDER_VIDEO_MEM_USED))
	for value in gpu: if not is_finite(value) or value <= 0: fail("invalid GPU timing " + label); return {"valid":false}
	var gd := int(grass.get("GpuCullingCompletedUpdates")) - grass_updates; var td := int(trees.get("GpuTreeCompletedUpdates")) - tree_updates
	if gd != 0 or td != 0 or trees.get("GpuTreeSourceEpoch") != source_epoch: fail("frozen stationary inputs changed during " + label); return {"valid":false}
	return {"valid":true, "label":label, "warmup_frames":WARMUP, "samples":SAMPLES, "wall_ms":stats(wall), "viewport_gpu_ms":stats(gpu), "viewport_cpu_ms":stats(cpu), "draw_calls":stats(calls), "reported_video_bytes":stats(video), "runtime":{"sun_shadow":sun.shadow_enabled, "atlas":graphics.get("EffectiveDirectionalShadowAtlasSize"), "filter":graphics.get("EffectiveDirectionalShadowFilter"), "trees_visible":trees.visible, "grass_visible":grass.visible, "compositor_effects":effect_state(), "environment_effects":screen_state(), "aa":aa_state()}, "vegetation":{"grass_roots":grass.get("ResidentGrassInstanceCount"), "grass_resident_batches":grass.get("GpuCullingBatchCount"), "tree_roots":trees.get("GpuTreeResidentRootCount"), "tree_resident_batches":trees.get("GpuTreeBatchCount"), "tree_source_epoch":source_epoch, "grass_compute_updates":gd, "tree_compute_updates":td}}

func effect_state() -> Array:
	var result := []
	for item in baseline.effects: result.append({"script_path":item.script_path, "enabled":item.effect.enabled})
	return result
func screen_state() -> Dictionary:
	var result := {}
	for key in baseline.screen: result[key] = environment.get(key)
	return result
func aa_state() -> Dictionary:
	var result := {}
	for key in baseline.aa: result[key] = root.get(key)
	return result
func find_camera(node: Node) -> Camera3D:
	if node is Camera3D: return node
	for child in node.get_children():
		var found := find_camera(child)
		if found != null: return found
	return null
func stats(values: Array) -> Dictionary:
	var sorted := values.duplicate(); sorted.sort(); var sum := 0.0
	for value in sorted: sum += value
	return {"mean":sum/sorted.size(), "median":sorted[int(sorted.size()/2)], "p95":sorted[mini(sorted.size()-1, int(ceil(sorted.size()*.95))-1)], "min":sorted[0], "max":sorted[-1]}
func write_report(report: Dictionary) -> bool:
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path("res://artifacts/render-feature-cost/"))
	var file := FileAccess.open(OUTPUT, FileAccess.WRITE)
	if file == null: fail("report write failed: " + OUTPUT); return false
	file.store_string(JSON.stringify(report, "\t")); file.close(); return true
func _freeze_all(node: Node) -> void:
	node.process_mode = Node.PROCESS_MODE_DISABLED
	if node is GPUParticles3D: node.speed_scale = 0.0
	for child in node.get_children(): _freeze_all(child)
func _freeze_except_layers(node: Node) -> void:
	node.process_mode = Node.PROCESS_MODE_ALWAYS if node == grass or node == trees else Node.PROCESS_MODE_DISABLED
	if node is GPUParticles3D: node.speed_scale = 0.0
	for child in node.get_children(): _freeze_except_layers(child)
func fail(message: String) -> void:
	if failed: return
	failed = true; RenderingServer.viewport_set_measure_render_time(root.get_viewport_rid(), false); push_error("RENDER_FEATURE_COST_PROFILE=FAIL " + message)
	if game != null: game.queue_free()
	quit(1)
