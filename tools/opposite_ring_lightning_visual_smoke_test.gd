extends SceneTree

## GPU smoke: real Certified far renderer in day/night/totality, shared preview
## event, and stable main-viewport OFF/ON/OFF controls.
const WORLD_SCENE := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const LAUNCHER_SCENE := preload("res://scenes/WorldSizeLauncher.tscn")
const OUTPUT := "res://.godot/weather_phase2_visual"
var _world: Node3D

func _initialize() -> void:
	if RenderingServer.get_rendering_device() == null:
		return _fail("RenderingDevice required; headless/Compatibility is not a GPU result")
	call_deferred("_run")

func _run() -> void:
	_world = WORLD_SCENE.instantiate() as Node3D
	var launcher := LAUNCHER_SCENE.instantiate()
	root.add_child(launcher)
	if not launcher.is_node_ready(): await launcher.ready
	launcher.call("_on_certified_preset_pressed")
	launcher.call("_configure_game", _world, 5000000.0)
	launcher.free()
	root.add_child(_world)
	for _frame in range(20): await process_frame
	var weather := _world.get_node("RingVolumetricClouds")
	var far := _world.get_node("ScaledFarViewport")
	var grid := _world.get_node("ChunkGridManager")
	var camera := _world.get_node("Explorer/Head/Camera3D") as Camera3D
	var explorer := _world.get_node("Explorer")
	var clock := _world.get_node("RingDayNightCycle")
	var day_side_atmosphere := _world.get_node_or_null("DaySideAtmosphere")
	var sunshine_bridge := _world.get_node_or_null("SunshineCloudBridge")
	var sunshine_driver := _world.get_node_or_null("SunshineCloudsDriver")
	var mode := "day"
	var celestial_time := 0.0
	if "--lightning-night" in OS.get_cmdline_user_args():
		mode = "night"
	elif "--lightning-totality" in OS.get_cmdline_user_args():
		mode = "totality"
		celestial_time = 28187.36649295002 * 0.5
	clock.set("Paused", true)
	var circumference := float(grid.call("GetCanonicalRingCircumferenceMeters64"))
	var ring_width := float(grid.get("RingAcrossWidthMeters"))
	var across := ring_width * 0.5
	if sunshine_bridge != null:
		sunshine_bridge.call("SetBenchmarkViewerPosition", 0.0, 500.0, across)
	if mode == "day" or mode == "night":
		for candidate in range(0, 4501, 75):
			clock.call("set_simulation_seconds", float(candidate))
			var daylight := float(clock.get("DaylightFactor"))
			var elevation := float(clock.get("SunElevationDegrees"))
			var eclipse := float(clock.get("RawEclipseCoverage"))
			if (mode == "day" and daylight >= 0.9 and eclipse <= 0.000001) or (mode == "night" and elevation <= -18.0 and eclipse <= 0.000001):
				celestial_time = float(candidate)
				break
	clock.call("set_simulation_seconds", celestial_time)
	for _settle in range(20): await process_frame
	var target := grid.call("CanonicalRingPositionToWorld", Vector3(circumference * 0.5, 600.0, across)) as Vector3
	camera.look_at(target, Vector3.UP)
	for _aim_settle in range(20): await process_frame
	# The atmosphere owns several deliberately smooth state transitions.  Resolve
	# them before freezing this A/B fixture so the controls cannot measure sky
	# interpolation instead of the shared lightning event.
	if day_side_atmosphere != null:
		day_side_atmosphere.call("_process", 10.0)
		for _atmosphere_settle in range(4): await process_frame
	if mode == "day" and float(clock.get("DaylightFactor")) < 0.9:
		return _fail("day mode did not find a daylight observer phase")
	if mode == "night" and float(clock.get("SunElevationDegrees")) > -18.0:
		return _fail("night mode did not find an astronomical-night observer phase")
	if mode == "totality" and float(clock.get("RawEclipseCoverage")) < 0.999999:
		return _fail("totality mode did not reach raw eclipse coverage")
	explorer.set_process(false)
	explorer.set_physics_process(false)
	weather.set_process(false)
	if day_side_atmosphere != null: day_side_atmosphere.set_process(false)
	if sunshine_bridge != null: sunshine_bridge.set_process(false)
	if sunshine_driver != null: sunshine_driver.set_process(false)
	var world_environment := _world.get_node("WorldEnvironment") as WorldEnvironment
	var attributes := world_environment.camera_attributes as CameraAttributesPractical
	var fixture_auto_exposure_disabled := false
	if attributes != null and attributes.auto_exposure_enabled:
		# Exposure adaptation is temporal GPU state, not a lightning signal.
		attributes.auto_exposure_enabled = false
		fixture_auto_exposure_disabled = true
	for layer: CanvasLayer in _world.find_children("*", "CanvasLayer", true, false):
		if layer.layer > 0: layer.hide()
	if DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(OUTPUT)) != OK: return _fail("could not create output directory")
	weather.set("CloudSimulationTimeSeconds", 100.0)
	var atlas_ready := false
	for atlas_retry in range(120):
		far.call("_Process", 0.0)
		if not bool(far.get("FarWeatherAtlasRefreshInProgress")) and absf(float(far.get("FarWeatherAtlasSnapshotTimeSeconds")) - 100.0) <= 0.0001:
			atlas_ready = true
			break
		await process_frame
	if not atlas_ready:
		return _fail("far weather atlas did not publish frozen epoch=100 snapshot=%.6f pending=%s publications=%s" % [float(far.get("FarWeatherAtlasSnapshotTimeSeconds")), far.get("FarWeatherAtlasRefreshInProgress"), far.get("FarWeatherAtlasPublicationCount")])
	far.set("FarStormLightningEnabled", false)
	far.call("_Process", 0.0)
	far.set_process(false)
	Engine.time_scale = 0.0
	# Let the existing cloud/TAA history converge after the final lighting and
	# fixed-exposure change. These frames do not advance weather or the clock.
	for _frozen_settle in range(30): await process_frame
	var off_a := await _capture(_world.get_viewport(), "opposite_ring_%s_main_off_a" % mode)
	var far_viewport := far.get_node("FarRingViewport") as SubViewport
	var far_off_a := await _capture(far_viewport, "opposite_ring_%s_far_off_a" % mode)
	await process_frame
	var off_b := await _capture(_world.get_viewport(), "opposite_ring_%s_main_off_b" % mode)
	var far_off_b := await _capture(far_viewport, "opposite_ring_%s_far_off_b" % mode)
	var main_baseline := _roi_max_absolute_luma(off_a, off_b)
	var far_baseline := _roi_max_absolute_luma(far_off_a, far_off_b)
	if main_baseline > 0.005 or far_baseline > 0.005:
		return _fail("OFF/OFF target ROI was not stable main=%.6f far=%.6f" % [main_baseline, far_baseline])
	far.set("FarStormLightningEnabled", true)
	var source_event := weather.call("TriggerSharedLightningPreviewAt", circumference * 0.5, across, -9201, 1.0, 80000.0) as Dictionary
	weather.set("CloudSimulationTimeSeconds", 100.035)
	far.call("_Process", 0.0)
	far.set_process(false)
	await process_frame
	var on_image := await _capture(_world.get_viewport(), "opposite_ring_%s_main_on" % mode)
	var far_on := await _capture(far_viewport, "opposite_ring_%s_far_on" % mode)
	var ids := (far.get("SelectedFarLightningEventIds") as Array).duplicate()
	var shell := far_viewport.get_node("ScaledCloudShell") as MeshInstance3D
	var shell_material := shell.material_override as ShaderMaterial
	var event_a := shell_material.get_shader_parameter("far_lightning_event_a") as Vector4
	print("OPPOSITE_LIGHTNING_MODE mode=%s time=%.3f daylight=%.4f elevation=%.3f eclipse=%.6f phase=%s" % [mode, celestial_time, float(clock.get("DaylightFactor")), float(clock.get("SunElevationDegrees")), float(clock.get("RawEclipseCoverage")), clock.get("LightingPhaseName")])
	print("OPPOSITE_LIGHTNING_DIAG source=%s camera=%s explorer_underwater=%s shell_visible=%s shell_aabb=%s event_a=%s ring_width=%s shell_camera=%s handoff=%s fixture_auto_exposure_disabled=%s baseline_main=%.6f baseline_far=%.6f" % [source_event, camera.global_position, explorer.get("IsCameraUnderwater"), shell.visible, shell.get_aabb(), event_a, shell_material.get_shader_parameter("ring_width_km"), shell_material.get_shader_parameter("camera_world_position"), shell_material.get_shader_parameter("far_clipmap_handoff_start_meters"), fixture_auto_exposure_disabled, main_baseline, far_baseline])
	if source_event.is_empty() or int(source_event.get("event_id", 0)) <= 0 or not ids.has(int(source_event["event_id"])) or event_a.w <= 0.2:
		return _fail("shared preview was not selected/pulsing (source=%s ids=%s)" % [source_event, str(ids)])
	far.set("FarStormLightningEnabled", false)
	far.call("_Process", 0.0)
	await process_frame
	var off_image := await _capture(_world.get_viewport(), "opposite_ring_%s_main_off" % mode)
	var far_off := await _capture(far_viewport, "opposite_ring_%s_far_off" % mode)
	var main_contrast := _localized_positive_contrast(on_image, off_image)
	var far_contrast := _localized_positive_contrast(far_on, far_off)
	var main_control_contrast := _localized_positive_contrast(on_image, off_a)
	var far_control_contrast := _localized_positive_contrast(far_on, far_off_a)
	var main_post_drift := _roi_max_absolute_luma(off_a, off_image)
	var far_post_drift := _roi_max_absolute_luma(far_off_a, far_off)
	print("OPPOSITE_LIGHTNING_POST_BASELINE mode=%s main=%.6f far=%.6f" % [mode, main_post_drift, far_post_drift])
	if main_post_drift > 0.005 or far_post_drift > 0.005:
		return _fail("OFF-pre/OFF-post target ROI drifted main=%.6f far=%.6f" % [main_post_drift, far_post_drift])
	if "--lightning-isolation" in OS.get_cmdline_user_args():
		# Optional render diagnosis; never part of the normal acceptance path.
		shell.hide()
		await _capture(far_viewport, "diagnostic_far_shell_hidden")
		shell.show()
		var terrain := far_viewport.get_node_or_null("ScaledSeededRingSurface") as MeshInstance3D
		if terrain != null:
			terrain.hide()
			await _capture(far_viewport, "diagnostic_far_terrain_hidden")
			terrain.show()
	if main_contrast.x < 4.0 or main_contrast.y < 0.02 or far_contrast.x < 4.0 or far_contrast.y < 0.02 or main_control_contrast.x < 4.0 or main_control_contrast.y < 0.02 or far_control_contrast.x < 4.0 or far_control_contrast.y < 0.02:
		return _fail("shared opposite-ring lightning lacked localized positive contrast main=%s/%s far=%s/%s" % [main_contrast, main_control_contrast, far_contrast, far_control_contrast])
	print("OPPOSITE_RING_LIGHTNING_VISUAL_SMOKE_TEST=PASS source=%s ids=%s main=%s/%s far=%s/%s output=%s" % [source_event, str(ids), main_contrast, main_control_contrast, far_contrast, far_control_contrast, OUTPUT])
	quit(0)

func _capture(viewport: Viewport, label: String) -> Image:
	await RenderingServer.frame_post_draw
	var image := viewport.get_texture().get_image()
	if image == null or image.is_empty(): return Image.new()
	if image.save_png("%s/%s.png" % [OUTPUT, label]) != OK: return Image.new()
	return image

func _localized_positive_contrast(a: Image, b: Image) -> Vector2:
	if a.is_empty() or b.is_empty() or a.get_size() != b.get_size(): return Vector2.ZERO
	var positive := 0
	var maximum := 0.0
	var half_extent := 32
	var center_x := a.get_width() / 2
	var center_y := a.get_height() / 2
	for y in range(maxi(0, center_y - half_extent), mini(a.get_height(), center_y + half_extent)):
		for x in range(maxi(0, center_x - half_extent), mini(a.get_width(), center_x + half_extent)):
			var ca := a.get_pixel(x, y)
			var cb := b.get_pixel(x, y)
			var delta := (ca.r - cb.r) * 0.2126 + (ca.g - cb.g) * 0.7152 + (ca.b - cb.b) * 0.0722
			if delta > 0.02: positive += 1
			maximum = maxf(maximum, delta)
	return Vector2(float(positive), maximum)

func _roi_max_absolute_luma(a: Image, b: Image) -> float:
	if a.is_empty() or b.is_empty() or a.get_size() != b.get_size(): return INF
	var maximum := 0.0
	var half_extent := 32
	var center_x := a.get_width() / 2
	var center_y := a.get_height() / 2
	for y in range(maxi(0, center_y - half_extent), mini(a.get_height(), center_y + half_extent)):
		for x in range(maxi(0, center_x - half_extent), mini(a.get_width(), center_x + half_extent)):
			var ca := a.get_pixel(x, y)
			var cb := b.get_pixel(x, y)
			var delta := (ca.r - cb.r) * 0.2126 + (ca.g - cb.g) * 0.7152 + (ca.b - cb.b) * 0.0722
			maximum = maxf(maximum, absf(delta))
	return maximum

func _full_difference(a: Image, b: Image) -> Vector2:
	if a.is_empty() or b.is_empty() or a.get_size() != b.get_size(): return Vector2.ZERO
	var signed_max := -INF
	var absolute_max := 0.0
	for y in range(0, a.get_height(), 4):
		for x in range(0, a.get_width(), 4):
			var ca := a.get_pixel(x, y)
			var cb := b.get_pixel(x, y)
			var delta := (ca.r - cb.r) * 0.2126 + (ca.g - cb.g) * 0.7152 + (ca.b - cb.b) * 0.0722
			signed_max = maxf(signed_max, delta)
			absolute_max = maxf(absolute_max, absf(delta))
	return Vector2(signed_max, absolute_max)

func _fail(message: String) -> void:
	push_error("OPPOSITE_RING_LIGHTNING_VISUAL_SMOKE_TEST=FAIL %s" % message)
	quit(1)
