extends SceneTree

## T2R-19: site intakes in the drainage bake (see tools/DrainageSiteIntakeTest.cs). Headless. Run with tools/run_tests.ps1 -Tests drainage_site_intake_test.
const TEST := preload("res://tools/DrainageSiteIntakeTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TEST.new()
	var code: int = runner.Run()
	runner = null
	if code == 0:
		print("DRAINAGE_SITE_INTAKE_WRAPPER=PASS")
	else:
		push_error("DRAINAGE_SITE_INTAKE_WRAPPER=FAIL exit=%d" % code)
	quit(code)
