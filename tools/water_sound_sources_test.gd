extends SceneTree

## T3R-10b: finding the water for the water sound (see tools/WaterSoundSourcesTest.cs). Headless. Run with tools/run_tests.ps1 -Tests water_sound_sources_test.
const TEST := preload("res://tools/WaterSoundSourcesTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TEST.new()
	var code: int = runner.Run()
	runner = null
	if code == 0:
		print("WATER_SOUND_SOURCES_WRAPPER=PASS")
	else:
		push_error("WATER_SOUND_SOURCES_WRAPPER=FAIL exit=%d" % code)
	quit(code)
