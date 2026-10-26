extends SceneTree
# Run: Godot --headless --path . --script res://tools/lens_wetness_model_test.gd
const TEST := preload("res://tools/LensWetnessModelTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TEST.new()
	var code: int = runner.Run()
	runner = null
	if code == 0:
		print("LENS_WETNESS_MODEL_WRAPPER=PASS")
	else:
		push_error("LENS_WETNESS_MODEL_WRAPPER=FAIL exit=%d" % code)
	quit(code)
