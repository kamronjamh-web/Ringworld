extends SceneTree
# Headless: pure megastorm model (re-anchor, wrap, inactive, TimeToArrival, slab error, determinism). Megastorm WO1.
# Run: Godot --headless --path . --script res://tools/megastorm_model_test.gd. Prints MEGASTORM_MODEL=PASS.
const TEST := preload("res://tools/MegastormModelTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TEST.new()
	var code: int = runner.Run()
	runner = null
	if code == 0:
		print("MEGASTORM_MODEL_WRAPPER=PASS")
	else:
		push_error("MEGASTORM_MODEL_WRAPPER=FAIL exit=%d" % code)
	quit(code)
