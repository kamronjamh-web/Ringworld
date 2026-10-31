extends SceneTree
# Headless: megastorm hazard visuals and audio (WO11): blast profile, burst, distortion, bullet-rain constants, hammer / ear pop / hush through a passage, assets.
# Run: Godot --headless --path . --script res://tools/megastorm_hazard_fx_test.gd. Prints MEGASTORM_HAZARD_FX=PASS.
const TEST := preload("res://tools/MegastormHazardFxTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TEST.new()
	var code: int = runner.Run()
	runner = null
	if code == 0:
		print("MEGASTORM_HAZARD_FX_WRAPPER=PASS")
	else:
		push_error("MEGASTORM_HAZARD_FX_WRAPPER=FAIL exit=%d" % code)
	quit(code)
