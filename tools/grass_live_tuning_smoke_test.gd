extends SceneTree

# Isolated integration fixture for the Quoteleft live drawer's sparse grass settings.
const SCENE := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const PREFS := "res://.godot/grass_live_tuning_smoke.tres"
const WATER_PREFS := "res://.godot/grass_live_tuning_water_smoke.tres"
var game: Node
var drawer: Node
var previous_hardware_tier: Variant

func _initialize() -> void:
	call_deferred("boot")

func boot() -> void:
	ProjectSettings.set_setting("ringworld/runtime/active_world_directory", "")
	remove_test_prefs(PREFS)
	remove_test_prefs(WATER_PREFS)
	previous_hardware_tier = ProjectSettings.get_setting("ringworld/runtime/hardware_tier", "desktop_mid_high")
	ProjectSettings.set_setting("ringworld/runtime/hardware_tier", "desktop_mid_high")
	game = SCENE.instantiate()
	var saves := game.get_node_or_null("WorldSaveManager")
	if saves != null: saves.free()
	drawer = game.get_node_or_null("LiveTuningDrawer")
	if drawer == null: return fail("LiveTuningDrawer missing")
	drawer.call("InstallGrassDefaults", PREFS)
	drawer.call("InstallWaterDefaults", WATER_PREFS)
	root.add_child(game)
	for i in range(8): await process_frame
	var quality := game.get_node_or_null("GraphicsQuality")
	var grass := game.get_node_or_null("StandingGrass")
	if quality == null or grass == null: return fail("quality or grass missing")
	if not gameplay_detail_defaults_match(grass): return fail("empty preferences did not restore gameplay grass detail defaults")
	if not camera_facing_defaults_safe(grass): return fail("empty preferences did not complete camera facing before near handoff")
	# Untouched camera-facing defaults must adapt when the handoff moves closer.
	drawer.call("SetGrassTuningValue", "LodNearDistanceMeters", 20.0)
	drawer.call("SetGrassTuningValue", "LodTransitionWidthMeters", 12.0)
	if not camera_facing_defaults_safe(grass): return fail("untouched camera-facing defaults did not follow near/transition overrides")
	if float(grass.get("GrassCameraFacingEndMeters")) > 13.5: return fail("adapted camera-facing default did not retain handoff margin")
	# Explicit unsafe facing remains the player's choice and uses crossed geometry fallback.
	drawer.call("SetGrassTuningValue", "GrassCameraFacingEndMeters", 80.0)
	if not is_equal_approx(float(grass.get("GrassCameraFacingEndMeters")), 80.0): return fail("explicit unsafe camera-facing end was replaced")
	drawer.call("SetGrassTuningValue", "LodNearDistanceMeters", 18.0)
	if not is_equal_approx(float(grass.get("GrassCameraFacingEndMeters")), 80.0): return fail("near override replaced explicit unsafe camera-facing end")
	drawer.call("ResetGrassSettings")
	if not gameplay_detail_defaults_match(grass) or not camera_facing_defaults_safe(grass): return fail("handoff override reset did not restore gameplay defaults")
	drawer.call("SetBenchmarkDrawerOpen", true)
	for i in range(2): await process_frame
	var grass_tab := drawer.find_child("Grass", true, false)
	var density_slider := drawer.find_child("BladesPerSquareMeter", true, false) as HSlider
	var density_entry := drawer.find_child("BladesPerSquareMeterEntry", true, false) as SpinBox
	var debug_mode := drawer.find_child("GrassDebugMode", true, false) as OptionButton
	var tile_bounds := drawer.find_child("ShowGrassTileBounds", true, false) as CheckButton
	var tint_picker := drawer.find_child("ForestBiomeTint", true, false) as ColorPickerButton
	var prototype_tiles := drawer.find_child("GpuGenerationPrototypeTileCount", true, false) as HSlider
	var surface_cache := drawer.find_child("SurfaceCacheMegabytes", true, false) as HSlider
	var grouped_compute := drawer.find_child("GroupGrassComputePhases", true, false) as CheckButton
	var tile_culling := drawer.find_child("CullGrassTiles", true, false) as CheckButton
	var grass_clumps := drawer.find_child("UseGrassClumps", true, false) as CheckButton
	var gust_enabled := drawer.find_child("EnableGrassGustTexture", true, false) as CheckButton
	var gust_strength := drawer.find_child("GrassGustStrength", true, false) as HSlider
	var frustum_padding := drawer.find_child("GrassFrustumPaddingMeters", true, false) as HSlider
	var modifier_enabled := drawer.find_child("EnableGrassModifiers", true, false) as CheckButton
	var modifier_tint := drawer.find_child("GrassModifierPreviewTint", true, false) as ColorPickerButton
	var place_modifier := drawer.find_child("PlaceGrassModifierPreview", true, false) as Button
	var camera_facing := drawer.find_child("EnableGrassCameraFacing", true, false) as CheckButton
	var camera_start := drawer.find_child("GrassCameraFacingStartMeters", true, false) as HSlider
	var camera_end := drawer.find_child("GrassCameraFacingEndMeters", true, false) as HSlider
	var prototype_run := drawer.find_child("GrassGpuGenerationPrototypeRun", true, false) as Button
	var prototype_status := drawer.find_child("GrassGpuGenerationPrototypeStatus", true, false) as Label
	if grass_tab == null or density_slider == null or density_entry == null: return fail("rendered Grass tab controls missing")
	if debug_mode == null or tile_bounds == null or tint_picker == null: return fail("appearance/debug controls missing")
	if prototype_tiles == null or prototype_run == null or prototype_status == null: return fail("GPU generation prototype controls missing")
	if surface_cache == null: return fail("surface cache control missing")
	if grouped_compute == null or tile_culling == null or grass_clumps == null: return fail("production grass controls missing")
	if gust_enabled == null or gust_strength == null or frustum_padding == null: return fail("gust/culling controls missing")
	if modifier_enabled == null or modifier_tint == null or place_modifier == null: return fail("modifier controls missing")
	if camera_facing == null or camera_start == null or camera_end == null: return fail("camera-facing controls missing")
	if not bool(grass.get("GroupGrassComputePhases")) or not bool(grass.get("CullGrassTiles")) or not bool(grass.get("UseGrassClumps")): return fail("missing production keys did not default enabled")
	if prototype_run.disabled: return fail("GPU generation prototype button is unavailable")
	if bool(grass.call("CaptureGpuGenerationPrototypeDiagnostics").get("running", true)): return fail("GPU generation prototype started automatically")
	prototype_tiles.value = 1
	await create_timer(.25).timeout
	if int(grass.get("GpuGenerationPrototypeTileCount")) != 1: return fail("prototype minimum tile count did not apply")
	prototype_tiles.value = 4
	await create_timer(.25).timeout
	if int(grass.get("GpuGenerationPrototypeTileCount")) != 4: return fail("prototype tile count did not apply")
	drawer.call("SetGrassTuningValue", "GpuGenerationPrototypeTileCount", 99)
	if int(grass.get("GpuGenerationPrototypeTileCount")) != 4: return fail("prototype tile count was not clamped")
	drawer.call("SetGrassTuningValue", "GpuGenerationPrototypeTileCount", 2)
	if int(grass.get("GpuGenerationPrototypeTileCount")) != 2: return fail("prototype persisted tile count did not apply")
	surface_cache.value = 2.5
	await create_timer(.25).timeout
	if not is_equal_approx(float(grass.get("SurfaceCacheMegabytes")), 2.5): return fail("surface cache cap did not apply")
	gust_enabled.button_pressed = false
	gust_strength.value = 1.2
	frustum_padding.value = 9.0
	camera_facing.button_pressed = true
	camera_start.value = 100.0
	camera_end.value = 20.0
	await create_timer(.25).timeout
	if bool(grass.get("EnableGrassGustTexture")) or not is_equal_approx(float(grass.get("GrassGustStrength")), 1.2): return fail("gust controls did not apply")
	if not is_equal_approx(float(grass.get("GrassFrustumPaddingMeters")), 8.0): return fail("frustum padding was not clamped")
	if not bool(grass.get("EnableGrassCameraFacing")) or float(grass.get("GrassCameraFacingStartMeters")) >= float(grass.get("GrassCameraFacingEndMeters")): return fail("camera-facing range was not ordered")
	modifier_enabled.button_pressed = false
	drawer.call("SetGrassTuningValue", "GrassModifierPreviewTint", Color(.3, .2, .1, 1))
	place_modifier.pressed.emit()
	if not bool(grass.get("GrassModifierPreviewEnabled")): return fail("placing modifier preview did not enable it")
	var tabs := grass_tab.get_parent() as TabContainer
	if tabs == null: return fail("Grass tab container missing")
	tabs.current_tab = grass_tab.get_index()
	await RenderingServer.frame_post_draw
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path("res://artifacts/grass_lod"))
	var image := root.get_viewport().get_texture().get_image()
	if image.save_png("res://artifacts/grass_lod/grass_menu.png") != OK: return fail("Grass tab screenshot failed")
	# A slider change queues the expensive grass setting, so it must remain pending briefly then commit.
	density_slider.value = 19.0
	if float(grass.get("BladesPerSquareMeter")) == 19.0: return fail("slider skipped debounce")
	await create_timer(.25).timeout
	if float(grass.get("BladesPerSquareMeter")) != 19.0: return fail("slider debounce did not commit")
	# Numeric entry is deliberate input and commits immediately without leaving a stale queued slider value.
	density_entry.value = 21.0
	if float(grass.get("BladesPerSquareMeter")) != 21.0: return fail("numeric entry did not commit")
	drawer.call("SetGrassTuningValue", "Enabled", false)
	if bool(grass.get("Enabled")): return fail("live enable toggle was not applied")
	drawer.call("SetGrassTuningValue", "GroupGrassComputePhases", false)
	drawer.call("SetGrassTuningValue", "CullGrassTiles", false)
	grass_clumps.button_pressed = false
	if not bool(grass.get("UseGrassClumps")): return fail("grass clump toggle skipped debounce")
	await create_timer(.25).timeout
	if bool(grass.get("GroupGrassComputePhases")) or bool(grass.get("CullGrassTiles")) or bool(grass.get("UseGrassClumps")): return fail("production grass toggles did not apply false")
	drawer.call("SetGrassTuningValue", "GrassInteractionRadiusMeters", 1.8)
	drawer.call("SetGrassTuningValue", "ForestBiomeDensity", 0.4)
	drawer.call("SetGrassTuningValue", "ForestBiomeTint", Color(.1, .3, .05, 1))
	drawer.call("SetGrassTuningValue", "ForestBiomeTint", Color(.2, .4, .1, 1))
	debug_mode.select(4)
	debug_mode.item_selected.emit(4)
	tile_bounds.button_pressed = true
	if int(grass.get("GrassDebugMode")) != 4 or not bool(grass.get("ShowGrassTileBounds")): return fail("debug controls did not apply")
	for key in ["UseReducedNearGrassGeometry", "UseSingleRibbonIntermediateGrass", "UseSimplifiedIntermediateGrassShading"]:
		if drawer.find_child(key, true, false) == null: return fail("experimental detail control missing: " + key)
		drawer.call("SetGrassTuningValue", key, false)
		if bool(grass.get(key)): return fail("experimental detail setting did not apply false: " + key)
	if not bool(drawer.call("FlushGrassDefaults")): return fail("experimental detail false settings save failed")
	drawer.call("InstallGrassDefaults", PREFS)
	for key in ["UseReducedNearGrassGeometry", "UseSingleRibbonIntermediateGrass", "UseSimplifiedIntermediateGrassShading"]:
		if bool(grass.get(key)): return fail("experimental detail setting did not reload false: " + key)
		drawer.call("SetGrassTuningValue", key, true)
		if not bool(grass.get(key)): return fail("experimental detail setting did not apply: " + key)
	if not bool(drawer.call("FlushGrassDefaults")): return fail("grass settings save failed")
	# Reinstall emulates scene-reload preference loading without touching user preferences.
	drawer.call("InstallGrassDefaults", PREFS)
	for key in ["UseReducedNearGrassGeometry", "UseSingleRibbonIntermediateGrass", "UseSimplifiedIntermediateGrassShading"]:
		if not bool(grass.get(key)): return fail("experimental detail setting did not reload: " + key)
	if float(grass.get("BladesPerSquareMeter")) != 21.0 or bool(grass.get("Enabled")): return fail("saved grass settings did not reload")
	if not is_equal_approx(float(grass.get("GrassInteractionRadiusMeters")), 1.8): return fail("interaction settings did not reload")
	if not is_equal_approx(float(grass.get("ForestBiomeDensity")), .4): return fail("biome density did not reload")
	if not (grass.get("ForestBiomeTint") as Color).is_equal_approx(Color(.2, .4, .1, 1)): return fail("biome color did not reload")
	if not tint_picker.color.is_equal_approx(Color(.2, .4, .1, 1)): return fail("color picker did not refresh")
	if int(grass.get("GrassDebugMode")) != 4 or not bool(grass.get("ShowGrassTileBounds")): return fail("debug settings did not reload")
	if int(grass.get("GpuGenerationPrototypeTileCount")) != 2: return fail("prototype tile count did not reload")
	if not is_equal_approx(float(grass.get("SurfaceCacheMegabytes")), 2.5): return fail("surface cache cap did not reload")
	if bool(grass.get("EnableGrassGustTexture")) or not is_equal_approx(float(grass.get("GrassGustStrength")), 1.2): return fail("gust settings did not reload")
	if not is_equal_approx(float(grass.get("GrassFrustumPaddingMeters")), 8.0): return fail("frustum padding did not reload")
	if not bool(grass.get("EnableGrassCameraFacing")) or float(grass.get("GrassCameraFacingStartMeters")) >= float(grass.get("GrassCameraFacingEndMeters")): return fail("camera-facing settings did not reload ordered")
	if bool(grass.get("EnableGrassModifiers")) or not (grass.get("GrassModifierPreviewTint") as Color).is_equal_approx(Color(.3, .2, .1, 1)): return fail("modifier settings did not reload")
	if bool(grass.get("GroupGrassComputePhases")) or bool(grass.get("CullGrassTiles")) or bool(grass.get("UseGrassClumps")): return fail("production grass toggles did not reload false")
	drawer.call("SetGrassTuningValue", "GroupGrassComputePhases", true)
	drawer.call("SetGrassTuningValue", "CullGrassTiles", true)
	drawer.call("SetGrassTuningValue", "UseGrassClumps", true)
	if not bool(drawer.call("FlushGrassDefaults")): return fail("production grass toggle save failed")
	drawer.call("InstallGrassDefaults", PREFS)
	if not bool(grass.get("GroupGrassComputePhases")) or not bool(grass.get("CullGrassTiles")) or not bool(grass.get("UseGrassClumps")): return fail("production grass toggles did not reload true")
	# Capture the additional controls while they are actually visible in the scrolled tab.
	var scroll: ScrollContainer = find_scroll_ancestor(tint_picker)
	if scroll == null: return fail("Grass appearance scroll container missing")
	scroll.ensure_control_visible(tint_picker)
	for i in range(3): await process_frame
	await RenderingServer.frame_post_draw
	if root.get_viewport().get_texture().get_image().save_png("res://artifacts/grass_lod/grass_menu_appearance.png") != OK: return fail("appearance screenshot failed")
	quality.call("ApplyPreset", 0, true, false)
	if float(grass.get("BladesPerSquareMeter")) != 21.0: return fail("explicit density did not survive preset change")
	if float(grass.get("VisibilityRadiusMeters")) != 28.0: return fail("untouched range did not follow Low desktop profile")
	drawer.call("ResetGrassSettings")
	if not gameplay_detail_defaults_match(grass): return fail("experimental detail reset did not restore gameplay defaults")
	if int(drawer.call("GetGrassTuningDiagnostics").get("override_count", -1)) != 0: return fail("reset retained overrides")
	if not bool(grass.get("Enabled")): return fail("reset did not restore profile Enabled")
	if not is_equal_approx(float(grass.get("GrassInteractionRadiusMeters")), 1.1): return fail("interaction reset did not restore defaults")
	if not is_equal_approx(float(grass.get("ForestBiomeDensity")), .65): return fail("biome reset did not restore defaults")
	if debug_mode.selected != 0 or tile_bounds.button_pressed: return fail("debug controls did not reset")
	if int(grass.get("GpuGenerationPrototypeTileCount")) != 4: return fail("prototype reset did not restore default")
	if not is_equal_approx(float(grass.get("SurfaceCacheMegabytes")), 4.0): return fail("surface cache reset did not restore default")
	if not bool(grass.get("EnableGrassGustTexture")) or not is_equal_approx(float(grass.get("GrassGustStrength")), .45): return fail("gust reset did not restore defaults")
	if not is_equal_approx(float(grass.get("GrassFrustumPaddingMeters")), .15): return fail("frustum padding reset did not restore default")
	if not camera_facing_defaults_safe(grass): return fail("camera-facing reset did not restore safe gameplay defaults")
	if not bool(grass.get("EnableGrassModifiers")) or bool(grass.get("GrassModifierPreviewEnabled")): return fail("modifier reset did not restore defaults")
	if not bool(grass.get("GroupGrassComputePhases")) or not bool(grass.get("CullGrassTiles")) or not bool(grass.get("UseGrassClumps")): return fail("production grass toggles did not reset enabled")
	var expected := float(grass.get("BladesPerSquareMeter"))
	quality.call("ApplyPreset", 2, true, false)
	if float(grass.get("BladesPerSquareMeter")) == expected: return fail("reset density did not return to current-preset control")
	print("GRASS_LIVE_TUNING=PASS")
	game.queue_free()
	ProjectSettings.set_setting("ringworld/runtime/hardware_tier", previous_hardware_tier)
	quit(0)

func fail(reason: String) -> void:
	push_error("GRASS_LIVE_TUNING=FAIL " + reason)
	if game != null: game.queue_free()
	if previous_hardware_tier != null: ProjectSettings.set_setting("ringworld/runtime/hardware_tier", previous_hardware_tier)
	quit(1)

func remove_test_prefs(path: String) -> void:
	if FileAccess.file_exists(path): DirAccess.remove_absolute(ProjectSettings.globalize_path(path))

func find_scroll_ancestor(control: Control) -> ScrollContainer:
	var current: Node = control
	while current != null:
		if current is ScrollContainer: return current as ScrollContainer
		current = current.get_parent()
	return null

func gameplay_detail_defaults_match(grass: Node) -> bool:
	return bool(grass.get("UseReducedNearGrassGeometry")) and bool(grass.get("UseSingleRibbonIntermediateGrass")) and not bool(grass.get("UseSimplifiedIntermediateGrassShading"))

func camera_facing_defaults_safe(grass: Node) -> bool:
	var handoff_start := float(grass.get("LodNearDistanceMeters")) - maxf(1.0, float(grass.get("LodTransitionWidthMeters"))) * .5
	return bool(grass.get("EnableGrassCameraFacing")) and is_equal_approx(float(grass.get("GrassCameraFacingStrength")), 1.0) and float(grass.get("GrassCameraFacingStartMeters")) < float(grass.get("GrassCameraFacingEndMeters")) and float(grass.get("GrassCameraFacingEndMeters")) <= handoff_start
