extends SceneTree

const FIXTURE := preload("res://tools/GpuTreeIntegrationTest.cs")
var fixture
var elapsed := 0

func _initialize() -> void:
	if RenderingServer.get_rendering_device() == null:
		push_error("GPU_TREE_INTEGRATION=FAIL RenderingDevice required")
		quit(1)
		return
	fixture = FIXTURE.new()
	root.add_child(fixture)
	fixture.call_deferred("Start")

func _process(_delta: float) -> bool:
	elapsed += 1
	var result: String = fixture.get("Result")
	if result != "RUNNING":
		if result.begins_with("PASS"):
			print("GPU_TREE_INTEGRATION=" + result)
			quit(0)
		else:
			push_error("GPU_TREE_INTEGRATION=" + result)
			quit(1)
		return true
	if elapsed > 600:
		push_error("GPU_TREE_INTEGRATION=FAIL scene timeout")
		quit(1)
	return false
