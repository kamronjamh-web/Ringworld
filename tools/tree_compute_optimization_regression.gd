extends SceneTree

const DRIVER := preload("res://tools/TreeComputeOptimizationRegression.cs")
var driver
var frames := 0
var cleanup_frames := -1
var exit_code := 1

func _initialize() -> void:
	if RenderingServer.get_rendering_device() == null:
		push_error("TREE_COMPUTE_OPTIMIZATION_REGRESSION=FAIL RenderingDevice required")
		quit(1)
		return
	driver = DRIVER.new()
	root.add_child(driver)
	driver.call_deferred("Start")

func _process(_delta: float) -> bool:
	frames += 1
	if cleanup_frames >= 0:
		cleanup_frames += 1
		if cleanup_frames >= 12:
			quit(exit_code)
		return false
	var result: String = driver.get("Result")
	if result != "RUNNING":
		exit_code = 0 if result.begins_with("PASS") else 1
		if exit_code == 0:
			print("TREE_COMPUTE_OPTIMIZATION_REGRESSION=" + result)
		else:
			push_error("TREE_COMPUTE_OPTIMIZATION_REGRESSION=" + result)
		driver.queue_free()
		cleanup_frames = 0
		return false
	if frames > 900:
		push_error("TREE_COMPUTE_OPTIMIZATION_REGRESSION=FAIL timeout")
		driver.queue_free()
		cleanup_frames = 0
	return false
