extends SceneTree

## Headless state contract for shared source events -> far shell/clipmaps.
const SCENE := preload("res://scenes/VoxelRingIntegrationTest.tscn")
var _world: Node3D

func _initialize() -> void:
	call_deferred("_run")

func _run() -> void:
	_world = SCENE.instantiate() as Node3D
	var far := _world.get_node("ScaledFarViewport")
	far.set("Enabled", true)
	# Keep this fixture on the normal 20 km ring rather than inheriting a
	# Certified-scale far viewport override.
	far.set("RingWidthMeters", 20000)
	root.add_child(_world)
	await process_frame
	await process_frame
	var weather := _world.get_node("RingVolumetricClouds")
	weather.set_process(false)
	weather.set("CloudSimulationTimeSeconds", 10.0)
	var grid := _world.get_node("ChunkGridManager")
	var camera := _world.get_node("Explorer/Head/Camera3D") as Camera3D
	var circumference := float(grid.call("GetCanonicalRingCircumferenceMeters64"))
	var target := grid.call("CanonicalRingPositionToWorld", Vector3(circumference * 0.5, 500.0, 10000.0)) as Vector3
	camera.look_at(target, Vector3.UP)
	var source_a := weather.call("TriggerSharedLightningPreviewAt", circumference * 0.5, 10000.0, -9101, 1.0, 60000.0) as Dictionary
	var source_b := weather.call("TriggerSharedLightningPreviewAt", circumference * 0.5 + 20000.0, 10000.0, -9102, 1.0, 60000.0) as Dictionary
	var expected_ids: Array = [int(source_a.get("event_id", 0)), int(source_b.get("event_id", 0))]
	if expected_ids[0] == 0 or expected_ids[1] == 0: return _fail("shared preview source did not return event IDs: %s %s" % [source_a, source_b])
	await process_frame
	var ids = (far.get("SelectedFarLightningEventIds") as Array).duplicate()
	if ids.size() != 2 or not ids.has(expected_ids[0]) or not ids.has(expected_ids[1]): return _fail("selected IDs %s did not exactly include source IDs %s" % [ids, expected_ids])
	var far_world := far.get_node("FarRingViewport")
	var shell := far_world.get_node("ScaledCloudShell") as MeshInstance3D
	var shell_mat := shell.material_override as ShaderMaterial
	var shell_a := shell_mat.get_shader_parameter("far_lightning_event_a") as Vector4
	var shell_b := shell_mat.get_shader_parameter("far_lightning_event_b") as Vector4
	if shell_a.w <= 0.0 or shell_b.w <= 0.0: return _fail("both shared preview pulses must reach shell: A=%s B=%s" % [shell_a, shell_b])
	for index in range(3):
		var layer := far_world.get_node("FarCloudClipmapLayer%d" % index) as MeshInstance3D
		var material := layer.material_override as ShaderMaterial
		var clip_a := material.get_shader_parameter("far_lightning_event_a") as Vector4
		var clip_b := material.get_shader_parameter("far_lightning_event_b") as Vector4
		if clip_a != shell_a or clip_b != shell_b: return _fail("clipmap %d event mismatch A=%s B=%s" % [index, clip_a, clip_b])
	far.set("FarStormLightningEnabled", false)
	far.call("_Process", 0.0)
	if (far.get("SelectedFarLightningEventIds") as Array).size() != 0: return _fail("disabled far lightning retained selected IDs")
	if (shell_mat.get_shader_parameter("far_lightning_event_a") as Vector4).w != 0.0 or (shell_mat.get_shader_parameter("far_lightning_event_b") as Vector4).w != 0.0: return _fail("disabled far lightning retained shell uniforms")
	far.set("FarStormLightningEnabled", true)
	weather.set("CloudSimulationTimeSeconds", 11.0)
	far.call("_Process", 0.0)
	await process_frame
	if (far.get("SelectedFarLightningEventIds") as Array).size() != 0: return _fail("expired shared event IDs remained published")
	if (shell_mat.get_shader_parameter("far_lightning_event_a") as Vector4).w != 0.0 or (shell_mat.get_shader_parameter("far_lightning_event_b") as Vector4).w != 0.0: return _fail("expired shared event remained published")
	print("SHARED_LIGHTNING_FAR_SMOKE_TEST=PASS ids=%s" % str(ids))
	quit(0)

func _fail(message: String) -> void:
	push_error("SHARED_LIGHTNING_FAR_SMOKE_TEST=FAIL %s" % message)
	quit(1)
