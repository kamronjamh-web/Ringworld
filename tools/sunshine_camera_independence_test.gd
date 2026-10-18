extends SceneTree

# P0 of docs/cloud-camera-independence-plan.md: sweeps the render-weather-system collection centre at the Certified
# capture pose and checks camera independence. Always builds the real Certified world through the launcher:
#   godot --path . --headless --script res://tools/sunshine_camera_independence_test.gd
const TEST_SCENE := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const LAUNCHER_SCENE := preload("res://scenes/WorldSizeLauncher.tscn")
const DRIVER := preload("res://tools/SunshineCameraIndependenceTest.cs")
const FALLBACK_HANDOFF_END_M := 171500.0
const FALLBACK_LIGHT_REACH_M := 10000.0
var _scene: Node
var _frames := 0

func _initialize() -> void:
	call_deferred("_setup")

func _setup() -> void:
	_scene = TEST_SCENE.instantiate()
	var launcher := LAUNCHER_SCENE.instantiate()
	root.add_child(launcher)
	if not launcher.is_node_ready(): await launcher.ready
	launcher.call("_on_certified_preset_pressed")
	launcher.call("_configure_game", _scene, 5000000.0)
	launcher.free()
	root.add_child(_scene)

func _process(_delta: float) -> bool:
	_frames += 1
	if _frames < 3: return false
	var layer := _scene.get_node("RingVolumetricClouds")
	var bridge := _scene.get_node("SunshineCloudBridge")
	var clouds := _scene.get_node("SunshineCloudsDriver").get("clouds_resource") as Resource
	var handoff_end := float(bridge.get("EffectiveHandoffEndMeters"))
	if handoff_end < 1000.0: handoff_end = FALLBACK_HANDOFF_END_M
	var light_reach := float(clouds.get("lighting_travel_distance"))
	if light_reach < 1.0: light_reach = FALLBACK_LIGHT_REACH_M
	layer.set("CloudSimulationTimeSeconds", 4506.117868)
	layer.set("CloudSimulationPaused", true)
	var driver = DRIVER.new()
	var code: int = driver.call("Run", layer, handoff_end, light_reach)
	driver = null
	if code != 0:
		push_error("SUNSHINE_CAMERA_INDEPENDENCE_WRAPPER=FAIL exit=%d" % code)
	else:
		print("SUNSHINE_CAMERA_INDEPENDENCE_WRAPPER=PASS handoff_end=%.0f light_reach=%.0f" % [handoff_end, light_reach])
	quit(code)
	return false
