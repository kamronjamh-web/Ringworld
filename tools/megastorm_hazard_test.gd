extends SceneTree
# Headless: megastorm hazard model (boom overpressure from the audio envelope, shelter attenuation, bullet-rain and wake wind, pressure trough, rain impact,
# wind-aligned cover probe, scythe cut probability, edge events, save/load determinism). Megastorm WO10.
# Run: Godot --headless --path . --script res://tools/megastorm_hazard_test.gd. Prints MEGASTORM_HAZARD=PASS.
const TEST := preload("res://tools/MegastormHazardTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TEST.new()
	var code: int = runner.Run(root)
	runner = null
	await process_frame
	if code == 0:
		print("MEGASTORM_HAZARD_WRAPPER=PASS")
	else:
		push_error("MEGASTORM_HAZARD_WRAPPER=FAIL exit=%d" % code)
	quit(code)
