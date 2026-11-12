extends SceneTree

## T2R-3: CPU valley-floor grid (see tools/DrainageGridTest.cs). Headless. Run with tools/run_tests.ps1 -Tests drainage_grid_test.
const TEST := preload("res://tools/DrainageGridTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TEST.new()
	var code: int = runner.Run()
	runner = null
	if code == 0:
		print("DRAINAGE_GRID_WRAPPER=PASS")
	else:
		push_error("DRAINAGE_GRID_WRAPPER=FAIL exit=%d" % code)
	quit(code)
