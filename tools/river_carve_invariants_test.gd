extends SceneTree

## T2R-7: the river operator in the height model (see tools/RiverCarveInvariantsTest.cs). Headless. Run with tools/run_tests.ps1 -Tests river_carve_invariants_test.
const TEST := preload("res://tools/RiverCarveInvariantsTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TEST.new()
	var code: int = runner.Run()
	runner = null
	if code == 0:
		print("RIVER_CARVE_INVARIANTS_WRAPPER=PASS")
	else:
		push_error("RIVER_CARVE_INVARIANTS_WRAPPER=FAIL exit=%d" % code)
	quit(code)
