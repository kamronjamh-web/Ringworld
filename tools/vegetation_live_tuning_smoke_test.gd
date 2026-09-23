extends SceneTree

const PREFS := "res://.godot/vegetation_live_tuning_smoke.tres"
const TREE_LAYER := preload("res://scripts/Voxel/LOD/RingBiomeDecorationLayer.cs")
const DRAWER := preload("res://scripts/ui/RingLiveTuningDrawer.cs")
const GRASS_LAYER := preload("res://scripts/Voxel/LOD/RingStandingGrassLayer.cs")
const GRAPHICS := preload("res://scripts/Voxel/LOD/RingGraphicsQualityController.cs")
const GRASS_PREFS := "res://.godot/vegetation_menu_grass_smoke.tres"
var game: Node
var drawer: Node
var started_at_msec := 0
var finished := false

func _initialize() -> void:
	started_at_msec = Time.get_ticks_msec()
	call_deferred("run")

func _process(_delta: float) -> bool:
	if not finished and Time.get_ticks_msec() - started_at_msec > 60000:
		fail("wall-time watchdog exceeded 60 seconds")
	return false

func run() -> void:
	remove_pref()
	# Keep CurrentScene unset. The drawer must discover this disabled layer via
	# its parent/root fallback without booting the full gameplay world.
	game = Node3D.new()
	var trees := TREE_LAYER.new()
	trees.name = "BiomeDecorations"
	trees.set("Enabled", false)
	game.add_child(trees)
	var grass := GRASS_LAYER.new()
	grass.name = "StandingGrass"
	game.add_child(grass)
	var graphics := GRAPHICS.new()
	graphics.name = "GraphicsQuality"
	game.add_child(graphics)
	drawer = DRAWER.new()
	drawer.name = "LiveTuningDrawer"
	drawer.call("InstallVegetationDefaults", PREFS)
	drawer.call("InstallGrassDefaults", GRASS_PREFS)
	game.add_child(drawer)
	root.add_child(game)
	for i in range(8): await process_frame
	drawer.call("SetBenchmarkDrawerOpen", true)
	for i in range(3): await process_frame
	trees = game.find_child("BiomeDecorations", true, false)
	var toggle := drawer.find_child("UseGpuTreeCulling", true, false) as CheckButton
	if trees == null or toggle == null: return fail("vegetation layer or GPU checkbox missing")
	var grass_gpu := drawer.find_child("UseGpuCulling", true, false) as CheckButton
	var grass_frustum := drawer.find_child("GpuFrustumCulling", true, false) as CheckButton
	var grass_skip := drawer.find_child("SkipUnchangedGpuCulling", true, false) as CheckButton
	if grass_gpu == null or grass_frustum == null or grass_skip == null: return fail("grass GPU controls missing")
	var hierarchy := drawer.find_child("EnableHierarchicalCoverage", true, false) as CheckButton
	var mid_distance := drawer.find_child("LodMidDistanceMeters", true, false) as HSlider
	var far_density := drawer.find_child("LodFarSourceDensity", true, false) as HSlider
	var terrain_enabled := drawer.find_child("EnableTerrainGrassCoverage", true, false) as CheckButton
	var terrain_strength := drawer.find_child("TerrainGrassCoverageStrength", true, false) as HSlider
	var batch_budget := drawer.find_child("MaximumGpuCullingBatchesPerFrame", true, false) as HSlider
	if hierarchy == null or mid_distance == null or far_density == null or terrain_enabled == null or terrain_strength == null or batch_budget == null: return fail("grass coverage LOD controls missing")
	drawer.call("SetGrassTuningValue", "EnableHierarchicalCoverage", false)
	drawer.call("SetGrassTuningValue", "LodFarDistanceMeters", 650.0)
	drawer.call("SetGrassTuningValue", "LodMidDistanceMeters", 300.0)
	drawer.call("SetGrassTuningValue", "LodFarSourceDensity", .1)
	drawer.call("SetGrassTuningValue", "EnableTerrainGrassCoverage", false)
	drawer.call("SetGrassTuningValue", "TerrainGrassCoverageStrength", .7)
	drawer.call("SetGrassTuningValue", "MaximumGpuCullingBatchesPerFrame", 128)
	if bool(grass.get("EnableHierarchicalCoverage")) or not is_equal_approx(float(grass.get("LodMidDistanceMeters")), 300) or not is_equal_approx(float(grass.get("LodFarSourceDensity")), .1) or int(grass.get("MaximumGpuCullingBatchesPerFrame")) != 128: return fail("grass coverage LOD controls did not apply")
	var terrain_enabled_global: Variant = RenderingServer.global_shader_parameter_get("ring_grass_terrain_coverage_enabled")
	var terrain_strength_global: Variant = RenderingServer.global_shader_parameter_get("ring_grass_terrain_coverage_strength")
	# The dummy/headless renderer may not expose global readback. Validate it when
	# available; the runtime setter and persisted values are still exercised.
	if terrain_enabled_global != null and (not is_zero_approx(float(terrain_enabled_global)) or not is_equal_approx(float(terrain_strength_global), .7)): return fail("terrain grass coverage globals did not apply")
	grass_gpu.button_pressed = false
	grass_frustum.button_pressed = false
	grass_skip.button_pressed = false
	if bool(grass.get("UseGpuCulling")) or bool(grass.get("GpuFrustumCulling")) or bool(grass.get("SkipUnchangedGpuCulling")): return fail("grass GPU controls did not apply")
	drawer.call("FlushGrassDefaults")
	drawer.call("InstallGrassDefaults", GRASS_PREFS)
	if bool(grass.get("UseGpuCulling")) or bool(grass.get("GpuFrustumCulling")) or bool(grass.get("SkipUnchangedGpuCulling")) or bool(grass.get("EnableHierarchicalCoverage")) or not is_equal_approx(float(grass.get("LodMidDistanceMeters")), 300) or not is_equal_approx(float(grass.get("LodFarSourceDensity")), .1) or int(grass.get("MaximumGpuCullingBatchesPerFrame")) != 128: return fail("grass controls did not persist")
	drawer.call("ResetGrassSettings")
	if not bool(grass.get("UseGpuCulling")) or not bool(grass.get("GpuFrustumCulling")) or not bool(grass.get("SkipUnchangedGpuCulling")): return fail("grass GPU controls did not reset")
	var tree_skip := drawer.find_child("SkipUnchangedGpuTreeCulling", true, false) as CheckButton
	var tree_merge := drawer.find_child("MergeFarGpuTreeBatches", true, false) as CheckButton
	var tree_fallbacks := drawer.find_child("KeepTreeLodFallbacksUntilReady", true, false) as CheckButton
	if tree_skip == null or tree_merge == null or tree_fallbacks == null: return fail("tree GPU controls missing")
	tree_skip.button_pressed = false
	tree_merge.button_pressed = false
	tree_fallbacks.button_pressed = false
	if bool(trees.get("SkipUnchangedGpuCulling")) or bool(trees.get("MergeFarGpuTreeBatches")) or bool(trees.get("KeepTreeLodFallbacksUntilReady")): return fail("tree GPU controls did not apply")
	toggle.button_pressed = false
	if bool(trees.get("UseGpuCulling")): return fail("live GPU disable did not apply")
	var roots := drawer.find_child("MaximumGpuTreeRoots", true, false) as SpinBox
	var batches := drawer.find_child("MaximumGpuTreeBatches", true, false) as SpinBox
	var streaming_budget := drawer.find_child("TreeStreamingBudgetMilliseconds", true, false) as SpinBox
	var streaming_section := drawer.find_child("SectionVegetationStreaming", true, false) as FoldableContainer
	if roots == null or batches == null or streaming_budget == null or streaming_section == null or not streaming_section.folded: return fail("GPU cap or collapsed streaming budget controls/section missing")
	roots.value = 2048
	batches.value = 48
	streaming_budget.value = 3.25
	if int(trees.get("MaximumGpuTreeRoots")) != 2048 or int(trees.get("MaximumGpuTreeBatches")) != 48 or not is_equal_approx(float(trees.get("TreeStreamingBudgetMilliseconds")), 3.25): return fail("live GPU caps or streaming budget did not apply")
	await create_timer(.7).timeout
	drawer.call("InstallVegetationDefaults", PREFS)
	if bool(trees.get("UseGpuCulling")) or bool(trees.get("SkipUnchangedGpuCulling")) or bool(trees.get("MergeFarGpuTreeBatches")) or bool(trees.get("KeepTreeLodFallbacksUntilReady")) or int(trees.get("MaximumGpuTreeRoots")) != 2048 or int(trees.get("MaximumGpuTreeBatches")) != 48 or not is_equal_approx(float(trees.get("TreeStreamingBudgetMilliseconds")), 3.25): return fail("saved GPU settings did not reload")
	toggle.button_pressed = true
	await create_timer(.7).timeout
	drawer.call("InstallVegetationDefaults", PREFS)
	if not bool(trees.get("UseGpuCulling")): return fail("live GPU enable did not reload")
	var reset := drawer.find_child("VegetationResetDefaults", true, false) as Button
	if reset == null: return fail("vegetation reset missing")
	reset.pressed.emit()
	if not bool(trees.get("SkipUnchangedGpuCulling")) or not bool(trees.get("MergeFarGpuTreeBatches")) or not bool(trees.get("KeepTreeLodFallbacksUntilReady")) or int(trees.get("MaximumGpuTreeRoots")) != 65536 or int(trees.get("MaximumGpuTreeBatches")) != 1024 or not is_equal_approx(float(trees.get("TreeStreamingBudgetMilliseconds")), 2.0): return fail("GPU caps or streaming budget did not reset")
	if int(roots.value) != 65536 or int(batches.value) != 1024 or not is_equal_approx(streaming_budget.value, 2.0): return fail("GPU controls did not refresh after reset")
	await create_timer(.3).timeout
	drawer.call("InstallVegetationDefaults", PREFS)
	if not bool(trees.get("SkipUnchangedGpuCulling")) or not bool(trees.get("MergeFarGpuTreeBatches")) or not bool(trees.get("KeepTreeLodFallbacksUntilReady")) or int(trees.get("MaximumGpuTreeRoots")) != 65536 or int(trees.get("MaximumGpuTreeBatches")) != 1024 or not is_equal_approx(float(trees.get("TreeStreamingBudgetMilliseconds")), 2.0): return fail("reset defaults did not persist")
	print("VEGETATION_LIVE_TUNING=PASS")
	finished = true
	game.queue_free()
	await process_frame
	remove_pref()
	quit(0)

func fail(reason: String) -> void:
	finished = true
	push_error("VEGETATION_LIVE_TUNING=FAIL " + reason)
	if game != null: game.queue_free()
	quit(1)

func remove_pref() -> void:
	if FileAccess.file_exists(PREFS): DirAccess.remove_absolute(ProjectSettings.globalize_path(PREFS))
	if FileAccess.file_exists(GRASS_PREFS): DirAccess.remove_absolute(ProjectSettings.globalize_path(GRASS_PREFS))
