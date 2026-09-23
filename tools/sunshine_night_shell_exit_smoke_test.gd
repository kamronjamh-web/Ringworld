extends SceneTree

## Run with:
## godot --path . --script res://tools/sunshine_night_shell_exit_smoke_test.gd
const TEST_SCENE := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const GeneratedOutputPaths := preload("res://tools/generated_output_paths.gd")
const CAPTURE_FILE := "sunshine_night_shell_exit.png"
const SETTLE_FRAMES := 48

var _scene: Node
var _frame := 0
var _night_setup_complete := false
var _capture_path := ""


func _initialize() -> void:
	_capture_path = GeneratedOutputPaths.smoke_capture_path(CAPTURE_FILE)
	_scene = TEST_SCENE.instantiate()
	root.add_child(_scene)
	if not _has_first_exit_guard() or not _verify_shell_traversal_logic():
		_fail("Sunshine first-exit shader guard is missing")
		return


func _process(_delta: float) -> bool:
	_frame += 1
	if _frame == 2:
		var cycle := _scene.get_node_or_null("RingDayNightCycle")
		if cycle == null or not cycle.has_method("set_cycle_fraction"):
			return _fail("Ring day/night controller is unavailable")
		cycle.set_process(false)
		cycle.call("set_cycle_fraction", 0.5)
		var camera := _scene.get_node_or_null("Explorer/Head/Camera3D") as Camera3D
		if camera == null:
			return _fail("Integration camera is unavailable")
		# At the scene's ring-surface spawn, local +X is the around-ring tangent.
		# Point the camera along it so the capture exercises the curved horizon.
		camera.rotation.y = -PI * 0.5
		if not _sunshine_compositor_is_attached():
			return _fail("Sunshine compositor is not attached before the night horizon capture")
		_night_setup_complete = true
	if not _night_setup_complete:
		return false
	if _frame < SETTLE_FRAMES:
		return false
	if not _sunshine_compositor_is_attached():
		return _fail("Sunshine compositor detached during the night horizon capture")
	var image := root.get_texture().get_image()
	if image == null or image.is_empty():
		return _fail("Night horizon capture produced no image")
	var error := image.save_png(_capture_path)
	if error != OK:
		return _fail("Night horizon capture failed: %s (%s)" % [_capture_path, error])
	print("Sunshine night shell-exit smoke passed: compositor attached, night=0.5, capture=%s" % _capture_path)
	quit(0)
	return true


func _sunshine_compositor_is_attached() -> bool:
	var environment_node := _scene.get_node_or_null("WorldEnvironment") as WorldEnvironment
	var driver := _scene.get_node_or_null("SunshineCloudsDriver")
	if environment_node == null or environment_node.environment == null or driver == null:
		return false
	var compositor: Compositor = environment_node.compositor
	var clouds := driver.get("clouds_resource") as CompositorEffect
	return compositor != null and clouds != null and clouds in compositor.compositor_effects


func _has_first_exit_guard() -> bool:
	var source := FileAccess.get_file_as_string("res://addons/SunshineClouds2/SunshineCloudsCompute.glsl")
	return source.contains("bool enteredCloudAltitudeBand = false") \
		and source.contains("cloudBandTraversalDirection") \
		and source.contains("cameraStartsInsideCloudAltitudeBand") \
		and source.contains("analyticAltitudeMotion") \
		and source.contains("enteredCloudAltitudeBand || leavingAbove || leavingBelow")


func _verify_shell_traversal_logic() -> bool:
	# Analytic equivalents of the shader's altitude-band state machine. These
	# cover a tangent apex contained within the band and an ordinary crossing
	# from above, independent of procedural density/noise samples.
	return not _terminates_first_band_traversal([1000.0, 1000.0, 1000.0], [0.0, 0.0], true, 500.0, 3000.0) \
		and not _terminates_first_band_traversal([1000.0, 1000.0, 1000.0], [0.0001, 0.0001], true, 500.0, 3000.0) \
		and not _terminates_first_band_traversal([1000.0, 1001.0, 1000.0], [0.0001, 0.0, -0.0001], true, 500.0, 3000.0) \
		and not _terminates_first_band_traversal([600.0, 600.0, 600.0], [0.0001, 0.0, 0.0001], false, 500.0, 3000.0) \
		and _terminates_first_band_traversal([600.0, 600.0, 600.0], [0.0001, 0.0, -0.0001], false, 500.0, 3000.0) \
		and _terminates_first_band_traversal([600.0, 1200.0, 900.0], [1.0, -1.0], false, 500.0, 3000.0) \
		and _terminates_first_band_traversal([600.0, 1200.0, 400.0], [1.0, 1.0], true, 500.0, 3000.0)


func _terminates_first_band_traversal(altitudes: Array, motions: Array, starts_inside: bool, floor: float, ceiling: float) -> bool:
	var entered := false
	var direction := 0.0
	for index in range(altitudes.size()):
		var altitude: float = float(altitudes[index])
		var inside: bool = altitude >= floor and altitude <= ceiling
		if inside:
			if not entered:
				entered = true
			if not starts_inside and index < motions.size():
				var motion: float = float(motions[index])
				if absf(direction) <= 0.000001 and absf(motion) > 0.000001:
					direction = signf(motion)
				if absf(direction) > 0.000001 and motion * direction < -0.000001:
					return true
		elif entered:
			return true
	return false


func _fail(message: String) -> bool:
	push_error(message)
	quit(1)
	return true
