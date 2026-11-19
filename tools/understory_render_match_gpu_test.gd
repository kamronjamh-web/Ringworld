extends SceneTree

## T6-4b: the understory carrier shader against the same saplings drawn through the base catalog (front-lit, backlit, cloud shadow, 500 m of haze), plus a layer smoke and the feature-off rule;
## see tools/UnderstoryRenderMatchGpuTest.cs. Needs a real RenderingDevice: run windowed.
## godot --path . --script res://tools/understory_render_match_gpu_test.gd   (or tools/run_tests.ps1 -Tests understory_render_match_gpu_test)
const FIXTURE := preload("res://tools/UnderstoryRenderMatchGpuTest.cs")
var fixture
var frames := 0

func _initialize() -> void:
	if RenderingServer.get_rendering_device() == null:
		push_error("UNDERSTORY_RENDER_MATCH_GPU=FAIL RenderingDevice required")
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
			print("UNDERSTORY_RENDER_MATCH_GPU=" + result)
			quit(0)
		else:
			push_error("UNDERSTORY_RENDER_MATCH_GPU=" + result)
			print("UNDERSTORY_RENDER_MATCH_GPU=" + result)
			quit(1)
		return true
	if frames > 6000:
		push_error("UNDERSTORY_RENDER_MATCH_GPU=FAIL timeout")
		quit(1)
	return false
