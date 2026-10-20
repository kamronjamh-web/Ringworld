extends SceneTree

## Shadow revamp S4c GPU contract: the 3-layer output array (storage slice views per layer) produces the same maps as the single-layer build, sliced like the
## scheduler does, and writes only its target layer. Local RenderingDevice, needs a real Vulkan renderer (do not pass --headless):
##   godot --path . --script res://tools/height_shadow_publish_gpu_test.gd

const ScheduleTest := preload("res://tools/HeightShadowScheduleTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var passed: bool = ScheduleTest.new().RunGpu()
	quit(0 if passed else 1)
