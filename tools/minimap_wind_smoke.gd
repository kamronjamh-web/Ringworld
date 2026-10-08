extends SceneTree

# Functional map/input coverage. Run with the normal integration scene resources.
const WIND_CONTRACT := preload("res://scripts/Tools/MinimapWindContractTest.cs")
var _world: Node
var _outputs := ""
var _frames := 0
var _finished := false
var _phase := "projection"
const WATCHDOG_FRAMES := 1800
const TERRAIN_WAIT_FRAMES := 600

func _initialize() -> void:
	call_deferred("_run")

func _process(_delta: float) -> bool:
	_frames += 1
	if not _finished and _frames > WATCHDOG_FRAMES:
		_fail("watchdog exceeded while " + _phase)
	return false

func _run() -> void:
	var contract = WIND_CONTRACT.new()
	var result: Dictionary = contract.call("ExecuteFixture", "projection", null, "")
	if not bool(result.get("pass", false)):
		return _fail("wind projection contract failed: " + str(result.get("detail", "")))
	var world = load("res://scenes/VoxelRingIntegrationTest.tscn").instantiate()
	_world = world
	_outputs = "res://.godot/minimap-wind-smoke/%d" % Time.get_ticks_msec()
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(_outputs))
	ProjectSettings.set_setting("ringworld/runtime/active_world_directory", "")
	_phase = "installing isolated preferences"
	result = contract.call("ExecuteFixture", "isolation", world, _outputs)
	if not bool(result.get("pass", false)):
		return _fail("preference isolation failed: " + str(result.get("detail", "")))
	root.add_child(world)
	await process_frame
	var map = world.get_node("TerrainMinimap")
	var panel = map.get_node("TerrainMinimapPanel")
	var viewport = map.get_node("TerrainMapViewport")
	var terrain = viewport.get_node("PlayerRelativeContent/TerrainRoot/ElevationTerrain")
	world.get_node("Explorer").set_physics_process(false)
	_phase = "waiting for real terrain map"
	for frame in range(TERRAIN_WAIT_FRAMES):
		if terrain.mesh != null: break
		await process_frame
	if terrain.mesh == null:
		return _fail("terrain map did not build within bounded startup wait")
	map.set_process(false)
	var viewport_id = viewport.get_instance_id()
	var texture_id = viewport.get_texture().get_instance_id()
	var mesh = terrain.mesh
	var span = map.get("MapSpanMeters")
	if not panel.visible or map.get("DetailsExpanded"):
		return _fail("map must start visible with collapsed details")
	_phase = "checking M input"
	for expected in [true, false, true, false]:
		var key = InputEventKey.new()
		key.keycode = KEY_M
		key.pressed = true
		Input.parse_input_event(key)
		await process_frame
		if map.get("DetailsExpanded") != expected or not panel.visible:
			return _fail("M must toggle details and keep map visible")
		if viewport.get_instance_id() != viewport_id or viewport.get_texture().get_instance_id() != texture_id:
			return _fail("M recreated map viewport or texture")
		if terrain.mesh != mesh or map.get("MapSpanMeters") != span:
			return _fail("M rebuilt terrain or changed zoom")
		var release = InputEventKey.new()
		release.keycode = KEY_M
		Input.parse_input_event(release)
		await process_frame
	_phase = "checking radar layering under live tuning"
	var drawer = world.get_node("LiveTuningDrawer")
	drawer.call("SetBenchmarkDrawerOpen", true)
	await process_frame
	var tuning_panel = drawer.get_node("LiveTuningDrawer")
	if not tuning_panel.visible or map.layer >= drawer.layer:
		return _fail("open live tuning drawer must draw above radar controls")
	for expanded in [true, false]:
		map.call("SetDetailsExpanded", expanded)
		await process_frame
		if not panel.visible or map.layer >= drawer.layer:
			return _fail("radar details toggle must stay visible beneath the open tuning drawer")
		if viewport.get_instance_id() != viewport_id or terrain.mesh != mesh:
			return _fail("opening tuning must preserve radar viewport and terrain")
	drawer.call("SetBenchmarkDrawerOpen", false)
	await process_frame
	if not panel.visible or map.get("DetailsExpanded"):
		return _fail("closing tuning must retain the visible collapsed radar")
	_phase = "checking real cached weather source"
	result = contract.call("ExecuteFixture", "live", world, "")
	if not bool(result.get("pass", false)):
		return _fail("live cached weather source contract failed: " + str(result.get("detail", "")))
	if RenderingServer.get_rendering_device() != null:
		_phase = "capturing built terrain map"
		map.call("SetDetailsExpanded", false)
		if not await _capture("collapsed"): return _fail("collapsed capture failed")
		for heading_up in [false, true]:
			for yaw in [0.0, 90.0]:
				if not contract.call("SetCaptureFacing", world, yaw, heading_up):
					return _fail("facing capture lost live compass needle")
				var label := "expanded_%s_facing_%d" % ["heading_up" if heading_up else "north_up", yaw]
				if not await _capture(label): return _fail("facing capture failed: " + label)
	print("Minimap wind smoke: PASS (default map, M details, stable viewport/terrain, fixed live wind follows character yaw/wrap independent of map orientation and pitch/roll, calm/disabled/preference)")
	_finished = true
	await _cleanup()
	quit(0)

func _capture(label: String) -> bool:
	await RenderingServer.frame_post_draw
	var image := root.get_texture().get_image()
	var path := _outputs.path_join("minimap_" + label + ".png")
	if image.is_empty() or image.save_png(path) != OK:
		return false
	print("Minimap capture: " + ProjectSettings.globalize_path(path))
	return true

func _cleanup() -> void:
	# Let every owner flush only to the fixture directory before removing prefs.
	if is_instance_valid(_world):
		_world.queue_free()
		await process_frame
	for name in ["aa", "motion", "water", "wind", "grass", "vegetation", "shadows", "surface", "atmosphere", "resolution", "lod", "coverage", "clouds"]:
		DirAccess.remove_absolute(ProjectSettings.globalize_path(_outputs.path_join(name + ".tres")))

func _fail(message: String) -> void:
	if _finished: return
	_finished = true
	push_error("Minimap wind smoke: " + message)
	await _cleanup()
	quit(1)
