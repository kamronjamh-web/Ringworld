extends SceneTree
# Headless: megastorm PDA lines (350 m/s approach, episodes, rate limits, resync, hazards, database unlock persistence). Megastorm WO6b.
# Run: Godot --headless --path . --script res://tools/megastorm_pda_test.gd. Prints MEGASTORM_PDA=PASS.
const TEST := preload("res://tools/MegastormPdaTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TEST.new()
	var code: int = runner.Run(root)
	runner = null
	await process_frame
	if code == 0:
		print("MEGASTORM_PDA_WRAPPER=PASS")
	else:
		push_error("MEGASTORM_PDA_WRAPPER=FAIL exit=%d" % code)
	quit(code)
