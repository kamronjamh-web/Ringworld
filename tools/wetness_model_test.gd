extends SceneTree
# Run: Godot --headless --path . --script res://tools/wetness_model_test.gd
const TEST := preload("res://tools/WetnessModelTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TEST.new()
	var code: int = runner.Run()
	runner = null
	if code == 0:
		print("WETNESS_MODEL_WRAPPER=PASS")
	else:
		push_error("WETNESS_MODEL_WRAPPER=FAIL exit=%d" % code)
	quit(code)
