extends SceneTree

## T3R-10a: the pure loudness arithmetic of water sound (see tools/WaterSoundFieldTest.cs). Headless. Run with tools/run_tests.ps1 -Tests water_sound_field_test.
const TEST := preload("res://tools/WaterSoundFieldTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TEST.new()
	var code: int = runner.Run()
	runner = null
	if code == 0:
		print("WATER_SOUND_FIELD_WRAPPER=PASS")
	else:
		push_error("WATER_SOUND_FIELD_WRAPPER=FAIL exit=%d" % code)
	quit(code)
