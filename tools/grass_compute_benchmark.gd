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
	# Scale timeout for explicitly requested larger sample/repetition counts.
	if frames > _frame_budget():
		push_error("GRASS_COMPUTE_BENCHMARK=FAIL timeout")
		quit(1)
	return false

func _frame_budget() -> int:
	var args := OS.get_cmdline_user_args()
	var samples := 180
	var warmup := 60
	var repetitions := 3
	for key in ["--grass-compute-samples", "--grass-compute-warmup", "--grass-compute-repetitions"]:
		var index := args.find(key)
		if index >= 0 and index + 1 < args.size():
			var value := int(args[index + 1])
			if key == "--grass-compute-samples":
				samples = maxi(samples, value)
			elif key == "--grass-compute-warmup":
				warmup = maxi(warmup, value)
			else:
				repetitions = maxi(repetitions, value)
	return maxi(12000, 6 * repetitions * (samples + warmup + 64))
