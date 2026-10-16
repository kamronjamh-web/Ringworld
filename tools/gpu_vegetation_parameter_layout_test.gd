extends SceneTree

const TEST := preload("res://tools/GpuVegetationParameterLayoutTest.cs")

func _initialize() -> void:
	var test: RefCounted = TEST.new()
	if not bool(test.call("Run")):
		push_error("PARAM_LAYOUT=FAIL")
		quit(1)
		return
	quit(0)
