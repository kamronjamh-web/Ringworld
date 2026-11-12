extends SceneTree

## T3R-3: spans, calibrated hydraulics, classes and landings on the D4 strip (see tools/RiverDangerTest.cs). Headless. Run with tools/run_tests.ps1 -Tests river_danger_test.
const TEST := preload("res://tools/RiverDangerTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TEST.new()
	var code: int = runner.Run()
	runner = null
	if code == 0:
		print("RIVER_DANGER_WRAPPER=PASS")
	else:
		push_error("RIVER_DANGER_WRAPPER=FAIL exit=%d" % code)
	quit(code)
