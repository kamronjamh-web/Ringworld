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
	var meadow_reason: String = await verify_meadow_tuning(grass, quality)
	if not meadow_reason.is_empty(): return fail(meadow_reason)
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
	# The fixture's active graphics preset may have enough residency for groups.
	# Fix a small resident window explicitly before testing budget-limited coordination.
	drawer.call("SetGrassTuningValue", "MaximumResidentTiles", 16)
	await create_timer(.3).timeout
	var range_status := drawer.find_child("GrassRuntimeStatus", true, false) as Label
	var range_inputs := "tile=%s residents=%s instances=%s per_tile=%s coordination=%s appearance=%s far=%s" % [grass.get("SpatialTileMeters"), grass.get("MaximumResidentTiles"), grass.get("MaximumInstances"), grass.get("MaximumInstancesPerTile"), grass.get("EnableCoordinatedGrassHandoff"), grass.get("EnableGrassAppearance"), grass.get("LodFarDistanceMeters")]
	if range_status == null or not range_status.text.contains("effective draw distance"):
		return "Grass status omitted the effective range: " + range_inputs
	if adaptive_inactive_reason(grass).is_empty() and not status.text.contains("geometry ends around"):
		return "Small resident window hid the coordinated coverage budget limit: " + range_inputs + " " + status.text
	print("GRASS_GROUPING_COORDINATED_RANGE " + range_inputs + " " + status.text)
	# Exercise the eligible manual range separately from the budget-limited coordinated band.
	drawer.call("SetGrassTuningValue", "EnableCoordinatedGrassHandoff", false)
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

# Exercise real controls plus the same sparse save/load/reset path as gameplay.
func verify_meadow_tuning(grass: Node, quality: Node) -> String:
	for key in ["EnableMeadowTufts", "GrassMinimumHeightMeters", "GrassMaximumHeightMeters", "MeadowTuftSpread", "MeadowTuftVariant", "EnableCoordinatedGrassHandoff"]:
		if drawer.find_child(key, true, false) == null: return "Meadow tuning control missing: " + key
	if not meadow_defaults_match(grass): return "Empty preferences did not restore short meadow defaults"
	var height_entry := drawer.find_child("GrassMaximumHeightMetersEntry", true, false) as SpinBox
	var coordination := drawer.find_child("EnableCoordinatedGrassHandoff", true, false) as CheckButton
	height_entry.value = .28
	coordination.button_pressed = false
	if not is_equal_approx(float(grass.get("GrassMaximumHeightMeters")), .28) or bool(grass.get("EnableCoordinatedGrassHandoff")): return "Meadow controls did not apply to live grass"
	drawer.call("SetGrassTuningValue", "GrassMinimumHeightMeters", 1.0)
	if not is_equal_approx(float(grass.get("GrassMinimumHeightMeters")), .27): return "Meadow height range was not ordered"
	drawer.call("SetGrassTuningValue", "GrassMinimumHeightMeters", .12)
	drawer.call("SetGrassTuningValue", "MeadowTuftSpread", 100.0)
	drawer.call("SetGrassTuningValue", "MeadowTuftVariant", 100)
	if not is_equal_approx(float(grass.get("MeadowTuftSpread")), 2.0) or int(grass.get("MeadowTuftVariant")) != 3: return "Meadow shape limits were not clamped"
	drawer.call("SetGrassTuningValue", "MeadowTuftSpread", NAN)
	drawer.call("SetGrassTuningValue", "EnableMeadowTufts", 1)
	if not is_equal_approx(float(grass.get("MeadowTuftSpread")), 2.0) or not bool(grass.get("EnableMeadowTufts")): return "Invalid meadow input changed live settings"
	drawer.call("SetGrassTuningValue", "EnableMeadowTufts", false)
	if not bool(drawer.call("FlushGrassDefaults")): return "Meadow settings save failed"
	drawer.call("InstallGrassDefaults", PREFS)
	if not meadow_overrides_match(grass): return "Meadow settings did not reload"
	var preset: int = quality.get("CurrentPreset")
	quality.call("ApplyPreset", 0, true, false)
	if not meadow_overrides_match(grass): return "Quality preset replaced explicit meadow settings"
	quality.call("ApplyPreset", preset, true, false)
	drawer.call("ResetGrassSettings")
	if not meadow_defaults_match(grass): return "Reset did not restore short meadow defaults"
	var style_reason := verify_render_style(grass, quality)
	if not style_reason.is_empty(): return style_reason
	var card_reason: String = await verify_card_settings(grass, quality)
	if not card_reason.is_empty(): return card_reason
	var persistence_reason := verify_loaded_height_ordering(grass, quality)
	if not persistence_reason.is_empty(): return persistence_reason
	print("GRASS_MEADOW_TUNING=PASS controls, validation, sparse save/reload, quality preservation and reset")
	return ""

# S5: the grass render style switch (Blades default, explicit saved style wins over the profile, Reset restores Blades).
func verify_render_style(grass: Node, quality: Node) -> String:
	for key in ["GrassRenderStyle", "GrassCardReachMeters", "GrassCardGreenPull", "GrassCardComputeMarginMeters"]:
		if drawer.find_child(key, true, false) == null: return "Render style control missing: " + key
	if int(grass.get("GrassRenderStyle")) != 0 or not is_equal_approx(float(grass.get("GrassCardReachMeters")), 400.0) or not is_equal_approx(float(grass.get("GrassCardGreenPull")), .15) or not is_equal_approx(float(grass.get("GrassCardComputeMarginMeters")), 2.0): return "Render style did not default to Blades / reach 400 / green pull 0.15 / compute margin 2"
	var style_option := drawer.find_child("GrassRenderStyle", true, false) as OptionButton
	style_option.select(1)
	style_option.item_selected.emit(1)
	if int(grass.get("GrassRenderStyle")) != 1: return "Render style option did not apply Cards"
	drawer.call("SetGrassTuningValue", "GrassCardGreenPull", .4)
	drawer.call("SetGrassTuningValue", "GrassCardReachMeters", 9999.0)
	drawer.call("SetGrassTuningValue", "GrassCardComputeMarginMeters", 99.0)
	if not is_equal_approx(float(grass.get("GrassCardComputeMarginMeters")), 8.0): return "Compute margin did not clamp"
	drawer.call("SetGrassTuningValue", "GrassCardComputeMarginMeters", 3.5)
	if not is_equal_approx(float(grass.get("GrassCardGreenPull")), .4) or not is_equal_approx(float(grass.get("GrassCardReachMeters")), 600.0): return "Card settings did not apply / clamp"
	drawer.call("SetGrassTuningValue", "GrassRenderStyle", 7)
	if int(grass.get("GrassRenderStyle")) != 1: return "Out-of-range render style was not clamped to Cards"
	if not bool(drawer.call("FlushGrassDefaults")): return "Render style save failed"
	drawer.call("SetGrassTuningValue", "GrassRenderStyle", 0)
	if int(grass.get("GrassRenderStyle")) != 0: return "Live switch back to Blades did not apply"
	drawer.call("InstallGrassDefaults", PREFS)
	if int(grass.get("GrassRenderStyle")) != 1: return "Saved Cards style did not reload over an unsaved live edit"
	drawer.call("InstallGrassDefaults", PREFS)
	if int(grass.get("GrassRenderStyle")) != 1 or not is_equal_approx(float(grass.get("GrassCardGreenPull")), .4) or not is_equal_approx(float(grass.get("GrassCardReachMeters")), 600.0) or not is_equal_approx(float(grass.get("GrassCardComputeMarginMeters")), 3.5): return "Render style settings did not reload"
	var preset: int = quality.get("CurrentPreset")
	quality.call("ApplyPreset", 0, true, false)
	if int(grass.get("GrassRenderStyle")) != 1: return "Quality preset replaced the explicit render style"
	quality.call("ApplyPreset", preset, true, false)
	drawer.call("ResetGrassSettings")
	if int(grass.get("GrassRenderStyle")) != 0 or not is_equal_approx(float(grass.get("GrassCardReachMeters")), 400.0) or not is_equal_approx(float(grass.get("GrassCardGreenPull")), .15) or not is_equal_approx(float(grass.get("GrassCardComputeMarginMeters")), 2.0): return "Reset did not restore Blades and the card defaults"
	return ""

# S8: Grass cards settings. Key, default, min, max, test value, rebuild class (label carries "(rebuilds)").
const CARD_KEYS := [
	["GrassCardReachMeters", 400.0, 100.0, 600.0, 350.0, true],
	["GrassCardTileMeters", 32.0, 16.0, 64.0, 24.0, true],
	["GrassCardNearDensity", 14.0, 2.0, 24.0, 10.0, true],
	["GrassCardLowLayerDensity", 18.0, 0.0, 30.0, 10.0, true],
	["GrassCardLowLayerEndMeters", 22.0, 10.0, 60.0, 30.0, false],
	["GrassCardBaseDensity", 2.5, 0.5, 6.0, 3.0, false],
	["GrassCardFloorDensity", 0.15, 0.02, 1.0, 0.3, false],
	["GrassCardDensityFalloff", 1.3, 0.5, 2.5, 1.6, false],
	["GrassCardNearBoostStartMeters", 8.0, 0.0, 100.0, 12.0, false],
	["GrassCardNearBoostEndMeters", 45.0, 1.0, 200.0, 60.0, false],
	["GrassCardScaleCompensationMax", 2.5, 1.0, 2.5, 2.0, false],
	["GrassCardFadeBandPercent", 15.0, 1.0, 50.0, 25.0, false],
	["GrassCardSingleCardStartMeters", 90.0, 20.0, 600.0, 100.0, false],
	["GrassCardSingleCardEndMeters", 160.0, 30.0, 700.0, 200.0, false],
	["GrassCardTallHeight", 0.6, 0.2, 1.5, 0.8, true],
	["GrassCardShortHeight", 0.3, 0.1, 1.0, 0.2, true],
	["GrassCardNearWidthScale", 0.7, 0.3, 1.0, 0.8, false],
	["GrassCardLean", 0.12, 0.0, 0.5, 0.2, true],
	["GrassCardTilt", 0.08, 0.0, 0.4, 0.2, false],
	["GrassCardNormalOut", 0.45, 0.0, 1.0, 0.6, false],
	["GrassCardNormalFace", 0.25, 0.0, 1.0, 0.35, false],
	["GrassCardBacklight", 0.28, 0.0, 1.0, 0.4, false],
	["GrassCardAlphaCutoff", 0.5, 0.1, 0.9, 0.7, false],
	["GrassCardAtlasBlades", 56.0, 4.0, 96.0, 40.0, true],
	["GrassCardGreenPull", 0.15, 0.0, 1.0, 0.4, false],
	["GrassCardRootAo", 0.5, 0.0, 1.0, 0.3, false],
	["GrassCardTipLight", 0.16, 0.0, 0.5, 0.25, false],
	["GrassCardBladeValueJitter", 0.22, 0.0, 0.6, 0.3, false],
	["GrassCardBladeHueJitter", 0.10, 0.0, 0.3, 0.15, false],
	["GrassCardDryFraction", 0.07, 0.0, 0.5, 0.2, false],
	["GrassCardTuftValueJitter", 0.14, 0.0, 0.5, 0.25, false],
	["GrassCardFragmentColorMeters", 70.0, 10.0, 400.0, 120.0, false],
	["GrassCardConvergeStartPercent", 60.0, 0.0, 99.0, 50.0, false],
	["GrassCardConvergeEndPercent", 90.0, 1.0, 100.0, 80.0, false],
]
# Pairs that clamp each other: key set, value set, expected value of that key. Every case starts from the defaults.
const CARD_COMPANIONS := [
	["GrassCardNearBoostEndMeters", 5.0, 9.0],
	["GrassCardNearBoostStartMeters", 99.0, 44.0],
	["GrassCardSingleCardStartMeters", 600.0, 159.0],
	["GrassCardSingleCardEndMeters", 30.0, 91.0],
	["GrassCardConvergeStartPercent", 99.0, 89.0],
	["GrassCardConvergeEndPercent", 10.0, 61.0],
	["GrassCardShortHeight", 1.0, 0.59],
	["GrassCardTallHeight", 0.2, 0.31],
]

func card_defaults_match(grass: Node) -> bool:
	for entry in CARD_KEYS:
		if not is_equal_approx(float(grass.get(entry[0])), float(entry[1])): return false
	return true

func verify_card_settings(grass: Node, quality: Node) -> String:
	var baseline_overrides := int(drawer.call("GetGrassTuningDiagnostics").get("override_count", -1))
	for title in ["Grass cards — coverage", "Grass cards — form", "Grass cards — colour"]:
		var found := false
		for section in drawer.find_children("*", "FoldableContainer", true, false):
			if (section as FoldableContainer).title == title: found = true
		if not found: return "Card group missing: " + title
	if drawer.find_child("GrassStreamingHysteresisMeters", true, false) == null: return "Shared hysteresis slider missing"
	for entry in CARD_KEYS:
		var key: String = entry[0]
		if drawer.find_child(key, true, false) == null or drawer.find_child(key + "Entry", true, false) == null: return "Card control missing: " + key
	if not card_defaults_match(grass): return "Card settings do not default to the variant D values"
	# Clamp both ways. Companion keys only have to stay inside their range (their partner may bound them first).
	for entry in CARD_KEYS:
		var key: String = entry[0]
		drawer.call("SetGrassTuningValue", key, 1.0e6)
		var high := float(grass.get(key))
		drawer.call("SetGrassTuningValue", key, -1.0e6)
		var low := float(grass.get(key))
		if high > float(entry[3]) + 0.0001 or high < float(entry[2]) - 0.0001 or low > float(entry[3]) + 0.0001 or low < float(entry[2]) - 0.0001: return "Card setting left its range: %s high=%s low=%s" % [key, high, low]
	drawer.call("SetGrassTuningValue", "GrassCardNearDensity", NAN)
	if is_nan(float(grass.get("GrassCardNearDensity"))) or float(grass.get("GrassCardNearDensity")) < 2.0: return "A non-finite card setting was accepted"
	drawer.call("ResetGrassSettings")
	if not card_defaults_match(grass): return "Reset did not restore the card defaults"
	if int(drawer.call("GetGrassTuningDiagnostics").get("override_count", -1)) != baseline_overrides: return "Card reset retained overrides"
	# Companion clamps.
	for case in CARD_COMPANIONS:
		drawer.call("SetGrassTuningValue", case[0], case[1])
		if absf(float(grass.get(case[0])) - float(case[2])) > 0.0001: return "Companion clamp %s=%s gave %s, expected %s" % [case[0], case[1], grass.get(case[0]), case[2]]
		drawer.call("ResetGrassSettings")
	drawer.call("SetGrassTuningValue", "GrassCardFloorDensity", 0.8)
	drawer.call("SetGrassTuningValue", "GrassCardBaseDensity", 0.5)
	if absf(float(grass.get("GrassCardBaseDensity")) - 0.8) > 0.0001: return "Base density was allowed below the floor density"
	drawer.call("ResetGrassSettings")
	# Debounce: rebuild-class sliders commit after the pause, never on the drag itself.
	for key in ["GrassCardNearDensity", "GrassCardLean", "GrassCardAtlasBlades"]:
		var slider := drawer.find_child(key, true, false) as HSlider
		var before := float(grass.get(key))
		slider.value = slider.max_value if before < slider.max_value else slider.min_value
		if float(grass.get(key)) != before: return "Rebuild-class slider skipped the debounce: " + key
		await create_timer(.3).timeout
		if float(grass.get(key)) == before: return "Rebuild-class slider never committed: " + key
	drawer.call("ResetGrassSettings")
	# Persistence: every key saves and reloads.
	for entry in CARD_KEYS:
		drawer.call("SetGrassTuningValue", entry[0], entry[4])
	for entry in CARD_KEYS:
		if absf(float(grass.get(entry[0])) - float(entry[4])) > 0.0001: return "Card setting did not apply: %s=%s" % [entry[0], grass.get(entry[0])]
	if not bool(drawer.call("FlushGrassDefaults")): return "Card settings save failed"
	drawer.call("InstallGrassDefaults", PREFS)
	for entry in CARD_KEYS:
		if absf(float(grass.get(entry[0])) - float(entry[4])) > 0.0001: return "Card setting did not reload: %s=%s" % [entry[0], grass.get(entry[0])]
	var slider_check := drawer.find_child("GrassCardLean", true, false) as HSlider
	if absf(slider_check.value - 0.2) > 0.0001: return "Card slider did not follow the reloaded value"
	var preset: int = quality.get("CurrentPreset")
	quality.call("ApplyPreset", 0, true, false)
	for entry in CARD_KEYS:
		if absf(float(grass.get(entry[0])) - float(entry[4])) > 0.0001: return "Quality preset replaced explicit card setting " + entry[0]
	quality.call("ApplyPreset", preset, true, false)
	drawer.call("ResetGrassSettings")
	if not card_defaults_match(grass): return "Reset after reload did not restore the card defaults"
	# Quality profile budgets: Low gets reach 300, the current preset keeps the variant D reach; both follow the preset unless saved.
	quality.call("ApplyPreset", 0, true, false)
	if absf(float(grass.get("GrassCardReachMeters")) - 300.0) > 0.0001 or absf(float(grass.get("GrassCardNearDensity")) - 14.0) > 0.0001: return "Low preset card budget is not reach 300 / near density 14"
	quality.call("ApplyPreset", preset, true, false)
	if absf(float(grass.get("GrassCardReachMeters")) - 400.0) > 0.0001: return "Returning from Low did not restore the card reach"
	drawer.call("ResetGrassSettings")
	# Old saves: only legacy keys. New card settings stay at their defaults and the explicit style/reach still apply.
	var legacy := ResourceLoader.load(PREFS, "", ResourceLoader.CACHE_MODE_IGNORE)
	if legacy == null: return "Could not reload the saved preferences to author a legacy save"
	legacy.set("Overrides", {"BladesPerSquareMeter": 21.0, "GrassRenderStyle": 1, "GrassCardReachMeters": 350.0, "GrassCardGreenPull": 0.3})
	if ResourceSaver.save(legacy, PREFS) != OK: return "Could not write the legacy save"
	drawer.call("InstallGrassDefaults", PREFS)
	if int(grass.get("GrassRenderStyle")) != 1 or absf(float(grass.get("GrassCardReachMeters")) - 350.0) > 0.0001 or absf(float(grass.get("GrassCardGreenPull")) - 0.3) > 0.0001: return "Legacy save lost its explicit style, reach or green pull"
	for entry in CARD_KEYS:
		if entry[0] in ["GrassCardReachMeters", "GrassCardGreenPull"]: continue
		if absf(float(grass.get(entry[0])) - float(entry[1])) > 0.0001: return "Legacy save changed the default of " + entry[0]
	# Blades-only keys never reach Cards: tweak blade keys under Cards, the card values stay put.
	drawer.call("SetGrassTuningValue", "BladesPerSquareMeter", 90.0)
	drawer.call("SetGrassTuningValue", "GrassMaximumHeightMeters", 0.9)
	if absf(float(grass.get("GrassCardNearDensity")) - 14.0) > 0.0001 or int(grass.get("GrassCardAtlasBlades")) != 56: return "Blade keys changed card settings"
	drawer.call("ResetGrassSettings")
	if int(drawer.call("GetGrassTuningDiagnostics").get("override_count", -1)) != baseline_overrides: return "Legacy-save reset retained overrides"
	return ""

func meadow_defaults_match(grass: Node) -> bool:
	return bool(grass.get("EnableMeadowTufts")) and bool(grass.get("EnableCoordinatedGrassHandoff")) and is_equal_approx(float(grass.get("GrassMinimumHeightMeters")), .10) and is_equal_approx(float(grass.get("GrassMaximumHeightMeters")), .20) and is_equal_approx(float(grass.get("MeadowTuftSpread")), 1.0) and int(grass.get("MeadowTuftVariant")) == 0

func meadow_overrides_match(grass: Node) -> bool:
	return not bool(grass.get("EnableMeadowTufts")) and not bool(grass.get("EnableCoordinatedGrassHandoff")) and is_equal_approx(float(grass.get("GrassMinimumHeightMeters")), .12) and is_equal_approx(float(grass.get("GrassMaximumHeightMeters")), .28) and is_equal_approx(float(grass.get("MeadowTuftSpread")), 2.0) and int(grass.get("MeadowTuftVariant")) == 3

# Simulate authored/older sparse resources that cross the inherited height bound.
# Effective companions may adapt, but loading must preserve the authored sparse keys.
func verify_loaded_height_ordering(grass: Node, quality: Node) -> String:
	var cases := [
		{"saved": {"GrassMinimumHeightMeters": .8}, "minimum": .8, "maximum": .81},
		{"saved": {"GrassMaximumHeightMeters": .05}, "minimum": .04, "maximum": .05},
		{"saved": {"GrassMinimumHeightMeters": .8, "GrassMaximumHeightMeters": .05}, "minimum": .8, "maximum": .81}
	]
	var preset: int = quality.get("CurrentPreset")
	for entry in cases:
		var settings := ResourceLoader.load(PREFS, "", ResourceLoader.CACHE_MODE_IGNORE)
		if settings == null: return "Height persistence fixture could not load isolated settings"
		settings.set("Overrides", entry.saved)
		if ResourceSaver.save(settings, PREFS) != OK: return "Height persistence fixture could not save crossed settings"
		drawer.call("InstallGrassDefaults", PREFS)
		for selected_preset in [0, preset]:
			quality.call("ApplyPreset", selected_preset, true, false)
			if not is_equal_approx(float(grass.get("GrassMinimumHeightMeters")), entry.minimum) or not is_equal_approx(float(grass.get("GrassMaximumHeightMeters")), entry.maximum): return "Loaded sparse height bounds were not ordered after preset change"
			for key in ["GrassMinimumHeightMeters", "GrassMaximumHeightMeters"]:
				var height_control := drawer.find_child(key + "Entry", true, false) as SpinBox
				# Refresh is periodic after direct quality changes, while initial load refreshes immediately.
				if height_control == null: return "Loaded height control missing"
				if selected_preset == 0 and not is_equal_approx(height_control.value, float(grass.get(key))): return "Reloaded height UI disagreed with live bounds"
		var persisted := ResourceLoader.load(PREFS, "", ResourceLoader.CACHE_MODE_IGNORE)
		var overrides: Dictionary = persisted.get("Overrides")
		if overrides.size() != entry.saved.size(): return "Height loading added persisted derived keys"
		for key in entry.saved:
			if not is_equal_approx(float(overrides.get(key, -1)), float(entry.saved[key])): return "Height loading discarded an authored bound"
		drawer.call("ResetGrassSettings")
	if not meadow_defaults_match(grass): return "Crossed height persistence reset did not restore defaults"
	return ""
