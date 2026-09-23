extends SceneTree

const TEST_SCENE := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const GeneratedOutputPaths := preload("res://tools/generated_output_paths.gd")
const CAPTURE_FILE := "cloud_lighting_storm.png"
const MAX_FRAMES := 1200

var _scene: Node
var _bridge: Node
var _environment: WorldEnvironment
var _frame := 0
var _storm_spawned := false
var _dark_since_frame := -1
var _recovering := false
var _receiver_toggle_started := -1
var _capture_path := ""


func _initialize() -> void:
	_capture_path = GeneratedOutputPaths.smoke_capture_path(CAPTURE_FILE)
	_scene = TEST_SCENE.instantiate()
	_bridge = _scene.get_node("SunshineCloudBridge")
	_environment = _scene.get_node("WorldEnvironment") as WorldEnvironment
	_bridge.set("CloudResolutionScale", 0)
	_bridge.set("RaymarchSteps", 128)
	_bridge.set("CastCloudShadows", true)
	# This test intentionally certifies the asynchronous callback fallback. The
	# GPU path has its own smoke test so neither implementation can mask the other.
	_bridge.set("PreferGpuCloudShadows", false)
	_bridge.set("SpawnDistanceMeters", 0.0)
	_bridge.set("LightingResponseSpeed", 4.0)
	_bridge.set("ShadowRefreshSeconds", 60.0)
	root.add_child(_scene)


func _process(_delta: float) -> bool:
	_frame += 1
	if _frame == 2:
		# Make this a controlled maximum-occlusion measurement. The production sun
		# remains oblique, which correctly displaces an anvil's shadow away from the
		# cloud center; here the sun is directly overhead so the sampled camera cell
		# must sit beneath the dense core.
		var viewer := _scene.get_node("Explorer") as Node3D
		var sun := _scene.get_node("StarLight") as DirectionalLight3D
		var local_up := viewer.global_basis.y.normalized()
		sun.look_at(sun.global_position - local_up, viewer.global_basis.z.normalized())
	if _frame == 12:
		# The broad front reliably covers the camera region while retaining broken
		# convective structure. Peak opacity separately verifies that dense cores
		# can eliminate direct sun without forcing every cell to be black.
		_call_bridge("SpawnStormFrontAhead", "spawn_storm_front_ahead")
		_storm_spawned = true

	if _recovering:
		var recovered_sun := float(_call_bridge_result(
			"GetCameraDirectSunTransmittance", "get_camera_direct_sun_transmittance", 0.0))
		var recovered_ambient := float(_call_bridge_result(
			"GetCameraAmbientTransmittance", "get_camera_ambient_transmittance", 0.0))
		var recovered_energy := _environment.environment.ambient_light_energy
		if recovered_sun > 0.90 and recovered_ambient > 0.90 and recovered_energy > 0.32:
			print(
				"Cloud lighting recovery passed: sun=%.3f, ambient=%.3f, environment=%.3f"
				% [recovered_sun, recovered_ambient, recovered_energy])
			quit(0)
			return true
		if _frame >= MAX_FRAMES:
			return _fail(
				"Cloud lighting did not recover: sun=%.3f, ambient=%.3f, environment=%.3f"
				% [recovered_sun, recovered_ambient, recovered_energy])
		return false

	if _storm_spawned and _frame > 30:
		var installed_resolution := int(_call_bridge_result(
			"GetInstalledShadowResolution", "get_installed_shadow_resolution", 0))
		var sun_transmittance := float(_call_bridge_result(
			"GetCameraDirectSunTransmittance", "get_camera_direct_sun_transmittance", 1.0))
		var ambient_transmittance := float(_call_bridge_result(
			"GetCameraAmbientTransmittance", "get_camera_ambient_transmittance", 1.0))
		var receiver_count := int(_call_bridge_result(
			"GetCloudShadowReceiverCount", "get_cloud_shadow_receiver_count", 0))
		var peak_opacity := float(_call_bridge_result(
			"GetInstalledShadowPeakOpacity", "get_installed_shadow_peak_opacity", 0.0))
		if installed_resolution >= 64 and peak_opacity > 0.95 and receiver_count >= 8:
			if _dark_since_frame < 0:
				_dark_since_frame = _frame
				return false
			if _frame - _dark_since_frame < 45:
				return false
			if _receiver_toggle_started < 0:
				_bridge.set("CloudShadowReceiversEnabled", false)
				_receiver_toggle_started = _frame
				return false
			if _frame == _receiver_toggle_started + 2:
				if bool(_call_bridge_result(
						"IsCloudShadowProjectorEnabled", "is_cloud_shadow_projector_enabled", true)):
					return _fail("CPU fallback receiver profiling toggle did not disable the projector")
				_bridge.set("CloudShadowReceiversEnabled", true)
				return false
			if _frame < _receiver_toggle_started + 4:
				return false
			if not bool(_call_bridge_result(
					"IsCloudShadowProjectorEnabled", "is_cloud_shadow_projector_enabled", false)):
				return _fail("CPU fallback receiver did not restore after profiling toggle")
			var environment_energy := _environment.environment.ambient_light_energy
			if sun_transmittance >= 0.75 or ambient_transmittance >= 0.70:
				return _fail(
					"Installed storm shadow did not darken the camera region: peak=%.3f, sun=%.3f, ambient=%.3f"
					% [peak_opacity, sun_transmittance, ambient_transmittance])
			if environment_energy >= 0.30:
				return _fail(
					"Storm ambient controller did not darken the environment: %.3f"
					% environment_energy)
			var image := root.get_texture().get_image()
			var error := image.save_png(_capture_path)
			if error != OK:
				return _fail("Cloud-lighting capture failed: %s" % error)
			print(
				"Cloud lighting smoke passed: map=%dpx, receivers=%d, peak=%.3f, sun=%.3f, ambient=%.3f, environment=%.3f, capture=%s"
				% [installed_resolution, receiver_count, peak_opacity, sun_transmittance, ambient_transmittance, environment_energy, _capture_path])
			_call_bridge("ClearSpawnedWeather", "clear_spawned_weather")
			_bridge.set("CastCloudShadows", false)
			_recovering = true
			return false

	if _frame >= MAX_FRAMES:
		return _fail(
			"Cloud lighting did not converge: %s"
			% str(_call_bridge_result(
				"GetCloudLightingDiagnostics", "get_cloud_lighting_diagnostics", "unavailable")))
	return false


func _call_bridge(pascal_name: StringName, snake_name: StringName) -> void:
	var method_name := pascal_name if _bridge.has_method(pascal_name) else snake_name
	if not _bridge.has_method(method_name):
		_fail("Sunshine bridge method is unavailable: %s / %s" % [pascal_name, snake_name])
		return
	_bridge.call(method_name)


func _call_bridge_result(
		pascal_name: StringName,
		snake_name: StringName,
		fallback: Variant) -> Variant:
	var method_name := pascal_name if _bridge.has_method(pascal_name) else snake_name
	return _bridge.call(method_name) if _bridge.has_method(method_name) else fallback


func _fail(message: String) -> bool:
	push_error(message)
	quit(1)
	return true
