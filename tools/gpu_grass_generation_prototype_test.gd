extends SceneTree
const Driver = preload("res://tools/GpuGrassGenerationPrototypeTest.cs")
var driver
var frames := 0
func _initialize() -> void:
	if RenderingServer.get_rendering_device() == null:
		push_error("GPU_GRASS_GENERATION_PROTOTYPE=FAIL RenderingDevice required")
		quit(1)
		return
	driver = Driver.new()
	root.add_child(driver)
	driver.call_deferred("Start")
func _process(_delta: float) -> bool:
	frames += 1
	var result: String = driver.get("Result")
	if result != "RUNNING":
		if result.begins_with("PASS"):
			print("GPU_GRASS_GENERATION_PROTOTYPE=" + result)
			quit(0)
		else:
			push_error("GPU_GRASS_GENERATION_PROTOTYPE=" + result)
			quit(1)
		return true
	if frames > 1200:
		push_error("GPU_GRASS_GENERATION_PROTOTYPE=FAIL timeout")
		quit(1)
	return false
