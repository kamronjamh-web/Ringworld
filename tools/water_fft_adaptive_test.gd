extends SceneTree

const SMOKE := preload("res://scripts/Tools/WaterFftAdaptiveSmoke.cs")

func _initialize() -> void:
	var result: Dictionary = SMOKE.new().Run()
	if not bool(result.get("pass", false)):
		push_error("WATER_FFT_ADAPTIVE_TEST_FAIL " + str(result.get("detail", "unknown failure")))
		quit(1)
		return
	print("WATER_FFT_ADAPTIVE_TEST_PASS " + str(result.get("detail", "")))
	quit(0)
