extends SceneTree

## T3R-6: the kinematic floating integrator against a synthetic channel (see tools/FloatingBodyTest.cs). Headless. Run with tools/run_tests.ps1 -Tests floating_body_test.
const TEST := preload("res://tools/FloatingBodyTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TEST.new()
	var code: int = runner.Run()
	runner = null
	if code == 0:
		print("FLOATING_BODY_WRAPPER=PASS")
	else:
		push_error("FLOATING_BODY_WRAPPER=FAIL exit=%d" % code)
	quit(code)
