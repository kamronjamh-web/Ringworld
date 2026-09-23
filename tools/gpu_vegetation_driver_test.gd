extends SceneTree

const DRIVER := preload("res://tools/GpuVegetationDriverTest.cs")

# Bounded main-RD test; it intentionally avoids loading the game world.
var driver
var elapsed := 0

func _initialize() -> void:
	if RenderingServer.get_rendering_device() == null:
		push_error("GPU_VEGETATION_DRIVER=FAIL RenderingDevice required")
		quit(1)
		return
	driver = DRIVER.new()
	root.add_child(driver)
	driver.call_deferred("Start")

func _process(_delta: float) -> bool:
	elapsed += 1
	var result: String = driver.get("Result")
	if result != "RUNNING":
		if result.begins_with("PASS"):
			print("GPU_VEGETATION_DRIVER=" + result)
			quit(0)
		else:
			push_error("GPU_VEGETATION_DRIVER=" + result)
			quit(1)
		return true
	if elapsed > 300:
		push_error("GPU_VEGETATION_DRIVER=FAIL scene timeout")
		quit(1)
	return false
