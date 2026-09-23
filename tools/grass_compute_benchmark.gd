extends SceneTree

const Driver := preload("res://tools/GrassComputeBenchmark.cs")
var driver: Node
var frames := 0

func _initialize() -> void:
	if RenderingServer.get_rendering_device() == null:
		push_error("GRASS_COMPUTE_BENCHMARK=FAIL Forward+ RenderingDevice required")
		quit(1)
		return
	DisplayServer.window_set_size(Vector2i(1280, 720))
	DisplayServer.window_set_vsync_mode(DisplayServer.VSYNC_DISABLED)
	Engine.max_fps = 0
	driver = Driver.new()
	root.add_child(driver)
	driver.call_deferred("Start")

func _process(_delta: float) -> bool:
	frames += 1
	var result: String = driver.get("Result")
	if result != "RUNNING":
		if result.begins_with("PASS"):
			print("GRASS_COMPUTE_BENCHMARK=" + result)
			quit(0)
		else:
			push_error("GRASS_COMPUTE_BENCHMARK=" + result)
			quit(1)
		return true
	# Full run needs 6 cases x 3 repetitions x 240+ frames; timeout is deliberately generous.
	if frames > 12000:
		push_error("GRASS_COMPUTE_BENCHMARK=FAIL timeout")
		quit(1)
	return false
