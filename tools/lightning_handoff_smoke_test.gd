extends SceneTree

## Headless state contract for the complementary local/regional/shell lightning handoff.
const SCENE := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const LAUNCHER_SCENE := preload("res://scenes/WorldSizeLauncher.tscn")
var _world: Node3D
var _weather: Node
var _far: Node
var _local: Node
var _bridge: Node
var _grid: Node
var _camera: Camera3D
var _event_around := 0.0
var _across := 10000.0
var _circumference := 1.0
var _width := 20000.0
var _event_id := 0

func _initialize() -> void:
	call_deferred("_run")

func _run() -> void:
	_world = SCENE.instantiate() as Node3D
	_far = _world.get_node("ScaledFarViewport")
	_far.set("Enabled", true)
	if "--lightning-handoff-certified" in OS.get_cmdline_user_args():
		var launcher := LAUNCHER_SCENE.instantiate()
		root.add_child(launcher)
		if not launcher.is_node_ready(): await launcher.ready
		launcher.call("_on_certified_preset_pressed")
		launcher.call("_configure_game", _world, 5000000.0)
		launcher.free()
	else:
		_far.set("RingWidthMeters", 20000)
	root.add_child(_world)
	for _frame in range(4): await process_frame
	_weather = _world.get_node("RingVolumetricClouds")
	_local = _world.get_node("LocalWeatherEffects")
	_bridge = _world.get_node("SunshineCloudBridge")
	_grid = _world.get_node("ChunkGridManager")
	_camera = _world.get_node("Explorer/Head/Camera3D") as Camera3D
	_weather.set_process(false)
	_local.set("EnableLightning", true)
	_weather.set("CloudSimulationTimeSeconds", 100.0)
	_circumference = float(_grid.call("GetCanonicalRingCircumferenceMeters64"))
	_width = float(_grid.get("RingAcrossWidthMeters"))
	_across = _width * 0.5
	_event_around = _circumference * 0.5
	var preview := _weather.call("TriggerSharedLightningPreviewAt", _event_around, _across, -9301, 1.0, 60000.0) as Dictionary
	_event_id = int(preview.get("event_id", 0))
	if _event_id <= 0: return _fail("preview did not create a shared source event")
	_weather.set("CloudSimulationTimeSeconds", 100.035)
	var outer := float(_far.get("EffectiveFarCloudClipmapRadiusMeters"))
	if outer <= 72000.0: return _fail("normal far outer radius must exceed local fade, got %.2f" % outer)
	var at_48 := await _sample(48000.0)
	var at_60 := await _sample(60000.0)
	var at_72 := await _sample(72000.0)
	var at_inner_shell := await _sample(outer * 0.72)
	var at_mid_shell := await _sample(outer * 0.86)
	var at_outer := await _sample(outer)
	var at_across_boundary := await _sample(60000.0, _width * 0.5)
	var at_diagonal_boundary := await _sample(outer * 0.86 / sqrt(2.0), _width * 0.5 * 0.86 / sqrt(2.0))
	var at_wrapped_seam := await _sample(_circumference - 1000.0)
	if at_48.local_weight < 0.999 or at_48.regional_sum > 0.001 or at_48.shell_weight > 0.001:
		return _fail("48km ownership mismatch %s" % at_48)
	if at_60.local_weight <= 0.01 or at_60.regional_sum <= 0.01 or not at_60.selected:
		return _fail("60km must hand off one source event to local+far %s" % at_60)
	if at_72.local_weight > 0.001 or at_72.regional_sum < 0.99 or not at_72.selected:
		return _fail("72km ownership mismatch %s" % at_72)
	if at_inner_shell.shell_weight > 0.001 or at_inner_shell.regional_sum < 0.99:
		return _fail("regional inner shell boundary mismatch %s" % at_inner_shell)
	if at_mid_shell.shell_weight <= 0.01 or at_mid_shell.regional_sum <= 0.01:
		return _fail("regional/shell overlap mismatch %s" % at_mid_shell)
	if at_outer.shell_weight < 0.99 or at_outer.regional_sum > 0.001:
		return _fail("outer shell ownership mismatch %s" % at_outer)
	if at_across_boundary.shell_weight <= 0.01 or at_across_boundary.regional_sum > 0.001:
		return _fail("across ellipse boundary failed to transfer to shell %s" % at_across_boundary)
	if at_diagonal_boundary.shell_weight <= 0.01 or at_diagonal_boundary.regional_sum <= 0.01:
		return _fail("diagonal ellipse transition mismatch %s" % at_diagonal_boundary)
	if at_wrapped_seam.local_weight < 0.99 or at_wrapped_seam.regional_sum > 0.001 or at_wrapped_seam.shell_weight > 0.001:
		return _fail("wrapped seam did not use shortest canonical distance %s" % at_wrapped_seam)
	for sample in [at_48, at_60, at_72, at_inner_shell, at_mid_shell, at_outer, at_across_boundary, at_diagonal_boundary, at_wrapped_seam]:
		if absf(sample.local_weight + sample.regional_sum + sample.shell_weight - 1.0) > 0.02:
			return _fail("handoff weight sum was not complementary %s" % sample)
	# Re-enter local range while the original shared event is still alive: no new
	# bolt may be generated and the original source ID must remain authoritative.
	var flash_count := int(_local.call("get_local_shared_lightning_diagnostics").get("flash_count", 0))
	var reentered := await _sample(60000.0)
	var local_diag := _local.call("get_local_shared_lightning_diagnostics") as Dictionary
	if int(local_diag.get("last_event_id", 0)) != _event_id or int(local_diag.get("flash_count", 0)) != flash_count:
		return _fail("far-to-local reentry regenerated the source event: %s" % local_diag)
	# A disabled regional clipmap transfers all post-local ownership to the
	# shell; it must not leave a 48 km-to-outer-radius lightning gap.
	_far.set("FarCloudClipmapEnabled", false)
	_far.call("_Process", 0.0)
	var disabled_regional := await _read_far_weights()
	if disabled_regional.shell_weight <= 0.49 or disabled_regional.regional_sum > 0.001:
		return _fail("clipmap-disabled shell fallback mismatch %s" % disabled_regional)
	_far.set("FarCloudClipmapEnabled", true)
	_far.call("_Process", 0.0)
	# A deliberate clock rewind may replay a bolt, but must preserve its source identity.
	_weather.set("CloudSimulationTimeSeconds", 100.020)
	_far.call("_Process", 0.0)
	_local.call("_Process", 0.0)
	var rewound_ids := (_far.get("SelectedFarLightningEventIds") as Array).duplicate()
	if rewound_ids.size() != 1 or int(rewound_ids.front()) != _event_id:
		return _fail("rewind lost the authoritative far source ID")
	_far.set("FarStormLightningEnabled", false)
	_far.call("_Process", 0.0)
	if (_far.get("SelectedFarLightningEventIds") as Array).size() != 0:
		return _fail("disable retained far selected IDs")
	_local.set("EnableLightning", false)
	_local.call("_Process", 0.0)
	if int((_local.call("get_local_shared_lightning_diagnostics") as Dictionary).get("event_id", -1)) != 0:
		return _fail("disable retained local active lightning")
	_far.set("FarStormLightningEnabled", true)
	_local.set("EnableLightning", true)
	_weather.set("CloudSimulationTimeSeconds", 101.0)
	_far.call("_Process", 0.0)
	_local.call("_Process", 0.0)
	if (_far.get("SelectedFarLightningEventIds") as Array).size() != 0:
		return _fail("expiry retained far event IDs")
	print("LIGHTNING_HANDOFF_SMOKE_TEST=PASS event=%d outer=%.1f 48=%s 60=%s 72=%s inner=%s mid=%s outer_sample=%s reentry=%s" % [_event_id, outer, at_48, at_60, at_72, at_inner_shell, at_mid_shell, at_outer, reentered])
	quit(0)

func _sample(distance_meters: float, across_offset_meters: float = 0.0) -> Dictionary:
	_bridge.call("SetBenchmarkViewerPosition", _event_around - distance_meters, 120.0, clampf(_across + across_offset_meters, 0.0, _width))
	await process_frame
	var target := _grid.call("CanonicalRingPositionToWorld", Vector3(_event_around, 600.0, _across)) as Vector3
	_camera.look_at(target, Vector3.UP)
	_far.call("_Process", 0.0)
	_local.call("_Process", 0.0)
	await process_frame
	var far_values := _read_far_weights()
	var local_diag := _local.call("get_local_shared_lightning_diagnostics") as Dictionary
	return {"distance": distance_meters, "local_weight": float(local_diag.get("visual_weight", 0.0)), "regional_sum": far_values.regional_sum, "shell_weight": far_values.shell_weight, "selected": far_values.ids.has(_event_id), "ids": far_values.ids}

func _read_far_weights() -> Dictionary:
	var far_world := _far.get_node("FarRingViewport")
	var shell_mat := (far_world.get_node("ScaledCloudShell") as MeshInstance3D).material_override as ShaderMaterial
	var shell_weights := shell_mat.get_shader_parameter("far_lightning_event_weights") as Vector2
	var regional_sum := 0.0
	for index in range(3):
		var material := (far_world.get_node("FarCloudClipmapLayer%d" % index) as MeshInstance3D).material_override as ShaderMaterial
		regional_sum += (material.get_shader_parameter("far_lightning_event_weights") as Vector2).x
	var ids := (_far.get("SelectedFarLightningEventIds") as Array).duplicate()
	return {"regional_sum": regional_sum, "shell_weight": shell_weights.x, "ids": ids}

func _fail(message: String) -> void:
	push_error("LIGHTNING_HANDOFF_SMOKE_TEST=FAIL " + message)
	quit(1)
