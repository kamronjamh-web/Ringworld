extends SceneTree

## GPU-only regression coverage for the actual ring-local sky material.  This
## intentionally has no terrain, clouds, or the gameplay WorldEnvironment in
## its viewport: a failed comparison therefore points at celestial rendering,
## rather than an occluder in the main world.

const WORLD_SCENE: PackedScene = preload("res://scenes/VoxelRingIntegrationTest.tscn")
const LAUNCHER_SCENE: PackedScene = preload("res://scenes/WorldSizeLauncher.tscn")
const EFFECTIVE_DAY_DIAGNOSTIC: Shader = preload("res://materials/celestial_lighting_diagnostic.gdshader")
const SIZE := Vector2i(1024, 1024)

var _output_dir := "res://.godot/celestial_visual"
var _world: Node3D
var _clock: Node
var _atmosphere: Node
var _viewport: SubViewport
var _camera: Camera3D
var _sky_material: ShaderMaterial
var _effective_day_viewport: SubViewport
var _effective_day_material: ShaderMaterial


func _initialize() -> void:
	# A RenderingDevice is required.  Do not allow --headless to falsely certify
	# shader output merely because the state-side tests happened to pass.
	if RenderingServer.get_rendering_device() == null:
		_fail("no RenderingDevice; this test must run on a GPU renderer (not headless/Compatibility)")
		return
	# SceneTree._initialize runs before the root can dispatch Node._Ready.
	# The launcher must have its real controls built before applying a preset.
	call_deferred("_setup")


func _setup() -> void:
	_world = WORLD_SCENE.instantiate() as Node3D
	if "--celestial-visual-certified" in OS.get_cmdline_user_args():
		_output_dir += "/certified"
		# Exercise the production launcher configuration without selecting or
		# writing a save slot. The raw integration scene has its far view disabled.
		var launcher := LAUNCHER_SCENE.instantiate()
		root.add_child(launcher)
		if not launcher.is_node_ready():
			await launcher.ready
		launcher.call("_on_certified_preset_pressed")
		launcher.call("_configure_game", _world, 5000000.0)
		launcher.free()
		if not bool(_world.get_node("ScaledFarViewport").get("Enabled")):
			return _fail("certified launcher preset did not enable the far sky viewport")
	root.add_child(_world)
	call_deferred("_run")


func _run() -> void:
	await process_frame
	await process_frame
	_clock = _world.get_node_or_null("RingDayNightCycle")
	_atmosphere = _world.get_node_or_null("DaySideAtmosphere")
	if _clock == null or _atmosphere == null:
		return _fail("VoxelRingIntegrationTest did not provide its clock/DaySideAtmosphere")
	if absf(float(_clock.get("FullCycleSeconds")) - 4500.0) > 0.01 \
			or absf(float(_clock.get("GiantDistanceKm")) - 136610.390625) > 0.1:
		return _fail("active scene did not load the 75-minute / 136,610.390625-km celestial preset")
	_clock.set("Paused", true)
	# Let the real atmosphere perform its normal initial material binding before
	# taking our diagnostic duplicate.
	await process_frame
	var source := _atmosphere.get("ring_sky_material") as ShaderMaterial
	if source == null or source.shader == null:
		return _fail("DaySideAtmosphere did not bind ring_sky_material")
	_sky_material = _source_faithful_snapshot(source)
	if _sky_material == null:
		return _fail("could not duplicate the actual ring sky material")
	_make_sky_viewport()
	_make_effective_day_fixture()
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(_output_dir))
	_verify_far_sky_binding(source)

	# Opposition: the giant should be an obvious rendered disk even after sky
	# scattering is disabled for this local comparison.
	await _set_time(0.0)
	var giant_preview := _preview_button("View giant")
	if giant_preview == null:
		return _fail("View giant debug control is missing")
	giant_preview.pressed.emit()
	await process_frame
	await process_frame
	# The preview advances the clock; refresh from the production-bound source
	# before capturing this event rather than retaining the startup duplicate.
	var preview_source := _atmosphere.get("ring_sky_material") as ShaderMaterial
	if preview_source == null:
		return _fail("DaySideAtmosphere lost its sky material after giant preview")
	_sky_material = _source_faithful_snapshot(preview_source)
	(_viewport.get_child(0) as WorldEnvironment).environment.sky.sky_material = _sky_material
	var giant_direction := _clock.get("GiantDirection") as Vector3
	if giant_direction.length_squared() < 0.9:
		return _fail("authoritative giant direction was invalid")
	var giant_diameter := float(_clock.get("GiantAngularDiameterDegrees"))
	if giant_diameter < 59.18 or giant_diameter > 64.12:
		return _fail("giant physical diameter should be about 61.65 degrees, got %.5f" % giant_diameter)
	# Fit the physical disk with margin so its enlarged physical diameter cannot
	# be hidden by a cropped diagnostic capture.
	var giant_fov := maxf(14.0, giant_diameter * 1.7)
	# Actual gameplay source: air must veil the lit giant rather than relying on
	# the later atmosphere-free geometry diagnostic.
	_sky_material.set_shader_parameter("planet_enabled", true)
	_sky_material.set_shader_parameter("ring_atmosphere_enabled", true)
	var lit_hazed_giant := await _capture("lit_hazed_giant", giant_direction, giant_fov)
	_sky_material.set_shader_parameter("planet_enabled", false)
	var lit_without_giant := await _capture("lit_hazed_sky_without_giant", giant_direction, giant_fov)
	if _mean_abs_difference(lit_hazed_giant, lit_without_giant) < 0.00005:
		return _fail("actual source lit capture did not retain isolated giant contrast")
	_sky_material.set_shader_parameter("planet_enabled", true)
	_sky_material.set_shader_parameter("ring_atmosphere_enabled", false)
	var lit_same_base_no_shell := await _capture("lit_giant_no_shell_control", giant_direction, giant_fov)
	if _mean_abs_difference(lit_hazed_giant, lit_same_base_no_shell) < 0.00005:
		return _fail("actual source physical shell did not veil the lit giant")
	_sky_material.set_shader_parameter("atmosphere_factor", 0.0)
	var lit_no_air_giant := await _capture("lit_giant_no_air_control", giant_direction, giant_fov)
	_sky_material.set_shader_parameter("ring_atmosphere_enabled", true)
	_sky_material.set_shader_parameter("atmosphere_factor", 1.0)
	_configure_diagnostic(false, true)
	if absf(float(_sky_material.get_shader_parameter("planet_angular_radius_degrees")) * 2.0 - giant_diameter) > 0.0001:
		return _fail("sky shader does not use the authoritative giant angular diameter")
	var giant_on := await _capture("giant_on", giant_direction, giant_fov)
	await _capture_main_view_celestial("main_view_giant_direction", giant_direction)
	_sky_material.set_shader_parameter("planet_enabled", false)
	var giant_off := await _capture("giant_off", giant_direction, giant_fov)
	var giant_delta := _mean_abs_difference(giant_on, giant_off)
	var giant_changed := _changed_pixels(giant_on, giant_off, 0.04)
	if _mean_luma(giant_on) < 0.003 or giant_delta < 0.004 or giant_changed < 500:
		return _fail("giant disk was not visibly rendered (delta=%.6f pixels=%d)" % [giant_delta, giant_changed])
	# The isolated source-material capture proves the actual normal gameplay
	# framing. Comparing planet-on/off pixels avoids making assumptions about the
	# gas giant palette, while the projection calculation catches radius clamps.
	var gameplay_camera := _world.get_node_or_null("Explorer/Head/Camera3D") as Camera3D
	if gameplay_camera == null or absf(gameplay_camera.fov - 80.0) > 0.01 \
			or gameplay_camera.keep_aspect != Camera3D.KEEP_HEIGHT:
		return _fail("normal gameplay camera must use 80-degree vertical FOV with KEEP_HEIGHT")
	var gameplay_viewport_size := gameplay_camera.get_viewport().get_visible_rect().size
	if gameplay_viewport_size.x <= 0.0 or gameplay_viewport_size.y <= 0.0:
		return _fail("normal gameplay viewport has an invalid size")
	var actual_aspect := gameplay_viewport_size.x / gameplay_viewport_size.y
	if absf(actual_aspect - (16.0 / 9.0)) > 0.001:
		return _fail("normal gameplay viewport is not 16:9 (aspect=%.6f)" % actual_aspect)
	var normal_size := Vector2i(1280, int(round(1280.0 / actual_aspect)))
	_viewport.size = normal_size
	_camera.keep_aspect = Camera3D.KEEP_HEIGHT
	_configure_diagnostic(false, true)
	var normal_on := await _capture("giant_normal_fov_on", giant_direction, gameplay_camera.fov)
	_sky_material.set_shader_parameter("planet_enabled", false)
	var normal_off := await _capture("giant_normal_fov_off", giant_direction, gameplay_camera.fov)
	var measured_width := _center_scan_changed_width_fraction(normal_on, normal_off)
	var normal_aspect := float(normal_size.x) / float(normal_size.y)
	var expected_width := tan(deg_to_rad(giant_diameter * 0.5)) / (normal_aspect * tan(deg_to_rad(gameplay_camera.fov * 0.5)))
	var screen_width_tolerance := 0.025 if "--celestial-visual-certified" in OS.get_cmdline_user_args() else 0.01
	if measured_width < 0.0 or absf(measured_width - 0.40) > screen_width_tolerance \
			or absf(measured_width - expected_width) > maxf(2.0 / float(normal_size.x), 0.005):
		return _fail("normal-FOV giant width differs from physical projection (measured=%.6f expected=%.6f)" % [measured_width, expected_width])
	_viewport.size = SIZE
	_camera.keep_aspect = Camera3D.KEEP_HEIGHT

	# At the real grazing conjunction, measure the photosphere disk area while
	# excluding the intentionally broad halo/rim from the occultation metric.
	var eclipse_preview := _preview_button("Next visible partial eclipse")
	if eclipse_preview == null:
		return _fail("partial eclipse debug control is missing")
	eclipse_preview.pressed.emit()
	await process_frame
	await process_frame
	var coverage := float(_clock.get("GiantEclipseCoverage"))
	if coverage < 0.30 or coverage >= 1.0:
		return _fail("expected partial authoritative conjunction, got coverage %.5f" % coverage)
	_configure_diagnostic(false, true)
	_sky_material.set_shader_parameter("sun_halo_intensity", 0.0)
	var sun_direction := _clock.get("ToSun") as Vector3
	var eclipse_on := await _capture("eclipse_giant_on", sun_direction, 3.0)
	_sky_material.set_shader_parameter("planet_enabled", false)
	var eclipse_off := await _capture("eclipse_giant_off", sun_direction, 3.0)
	var visible_on := _photosphere_bright_fraction(eclipse_on, 3.0)
	var visible_off := _photosphere_bright_fraction(eclipse_off, 3.0)
	var expected_visible := 1.0 - coverage
	if visible_off < 0.70 or visible_on >= 0.80 or absf(visible_on - expected_visible) > 0.18:
		return _fail("photosphere area disagrees with grazing eclipse (on=%.5f off=%.5f expected=%.5f analytic=%.5f)" % [visible_on, visible_off, expected_visible, coverage])

	# Totality is an authored physical event, not a presentation override.  The
	# giant must remove the white photosphere, while the same frame with its disk
	# disabled exposes it again; the production bridge must also zero direct sun.
	var total_preview := _preview_button("Next visible total eclipse")
	if total_preview == null:
		return _fail("total eclipse debug control is missing")
	total_preview.pressed.emit()
	await process_frame
	await process_frame
	var total_coverage := float(_clock.get("GiantEclipseCoverage"))
	if total_coverage < 0.999999:
		return _fail("expected natural total eclipse, got raw coverage %.7f" % total_coverage)
	var star_light := _world.get_node_or_null("StarLight") as DirectionalLight3D
	var source_total := _atmosphere.get("ring_sky_material") as ShaderMaterial
	if star_light == null or source_total == null \
			or float(source_total.get_shader_parameter("eclipse_solar_transmittance")) > 0.000001 \
			or star_light.light_energy > 0.000001:
		return _fail("total eclipse did not zero the raw direct-solar bridge")
	var main_environment := _world.get_node_or_null("WorldEnvironment") as WorldEnvironment
	if main_environment == null or main_environment.environment == null \
			or main_environment.environment.ambient_light_energy <= 0.0:
		return _fail("total eclipse removed the safe WorldEnvironment ambient light")
	await _capture_main_view_celestial("main_view_total_eclipse", _clock.get("ToSun") as Vector3)
	# Capture the real source material with and without its physical local air.
	# The distant giant must still be changed by shell extinction at totality;
	# disabling air is the control, not a second fake giant/rim rendering path.
	_sky_material = _source_faithful_snapshot(source_total)
	(_viewport.get_child(0) as WorldEnvironment).environment.sky.sky_material = _sky_material
	_sky_material.set_shader_parameter("stars_enabled", true)
	_sky_material.set_shader_parameter("star_visibility", 1.0)
	var hazed_giant := await _capture("hazed_giant", _clock.get("GiantDirection") as Vector3, giant_fov)
	_sky_material.set_shader_parameter("planet_enabled", false)
	var night_without_giant := await _capture("real_total_night", _clock.get("GiantDirection") as Vector3, giant_fov)
	if _mean_abs_difference(hazed_giant, night_without_giant) < 0.00005:
		return _fail("total-night source capture did not retain an isolated giant contrast")
	_sky_material.set_shader_parameter("planet_enabled", true)
	_sky_material.set_shader_parameter("ring_atmosphere_enabled", false)
	_sky_material.set_shader_parameter("atmosphere_factor", 0.0)
	var no_air_giant := await _capture("hazed_giant_no_air_control", _clock.get("GiantDirection") as Vector3, giant_fov)
	_sky_material.set_shader_parameter("atmosphere_factor", 1.0)
	_print_totality_sky_snapshot(source_total, _sky_material, hazed_giant)
	if _mean_abs_difference(hazed_giant, no_air_giant) < 0.00005:
		return _fail("physical sky air did not affect the distant giant at totality")
	var helper_passed: bool = await _verify_effective_day_helper()
	if not helper_passed:
		return
	_configure_diagnostic(false, true)
	_sky_material.set_shader_parameter("sun_halo_intensity", 0.0)
	var total_sun_direction := _clock.get("ToSun") as Vector3
	var total_on := await _capture("total_eclipse_giant_on", total_sun_direction, 3.0)
	_sky_material.set_shader_parameter("planet_enabled", false)
	var total_off := await _capture("total_eclipse_giant_off", total_sun_direction, 3.0)
	var total_visible_on := _photosphere_bright_fraction(total_on, 3.0)
	var total_visible_off := _photosphere_bright_fraction(total_off, 3.0)
	if total_visible_on > 0.05 or total_visible_off < 0.70:
		return _fail("total eclipse photosphere mismatch (on=%.5f off=%.5f)" % [total_visible_on, total_visible_off])

	# Stars are sampled in an atmosphere-free night diagnostic.  The rotation
	# value is the actual clock output converted to the shader's radians uniform.
	await _set_time(0.0)
	_configure_diagnostic(true, false)
	var stars_initial := await _capture("stars_initial", Vector3(0.31, 0.72, -0.62).normalized(), 75.0)
	await _set_time(float(_clock.get("FullCycleSeconds")) * 0.25)
	_configure_diagnostic(true, false)
	var stars_quarter := await _capture("stars_quarter_spin", Vector3(0.31, 0.72, -0.62).normalized(), 75.0)
	await _set_time(float(_clock.get("FullCycleSeconds")))
	_configure_diagnostic(true, false)
	var stars_full := await _capture("stars_full_spin", Vector3(0.31, 0.72, -0.62).normalized(), 75.0)
	var stars_paused := await _capture("stars_paused", Vector3(0.31, 0.72, -0.62).normalized(), 75.0)
	var quarter_delta := _mean_abs_difference(stars_initial, stars_quarter)
	var full_delta := _mean_abs_difference(stars_initial, stars_full)
	var paused_delta := _mean_abs_difference(stars_full, stars_paused)
	if _mean_luma(stars_initial) < 0.00001 or quarter_delta < 0.00015:
		return _fail("stars did not move under a quarter ring spin (delta=%.6f)" % quarter_delta)
	if full_delta > 0.002 or paused_delta > 0.001:
		return _fail("stars are not periodic/stable (full=%.6f paused=%.6f)" % [full_delta, paused_delta])

	print("CELESTIAL_VISUAL_SMOKE_TEST=PASS diameter=%.5f giant_delta=%.6f giant_pixels=%d normal_width=%.6f normal_expected=%.6f partial=%.5f/%.5f total=%.5f/%.5f stars=%.6f/%.6f/%.6f output=%s" % [giant_diameter, giant_delta, giant_changed, measured_width, expected_width, visible_on, visible_off, total_visible_on, total_visible_off, quarter_delta, full_delta, paused_delta, _output_dir])
	quit(0)


func _make_sky_viewport() -> void:
	_viewport = SubViewport.new()
	_viewport.size = SIZE
	_viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	_viewport.transparent_bg = false
	_viewport.world_3d = World3D.new()
	root.add_child(_viewport)
	var environment := Environment.new()
	environment.background_mode = Environment.BG_SKY
	environment.tonemap_mode = Environment.TONE_MAPPER_FILMIC
	environment.tonemap_exposure = 1.0
	var sky := Sky.new()
	sky.sky_material = _sky_material
	environment.sky = sky
	var world_environment := WorldEnvironment.new()
	world_environment.environment = environment
	_viewport.add_child(world_environment)
	_camera = Camera3D.new()
	_camera.current = true
	_viewport.add_child(_camera)


func _make_effective_day_fixture() -> void:
	_effective_day_viewport = SubViewport.new()
	# Keep this strictly 2D and non-degenerate for certified renderer mip setup.
	_effective_day_viewport.size = Vector2i(32, 32)
	_effective_day_viewport.disable_3d = true
	_effective_day_viewport.use_hdr_2d = false
	_effective_day_viewport.world_2d = World2D.new()
	_effective_day_viewport.transparent_bg = false
	_effective_day_viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	root.add_child(_effective_day_viewport)
	_effective_day_material = ShaderMaterial.new()
	_effective_day_material.shader = EFFECTIVE_DAY_DIAGNOSTIC
	var pixel := ColorRect.new()
	pixel.position = Vector2.ZERO
	pixel.size = Vector2(32.0, 32.0)
	pixel.material = _effective_day_material
	_effective_day_viewport.add_child(pixel)


func _set_time(seconds: float) -> void:
	_clock.call("set_simulation_seconds", seconds)
	# The atmosphere owns the production parameter writer.  Give it a process
	# tick, then retain its values in our material copy for isolated rendering.
	await process_frame
	await process_frame


func _configure_diagnostic(stars: bool, planet: bool) -> void:
	var source := _atmosphere.get("ring_sky_material") as ShaderMaterial
	if source != null:
		var expected_rotation := deg_to_rad(fposmod(float(_clock.get("RingRotationAngleDegrees")), 360.0))
		var bridged_rotation := float(source.get_shader_parameter("starfield_rotation_radians"))
		if _wrapped_angle_delta(bridged_rotation, expected_rotation) > 0.0001:
			_fail("DaySideAtmosphere starfield bridge differs from clock (uniform=%.7f expected=%.7f)" % [bridged_rotation, expected_rotation])
		_sky_material = _source_faithful_snapshot(source)
		var sky := (_viewport.get_child(0) as WorldEnvironment).environment.sky
		sky.sky_material = _sky_material
	_sky_material.set_shader_parameter("atmosphere_factor", 0.0)
	_sky_material.set_shader_parameter("ring_atmosphere_enabled", false)
	_sky_material.set_shader_parameter("planet_haze_enabled", false)
	_sky_material.set_shader_parameter("stars_enabled", stars)
	_sky_material.set_shader_parameter("star_visibility", 1.0 if stars else 0.0)
	_sky_material.set_shader_parameter("planet_enabled", planet)
	_sky_material.set_shader_parameter("sun_disk_intensity", 0.0 if stars else 2.55)
	_sky_material.set_shader_parameter("sun_halo_intensity", 0.0 if stars else 0.20)


func _source_faithful_snapshot(source: ShaderMaterial) -> ShaderMaterial:
	# ShaderMaterial.duplicate(true) resets dynamic source_color vec3 uniforms to
	# shader defaults. Copy the live material's actual shader parameters instead.
	var snapshot := ShaderMaterial.new()
	snapshot.shader = source.shader
	for uniform_info: Dictionary in source.shader.get_shader_uniform_list():
		var uniform_name := StringName(uniform_info.get("name", ""))
		if uniform_name.is_empty() or uniform_name == &"ringworld_solar_visibility":
			continue
		var value = source.get_shader_parameter(uniform_name)
		if value != null:
			snapshot.set_shader_parameter(uniform_name, value)
	# These values specifically catch the prior sky-palette regression, while
	# sun direction and atmosphere factor cover the dynamic event bridge.
	for required_name: StringName in [&"sky_top_color", &"sky_horizon_color", &"sun_direction", &"atmosphere_factor"]:
		if snapshot.get_shader_parameter(required_name) != source.get_shader_parameter(required_name):
			_fail("source-faithful sky snapshot lost %s" % required_name)
	return snapshot


func _verify_far_sky_binding(source: ShaderMaterial) -> void:
	var far_root := _world.get_node_or_null("ScaledFarViewport")
	var far_viewport := far_root.get_node_or_null("FarRingViewport") if far_root != null else null
	var far_environment := far_viewport.get_node_or_null("FarWorldEnvironment") as WorldEnvironment if far_viewport != null else null
	var main_environment := _world.get_node_or_null("WorldEnvironment") as WorldEnvironment
	if far_root != null and not bool(far_root.get("Enabled")):
		if main_environment.environment.background_mode != Environment.BG_SKY \
				or main_environment.environment.sky.sky_material != source:
			return _fail("normal world does not render the authoritative celestial sky")
		return
	if main_environment == null or far_environment == null or main_environment.environment == null or far_environment.environment == null:
		return _fail("scaled FarRingViewport/FarWorldEnvironment was not created")
	if far_environment.environment.sky != main_environment.environment.sky \
			or main_environment.environment.sky == null \
			or main_environment.environment.sky.sky_material != source:
		return _fail("FarRingViewport does not share DaySideAtmosphere's actual sky material")


func _preview_button(text: String) -> Button:
	for button: Button in _world.get_node("CelestialDebugHud").find_children("*", "Button", true, false):
		if button.text == text:
			return button
	return null


func _capture(label: String, direction: Vector3, fov: float) -> Image:
	_camera.fov = fov
	_camera.global_position = Vector3.ZERO
	var up := Vector3.UP if absf(direction.normalized().dot(Vector3.UP)) < 0.98 else Vector3.FORWARD
	_camera.look_at(direction.normalized(), up)
	await process_frame
	await RenderingServer.frame_post_draw
	var image := _viewport.get_texture().get_image()
	if image == null or image.is_empty():
		_fail("GPU readback failed for %s" % label)
		return Image.new()
	image.save_png("%s/%s.png" % [_output_dir, label])
	print("CELESTIAL_VISUAL_CAPTURE path=%s/%s.png size=%dx%d" % [_output_dir, label, image.get_width(), image.get_height()])
	return image


func _capture_main_view_celestial(label: String, direction: Vector3) -> void:
	# This is evidence from the real composited main viewport, kept separate from
	# the sky-only assertions because terrain/clouds are allowed to occlude it.
	var gameplay_camera := _world.get_node_or_null("Explorer/Head/Camera3D") as Camera3D
	if gameplay_camera == null:
		return _fail("main gameplay camera is missing")
	var saved_transform := gameplay_camera.global_transform
	var hidden_huds: Array[CanvasLayer] = []
	for layer: CanvasLayer in _world.find_children("*", "CanvasLayer", true, false):
		if layer.layer > 0 and layer.visible:
			hidden_huds.append(layer)
			layer.hide()
	var up := Vector3.UP if absf(direction.dot(Vector3.UP)) < 0.98 else Vector3.FORWARD
	gameplay_camera.look_at(gameplay_camera.global_position + direction.normalized(), up)
	await process_frame
	await RenderingServer.frame_post_draw
	var image := _world.get_viewport().get_texture().get_image()
	gameplay_camera.global_transform = saved_transform
	for layer in hidden_huds:
		layer.show()
	if image == null or image.is_empty() or _mean_luma(image) <= 0.00001:
		return _fail("real main viewport celestial frame was empty/black (%s)" % label)
	image.save_png("%s/%s.png" % [_output_dir, label])
	print("CELESTIAL_VISUAL_CAPTURE path=%s/%s.png size=%dx%d" % [_output_dir, label, image.get_width(), image.get_height()])


func _mean_abs_difference(a: Image, b: Image) -> float:
	if a.get_size() != b.get_size(): return INF
	var sum := 0.0
	for y in range(0, a.get_height(), 2):
		for x in range(0, a.get_width(), 2):
			sum += absf(_luma(a.get_pixel(x, y)) - _luma(b.get_pixel(x, y)))
	return sum / float((a.get_width() / 2) * (a.get_height() / 2))


func _changed_pixels(a: Image, b: Image, threshold: float) -> int:
	var count := 0
	for y in range(a.get_height()):
		for x in range(a.get_width()):
			if absf(_luma(a.get_pixel(x, y)) - _luma(b.get_pixel(x, y))) > threshold: count += 1
	return count


func _center_scan_changed_width_fraction(on_image: Image, off_image: Image) -> float:
	# Centered opposition gives the most stable scan.  Its boundaries come only
	# from GPU-rendered on/off differences, never from an expected planet color.
	if on_image.get_size() != off_image.get_size() or on_image.is_empty():
		return -1.0
	var y := on_image.get_height() / 2
	var first := -1
	var last := -1
	for x in range(on_image.get_width()):
		if absf(_luma(on_image.get_pixel(x, y)) - _luma(off_image.get_pixel(x, y))) > 0.025:
			if first < 0:
				first = x
			last = x
	if first < 0 or last <= first:
		return -1.0
	return float(last - first + 1) / float(on_image.get_width())


func _photosphere_bright_fraction(image: Image, fov_degrees: float) -> float:
	# Inner 90% of the analytical solar disk: deliberately excludes shader AA and
	# halo pixels, while retaining enough area to compare against disc geometry.
	var radius := maxf(2.0, float(image.get_width()) * 0.265 / fov_degrees * 0.90)
	var center := Vector2(float(image.get_width()) * 0.5, float(image.get_height()) * 0.5)
	var bright := 0
	var total := 0
	for y in range(image.get_height()):
		for x in range(image.get_width()):
			if Vector2(x, y).distance_to(center) <= radius:
				total += 1
				# This measures the white photosphere, not the planet's lit blue
				# atmospheric rim. At the closer orbit that rim is broad enough
				# for a low 0.20 brightness threshold to falsely count it as Sun.
				if _luma(image.get_pixel(x, y)) > 0.85: bright += 1
	return float(bright) / maxf(1.0, float(total))


func _verify_effective_day_helper() -> bool:
	# Exercise the production include on GPU, including a temporary global
	# override. Restore the clock-owned global before returning to gameplay tests.
	# The atmosphere owns the authoritative published value. Its current value is
	# what must survive the isolated GPU override, not a project default lookup.
	var saved_visibility := float(_atmosphere.get("current_solar_visibility"))
	var saved_process_mode: Node.ProcessMode = _atmosphere.process_mode
	_atmosphere.process_mode = Node.PROCESS_MODE_DISABLED
	for visibility: float in PackedFloat32Array([0.0, 0.5, 1.0]):
		for geometric_day: float in PackedFloat32Array([0.0, 0.5, 1.0]):
			var expected: float = geometric_day * visibility
			RenderingServer.global_shader_parameter_set("ringworld_solar_visibility", visibility)
			_effective_day_material.set_shader_parameter("geometric_day", geometric_day)
			_effective_day_material.set_shader_parameter("expected_value", expected)
			await process_frame
			await RenderingServer.frame_post_draw
			var image: Image = _effective_day_viewport.get_texture().get_image()
			var actual: float = _luma(image.get_pixel(8, 16)) if image != null and not image.is_empty() else -1.0
			var control: float = _luma(image.get_pixel(24, 16)) if image != null and not image.is_empty() else -1.0
			var positive_reference_failed := expected > 0.0 and control <= 0.02
			if absf(actual - control) > 0.02 or positive_reference_failed:
				RenderingServer.global_shader_parameter_set("ringworld_solar_visibility", saved_visibility)
				_atmosphere.process_mode = saved_process_mode
				_fail("effective-day regression failed for geometric=%.2f visibility=%.2f actual=%.5f control=%.5f expected=%.5f hdr2d=%s" % [geometric_day, visibility, actual, control, expected, _effective_day_viewport.use_hdr_2d])
				return false
	RenderingServer.global_shader_parameter_set("ringworld_solar_visibility", saved_visibility)
	_atmosphere.process_mode = saved_process_mode
	return true


func _print_totality_sky_snapshot(source: ShaderMaterial, duplicate: ShaderMaterial, image: Image) -> void:
	# Source/duplicate values make a blue totality frame diagnosable without
	# modifying either material or its exposure.
	var sampled_background: float = _luma(image.get_pixel(8, 8)) if image != null and not image.is_empty() else -1.0
	print("CELESTIAL_TOTALITY_SKY source_top=%s source_horizon=%s source_atmosphere=%.5f duplicate_top=%s duplicate_horizon=%s duplicate_atmosphere=%.5f visibility=%.5f sampled_background=%.5f" % [
		source.get_shader_parameter("sky_top_color"),
		source.get_shader_parameter("sky_horizon_color"),
		float(source.get_shader_parameter("atmosphere_factor")),
		duplicate.get_shader_parameter("sky_top_color"),
		duplicate.get_shader_parameter("sky_horizon_color"),
		float(duplicate.get_shader_parameter("atmosphere_factor")),
		float(_atmosphere.get("current_solar_visibility")),
		sampled_background])


func _mean_luma(image: Image) -> float:
	var sum := 0.0
	for y in range(0, image.get_height(), 2):
		for x in range(0, image.get_width(), 2):
			sum += _luma(image.get_pixel(x, y))
	return sum / float((image.get_width() / 2) * (image.get_height() / 2))


func _wrapped_angle_delta(a: float, b: float) -> float:
	return absf(fposmod(a - b + PI, TAU) - PI)


func _luma(color: Color) -> float:
	return color.r * 0.2126 + color.g * 0.7152 + color.b * 0.0722


func _fail(message: String) -> void:
	push_error("CELESTIAL_VISUAL_SMOKE_TEST=FAIL %s" % message)
	quit(1)
