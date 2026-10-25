extends SceneTree
# Run: Godot --headless --path . --script res://tools/variant_equality_test.gd
const TEST := preload("res://tools/VariantEqualityTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TEST.new()
	var code: int = runner.Run()
	runner = null
	if code == 0:
		print("VARIANT_EQUALITY_WRAPPER=PASS")
	else:
		push_error("VARIANT_EQUALITY_WRAPPER=FAIL exit=%d" % code)
	quit(code)
