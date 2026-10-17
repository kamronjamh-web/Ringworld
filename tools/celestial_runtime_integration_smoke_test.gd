extends SceneTree

const WORLD_SCENE: PackedScene = preload("res://scenes/VoxelRingIntegrationTest.tscn")

var _world: Node = null


func _initialize() -> void:
	_world = WORLD_SCENE.instantiate()
	root.add_child(_world)
	call_deferred("_run")


func _run() -> void:
	# Let the clock and atmosphere complete their normal startup bindings.
	await process_frame
	await process_frame
	await process_frame
	await process_frame
	var clock := _world.get_node_or_null("RingDayNightCycle")
	var sun := _world.get_node_or_null("StarLight") as DirectionalLight3D
	var atmosphere := _world.get_node_or_null("DaySideAtmosphere")
	var debug_hud := _world.get_node_or_null("CelestialDebugHud") as CanvasLayer
	if clock == null or sun == null or atmosphere == null or debug_hud == null:
		return _fail("runtime celestial integration nodes are missing")
	var solar_global := ProjectSettings.get_setting("shader_globals/ringworld_solar_visibility", {}) as Dictionary
	if solar_global.is_empty() or float(solar_global.get("value", -1.0)) != 1.0:
		return _fail("ringworld solar-visibility global must default to 1 for scene/menu reset")
	var to_sun := clock.get("ToSun") as Vector3
	if not to_sun.is_finite() or to_sun.length_squared() < 0.99:
		return _fail("clock returned an invalid star direction")
	if to_sun.dot(sun.global_basis.z.normalized()) < 0.9999:
		return _fail("DirectionalLight +Z does not match the clock's direction to the star")
	var daylight := float(clock.get("DaylightFactor"))
	var twilight := float(clock.get("TwilightFactor"))
	var night := float(clock.get("NightFactor"))
	if absf(daylight + twilight + night - 1.0) > 0.0001:
		return _fail("runtime day/twilight/night factors are not normalized")
	if int(clock.get("DeterministicVisualSeed")) == 0:
		return _fail("terrain world seed was not bridged to the deterministic celestial visual seed")
	var sky_material := atmosphere.get("ring_sky_material") as ShaderMaterial
	var seed_offset: Variant = sky_material.get_shader_parameter("celestial_seed_offset") if sky_material != null else null
	if sky_material == null or not seed_offset is Vector3 or not (seed_offset as Vector3).is_finite() or (seed_offset as Vector3).length_squared() <= 0.0:
		return _fail("deterministic celestial visual seed was not bound to the ring sky material")
	if not is_finite(float(clock.get("SecondsUntilSunrise"))) \
			or not is_finite(float(clock.get("SecondsUntilSunset"))) \
			or not is_finite(float(clock.get("SecondsUntilGiantRise"))) \
			or not is_finite(float(clock.get("SecondsUntilGiantSet"))):
		return _fail("runtime rise/set prediction is missing")
	if float(clock.get("GiantAngularDiameterDegrees")) < 61.6 \
			or float(clock.get("GiantAngularDiameterDegrees")) > 61.7:
		return _fail("runtime gas-giant angular diameter is implausible")
	if clock.get("Configuration") == null:
		return _fail("celestial Resource was not assigned")
	if absf(float(clock.get("FullCycleSeconds")) - 4500.0) > 0.001 \
			or absf(float(clock.get("GiantDistanceKm")) - 136610.390625) > 0.01 \
			or absf(float(clock.get("GiantOrbitSeconds")) - 28187.36649295002) > 0.01 \
			or absf(float(clock.get("GiantGrazingMissDegrees")) - 10.87182510946549) > 0.0001:
		return _fail("effective celestial clock did not retain the physical 40%-width preset")
	var authored_config := load("res://resources/celestial/celestial_default.tres") as Resource
	var authored_giant := authored_config.get("GasGiant") as Resource
	if authored_giant == null or authored_giant.resource_path != "res://resources/celestial/gas_giant_default.tres":
		return _fail("authored gas-giant Resource was silently replaced by C# defaults; check exported property casing")
	if absf(float(authored_config.get("RingDaySeconds")) - 4500.0) > 0.001 \
			or absf(float(authored_giant.get("DistanceKm")) - 136610.390625) > 0.01 \
			or absf(float(authored_giant.get("RadiusKm")) - 70000.0) > 0.01 \
			or absf(float(authored_giant.get("OrbitSeconds")) - 28187.36649295002) > 0.01 \
			or absf(float(authored_giant.get("GrazingMissDegrees")) - 10.87182510946549) > 0.0001:
		return _fail("authored celestial Resource did not retain the physical 40%-width preset")
	var shader_rotation := float(sky_material.get_shader_parameter("starfield_rotation_radians"))
	var clock_rotation := deg_to_rad(float(clock.get("RingRotationAngleDegrees")))
	if absf(wrapf(shader_rotation - clock_rotation, -PI, PI)) > 0.01:
		return _fail("starfield did not receive the authoritative ring rotation")
	if not bool(sky_material.get_shader_parameter("planet_enabled")):
		return _fail("the default enabled giant was disabled in the rendered sky")
	if debug_hud.visible:
		return _fail("optional celestial debug overlay should start hidden")
	var preview_error := await _verify_preview_controls(clock, debug_hud)
	if not preview_error.is_empty():
		return _fail(preview_error)
	print("CELESTIAL_RUNTIME_INTEGRATION_TEST=PASS seed=%d sun=%.2f giant=%.2f" % [
		int(clock.get("DeterministicVisualSeed")),
		float(clock.get("SunElevationDegrees")),
		float(clock.get("GiantElevationDegrees"))])
	quit(0)


func _verify_preview_controls(clock: Node, hud: CanvasLayer) -> String:
	var explorer := _world.get_node("Explorer") as Node3D
	var camera := _world.get_node("Explorer/Head/Camera3D") as Camera3D
	var giant_button: Button
	var eclipse_button: Button
	var total_eclipse_button: Button
	var restore_button: Button
	for button: Button in hud.find_children("*", "Button", true, false):
		match button.text:
			"View giant": giant_button = button
			"Next visible partial eclipse": eclipse_button = button
			"Next visible total eclipse": total_eclipse_button = button
			"Restore preview": restore_button = button
	if giant_button == null or eclipse_button == null or total_eclipse_button == null or restore_button == null:
		return "celestial preview controls are missing"
	# Snapshot immediately before the first preview. The preview controls pause the
	# celestial clock, not world physics, so freeze only the observer during our
	# awaited atmosphere ticks. This keeps restore's teleport invariant strict.
	var initial_time := float(clock.call("get_simulation_seconds"))
	var initial_paused := bool(clock.get("Paused"))
	var initial_yaw := float(explorer.get("SavedYaw"))
	var initial_pitch := float(explorer.get("SavedPitch"))
	var initial_position := explorer.global_position
	var explorer_physics_was_enabled := explorer.is_physics_processing()
	explorer.set_physics_process(false)
	giant_button.pressed.emit()
	await process_frame
	await process_frame
	var giant_direction := clock.get("GiantDirection") as Vector3
	if not bool(clock.get("Paused")) or float(clock.get("GiantElevationDegrees")) < 15.0 \
			or (-camera.global_basis.z).dot(giant_direction) < 0.99:
		return "View giant did not pause at a visible natural giant and aim the camera"
	eclipse_button.pressed.emit()
	await process_frame
	await process_frame
	var coverage := float(clock.get("RawEclipseCoverage"))
	if coverage <= 0.0 or coverage >= 1.0 or float(clock.get("SunElevationDegrees")) < 15.0 \
			or (-camera.global_basis.z).dot(clock.get("ToSun") as Vector3) < 0.99:
		return "eclipse preview did not select/aim at a naturally visible partial event"
	total_eclipse_button.pressed.emit()
	# Preview changes the clock synchronously, but the production atmosphere owns
	# its render-state write on _process. Wait for that real tick before sampling.
	await process_frame
	await process_frame
	if not bool(clock.get("Paused")) or float(clock.get("RawEclipseCoverage")) < 0.99999 \
			or float(clock.get("SunElevationDegrees")) < 15.0 \
			or (-camera.global_basis.z).dot(clock.get("ToSun") as Vector3) < 0.99:
		return "total eclipse preview did not select/aim at a naturally visible total event"
	var atmosphere := _world.get_node_or_null("DaySideAtmosphere")
	var total_sun := _world.get_node_or_null("StarLight") as DirectionalLight3D
	var environment_node := _world.get_node_or_null("WorldEnvironment") as WorldEnvironment
	var sky_material := atmosphere.get("ring_sky_material") as ShaderMaterial if atmosphere != null else null
	var astronomical_day := float(clock.get("DaylightFactor"))
	if atmosphere == null or total_sun == null or environment_node == null \
			or environment_node.environment == null or sky_material == null:
		return "total eclipse effective-lighting dependencies are missing"
	if astronomical_day <= 0.001 \
			or absf(float(atmosphere.get("current_day_factor"))) > 0.000001 \
			or absf(float(atmosphere.get("current_solar_visibility"))) > 0.000001:
		return "totality did not separate astronomical daylight from exact received-light night"
	# Phase E2 (pitch-black eclipse) deliberately changes totality: the stars come out fully and the night sky palette, ambient and
	# atmosphere night colour are scaled by the darkness model (docs/eclipse-false-sun-spec.md D.1). The weight is smoothed over 0.5 s,
	# so wait for it to settle, then check the published multipliers end to end.
	await create_timer(0.9).timeout
	var darkness_weight := float(atmosphere.get("eclipse_darkness_weight"))
	var sky_multiplier := float(atmosphere.get("eclipse_sky_night_multiplier"))
	var ambient_multiplier := float(atmosphere.get("eclipse_ambient_multiplier"))
	if darkness_weight < 0.99 or sky_multiplier > 0.51 or ambient_multiplier > 0.16:
		return "totality did not reach full eclipse darkness: weight=%.3f sky=%.3f ambient=%.3f" % [darkness_weight, sky_multiplier, ambient_multiplier]
	var night_star_floor := float(atmosphere.get("night_surface_star_visibility"))
	var expected_star_floor := maxf(night_star_floor, float(atmosphere.get("eclipse_star_floor")))
	var actual_star_visibility := float(sky_material.get_shader_parameter("star_visibility"))
	var expected_top := (atmosphere.get("night_sky_top_color") as Color) * sky_multiplier
	var expected_horizon := (atmosphere.get("night_sky_horizon_color") as Color) * sky_multiplier
	var actual_top := sky_material.get_shader_parameter("sky_top_color") as Vector3
	var actual_horizon := sky_material.get_shader_parameter("sky_horizon_color") as Vector3
	if expected_star_floor < night_star_floor or absf(actual_star_visibility - expected_star_floor) > 0.0001 \
			or actual_top.distance_to(Vector3(expected_top.r, expected_top.g, expected_top.b)) > 0.0001 \
			or actual_horizon.distance_to(Vector3(expected_horizon.r, expected_horizon.g, expected_horizon.b)) > 0.0001:
		return "totality did not apply the eclipse star floor and darkened night sky palette"
	var night_light_scale := _global_float("ringworld_night_light_scale", float(atmosphere.get("eclipse_atmosphere_night_multiplier")))
	if absf(night_light_scale - float(atmosphere.get("eclipse_atmosphere_night_multiplier"))) > 0.0001 or night_light_scale > 0.36:
		return "totality did not publish the darkened atmosphere night scale: %.3f" % night_light_scale
	var hub_day_global := _global_float("ringworld_hub_day", 0.0)
	var hub_weight_global := _global_float("ringworld_hub_light_weight", 0.0)
	if hub_day_global != 0.0 or hub_weight_global != 0.0:
		return "E2 must leave the hub-light globals neutral"
	var night_ambient := float(atmosphere.get("night_ambient_energy"))
	if total_sun.light_energy > 0.000001 or environment_node.environment.ambient_light_energy <= 0.0 \
			or environment_node.environment.ambient_light_energy > night_ambient * 0.16:
		return "totality did not leave direct sun at zero with a faint navigable ambient"
	restore_button.pressed.emit()
	explorer.set_physics_process(explorer_physics_was_enabled)
	var time_delta := absf(float(clock.call("get_simulation_seconds")) - initial_time)
	var pause_changed := bool(clock.get("Paused")) != initial_paused
	var yaw_delta := absf(wrapf(float(explorer.get("SavedYaw")) - initial_yaw, -PI, PI))
	var pitch_delta := absf(float(explorer.get("SavedPitch")) - initial_pitch)
	var position_delta := explorer.global_position.distance_to(initial_position)
	if time_delta > 0.0001 or pause_changed or yaw_delta > 0.0001 \
			or pitch_delta > 0.0001 or position_delta > 0.0001:
		return "preview restore changed state: time=%.6f pause_changed=%s yaw=%.6f pitch=%.6f position=%.6f" % [
			time_delta, pause_changed, yaw_delta, pitch_delta, position_delta]
	await create_timer(2.5).timeout
	if float(clock.get("RawEclipseCoverage")) <= 0.0:
		if float(atmosphere.get("eclipse_darkness_weight")) != 0.0 \
				or _global_float("ringworld_night_light_scale", 1.0) != 1.0:
			return "darkness did not return exactly to neutral after the eclipse: w=%.5f cov=%.5f giantcov=%.5f" % [float(atmosphere.get("eclipse_darkness_weight")), float(clock.get("RawEclipseCoverage")), float(clock.get("GiantEclipseCoverage"))]
	return ""


func _fail(message: String) -> bool:
	push_error("CELESTIAL_RUNTIME_INTEGRATION_TEST=FAIL %s" % message)
	quit(1)
	return true


# The headless dummy renderer does not store global shader parameters (get returns null); fall back to the value the script published.
func _global_float(name: String, fallback: float) -> float:
	var value: Variant = RenderingServer.global_shader_parameter_get(name)
	return fallback if value == null else float(value)
