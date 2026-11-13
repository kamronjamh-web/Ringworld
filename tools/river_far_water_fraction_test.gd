extends SceneTree

## T2R-12: far river line coverage (see tools/RiverFarWaterFractionTest.cs). Headless. Run with tools/run_tests.ps1 -Tests river_far_water_fraction_test.
const TEST := preload("res://tools/RiverFarWaterFractionTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TEST.new()
	var code: int = runner.Run()
	runner = null
	if code == 0:
		print("RIVER_FAR_WATER_FRACTION_WRAPPER=PASS")
	else:
		push_error("RIVER_FAR_WATER_FRACTION_WRAPPER=FAIL exit=%d" % code)
	quit(code)
