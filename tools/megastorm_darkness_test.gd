extends SceneTree
# Headless: megastorm darkness term E(xi), scale, multiplier (docs/megastorm-design.md 5.6). Megastorm WO14.
# Run: Godot --headless --path . --script res://tools/megastorm_darkness_test.gd. Prints MEGASTORM_DARKNESS=PASS.
const TEST := preload("res://tools/MegastormDarknessTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TEST.new()
	var code: int = runner.Run()
	runner = null
	if code == 0:
		print("MEGASTORM_DARKNESS_WRAPPER=PASS")
	else:
		push_error("MEGASTORM_DARKNESS_WRAPPER=FAIL exit=%d" % code)
	quit(code)
