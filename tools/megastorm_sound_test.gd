extends SceneTree
# Headless: pure megastorm sound maths (hum reach, AirSound regime, boom window, gust swell, reload). Megastorm WO6b.
# Run: Godot --headless --path . --script res://tools/megastorm_sound_test.gd. Prints MEGASTORM_SOUND=PASS.
const TEST := preload("res://tools/MegastormSoundTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TEST.new()
	var code: int = runner.Run()
	runner = null
	if code == 0:
		print("MEGASTORM_SOUND_WRAPPER=PASS")
	else:
		push_error("MEGASTORM_SOUND_WRAPPER=FAIL exit=%d" % code)
	quit(code)
