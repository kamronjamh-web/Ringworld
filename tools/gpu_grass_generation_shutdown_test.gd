extends SceneTree

const Driver := preload("res://tools/GpuGrassGenerationShutdownTest.cs")
var driver: Node
var elapsed := 0.0

func _initialize() -> void:
	if RenderingServer.get_rendering_device() == null:
		push_error("GPU_GRASS_GENERATION_SHUTDOWN=FAIL RenderingDevice required")
		quit(1)
		return
	driver = Driver.new()
	root.add_child(driver)
	driver.call_deferred("Start")

func _process(delta: float) -> bool:
	elapsed += delta
	var result: String = driver.get("Result")
	if result != "RUNNING":
		if result.begins_with("PASS"):
			print("GPU_GRASS_GENERATION_SHUTDOWN=" + result)
			quit(0)
		else:
			push_error("GPU_GRASS_GENERATION_SHUTDOWN=" + result)
			quit(1)
		return true
	# Session startup can take several seconds on a cold shader cache; the post-ready teardown timeout is in C#.
	if elapsed > 20.0:
		push_error("GPU_GRASS_GENERATION_SHUTDOWN=FAIL outer timeout")
		quit(1)
	return false
