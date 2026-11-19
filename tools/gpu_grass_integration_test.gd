extends SceneTree
const Driver = preload("res://tools/GpuGrassIntegrationTest.cs")
var driver
var frames := 0
func _initialize() -> void:
	if RenderingServer.get_rendering_device() == null:
		push_error("GPU_GRASS_INTEGRATION=FAIL RenderingDevice required")
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
			print("GPU_GRASS_INTEGRATION=" + result)
			quit(0)
		else:
			push_error("GPU_GRASS_INTEGRATION=" + result)
			quit(1)
		return true
	# Frames alone are no clock: with VSync off (override.cfg, display asleep) 900 frames pass in under a second while the tile builds
	# run on the worker pool. Time out on frames and wall time together, inside the manifest's 60 s.
	if frames > 900 and Time.get_ticks_msec() > 45000:
		push_error("GPU_GRASS_INTEGRATION=FAIL timeout")
		quit(1)
	return false
