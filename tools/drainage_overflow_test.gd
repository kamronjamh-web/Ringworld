extends SceneTree

## T2R-22 Part A: overflows of closed basins on synthetic domains (see tools/DrainageOverflowTest.cs). Headless. Run with tools/run_tests.ps1 -Tests drainage_overflow_test.
const TEST := preload("res://tools/DrainageOverflowTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TEST.new()
	var code: int = runner.Run()
	runner = null
	if code == 0:
		print("DRAINAGE_OVERFLOW_WRAPPER=PASS")
	else:
		push_error("DRAINAGE_OVERFLOW_WRAPPER=FAIL exit=%d" % code)
	quit(code)
