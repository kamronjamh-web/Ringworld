extends RefCounted

# Called by the menu smoke fixture after its normal GPU checks.  This stays a
# helper (rather than a SceneTree script) so it cannot change the test's scene
# lifecycle or accidentally write the production preset.

var _failed: bool = false

func run(menu: Control, capture: bool) -> bool:
	_failed = false
	var tree: SceneTree = menu.get_tree()
	var backdrop: Node = menu.get_node_or_null("SpaceBackdrop")
	var drawer: Node = _find_drawer(menu)
	_require(backdrop != null, "missing SpaceBackdrop")
	_require(drawer != null, "missing MenuFlyoverTuningDrawer")
	if backdrop == null or drawer == null:
		return false
	var fixture_path: String = str(backdrop.call("GetMenuSettingsPath"))
	if fixture_path != "res://.godot/menu_tuning_fixture.tres":
		_require(false, "refusing to run tuning writes outside the fixture preset")
		return false
	var production_path: String = "res://resources/menu/menu_flyover_defaults.tres"
	var production_hash: PackedByteArray = _hash_file(production_path)
	var original: Resource = backdrop.call("GetMenuTuningSettings") as Resource
	_require(original != null, "backdrop did not return tuning settings")
	if original == null:
		return false
	var initial_drawer: Dictionary = drawer.call("GetDrawerDiagnostics") as Dictionary
	_require(not bool(initial_drawer.get("unsaved", true)), "drawer starts dirty")
	_require(int(initial_drawer.get("save_attempts", -1)) == 0, "drawer saved on initialization")
	_require(int(initial_drawer.get("control_count", -1)) == 50, "drawer must expose exactly 50 controls")
	var tabs: TabContainer = drawer.find_children("*", "TabContainer", true, false)[0] as TabContainer
	_require(tabs != null and tabs.get_tab_count() == 6, "drawer must expose six tabs")
	if tabs != null and tabs.get_tab_count() == 6:
		_require(tabs.get_tab_title(5) == "Terrain", "Terrain must be the sixth tuning tab")
	var values: Dictionary = initial_drawer.get("values", {}) as Dictionary
	_require(float(values.get("SunAzimuthDegrees", 0.0)) < 0.0, "negative sun azimuth was not initialized")
	_require(is_equal_approx(float(values.get("CloudDitherSpeed", -1.0)), 0.0) and is_equal_approx(float(values.get("CloudRayStartJitterStrength", -1.0)), 0.0) and is_equal_approx(float(values.get("CloudStationaryHistoryWeight", -1.0)), .10) and is_equal_approx(float(values.get("CloudMovingHistoryWeight", -1.0)), 0.0) and is_equal_approx(float(values.get("CloudAmbientDay", -1.0)), 1.0) and is_equal_approx(float(values.get("CloudAmbientNight", -1.0)), 1.0) and is_equal_approx(float(values.get("ShadowExtinctionPerKm", -1.0)), 1.35) and is_equal_approx(float(values.get("ShadowRadiusKm", -1.0)), 128.0) and bool(values.get("ShadowGenerationEnabled", false)) and bool(values.get("ShadowReceiversEnabled", false)), "new cloud/shadow defaults were not initialized")
	for property: String in values:
		var expected: Variant = original.get(property)
		if expected is bool:
			_require(bool(values[property]) == expected, "initial checkbox mismatch: " + property)
		else:
			_require(is_equal_approx(float(values[property]), float(expected)), "initial numeric control mismatch: " + property)
	var pause_before: bool = tree.paused
	var mouse_before: int = Input.mouse_mode
	var focus: SpinBox = _first_spinbox(drawer)
	_require(focus != null, "drawer has no focusable SpinBox")
	if focus != null:
		Input.parse_input_event(_key_event(KEY_QUOTELEFT))
		await tree.process_frame
		_release_key(KEY_QUOTELEFT)
		_require(bool((drawer.call("GetDrawerDiagnostics") as Dictionary).get("open", false)), "backtick did not open drawer")
		focus.get_line_edit().grab_focus()
		Input.parse_input_event(_key_event(KEY_ESCAPE))
		await tree.process_frame
		_release_key(KEY_ESCAPE)
		_require(not bool((drawer.call("GetDrawerDiagnostics") as Dictionary).get("open", true)), "Escape did not close drawer")
	var before: Dictionary = backdrop.call("GetBackdropDiagnostics") as Dictionary
	var samples_before: int = int(before.get("terrain_sample_count", -1))
	var travel_before: float = float(before.get("travel_m", -1.0))
	# Submit an actual editor control as well as exercising the programmatic API.
	drawer.call("SetDrawerOpen", true)
	var fov_control: SpinBox = drawer.find_child("FieldOfView", true, false) as SpinBox
	_require(fov_control != null, "FOV UI editor missing")
	if fov_control != null:
		fov_control.get_line_edit().text = "73"
		fov_control.get_line_edit().text_submitted.emit("73")
	drawer.call("SetTuningValue", "PitchDegrees", 38.0)
	drawer.call("SetTuningValue", "AltitudeKm", 31.0)
	drawer.call("SetTuningValue", "SpeedKmPerSecond", 7.0)
	drawer.call("SetTuningValue", "Paused", true)
	await tree.process_frame
	var camera_settings: Resource = backdrop.call("GetMenuTuningSettings") as Resource
	_require(camera_settings != null and is_equal_approx(float(camera_settings.get("FieldOfView")), 73.0), "camera FOV was not applied")
	_require(camera_settings != null and is_equal_approx(float(camera_settings.get("PitchDegrees")), 38.0), "camera pitch was not applied")
	_require(camera_settings != null and is_equal_approx(float(camera_settings.get("AltitudeKm")), 31.0), "camera altitude was not applied")
	_require(camera_settings != null and is_equal_approx(float(camera_settings.get("SpeedKmPerSecond")), 7.0) and bool(camera_settings.get("Paused")), "speed/pause was not applied")
	var after_camera: Dictionary = backdrop.call("GetBackdropDiagnostics") as Dictionary
	var menu_viewport: SubViewport = backdrop.get_node("MenuFlyoverViewport") as SubViewport
	var camera: Camera3D = menu_viewport.get_camera_3d()
	_require(is_equal_approx(camera.fov, 73.0) and is_equal_approx(camera.rotation_degrees.x, -38.0), "camera node did not receive live tuning")
	_require(absf(float(after_camera.get("altitude_m", 0.0)) - 31000.0) < 100.0, "camera did not change clearance")
	_require(int(after_camera.get("terrain_sample_count", -2)) == samples_before, "camera tuning rebuilt terrain")
	_require(is_equal_approx(float(after_camera.get("travel_m", -2.0)), travel_before), "camera tuning reset flyover travel")
	_require(tree.paused == pause_before and Input.mouse_mode == mouse_before, "menu tuning changed global pause or input capture")
	drawer.call("SetTuningValue", "HazeStartKm", 900.0)
	drawer.call("SetTuningValue", "HazeEndKm", 100.0)
	drawer.call("SetTuningValue", "CloudFloorKm", 7.0)
	drawer.call("SetTuningValue", "CloudCeilingKm", 1.0)
	drawer.call("SetTuningValue", "CloudDitherSpeed", 999.0)
	drawer.call("SetTuningValue", "CloudRayStartJitterStrength", -1.0)
	drawer.call("SetTuningValue", "CloudStationaryHistoryWeight", .10)
	drawer.call("SetTuningValue", "CloudMovingHistoryWeight", .90)
	drawer.call("SetTuningValue", "CloudAmbientDay", 2.0)
	drawer.call("SetTuningValue", "CloudAmbientNight", -1.0)
	drawer.call("SetTuningValue", "ShadowExtinctionPerKm", 99.0)
	drawer.call("SetTuningValue", "ShadowRadiusKm", 999.0)
	drawer.call("SetTuningValue", "ShadowUpdatesPerSecond", 0.0)
	drawer.call("SetTuningValue", "CloudMinStepMeters", 500.0)
	drawer.call("SetTuningValue", "CloudMaxStepMeters", 100.0)
	drawer.call("SetTuningValue", "NotASetting", 1.0)
	drawer.call("SetTuningValue", "FieldOfView", "invalid")
	var sanitized: Resource = backdrop.call("GetMenuTuningSettings") as Resource
	_require(sanitized != null and float(sanitized.get("HazeEndKm")) >= float(sanitized.get("HazeStartKm")) + 20.0, "haze companion values were not sanitized")
	_require(sanitized != null and float(sanitized.get("CloudCeilingKm")) >= float(sanitized.get("CloudFloorKm")) + 0.0499, "cloud altitude companion values were not sanitized")
	_require(sanitized != null and float(sanitized.get("CloudMaxStepMeters")) >= float(sanitized.get("CloudMinStepMeters")) + 10.0, "cloud step companion values were not sanitized")
	_require(sanitized != null and is_equal_approx(float(sanitized.get("CloudDitherSpeed")), 120.0) and is_equal_approx(float(sanitized.get("CloudRayStartJitterStrength")), 0.0) and is_equal_approx(float(sanitized.get("CloudStationaryHistoryWeight")), .10) and is_equal_approx(float(sanitized.get("CloudMovingHistoryWeight")), .10) and is_equal_approx(float(sanitized.get("CloudAmbientDay")), 1.0) and is_equal_approx(float(sanitized.get("CloudAmbientNight")), 0.0) and is_equal_approx(float(sanitized.get("ShadowExtinctionPerKm")), 10.0) and is_equal_approx(float(sanitized.get("ShadowRadiusKm")), 200.0) and is_equal_approx(float(sanitized.get("ShadowUpdatesPerSecond")), .25), "new cloud/shadow values were not sanitized")
	var moving_history_control: SpinBox = drawer.find_child("CloudMovingHistoryWeight", true, false) as SpinBox
	_require(moving_history_control != null and is_equal_approx(moving_history_control.value, .10), "history companion clamp did not refresh its editor")
	_require(sanitized != null and is_equal_approx(float(sanitized.get("FieldOfView")), 73.0), "invalid value was accepted")
	var terrain_material: ShaderMaterial = (menu_viewport.get_node("MenuFlyoverTerrain") as MeshInstance3D).material_override as ShaderMaterial
	var far_material: ShaderMaterial = (menu_viewport.get_node("MenuFlyoverFarTerrain") as MeshInstance3D).material_override as ShaderMaterial
	for material: ShaderMaterial in [terrain_material, far_material]:
		_require(is_equal_approx(float(material.get_shader_parameter("haze_end_km")), 920.0) and is_equal_approx(float(material.get_shader_parameter("cloud_ceiling_km")), 7.05), "terrain receiver did not apply sanitized haze/cloud layer")
	await tree.create_timer(0.65).timeout
	var saved: Dictionary = drawer.call("GetDrawerDiagnostics") as Dictionary
	_require(not bool(saved.get("unsaved", true)) and int(saved.get("save_successes", 0)) > 0, "debounced fixture save did not complete")
	var reloaded: Resource = ResourceLoader.load(fixture_path, "", ResourceLoader.CACHE_MODE_IGNORE) as Resource
	_require(reloaded != null and is_equal_approx(float(reloaded.get("FieldOfView")), 73.0) and is_equal_approx(float(reloaded.get("CloudDitherSpeed")), 120.0) and is_equal_approx(float(reloaded.get("CloudMovingHistoryWeight")), .10) and is_equal_approx(float(reloaded.get("CloudAmbientDay")), 1.0) and is_equal_approx(float(reloaded.get("CloudAmbientNight")), 0.0) and is_equal_approx(float(reloaded.get("ShadowRadiusKm")), 200.0), "CacheModeIgnore did not reload fixture values")
	var reload_backdrop: Control = load("res://scripts/ui/RingMenuSpaceBackdrop.cs").new()
	reload_backdrop.Seed = 1337
	reload_backdrop.MenuSettingsPath = fixture_path
	tree.root.add_child(reload_backdrop)
	var restored: Resource = reload_backdrop.call("GetMenuTuningSettings") as Resource
	_require(is_equal_approx(float(restored.get("FieldOfView")), 73.0) and bool(restored.get("Paused")) and is_equal_approx(float(restored.get("CloudDitherSpeed")), 120.0) and is_equal_approx(float(restored.get("CloudAmbientDay")), 1.0) and is_equal_approx(float(restored.get("CloudAmbientNight")), 0.0) and is_equal_approx(float(restored.get("ShadowExtinctionPerKm")), 10.0) and bool(restored.get("ShadowGenerationEnabled")) and bool(restored.get("ShadowReceiversEnabled")), "new menu instance did not load saved defaults")
	reload_backdrop.queue_free()
	await tree.process_frame
	# A failed write must leave the last good preset intact and back off retries.
	var fixture_hash: PackedByteArray = _hash_file(fixture_path)
	drawer.set("SavePathOverride", "res://.godot/menu_tuning_missing_folder/preset.tres")
	drawer.call("SetTuningValue", "FieldOfView", 74.0)
	_require(not bool(drawer.call("FlushPendingSave")), "invalid save destination unexpectedly succeeded")
	var failure: Dictionary = drawer.call("GetDrawerDiagnostics")
	await tree.create_timer(0.7).timeout
	var retry: Dictionary = drawer.call("GetDrawerDiagnostics")
	_require(int(retry.get("save_attempts", 0)) == int(failure.get("save_attempts", -1)) and not str(retry.get("last_error", "")).is_empty(), "save failure did not persist/back off")
	_require(_hash_file(fixture_path) == fixture_hash, "failed save damaged last good preset")
	drawer.set("SavePathOverride", "")
	_require(bool(drawer.call("FlushPendingSave")), "save did not recover after destination was restored")
	drawer.call("SetTuningValue", "SunEnergy", 0.0)
	drawer.call("SetDrawerOpen", false)
	var closed_save: Dictionary = drawer.call("GetDrawerDiagnostics") as Dictionary
	_require(not bool(closed_save.get("unsaved", true)), "closing drawer did not flush save: %s" % str(closed_save.get("last_error", "")))
	if RenderingServer.get_rendering_device() != null:
		await _check_gpu(tree, backdrop, drawer)
	var confirmation: ConfirmationDialog = drawer.get_node_or_null("ResetConfirmation") as ConfirmationDialog
	_require(confirmation != null, "missing reset confirmation dialog")
	if confirmation != null:
		confirmation.confirmed.emit()
		await tree.create_timer(0.65).timeout
		var reset: Resource = backdrop.call("GetMenuTuningSettings") as Resource
		_require(reset != null and is_equal_approx(float(reset.get("FieldOfView")), 58.0) and not bool(reset.get("Paused")), "reset did not restore factory defaults")
		_require(int((drawer.call("GetDrawerDiagnostics") as Dictionary).get("save_successes", 0)) >= 2, "reset did not autosave")
	# Do quality rebuilds after the receiver checks/reset: they invalidate the old
	# mesh references. Keep frozen travel and route stable across both presets.
	drawer.call("SetTuningValue", "Paused", true)
	var terrain_before: Dictionary = backdrop.call("GetBackdropDiagnostics") as Dictionary
	var terrain_generation_before: int = int(terrain_before.get("terrain_generation", -1))
	var seed_before: int = int(terrain_before.get("seed", -1))
	var route_start_before: float = float(terrain_before.get("route_start_m", -1.0))
	var travel_stable: float = float(terrain_before.get("travel_m", -1.0))
	var cloud_effect_id: int = (backdrop.get_node("MenuFlyoverSunshineLayer") as Node).get("Effect").get_instance_id()
	drawer.call("SetTuningValue", "TerrainSpacingMeters", 250)
	drawer.call("SetTuningValue", "TerrainSpacingMeters", 125)
	await tree.create_timer(.85).timeout
	_require(int((backdrop.call("GetBackdropDiagnostics") as Dictionary).get("terrain_generation", -1)) == terrain_generation_before, "cancelled terrain edit rebuilt unnecessarily")
	drawer.call("SetTuningValue", "TerrainSpacingMeters", 250)
	drawer.call("SetTuningValue", "FarTerrainDetail", 1)
	await tree.process_frame
	_require(bool((backdrop.call("GetBackdropDiagnostics") as Dictionary).get("terrain_rebuild_pending", false)), "terrain quality edit did not queue a rebuild")
	await tree.create_timer(1.0).timeout
	var terrain_after: Dictionary = await _wait_for_terrain_rebuild(backdrop, tree)
	_require(int(terrain_after.get("terrain_generation", -1)) == terrain_generation_before + 1 and int(terrain_after.get("terrain_cache_rows", -1)) == 1025 and int(terrain_after.get("terrain_cache_columns", -1)) == 769 and int(terrain_after.get("terrain_sample_count", -1)) == 788225 and int(terrain_after.get("near_triangles", -1)) == 1572864 and int(terrain_after.get("far_sample_count", -1)) == 33792 and int(terrain_after.get("far_triangles", -1)) == 65536 and terrain_after.get("far_atlas_size", Vector2i.ZERO) == Vector2i(1024, 33), "250m terrain rebuild dimensions/generation incorrect")
	_require(is_equal_approx(float(terrain_after.get("route_start_m", -2.0)), route_start_before) and is_equal_approx(float(terrain_after.get("travel_m", -2.0)), travel_stable) and int(terrain_after.get("seed", -2)) == seed_before and int(terrain_after.get("mesh_count", 0)) == 2 and (backdrop.get_node("MenuFlyoverSunshineLayer") as Node).get("Effect").get_instance_id() == cloud_effect_id, "250m rebuild changed stable flyover state")
	_check_terrain_receivers(menu_viewport, 1025, 769, 0.25)
	drawer.call("SetTuningValue", "TerrainSpacingMeters", 500)
	drawer.call("SetTuningValue", "FarTerrainDetail", 0)
	await tree.create_timer(1.0).timeout
	var terrain_low: Dictionary = await _wait_for_terrain_rebuild(backdrop, tree)
	_require(int(terrain_low.get("terrain_generation", -1)) == terrain_generation_before + 2 and int(terrain_low.get("terrain_cache_rows", -1)) == 513 and int(terrain_low.get("terrain_cache_columns", -1)) == 385 and int(terrain_low.get("terrain_sample_count", -1)) == 197505 and int(terrain_low.get("near_triangles", -1)) == 393216 and int(terrain_low.get("far_sample_count", -1)) == 8704 and int(terrain_low.get("far_triangles", -1)) == 16384 and terrain_low.get("far_atlas_size", Vector2i.ZERO) == Vector2i(512, 17), "500m terrain rebuild dimensions/generation incorrect")
	_require(is_equal_approx(float(terrain_low.get("route_start_m", -2.0)), route_start_before) and int(terrain_low.get("mesh_count", 0)) == 2, "500m rebuild changed stable flyover state")
	_require(is_equal_approx(float(terrain_low.get("travel_m", -2.0)), travel_stable) and int(terrain_low.get("seed", -2)) == seed_before and (backdrop.get_node("MenuFlyoverSunshineLayer") as Node).get("Effect").get_instance_id() == cloud_effect_id, "500m rebuild reset travel/seed/cloud effect")
	_check_terrain_receivers(menu_viewport, 513, 385, 0.5)
	_require(bool(drawer.call("FlushPendingSave")), "lower terrain quality was not saved")
	var low_reload: Control = load("res://scripts/ui/RingMenuSpaceBackdrop.cs").new()
	low_reload.Seed = 1337
	low_reload.MenuSettingsPath = fixture_path
	tree.root.add_child(low_reload)
	var restored_low: Dictionary = low_reload.call("GetBackdropDiagnostics")
	_require(int(restored_low.get("terrain_spacing_meters", 0)) == 500 and int(restored_low.get("far_terrain_detail", -1)) == 0 and int(restored_low.get("terrain_cache_rows", 0)) == 513, "fresh preview did not load saved terrain quality")
	# Closing during initial work and a queued quality change must retire safely.
	var pending_settings: Resource = low_reload.call("GetMenuTuningSettings")
	pending_settings.set("TerrainSpacingMeters", 250)
	low_reload.call("ApplyMenuTuningSettings", pending_settings)
	low_reload.queue_free()
	await tree.process_frame
	if capture and not _failed:
		drawer.call("SetDrawerOpen", true)
		menu.call("_show_main_menu")
		await tree.process_frame
		await tree.create_timer(1.0).timeout
		var settled: Dictionary = drawer.call("GetDrawerDiagnostics")
		_require(not bool(settled.get("unsaved", true)) and str(settled.get("status_text", "")).contains("Saved"), "open panel did not finish autosaving")
		await RenderingServer.frame_post_draw
		var prefix: String = "res://.godot/menu_space/menu_tuning_%dx%d_" % [menu_viewport.size.x, menu_viewport.size.y]
		for tab_index: int in [0, 1, 2, 3, 4, 5]:
			tabs.current_tab = tab_index
			await tree.process_frame
			await RenderingServer.frame_post_draw
			var image: Image = tree.root.get_texture().get_image()
			_require(image != null and image.save_png(prefix + tabs.get_tab_title(tab_index).to_lower() + ".png") == OK, "tuning capture failed")
	backdrop.call("ApplyMenuTuningSettings", original)
	drawer.call("SetDrawerOpen", false)
	# Check flushing the last edit when the drawer exits before its debounce.
	var exit_drawer: Control = load("res://scripts/ui/MenuFlyoverTuningDrawer.cs").new()
	exit_drawer.SavePathOverride = "res://.godot/menu_tuning_exit_fixture.tres"
	menu.add_child(exit_drawer)
	exit_drawer.call("SetTuningValue", "FieldOfView", 69.0)
	exit_drawer.queue_free()
	await tree.process_frame
	var exited: Resource = ResourceLoader.load("res://.godot/menu_tuning_exit_fixture.tres", "", ResourceLoader.CACHE_MODE_IGNORE)
	_require(exited != null and is_equal_approx(float(exited.get("FieldOfView")), 69.0), "drawer exit did not save the final edit")
	backdrop.call("ApplyMenuTuningSettings", original)
	_require(_hash_file(production_path) == production_hash, "menu tuning changed the production defaults file")
	if not _failed:
		print("MENU_FLYOVER_TUNING_CHECK=PASS ", drawer.call("GetDrawerDiagnostics"))
	return not _failed

func _check_gpu(tree: SceneTree, backdrop: Node, drawer: Node) -> void:
	# The earlier sanitizer test intentionally persisted the minimum .25 Hz. GPU
	# scheduling assertions below are bounded around the normal menu 5 Hz cadence.
	drawer.call("SetTuningValue", "ShadowUpdatesPerSecond", 5.0)
	var cloud_before: Dictionary = (backdrop.call("GetBackdropDiagnostics") as Dictionary).get("cloud", {}) as Dictionary
	var serial_before: int = int(cloud_before.get("shadow_wrapper_serial", 0))
	var adapter: Node = backdrop.get_node("MenuFlyoverSunshineLayer")
	var effect: CompositorEffect = adapter.get("Effect") as CompositorEffect
	var initial_wind: Vector3 = effect.get("large_scale_clouds_position")
	drawer.call("SetTuningValue", "CloudDriftMps", 80.0)
	_require((effect.get("large_scale_clouds_position") as Vector3).is_equal_approx(initial_wind), "wind speed edit jumped the cloud position")
	drawer.call("SetTuningValue", "CloudCoverage", 0.42)
	drawer.call("SetTuningValue", "CloudSharpness", 0.8)
	await tree.create_timer(0.8).timeout
	var coverage: Dictionary = (backdrop.call("GetBackdropDiagnostics") as Dictionary).get("cloud", {}) as Dictionary
	_require(int(coverage.get("shadow_wrapper_serial", 0)) > serial_before, "cloud coverage edit did not regenerate shadow map")
	_require(is_equal_approx(float(effect.get("clouds_coverage")), .42) and is_equal_approx(float(effect.get("clouds_sharpness")), .8), "cloud density/sharpness were not applied to Sunshine")
	var dispatch_before: int = int(coverage.get("requested_dispatch_count", 0))
	var edit_started: int = Time.get_ticks_msec()
	for edit: int in 20:
		drawer.call("SetTuningValue", "CloudCoverage", .5 + edit * .005)
		await tree.process_frame
	await tree.create_timer(.3).timeout
	var edits: Dictionary = adapter.call("GetCloudDiagnostics")
	_require(int(edits.get("requested_dispatch_count", 0)) - dispatch_before <= int(ceil((Time.get_ticks_msec() - edit_started) * .005)) + 1, "rapid slider edits bypassed the shadow update budget")
	_require(bool(edits.get("shadow_ready", false)), "rapid slider edits left frozen shadows stale")
	drawer.call("SetTuningValue", "CloudsEnabled", false)
	await tree.create_timer(0.3).timeout
	var disabled: Dictionary = (backdrop.call("GetBackdropDiagnostics") as Dictionary).get("cloud", {}) as Dictionary
	_require(not bool(disabled.get("cloud_enabled", true)) and not bool(disabled.get("shadow_ready", true)), "disabling clouds left shadows active")
	var callbacks: int = int(disabled.get("actual_effect_callback_serial", 0))
	await tree.create_timer(0.3).timeout
	var stopped: Dictionary = (backdrop.call("GetBackdropDiagnostics") as Dictionary).get("cloud", {}) as Dictionary
	_require(int(stopped.get("actual_effect_callback_serial", 0)) == callbacks, "disabled clouds still invoked compositor")
	drawer.call("SetTuningValue", "CloudsEnabled", true)
	drawer.call("SetTuningValue", "CloudShadowsEnabled", false)
	await tree.create_timer(0.4).timeout
	var shadows_off: Dictionary = (backdrop.call("GetBackdropDiagnostics") as Dictionary).get("cloud", {}) as Dictionary
	_require(bool(shadows_off.get("cloud_enabled", false)) and not bool(shadows_off.get("shadow_generation_enabled", true)) and int(shadows_off.get("actual_effect_callback_serial", 0)) > callbacks, "turning shadows off stopped clouds or generation")
	drawer.call("SetTuningValue", "CloudShadowsEnabled", true)
	drawer.call("SetTuningValue", "ShadowResolution", 256)
	drawer.call("SetTuningValue", "CloudResolution", 2)
	drawer.call("SetTuningValue", "CloudViewSteps", 64)
	drawer.call("SetTuningValue", "CloudLightingSteps", 3)
	drawer.call("SetTuningValue", "CloudMinStepMeters", 120.0)
	drawer.call("SetTuningValue", "CloudMaxStepMeters", 960.0)
	drawer.call("SetTuningValue", "CloudLightingTravelKm", 9.0)
	drawer.call("SetTuningValue", "CloudLodBias", 0.6)
	drawer.call("SetTuningValue", "CloudBlurPower", 0.05)
	drawer.call("SetTuningValue", "CloudBlurQuality", 3)
	drawer.call("SetTuningValue", "CloudFastReconstruction", true)
	drawer.call("SetTuningValue", "CloudDitherSpeed", 4.0)
	drawer.call("SetTuningValue", "CloudRayStartJitterStrength", .35)
	drawer.call("SetTuningValue", "CloudStationaryHistoryWeight", .40)
	drawer.call("SetTuningValue", "CloudMovingHistoryWeight", .10)
	drawer.call("SetTuningValue", "ShadowVerticalSamples", 7)
	await tree.create_timer(0.8).timeout
	var quality: Dictionary = (backdrop.call("GetBackdropDiagnostics") as Dictionary).get("cloud", {}) as Dictionary
	_require(bool(quality.get("shadow_ready", false)) and quality.get("shadow_resolution", Vector2i.ZERO) == Vector2i(256, 256) and int(quality.get("cloud_resolution_scale", -1)) == 2, "quality resolutions were not rebuilt safely")
	_require(int(quality.get("cloud_view_steps", 0)) == 64 and int(quality.get("cloud_lighting_steps", 0)) == 3, "cloud quality steps were not applied")
	_require(is_equal_approx(float(quality.get("min_step_distance_meters", 0.0)), 120.0) and is_equal_approx(float(quality.get("max_step_distance_meters", 0.0)), 960.0) and is_equal_approx(float(quality.get("lighting_travel_distance_meters", 0.0)), 9000.0), "advanced cloud march settings were not applied")
	_require(is_equal_approx(float(quality.get("cloud_lod_bias", 0.0)), 0.6) and is_equal_approx(float(quality.get("cloud_blur_power", -1.0)), 0.05) and int(quality.get("cloud_blur_quality", 0)) == 3 and bool(quality.get("cloud_fast_reconstruction", false)) and int(quality.get("shadow_vertical_samples", 0)) == 7, "advanced cloud reconstruction settings were not applied")
	_require(is_equal_approx(float(effect.get("min_step_distance")), 120.0) and is_equal_approx(float(effect.get("max_step_distance")), 960.0) and is_equal_approx(float(effect.get("lighting_travel_distance")), 9000.0) and is_equal_approx(float(effect.get("lod_bias")), .6) and is_equal_approx(float(effect.get("blur_power")), .05) and int(effect.get("blur_quality")) == 3, "advanced cloud values did not reach Sunshine properties")
	_require(is_equal_approx(float(quality.get("cloud_dither_speed", -1.0)), 4.0) and is_equal_approx(float(quality.get("cloud_ray_start_jitter_strength", -1.0)), .35) and is_equal_approx(float(quality.get("cloud_stationary_history_weight", -1.0)), .40) and is_equal_approx(float(quality.get("cloud_moving_history_weight", -1.0)), .10) and is_equal_approx(float(quality.get("effective_cloud_history_weight", -1.0)), .40), "temporal cloud settings were not selected for a frozen camera")
	_require(is_equal_approx(float(effect.get("dither_speed")), 4.0) and is_equal_approx(float(effect.get("ray_start_jitter_strength")), .35) and is_equal_approx(float(effect.get("accumulation_decay")), .40), "temporal cloud settings did not reach Sunshine properties")
	# A non-frozen zero-speed camera is still stationary; travel resumes only once
	# the menu speed is positive. Wait past the one-frame history clear after each edit.
	var prior_freeze_motion: bool = bool(backdrop.get("FreezeMotion"))
	var prior_speed: float = float((backdrop.call("GetMenuTuningSettings") as Resource).get("SpeedKmPerSecond"))
	var prior_paused: bool = bool((backdrop.call("GetMenuTuningSettings") as Resource).get("Paused"))
	backdrop.set("FreezeMotion", false)
	drawer.call("SetTuningValue", "Paused", false)
	drawer.call("SetTuningValue", "SpeedKmPerSecond", 0.0)
	await tree.create_timer(.15).timeout
	for frame: int in 3:
		await tree.process_frame
	var zero_speed: Dictionary = adapter.call("GetCloudDiagnostics") as Dictionary
	_require(is_equal_approx(float(zero_speed.get("effective_cloud_history_weight", -1.0)), .40) and is_equal_approx(float(effect.get("accumulation_decay")), .40), "zero-speed camera did not retain stationary history")
	drawer.call("SetTuningValue", "SpeedKmPerSecond", 5.0)
	await tree.create_timer(.15).timeout
	for frame: int in 3:
		await tree.process_frame
	var moving: Dictionary = adapter.call("GetCloudDiagnostics") as Dictionary
	_require(is_equal_approx(float(moving.get("effective_cloud_history_weight", -1.0)), .10) and is_equal_approx(float(effect.get("accumulation_decay")), .10), "moving camera did not select moving history")
	drawer.call("SetTuningValue", "SpeedKmPerSecond", prior_speed)
	drawer.call("SetTuningValue", "Paused", prior_paused)
	backdrop.set("FreezeMotion", prior_freeze_motion)
	await tree.create_timer(.15).timeout
	for frame: int in 3:
		await tree.process_frame
	# The day/night brightness controls scale the original colored ambient palette,
	# rather than replacing it with grayscale. Test both solar extremes directly.
	drawer.call("SetTuningValue", "CloudAmbientDay", .50)
	drawer.call("SetTuningValue", "CloudAmbientNight", .25)
	drawer.call("SetTuningValue", "SunElevationDegrees", 90.0)
	await tree.process_frame
	var day_ambient: Color = effect.get("cloud_ambient_color") as Color
	_require(is_equal_approx(day_ambient.r, .375) and is_equal_approx(day_ambient.g, .40) and is_equal_approx(day_ambient.b, .44), "day cloud ambient did not preserve the colored palette")
	drawer.call("SetTuningValue", "SunElevationDegrees", -90.0)
	await tree.process_frame
	var night_ambient: Color = effect.get("cloud_ambient_color") as Color
	_require(is_equal_approx(night_ambient.r, .1875) and is_equal_approx(night_ambient.g, .20) and is_equal_approx(night_ambient.b, .22), "night cloud ambient did not preserve the colored palette")
	_require(bool(effect.get("menu_fast_reconstruction")), "menu fast reconstruction flag was not applied to Sunshine")
	var packed: PackedByteArray = effect.get("general_data") as PackedByteArray
	_require(packed.size() == 720 and is_equal_approx(packed.decode_float(688), 1.0) and is_equal_approx(packed.decode_float(692), 1.0), "menu reconstruction/temporal UBO flags were not packed")
	var shadow_serial: int = int(quality.get("shadow_wrapper_serial", 0))
	drawer.call("SetTuningValue", "CloudFastReconstruction", false)
	drawer.call("SetTuningValue", "ShadowVerticalSamples", 8)
	await tree.create_timer(0.8).timeout
	var toggled: Dictionary = adapter.call("GetCloudDiagnostics") as Dictionary
	packed = effect.get("general_data") as PackedByteArray
	_require(not bool(toggled.get("cloud_fast_reconstruction", true)) and not bool(effect.get("menu_fast_reconstruction")) and packed.size() == 720 and is_equal_approx(packed.decode_float(688), 0.0) and int(toggled.get("shadow_vertical_samples", 0)) == 8 and int(toggled.get("shadow_wrapper_serial", 0)) > shadow_serial, "fast reconstruction toggle or shadow sample refresh was not applied")
	var cloud_texture: Texture2D = adapter.call("GetCloudTexture") as Texture2D
	var shadow_texture: Texture2D = effect.call("get_gpu_cloud_shadow_texture", 0) as Texture2D
	var target_size: Vector2i = (backdrop.get_node("MenuFlyoverViewport") as SubViewport).size
	_require(cloud_texture != null and cloud_texture.get_width() == target_size.x / 4 and shadow_texture != null and shadow_texture.get_width() == 256, "GPU textures do not match selected resolutions")
	_require(is_equal_approx(float(quality.get("sun_energy", -1.0)), 0.0) and is_equal_approx(float(quality.get("effective_cloud_light_energy", -1.0)), 0.0) and float(quality.get("cloud_light_energy", 0)) > 0, "zero sunlight was not propagated to clouds")
	# Keep the original 64 km lead: half the radius, a quarter of the full map span.
	drawer.call("SetTuningValue", "ShadowRadiusKm", 128.0)
	await _wait_for_shadow_settle(adapter, tree)
	var baseline_cloud: Dictionary = (backdrop.call("GetBackdropDiagnostics") as Dictionary).get("cloud", {}) as Dictionary
	var shadow_configs: Array = effect.get("cloud_shadow_configs") as Array
	var near_config: Dictionary = shadow_configs[0] as Dictionary if shadow_configs.size() > 0 else {}
	var canonical_center: Vector2 = near_config.get("canonical_center", Vector2.ZERO) as Vector2
	var route_around: float = float((backdrop.call("GetBackdropDiagnostics") as Dictionary).get("route_start_m", 0.0)) + float((backdrop.call("GetBackdropDiagnostics") as Dictionary).get("travel_m", 0.0))
	_require(is_equal_approx(float(baseline_cloud.get("requested_shadow_span_meters", 0.0)), 256000.0) and absf(canonical_center.x - route_around - 64000.0) <= 2.0, "shadow map center did not retain its original 64 km lead")
	# Exercise every permitted map bucket once, but leave the expensive 2048 map
	# frozen after it settles; the quarter-resolution cloud target keeps this bounded.
	for resolution: int in [128, 256, 512, 1024, 2048]:
		drawer.call("SetTuningValue", "ShadowResolution", resolution)
		var bucket: Dictionary = await _wait_for_shadow_settle(adapter, tree)
		var bucket_texture: Texture2D = effect.call("get_gpu_cloud_shadow_texture", 0) as Texture2D
		_require(bucket.get("shadow_resolution", Vector2i.ZERO) == Vector2i(resolution, resolution) and bool(bucket.get("shadow_ready", false)) and bucket_texture != null and bucket_texture.get_width() == resolution and bucket_texture.get_height() == resolution, "shadow resolution bucket did not publish: %d" % resolution)
	# Freeze only after the final dispatch has published, then prove a stationary
	# frame does not schedule another map. This avoids racing an in-flight GPU job.
	var frozen: Dictionary = await _wait_for_shadow_settle(adapter, tree)
	var frozen_dispatches: int = int(frozen.get("requested_dispatch_count", 0))
	var frozen_status: Dictionary = effect.call("get_gpu_cloud_shadow_status") as Dictionary
	var frozen_actual_dispatches: int = int(frozen_status.get("near_dispatches", 0))
	await tree.create_timer(.35).timeout
	var still_frozen: Dictionary = adapter.call("GetCloudDiagnostics") as Dictionary
	var still_frozen_status: Dictionary = effect.call("get_gpu_cloud_shadow_status") as Dictionary
	_require(int(still_frozen.get("requested_dispatch_count", 0)) == frozen_dispatches and int(still_frozen_status.get("near_dispatches", 0)) == frozen_actual_dispatches and is_equal_approx(float(still_frozen.get("effective_cloud_history_weight", -1.0)), .40), "frozen camera dispatched shadows or selected moving history")
	# Receiver visibility and map generation are independent diagnostic switches.
	drawer.call("SetTuningValue", "ShadowReceiversEnabled", false)
	drawer.call("SetTuningValue", "ShadowUpdatesPerSecond", 20.0)
	var hidden_status: Dictionary = effect.call("get_gpu_cloud_shadow_status") as Dictionary
	var hidden_dispatches: int = int(hidden_status.get("near_dispatches", 0))
	drawer.call("SetTuningValue", "CloudDensity", .61)
	await tree.create_timer(.2).timeout
	var receivers_off: Dictionary = adapter.call("GetCloudDiagnostics") as Dictionary
	hidden_status = effect.call("get_gpu_cloud_shadow_status") as Dictionary
	_require(bool(receivers_off.get("shadow_generation_actual", false)) and not bool(receivers_off.get("shadow_receivers_actual", true)) and not bool(receivers_off.get("shadow_ready", true)) and int(hidden_status.get("near_dispatches", 0)) > hidden_dispatches, "receiver toggle stopped generation or left terrain shadows visible")
	drawer.call("SetTuningValue", "ShadowReceiversEnabled", true)
	drawer.call("SetTuningValue", "ShadowGenerationEnabled", false)
	await tree.process_frame
	var generation_off: Dictionary = adapter.call("GetCloudDiagnostics") as Dictionary
	var paused_dispatches: int = int(generation_off.get("requested_dispatch_count", 0))
	var paused_actual_dispatches: int = int((effect.call("get_gpu_cloud_shadow_status") as Dictionary).get("near_dispatches", 0))
	await tree.create_timer(.35).timeout
	generation_off = adapter.call("GetCloudDiagnostics") as Dictionary
	_require(not bool(generation_off.get("shadow_generation_requested", true)) and not bool(generation_off.get("shadow_generation_actual", true)) and bool(generation_off.get("shadow_receivers_actual", false)) and int(generation_off.get("requested_dispatch_count", 0)) == paused_dispatches, "generation pause did not preserve receiver visibility or stop dispatches")
	_require(int((effect.call("get_gpu_cloud_shadow_status") as Dictionary).get("near_dispatches", 0)) == paused_actual_dispatches, "paused Sunshine producer still dispatched a shadow map")
	# A map whose projection or extinction changed while generation was paused is
	# unsafe to sample. It must stay cleared until a resumed dispatch publishes it.
	drawer.call("SetTuningValue", "ShadowRadiusKm", 64.0)
	drawer.call("SetTuningValue", "ShadowExtinctionPerKm", .75)
	drawer.call("SetTuningValue", "ShadowResolution", 128)
	await tree.process_frame
	var stale: Dictionary = adapter.call("GetCloudDiagnostics") as Dictionary
	_require(not bool(stale.get("shadow_ready", true)) and bool(stale.get("shadow_dirty", false)) and is_equal_approx(float(stale.get("shadow_radius_km", -1.0)), 64.0) and is_equal_approx(float(stale.get("shadow_extinction_per_km", -1.0)), .75), "paused shadow edit left a stale map visible")
	drawer.call("SetTuningValue", "ShadowGenerationEnabled", true)
	var resumed: Dictionary = await _wait_for_shadow_settle(adapter, tree)
	_require(bool(resumed.get("shadow_generation_actual", false)) and bool(resumed.get("shadow_ready", false)) and is_equal_approx(float(resumed.get("requested_shadow_span_meters", 0.0)), 128000.0) and is_equal_approx(float(resumed.get("published_shadow_span_meters", 0.0)), 128000.0), "resumed shadow map did not publish the requested radius span")
	for material: ShaderMaterial in [(backdrop.get_node("MenuFlyoverViewport").get_node("MenuFlyoverTerrain") as MeshInstance3D).material_override as ShaderMaterial, (backdrop.get_node("MenuFlyoverViewport").get_node("MenuFlyoverFarTerrain") as MeshInstance3D).material_override as ShaderMaterial]:
		_require(is_equal_approx(float(material.get_shader_parameter("shadow_extent_km")), 128.0), "terrain shadow shader span does not match the 64 km half-span")
	var viewport: SubViewport = backdrop.get_node_or_null("MenuFlyoverViewport") as SubViewport
	var environment: WorldEnvironment = viewport.get_node_or_null("MenuFlyoverEnvironment") as WorldEnvironment if viewport != null else null
	_require(environment != null and environment.environment != null and environment.environment.ambient_light_energy > 0.0, "ambient lighting did not remain after zero sunlight")
	print("MENU_TUNING_GPU_CHECK ", quality)

func _wait_for_shadow_settle(adapter: Node, tree: SceneTree) -> Dictionary:
	var deadline: int = Time.get_ticks_msec() + 20000
	var stats: Dictionary = adapter.call("GetCloudDiagnostics") as Dictionary
	while (not bool(stats.get("shadow_ready", false)) or bool(stats.get("shadow_pending", true)) or bool(stats.get("shadow_dirty", true))) and Time.get_ticks_msec() < deadline:
		await tree.process_frame
		stats = adapter.call("GetCloudDiagnostics") as Dictionary
	_require(bool(stats.get("shadow_ready", false)) and not bool(stats.get("shadow_pending", true)) and not bool(stats.get("shadow_dirty", true)), "shadow map did not settle before deadline")
	return stats

func _check_terrain_receivers(viewport: SubViewport, rows: int, columns: int, spacing_km: float) -> void:
	for mesh_name: String in ["MenuFlyoverTerrain", "MenuFlyoverFarTerrain"]:
		var mesh: MeshInstance3D = viewport.get_node(mesh_name) as MeshInstance3D
		var material: ShaderMaterial = mesh.material_override as ShaderMaterial
		_require(mesh.visible and material != null, "rebuilt terrain is not visible: " + mesh_name)
		_require(int(material.get_shader_parameter("rows")) == rows and int(material.get_shader_parameter("columns")) == columns, "receiver dimensions do not match rebuilt terrain: " + mesh_name)
		var info: Vector4 = material.get_shader_parameter("terrain_info")
		_require(is_equal_approx(info.z, spacing_km), "receiver spacing does not match rebuilt terrain: " + mesh_name)

func _wait_for_terrain_rebuild(backdrop: Node, tree: SceneTree) -> Dictionary:
	var started: int = Time.get_ticks_msec()
	var deadline: int = started + 60000
	var stats: Dictionary = backdrop.call("GetBackdropDiagnostics") as Dictionary
	while (bool(stats.get("terrain_rebuild_pending", false)) or not bool(stats.get("ready", false))) and Time.get_ticks_msec() < deadline:
		await tree.process_frame
		stats = backdrop.call("GetBackdropDiagnostics") as Dictionary
	_require(not bool(stats.get("terrain_rebuild_pending", true)) and bool(stats.get("ready", false)), "terrain rebuild did not become ready before deadline")
	print("MENU_TERRAIN_READY spacing=%s elapsed_ms=%d" % [stats.get("terrain_spacing_meters", -1), Time.get_ticks_msec() - started])
	# CPU cache readiness can precede the separately capped presentation by one
	# frame; process_frame is emitted before Node._Process updates visibility.
	await tree.create_timer(.05).timeout
	return stats

func _find_drawer(node: Node) -> Node:
	if node.has_method("GetDrawerDiagnostics"):
		return node
	for child: Node in node.get_children():
		var found: Node = _find_drawer(child)
		if found != null:
			return found
	return null

func _first_spinbox(node: Node) -> SpinBox:
	if node is SpinBox:
		return node as SpinBox
	for child: Node in node.get_children():
		var found: SpinBox = _first_spinbox(child)
		if found != null:
			return found
	return null

func _key_event(keycode: Key) -> InputEventKey:
	var event := InputEventKey.new()
	event.keycode = keycode
	event.pressed = true
	return event

func _release_key(keycode: Key) -> void:
	var event: InputEventKey = _key_event(keycode)
	event.pressed = false
	Input.parse_input_event(event)

func _hash_file(path: String) -> PackedByteArray:
	var hash := HashingContext.new()
	if hash.start(HashingContext.HASH_SHA256) != OK:
		return PackedByteArray()
	hash.update(FileAccess.get_file_as_bytes(path))
	return hash.finish()

func _require(condition: bool, message: String) -> void:
	if condition:
		return
	_failed = true
	push_error("MENU_FLYOVER_TUNING_CHECK: " + message)
