extends SceneTree

# Isolated integration fixture for the Quoteleft live drawer's sparse grass settings.
const SCENE := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const PREFS := "res://.godot/grass_live_tuning_smoke.tres"
const WATER_PREFS := "res://.godot/grass_live_tuning_water_smoke.tres"
const ISOLATION := preload("res://scripts/Tools/MinimapWindContractTest.cs")
const EXTRA_PREFS := "res://.godot/grass-live-tuning-preferences"
var game: Node
var drawer: Node
var previous_hardware_tier: Variant

func _initialize() -> void:
	call_deferred("boot")

func boot() -> void:
	ProjectSettings.set_setting("ringworld/runtime/active_world_directory", "")
	remove_test_prefs(PREFS)
	remove_test_prefs(WATER_PREFS)
	for name in ["aa", "motion", "water", "wind", "legacy-grass", "grass", "vegetation", "shadows", "surface", "atmosphere", "resolution", "lod", "coverage", "clouds", "rain-haze"]:
		remove_test_prefs(EXTRA_PREFS + "/" + str(name) + ".tres")
	previous_hardware_tier = ProjectSettings.get_setting("ringworld/runtime/hardware_tier", "desktop_mid_high")
	ProjectSettings.set_setting("ringworld/runtime/hardware_tier", "desktop_mid_high")
	game = SCENE.instantiate()
	var saves := game.get_node_or_null("WorldSaveManager")
	if saves != null: saves.free()
	drawer = game.get_node_or_null("LiveTuningDrawer")
	if drawer == null: return fail("LiveTuningDrawer missing")
	var isolation: Dictionary = ISOLATION.new().ExecuteFixture("isolation", game, EXTRA_PREFS)
	if not bool(isolation.get("pass", false)): return fail("preference isolation failed")
	drawer.call("InstallGrassDefaults", PREFS)
	drawer.call("InstallWaterDefaults", WATER_PREFS)
	root.add_child(game)
	for i in range(8): await process_frame
	var quality := game.get_node_or_null("GraphicsQuality")
	var grass := game.get_node_or_null("StandingGrass")
	if quality == null or grass == null: return fail("quality or grass missing")
	if not terrain_blend_defaults_match(grass): return fail("empty preferences did not restore terrain blend defaults")
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
	for key in ["EnableAdaptiveGrassGrouping", "CacheGrassPresentation", "GrassGroupingStartMeters", "GrassGroupingFarStartMeters", "GrassGroupingMaximumLevel", "GrassGroupingHysteresisMeters", "MaximumGrassSourcesPerGroup"]:
		if drawer.find_child(key, true, false) == null: return fail("adaptive grouping/cache control missing: " + key)
	if not adaptive_grouping_defaults_match(grass): return fail("empty preferences did not restore grouping/cache defaults")
	var pointer_reason: String = await verify_adaptive_pointer_controls(grass, grass_tab)
	if not pointer_reason.is_empty(): return fail(pointer_reason)
	var original_density := float(grass.get("BladesPerSquareMeter"))
	drawer.call("SetGrassTuningValue", "EnableAdaptiveGrassGrouping", false)
	drawer.call("SetGrassTuningValue", "CacheGrassPresentation", false)
	drawer.call("SetGrassTuningValue", "GrassGroupingStartMeters", 300.0)
	if not is_equal_approx(float(grass.get("GrassGroupingStartMeters")), 255.0): return fail("grouping start was not ordered")
	drawer.call("SetGrassTuningValue", "GrassGroupingFarStartMeters", 40.0)
	if not is_equal_approx(float(grass.get("GrassGroupingFarStartMeters")), 256.0): return fail("far grouping start was not ordered")
	drawer.call("SetGrassTuningValue", "GrassGroupingStartMeters", 96.0)
	drawer.call("SetGrassTuningValue", "GrassGroupingFarStartMeters", 192.0)
	drawer.call("SetGrassTuningValue", "GrassGroupingMaximumLevel", 99)
	drawer.call("SetGrassTuningValue", "GrassGroupingHysteresisMeters", -1.0)
	drawer.call("SetGrassTuningValue", "MaximumGrassSourcesPerGroup", 1)
	if int(grass.get("GrassGroupingMaximumLevel")) != 2 or float(grass.get("GrassGroupingHysteresisMeters")) != 0.0 or int(grass.get("MaximumGrassSourcesPerGroup")) != 256: return fail("grouping limits were not clamped")
	drawer.call("SetGrassTuningValue", "EnableAdaptiveGrassGrouping", 1)
	drawer.call("SetGrassTuningValue", "CacheGrassPresentation", 1)
	if bool(grass.get("EnableAdaptiveGrassGrouping")) or bool(grass.get("CacheGrassPresentation")): return fail("invalid grouping/cache boolean input was accepted")
	drawer.call("SetGrassTuningValue", "GrassGroupingHysteresisMeters", NAN)
	if float(grass.get("GrassGroupingHysteresisMeters")) != 0.0: return fail("nonfinite grouping hysteresis input was accepted")
	if not is_equal_approx(float(grass.get("BladesPerSquareMeter")), original_density): return fail("grouping settings changed grass density")
	if not bool(drawer.call("FlushGrassDefaults")): return fail("grouping/cache settings save failed")
	drawer.call("InstallGrassDefaults", PREFS)
	if not adaptive_grouping_overrides_match(grass): return fail("grouping/cache settings did not reload")
	var grouping_original_preset: int = quality.get("CurrentPreset")
	quality.call("ApplyPreset", 0, true, false)
	if not adaptive_grouping_overrides_match(grass): return fail("quality preset replaced explicit grouping/cache settings")
	quality.call("ApplyPreset", grouping_original_preset, true, false)
	drawer.call("ResetGrassSettings")
	if not adaptive_grouping_defaults_match(grass): return fail("grouping/cache reset did not restore defaults")
	for key in ["GrassTerrainBlendStartPercent", "GrassTerrainBlendEndPercent", "GrassTerrainLightingMatch", "GrassTerrainHandoffStrength"]:
		if drawer.find_child(key, true, false) == null or drawer.find_child(key + "Entry", true, false) == null: return fail("terrain blend control missing: " + key)
	var blade_shading := drawer.find_child("EnableGrassBladeShading", true, false) as CheckButton
	if blade_shading == null or blade_shading.button_pressed: return fail("legacy blade shading control missing or default enabled")
	blade_shading.button_pressed = true
	if not bool(grass.get("EnableGrassBladeShading")): return fail("blade shading toggle did not apply")
	drawer.call("SetGrassTuningValue", "EnableGrassBladeShading", 1.0)
	if not bool(grass.get("EnableGrassBladeShading")): return fail("invalid blade shading input changed boolean state")
	# Numeric entry commits immediately; crossed ranges retain a one-percent gap.
	var blend_start_entry := drawer.find_child("GrassTerrainBlendStartPercentEntry", true, false) as SpinBox
	var blend_end_entry := drawer.find_child("GrassTerrainBlendEndPercentEntry", true, false) as SpinBox
	blend_start_entry.value = 99.0
	if not is_equal_approx(float(grass.get("GrassTerrainBlendStartPercent")), 89.0): return fail("terrain blend start was not ordered")
	blend_end_entry.value = 10.0
	if not is_equal_approx(float(grass.get("GrassTerrainBlendEndPercent")), 90.0): return fail("terrain blend end was not ordered")
	blend_start_entry.value = 45.0
	blend_end_entry.value = 80.0
	drawer.call("SetGrassTuningValue", "GrassTerrainLightingMatch", .65)
	drawer.call("SetGrassTuningValue", "GrassTerrainHandoffStrength", .85)
	if not bool(drawer.call("FlushGrassDefaults")): return fail("terrain blend save failed")
	drawer.call("InstallGrassDefaults", PREFS)
	if not terrain_blend_overrides_match(grass): return fail("terrain blend overrides did not reload")
	var blend_original_preset: int = quality.get("CurrentPreset")
	quality.call("ApplyPreset", 0, true, false)
	if not terrain_blend_overrides_match(grass): return fail("graphics quality replaced explicit terrain blend overrides")
	quality.call("ApplyPreset", blend_original_preset, true, false)
	drawer.call("ResetGrassSettings")
	if not terrain_blend_defaults_match(grass): return fail("terrain blend reset did not restore defaults")
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
	if int(drawer.call("GetGrassTuningDiagnostics").get("override_count", -1)) != 2: return fail("Grass reset must preserve the two saved wind response overrides")
	if not bool(grass.get("Enabled")): return fail("reset did not restore profile Enabled")
	if not is_equal_approx(float(grass.get("GrassInteractionRadiusMeters")), 1.1): return fail("interaction reset did not restore defaults")
	if not is_equal_approx(float(grass.get("ForestBiomeDensity")), .65): return fail("biome reset did not restore defaults")
	if debug_mode.selected != 0 or tile_bounds.button_pressed: return fail("debug controls did not reset")
	if int(grass.get("GpuGenerationPrototypeTileCount")) != 4: return fail("prototype reset did not restore default")
	if not is_equal_approx(float(grass.get("SurfaceCacheMegabytes")), 4.0): return fail("surface cache reset did not restore default")
	if bool(grass.get("EnableGrassGustTexture")) or not is_equal_approx(float(grass.get("GrassGustStrength")), 1.2): return fail("Grass reset changed Wind response preferences: enabled=%s strength=%s" % [grass.get("EnableGrassGustTexture"), grass.get("GrassGustStrength")])
	if not is_equal_approx(float(grass.get("GrassFrustumPaddingMeters")), .15): return fail("frustum padding reset did not restore default")
	if not camera_facing_defaults_safe(grass): return fail("camera-facing reset did not restore safe gameplay defaults")
	if not bool(grass.get("EnableGrassModifiers")) or bool(grass.get("GrassModifierPreviewEnabled")): return fail("modifier reset did not restore defaults")
	if not bool(grass.get("GroupGrassComputePhases")) or not bool(grass.get("CullGrassTiles")) or not bool(grass.get("UseGrassClumps")): return fail("production grass toggles did not reset enabled")
	drawer.call("SetGrassTuningValue", "BladesPerSquareMeter", 17.0)
	drawer.call("ApplyDramaticStormPreset")
	if bool(grass.get("EnableGrassGustTexture")) or not is_equal_approx(float(grass.get("GrassGustStrength")), 1.2): return fail("storm preset changed grass response overrides")
	if not drawer.call("FlushWindDefaults"): return fail("Wind save failed to flush grass owner")
	drawer.call("InstallGrassDefaults", PREFS)
	if bool(grass.get("EnableGrassGustTexture")) or not is_equal_approx(float(grass.get("GrassGustStrength")), 1.2): return fail("Wind save did not persist original grass owner")
	drawer.call("ResetWindSettings")
	if not is_equal_approx(float(grass.get("BladesPerSquareMeter")), 17.0): return fail("Wind reset changed grass density")
	if not bool(grass.get("EnableGrassGustTexture")) or not is_equal_approx(float(grass.get("GrassGustStrength")), .45): return fail("Wind reset did not restore grass response defaults")
	drawer.call("ResetGrassSettings")
	if int(drawer.call("GetGrassTuningDiagnostics").get("override_count", -1)) != 0: return fail("scoped resets retained overrides")
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

# Send real viewport events through normal input and GUI hit testing. Setting
# button_pressed or emitting a signal would miss clipping/overlay regressions.
func gui_click(control: Control, section_header := false) -> String:
	var scroll := find_scroll_ancestor(control)
	if scroll == null: return "Pointer target has no scroll ancestor: " + str(control.name)
	scroll.ensure_control_visible(control)
	for i in range(4): await process_frame
	if not control.is_visible_in_tree(): return "Pointer target is hidden: " + str(control.name)
	var rect := control.get_global_rect()
	var point := rect.position + Vector2(rect.size.x - 22.0, rect.size.y * .5)
	if section_header: point = rect.position + Vector2(rect.size.x * .5, 12.0)
	if not root.get_visible_rect().has_point(point) or not scroll.get_global_rect().has_point(point):
		return "Pointer target is clipped: %s rect=%s point=%s scroll=%s" % [control.name, rect, point, scroll.get_global_rect()]
	var motion := InputEventMouseMotion.new()
	motion.position = point
	motion.global_position = point
	root.push_input(motion, true)
	await process_frame
	var hovered := root.gui_get_hovered_control()
	if hovered != control and (hovered == null or not control.is_ancestor_of(hovered)):
		return "Pointer target occluded: %s hovered=%s point=%s" % [control.name, hovered.get_path() if hovered != null else "none", point]
	for pressed in [true, false]:
		var event := InputEventMouseButton.new()
		event.button_index = MOUSE_BUTTON_LEFT
		event.button_mask = MOUSE_BUTTON_MASK_LEFT if pressed else 0
		event.position = point
		event.global_position = point
		event.pressed = pressed
		root.push_input(event, true)
		await process_frame
	await create_timer(.3).timeout
	return ""

func verify_adaptive_pointer_controls(grass: Node, grass_tab: Control) -> String:
	var tabs := grass_tab.get_parent() as TabContainer
	var section := drawer.find_child("SectionAdaptivegrassgroups", true, false) as FoldableContainer
	var grouping := drawer.find_child("EnableAdaptiveGrassGrouping", true, false) as CheckButton
	var caching := drawer.find_child("CacheGrassPresentation", true, false) as CheckButton
	var status := drawer.find_child("GrassGroupingRuntimeStatus", true, false) as Label
	if tabs == null or section == null or grouping == null or caching == null or status == null:
		return "Adaptive pointer fixture controls/status missing"
	tabs.current_tab = grass_tab.get_index()
	for i in range(3): await process_frame
	if section.folded:
		var reason := await gui_click(section, true)
		if not reason.is_empty(): return reason
	if section.folded: return "Actual header click did not expand Adaptive grass groups"
	if not status.is_visible_in_tree(): return "Adaptive status is hidden after expanding its section"
	var inactive_reason := adaptive_inactive_reason(grass)
	if inactive_reason.is_empty():
		if not status.text.contains("geometry ends around"):
			return "Default ineligible grouping range has no local explanation: " + status.text
	elif not status.text.contains(inactive_reason) or not status.text.contains("grass work is inactive"):
		return "Default inactive grass has no local explanation: " + status.text
	for control in [grouping, caching]:
		var key: String = str(control.name)
		for expected in [false, true, false]:
			var reason := await gui_click(control)
			if not reason.is_empty(): return reason
			if control.button_pressed != expected or bool(grass.get(key)) != expected:
				return "Actual GUI click failed for %s expected=%s checkbox=%s live=%s" % [key, expected, control.button_pressed, grass.get(key)]
			var feedback: String
			if key == "EnableAdaptiveGrassGrouping": feedback = "Grouping on" if expected else "Grouping off"
			else: feedback = "Presentation cache on" if expected else "Presentation cache off"
			if not status.text.contains(feedback): return "Actual GUI toggle left stale local feedback: " + status.text
	if not bool(drawer.call("FlushGrassDefaults")): return "Pointer-created false overrides failed to save"
	drawer.call("InstallGrassDefaults", PREFS)
	for i in range(3): await process_frame
	if grouping.button_pressed or caching.button_pressed or bool(grass.get("EnableAdaptiveGrassGrouping")) or bool(grass.get("CacheGrassPresentation")):
		return "Pointer-created false overrides did not reload into live properties and checkboxes"
	if not status.text.contains("Grouping off") or not status.text.contains("Presentation cache off"):
		return "Reloaded false overrides left stale local feedback: " + status.text
	drawer.call("ResetGrassSettings")
	if not grouping.button_pressed or not caching.button_pressed or not adaptive_grouping_defaults_match(grass):
		return "Reset did not restore pointer-tested toggle defaults"
	drawer.call("SetGrassTuningValue", "GrassGroupingStartMeters", 16.0)
	drawer.call("SetGrassTuningValue", "LodFarDistanceMeters", 200.0)
	drawer.call("SetGrassTuningValue", "GrassGroupingMaximumLevel", 2)
	await create_timer(.3).timeout
	var counts := "2-by-2: %d; 4-by-4: %d. Maximum level: %d." % [int(grass.get("AdaptiveGrassLevel1GroupCount")), int(grass.get("AdaptiveGrassLevel2GroupCount")), int(grass.get("GrassGroupingMaximumLevel"))]
	if not status.text.contains(counts): return "Local status omits actual level counts/configured maximum: " + status.text
	inactive_reason = adaptive_inactive_reason(grass)
	if inactive_reason.is_empty():
		if status.text.contains("geometry ends around") or not status.text.contains("Complete groups must be beyond"):
			return "Extended geometry range left stale ineligible feedback: " + status.text
	elif not status.text.contains(inactive_reason): return "Extended range hid grass inactivity: " + status.text
	drawer.call("ResetGrassSettings")
	await create_timer(.3).timeout
	var scroll := find_scroll_ancestor(status)
	scroll.ensure_control_visible(status)
	for i in range(4): await process_frame
	await RenderingServer.frame_post_draw
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path("res://artifacts/grass_lod"))
	if root.get_texture().get_image().save_png("res://artifacts/grass_lod/grass_grouping_menu.png") != OK:
		return "Adaptive grouping status screenshot failed"
	print("GRASS_ADAPTIVE_POINTER=PASS viewport press/release, false persistence, default range explanation and active count feedback")
	return ""

func adaptive_inactive_reason(grass: Node) -> String:
	if not bool(grass.get("Enabled")): return "Grass disabled."
	if bool(grass.get("StreamingSuspended")): return "Grass streaming suspended."
	if not grass.is_visible_in_tree(): return "Grass hidden."
	return ""

func remove_test_prefs(path: String) -> void:
	if FileAccess.file_exists(path): DirAccess.remove_absolute(ProjectSettings.globalize_path(path))

func terrain_blend_defaults_match(grass: Node) -> bool:
	return not bool(grass.get("EnableGrassBladeShading")) and is_equal_approx(float(grass.get("GrassTerrainBlendStartPercent")), 70.0) and is_equal_approx(float(grass.get("GrassTerrainBlendEndPercent")), 90.0) and is_equal_approx(float(grass.get("GrassTerrainLightingMatch")), 1.0) and is_equal_approx(float(grass.get("GrassTerrainHandoffStrength")), 1.0)

func terrain_blend_overrides_match(grass: Node) -> bool:
	return bool(grass.get("EnableGrassBladeShading")) and is_equal_approx(float(grass.get("GrassTerrainBlendStartPercent")), 45.0) and is_equal_approx(float(grass.get("GrassTerrainBlendEndPercent")), 80.0) and is_equal_approx(float(grass.get("GrassTerrainLightingMatch")), .65) and is_equal_approx(float(grass.get("GrassTerrainHandoffStrength")), .85)

func find_scroll_ancestor(control: Control) -> ScrollContainer:
	var current: Node = control
	while current != null:
		if current is ScrollContainer: return current as ScrollContainer
		current = current.get_parent()
	return null

func gameplay_detail_defaults_match(grass: Node) -> bool:
	return bool(grass.get("UseReducedNearGrassGeometry")) and bool(grass.get("UseSingleRibbonIntermediateGrass")) and not bool(grass.get("UseSimplifiedIntermediateGrassShading"))

func adaptive_grouping_defaults_match(grass: Node) -> bool:
	return bool(grass.get("EnableAdaptiveGrassGrouping")) and bool(grass.get("CacheGrassPresentation")) and is_equal_approx(float(grass.get("GrassGroupingStartMeters")), 128.0) and is_equal_approx(float(grass.get("GrassGroupingFarStartMeters")), 256.0) and int(grass.get("GrassGroupingMaximumLevel")) == 1 and is_equal_approx(float(grass.get("GrassGroupingHysteresisMeters")), 16.0) and int(grass.get("MaximumGrassSourcesPerGroup")) == 16384

func adaptive_grouping_overrides_match(grass: Node) -> bool:
	return not bool(grass.get("EnableAdaptiveGrassGrouping")) and not bool(grass.get("CacheGrassPresentation")) and is_equal_approx(float(grass.get("GrassGroupingStartMeters")), 96.0) and is_equal_approx(float(grass.get("GrassGroupingFarStartMeters")), 192.0) and int(grass.get("GrassGroupingMaximumLevel")) == 2 and float(grass.get("GrassGroupingHysteresisMeters")) == 0.0 and int(grass.get("MaximumGrassSourcesPerGroup")) == 256

func camera_facing_defaults_safe(grass: Node) -> bool:
	var handoff_start := float(grass.get("LodNearDistanceMeters")) - maxf(1.0, float(grass.get("LodTransitionWidthMeters"))) * .5
	return bool(grass.get("EnableGrassCameraFacing")) and is_equal_approx(float(grass.get("GrassCameraFacingStrength")), 1.0) and float(grass.get("GrassCameraFacingStartMeters")) < float(grass.get("GrassCameraFacingEndMeters")) and float(grass.get("GrassCameraFacingEndMeters")) <= handoff_start
