extends SceneTree

## T6 stand filler row (SF-2): see tools/StandFillerPlacementTest.cs. Headless.
## Run with tools/run_tests.ps1 -Tests stand_filler_placement_test.
const TEST := preload("res://tools/StandFillerPlacementTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TEST.new()
	var code: int = runner.Run()
	runner = null
	if code == 0:
		print("STAND_FILLER_PLACEMENT_WRAPPER=PASS")
	else:
		push_error("STAND_FILLER_PLACEMENT_WRAPPER=FAIL exit=%d" % code)
	quit(code)
