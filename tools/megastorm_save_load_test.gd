extends SceneTree
# Headless: megastorm save/load (save at T, run +600, reload, run +600: s, records, lightning ids identical; old save untriggered). Megastorm WO2.
# Run: Godot --headless --path . --script res://tools/megastorm_save_load_test.gd. Prints MEGASTORM_SAVELOAD=PASS.
const TEST := preload("res://tools/MegastormSaveLoadTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TEST.new()
	var code: int = runner.Run(root)
	runner = null
	await process_frame
	if code == 0:
		print("MEGASTORM_SAVELOAD_WRAPPER=PASS")
	else:
		push_error("MEGASTORM_SAVELOAD_WRAPPER=FAIL exit=%d" % code)
	quit(code)
