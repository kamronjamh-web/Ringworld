extends SceneTree

const DRIVER := preload("res://tools/TreeGpuBenchmark.cs")
var driver: Node
var frames := 0
var started := 0

func _initialize() -> void:
	if RenderingServer.get_rendering_device() == null:
		push_error("TREE_GPU_BENCHMARK=FAIL Forward+ RenderingDevice required")
		quit(1)
		return
	DisplayServer.window_set_size(Vector2i(1280, 720))
	DisplayServer.window_set_vsync_mode(DisplayServer.VSYNC_DISABLED)
	Engine.max_fps = 0
	started = Time.get_ticks_msec()
	driver = DRIVER.new()
	root.add_child(driver)
	driver.call_deferred("Start")

func _process(_delta: float) -> bool:
	frames += 1
	var result: String = driver.get("Result")
	if not result.begins_with("RUNNING"):
		if result.begins_with("PASS"):
			print("TREE_GPU_BENCHMARK=" + result)
			quit(0)
		else:
			push_error("TREE_GPU_BENCHMARK=" + result)
			quit(1)
		return true
	if frames > 12000 or Time.get_ticks_msec() - started > 600000:
		push_error("TREE_GPU_BENCHMARK=FAIL timeout")
		quit(1)
	return false
