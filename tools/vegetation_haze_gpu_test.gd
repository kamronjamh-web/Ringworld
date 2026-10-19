## Per-instance vegetation haze GPU test (tools/VegetationHazeGpuTest.cs). Needs a real RenderingDevice: run windowed, not --headless.
## godot --path . --script res://tools/vegetation_haze_gpu_test.gd
extends SceneTree

const FIXTURE := preload("res://tools/VegetationHazeGpuTest.cs")
var fixture
var frames := 0

func _initialize() -> void:
	if RenderingServer.get_rendering_device() == null:
		push_error("VEGETATION_HAZE_GPU=FAIL RenderingDevice required")
		quit(1)
		return
	fixture = FIXTURE.new()
	root.add_child(fixture)
	fixture.call_deferred("Start")

func _process(_delta: float) -> bool:
	frames += 1
	var result: String = fixture.get("Result")
	if result != "RUNNING":
		if result.begins_with("PASS"):
			print("VEGETATION_HAZE_GPU=" + result)
			quit(0)
		else:
			push_error("VEGETATION_HAZE_GPU=" + result)
			quit(1)
		return true
	if frames > 6000:
		push_error("VEGETATION_HAZE_GPU=FAIL timeout")
		quit(1)
	return false
