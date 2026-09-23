extends SceneTree

const TEST_SCENE := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const EPSILON := 0.01

var _scene: Node
var _bridge: Node
var _weather: Node
var _distant_cloud_shell: Node3D
var _scaled_far_viewport: Node
var _clouds: Resource
var _frame := 0
var _initial_system := Vector4.ZERO
var _initial_shape := Vector4.ZERO
var _initial_altitude := Vector4.ZERO
var _initial_background_altitude := Vector4.ZERO
var _respawned_altitude := Vector4.ZERO
var _first_movement_assertions_passed := false


func _initialize() -> void:
	_scene = TEST_SCENE.instantiate()
	_bridge = _scene.get_node("SunshineCloudBridge")
	_weather = _scene.get_node("RingVolumetricClouds")
	_distant_cloud_shell = _scene.get_node("RingCloudLayer")
	_scaled_far_viewport = _scene.get_node("ScaledFarViewport")
	# The startup graphics preset selects native/128; invalid dither is clamped.
	# Zero blur is now an intentional, supported gameplay quality control.
	_bridge.set("CloudResolutionScale", 3)
	_bridge.set("RaymarchSteps", 64)
	_bridge.set("DitherSpeed", -1.0)
	_bridge.set("BlurPower", 0.0)
	_bridge.set("CastCloudShadows", false)
	_clouds = _scene.get_node("SunshineCloudsDriver").get("clouds_resource") as Resource
	root.add_child(_scene)


func _process(_delta: float) -> bool:
	_frame += 1
	if _frame == 5:
		if int(_clouds.get("resolution_scale")) != 0 \
			or absf(float(_clouds.get("max_step_count")) - 128.0) > EPSILON \
			or absf(float(_clouds.get("dither_speed"))) > EPSILON \
			or absf(float(_clouds.get("blur_power"))) > EPSILON:
			_fail("startup profile or supported zero-blur tuning did not reach the live resource")
			return false
		if absf(float(_bridge.get("NaturalCloudBaseAltitudeMeters")) - 2000.0) > EPSILON \
			or absf(float(_bridge.get("StormFrontCloudBaseAltitudeMeters")) - 500.0) > EPSILON:
			_fail("normal and storm-front cloud-base defaults are not 2000m and 500m")
			return false
		if int(_bridge.get("GpuNearShadowMapResolution")) != 1024 \
			or absf(float(_bridge.get("GpuNearShadowUpdatesPerSecond")) - 10.0) > EPSILON:
			_fail("default near cloud-shadow map is not configured for 1024px at 10Hz")
			return false
		if absf(float(_bridge.get("HandoffStartMeters")) - 36000.0) > EPSILON \
			or absf(float(_bridge.get("HandoffEndMeters")) - 48000.0) > EPSILON \
			or absf(float(_clouds.get("max_step_distance")) - 560.0) > EPSILON:
			_fail("extended cloud range is not configured as 36-48km handoff / doubled maximum ray reach")
			return false
		_initial_background_altitude = _clouds.get("background_altitude")
		if absf(_initial_background_altitude.x - 2000.0) > EPSILON \
			or absf(_initial_background_altitude.y - 6000.0) > EPSILON:
			_fail("natural cloud deck is not pinned to the fixed 2000-6000m datum range")
			return false
		_call_bridge("SpawnAnvilClusterAhead", "spawn_anvil_cluster_ahead")
		_initial_system = _clouds.get("storm_system")
		_initial_shape = _clouds.get("storm_shape")
		_initial_altitude = _clouds.get("storm_altitude")
		if _initial_altitude.y <= _initial_altitude.x + 500.0:
			_fail("spawned anvil did not receive an independent altitude range")
			return false
		if absf(_initial_altitude.x - 500.0) > EPSILON \
			or absf(_initial_altitude.y - 18000.0) > EPSILON:
			_fail("spawned anvil altitude is not fixed to the ring datum")
			return false
		# Move beyond the major-radius edge. The previous implementation drove
		# storm strength and top altitude toward zero from this viewer distance.
		_call_bridge(
			"SetBenchmarkViewerPosition",
			"set_benchmark_viewer_position",
			[
				_initial_system.x + _initial_system.z * 1.10,
				3000.0,
				_initial_system.y,
			])
	elif _frame == 10:
		var moved_system: Vector4 = _clouds.get("storm_system")
		var moved_shape: Vector4 = _clouds.get("storm_shape")
		var moved_altitude: Vector4 = _clouds.get("storm_altitude")
		var moved_background_altitude: Vector4 = _clouds.get("background_altitude")
		var traversal_floor := float(_clouds.get("cloud_floor"))
		var traversal_ceiling := float(_clouds.get("cloud_ceiling"))
		if not _approximately_equal_vector4(moved_system, _initial_system):
			_fail("storm center or radii changed after moving away")
			return false
		if not _approximately_equal_vector4(moved_altitude, _initial_altitude):
			_fail("storm floor or ceiling changed after moving away")
			return false
		if not _approximately_equal_vector4(moved_background_altitude, _initial_background_altitude):
			_fail("natural cloud deck altitude drifted after viewer movement")
			return false
		if absf(moved_shape.z - _initial_shape.z) > EPSILON or moved_shape.z < 0.5:
			_fail("storm sculpt strength faded with viewer distance")
			return false
		if traversal_floor > moved_altitude.x + EPSILON \
			or traversal_ceiling < moved_altitude.y - EPSILON:
			_fail("ray-march traversal no longer contains the fixed storm altitude")
			return false
		_first_movement_assertions_passed = true
		_call_bridge("ClearSpawnedWeather", "clear_spawned_weather")
		_call_bridge("SpawnAnvilClusterAhead", "spawn_anvil_cluster_ahead")
	elif _frame == 15:
		_respawned_altitude = _clouds.get("storm_altitude")
		if not _first_movement_assertions_passed \
			or not _approximately_equal_vector4(_respawned_altitude, _initial_altitude):
			_fail("the same cloud type changed altitude after spawning over different terrain")
			return false
		_call_bridge("ClearSpawnedWeather", "clear_spawned_weather")
		_call_bridge("SpawnStormFrontAhead", "spawn_storm_front_ahead")
	elif _frame == 20:
		var storm_front_altitude: Vector4 = _clouds.get("storm_altitude")
		if absf(storm_front_altitude.x - 500.0) > EPSILON \
			or absf(storm_front_altitude.y - 9000.0) > EPSILON:
			_fail("storm-front altitude is not pinned to the 500-9000m datum range")
			return false
		_call_bridge("ClearSpawnedWeather", "clear_spawned_weather")
		_call_bridge("SpawnCatalogCloudAhead", "spawn_catalog_cloud_ahead", [8])
	elif _frame == 25:
		var showcase_label := str(_bridge.get("CloudShowcaseLabel"))
		var final_background_altitude: Vector4 = _clouds.get("background_altitude")
		if showcase_label.find("Cumulus") < 0 or showcase_label.find("Sunshine volumetric") < 0:
			_fail("catalog showcase did not route the Cumulus request through Sunshine")
			return false
		if bool(_weather.get("ArchetypesReady")) \
			or int(_weather.get("ActiveArchetypeCount")) != 0:
			_fail("metaball archetypes were built while Sunshine owned cloud rendering")
			return false
		if _distant_cloud_shell.visible \
			or bool(_scaled_far_viewport.get("FallbackCloudShellVisible")):
			_fail("a legacy far-cloud shell remained visible while Sunshine owned cloud rendering")
			return false
		if not _approximately_equal_vector4(final_background_altitude, _initial_background_altitude):
			_fail("natural cloud deck altitude changed during catalog spawning")
			return false
		var catalog_names := [
			"Cirrus", "Cirrostratus", "Cirrocumulus", "Altostratus", "Altocumulus",
			"Stratus", "Stratocumulus", "Nimbostratus", "Cumulus", "Cumulonimbus",
		]
		var catalog_profile_ids := [4, 5, 6, 7, 8, 9, 1, 10, 0, 3]
		for catalog_index in range(catalog_names.size()):
			_call_bridge("SpawnCatalogCloudAhead", "spawn_catalog_cloud_ahead", [catalog_index])
			showcase_label = str(_bridge.get("CloudShowcaseLabel"))
			var storm_detail: Vector4 = _clouds.get("storm_detail")
			if showcase_label.find(catalog_names[catalog_index]) < 0 \
				or showcase_label.find("Sunshine volumetric") < 0 \
				or absf(storm_detail.w - catalog_profile_ids[catalog_index]) > EPSILON:
				_fail("catalog showcase failed to route %s through its Sunshine density profile" % catalog_names[catalog_index])
				return false
		_call_bridge("ClearCatalogCloudShowcase", "clear_catalog_cloud_showcase")
		print(
			"Sunshine fixed-altitude/catalog test passed: natural=2000-6000m, storm=%.0f-%.0fm, all 10 catalog types, handoff=36-48km, maxStep=560m"
			% [_respawned_altitude.x, _respawned_altitude.y])
		quit(0)
	return false


func _approximately_equal_vector4(first: Vector4, second: Vector4) -> bool:
	return (
		absf(first.x - second.x) <= EPSILON
		and absf(first.y - second.y) <= EPSILON
		and absf(first.z - second.z) <= EPSILON
		and absf(first.w - second.w) <= EPSILON
	)


func _call_bridge(pascal_name: StringName, snake_name: StringName, arguments: Array = []) -> void:
	var method_name := pascal_name if _bridge.has_method(pascal_name) else snake_name
	if not _bridge.has_method(method_name):
		_fail("Sunshine bridge test method is unavailable: %s / %s" % [pascal_name, snake_name])
		return
	_bridge.callv(method_name, arguments)


func _fail(message: String) -> void:
	push_error("Sunshine storm persistence test failed: %s" % message)
	quit(1)
