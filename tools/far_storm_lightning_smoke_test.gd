extends SceneTree

const TEST_SCENE := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const MAX_FRAMES := 240
const EPSILON := 0.00001

var _scene: Node
var _weather: Node
var _far_viewport: Node
var _shell_material: ShaderMaterial
var _clipmap_materials: Array[ShaderMaterial] = []
var _frame := 0
var _expected_event_id := 0


func _initialize() -> void:
	_scene = TEST_SCENE.instantiate()
	_weather = _scene.get_node("RingVolumetricClouds")
	_far_viewport = _scene.get_node("ScaledFarViewport")
	# The launcher normally enables this only for the Certified ring. Keep the
	# focused smoke cheap while still building both far-cloud representations.
	_far_viewport.set("Enabled", true)
	_far_viewport.set("RingWidthMeters", 20000)
	_far_viewport.set("AroundSegments", 128)
	_far_viewport.set("AcrossSegments", 4)
	_far_viewport.set("FarWeatherAtlasWidth", 512)
	_far_viewport.set("FarWeatherAtlasHeight", 32)
	_far_viewport.set("CloudTransmittanceMaskEnabled", false)
	root.add_child(_scene)


func _process(_delta: float) -> bool:
	_frame += 1
	if _frame == 2 and not _resolve_materials():
		return false

	if _shell_material == null:
		return false
	if _expected_event_id == 0:
		# Scheduling has its own functional natural-event test. This test owns
		# the source -> shell/clipmap publication and bounded UV contract.
		_weather.set_process(false)
		_weather.set("CloudSimulationTimeSeconds", 10.0)
		var grid := _scene.get_node("ChunkGridManager")
		var circumference := float(grid.call("GetCanonicalRingCircumferenceMeters64"))
		var across := float(grid.get("RingAcrossWidthMeters")) * 0.5
		var camera := _scene.get_node("Explorer/Head/Camera3D") as Camera3D
		var target := grid.call("CanonicalRingPositionToWorld", Vector3(circumference * 0.5, 2200.0, across)) as Vector3
		camera.look_at(target, Vector3.UP)
		var event := _weather.call("TriggerSharedLightningPreviewAt", circumference * 0.5, across, -9301, 1.0, 45000.0) as Dictionary
		if event.is_empty() or int(event["event_id"]) <= 0:
			_fail("shared source did not publish a positive preview event")
			return false
		_expected_event_id = int(event["event_id"])
		_weather.set("CloudSimulationTimeSeconds", 10.035)
		return false

	var event_a := _shell_material.get_shader_parameter("far_lightning_event_a") as Vector4
	var event_b := _shell_material.get_shader_parameter("far_lightning_event_b") as Vector4
	if event_a.w > EPSILON or event_b.w > EPSILON:
		if not (_far_viewport.get("SelectedFarLightningEventIds") as Array).has(_expected_event_id):
			_fail("rendered event did not retain the shared source identity")
			return false
		if not _materials_share_events(event_a, event_b):
			_fail("whole-ring shell and clipmap layers received different events")
			return false
		if event_a.w > EPSILON and (event_a.z <= EPSILON or event_a.x < 0.0 or event_a.x > 1.0 or event_a.y < 0.0 or event_a.y > 1.0):
			_fail("event A was not encoded as a bounded canonical storm flash")
			return false
		if event_b.w > EPSILON and (event_b.z <= EPSILON or event_b.x < 0.0 or event_b.x > 1.0 or event_b.y < 0.0 or event_b.y > 1.0):
			_fail("event B was not encoded as a bounded canonical storm flash")
			return false
		print(
			"Far storm lightning smoke passed: frame=%d, clock=%.2fs, eventA=%s, eventB=%s, synchronizedMaterials=%d"
			% [_frame, float(_weather.get("CloudSimulationTimeSeconds")), event_a, event_b, _clipmap_materials.size() + 1])
		quit(0)
		return false

	if _frame >= MAX_FRAMES:
		_fail("shared source event never reached the far renderer")
	return false


func _resolve_materials() -> bool:
	var far_world := _far_viewport.get_node_or_null("FarRingViewport")
	if far_world == null:
		_fail("scaled far viewport did not build")
		return false
	var shell := far_world.get_node_or_null("ScaledCloudShell") as MeshInstance3D
	if shell == null or not (shell.material_override is ShaderMaterial):
		_fail("whole-ring cloud shell material is unavailable")
		return false
	_shell_material = shell.material_override as ShaderMaterial
	for layer_index in range(3):
		var layer := far_world.get_node_or_null("FarCloudClipmapLayer%d" % layer_index) as MeshInstance3D
		if layer == null or not (layer.material_override is ShaderMaterial):
			_fail("far cloud clipmap layer %d is unavailable" % layer_index)
			return false
		_clipmap_materials.append(layer.material_override as ShaderMaterial)
	return true


func _materials_share_events(event_a: Vector4, event_b: Vector4) -> bool:
	for material in _clipmap_materials:
		var candidate_a := material.get_shader_parameter("far_lightning_event_a") as Vector4
		var candidate_b := material.get_shader_parameter("far_lightning_event_b") as Vector4
		if not candidate_a.is_equal_approx(event_a) or not candidate_b.is_equal_approx(event_b):
			return false
	return true


func _fail(message: String) -> void:
	push_error("Far storm lightning smoke failed: %s" % message)
	quit(1)
