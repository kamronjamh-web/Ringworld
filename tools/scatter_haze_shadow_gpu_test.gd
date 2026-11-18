extends SceneTree

## T5-7a: the scatter rock shader against a reference ground (haze, shadow, cloud, wetness, strata, dither); see tools/ScatterHazeShadowGpuTest.cs. Needs a real RenderingDevice: run windowed.
## godot --path . --script res://tools/scatter_haze_shadow_gpu_test.gd   (or tools/run_tests.ps1 -Tests scatter_haze_shadow_gpu_test)
const FIXTURE := preload("res://tools/ScatterHazeShadowGpuTest.cs")
var fixture
var frames := 0

func _initialize() -> void:
	if RenderingServer.get_rendering_device() == null:
		push_error("SCATTER_HAZE_SHADOW_GPU=FAIL RenderingDevice required")
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
			print("SCATTER_HAZE_SHADOW_GPU=" + result)
			quit(0)
		else:
			push_error("SCATTER_HAZE_SHADOW_GPU=" + result)
			quit(1)
		return true
	if frames > 6000:
		push_error("SCATTER_HAZE_SHADOW_GPU=FAIL timeout")
		quit(1)
	return false
