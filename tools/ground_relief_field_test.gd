extends SceneTree

## T4-4a: the 64 m convexity C64 in the proxy builders' data (see tools/GroundReliefFieldTest.cs). Headless.
## Run with tools/run_tests.ps1 -Tests ground_relief_field_test; --capture (after --) rewrites tools/fixtures/ground_relief_golden.txt.
const TEST := preload("res://tools/GroundReliefFieldTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TEST.new()
	var code: int = runner.Run()
	runner = null
	if code == 0:
		print("GROUND_RELIEF_FIELD_WRAPPER=PASS")
	else:
		push_error("GROUND_RELIEF_FIELD_WRAPPER=FAIL exit=%d" % code)
	quit(code)
