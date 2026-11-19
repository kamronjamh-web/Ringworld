extends SceneTree

## NF-3: near ground fog drivers and near-water field (see tools/NearFogDriversTest.cs). Headless. Run with tools/run_tests.ps1 -Tests near_fog_drivers_test.
const TEST := preload("res://tools/NearFogDriversTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TEST.new()
	var code: int = runner.Run()
	runner = null
	if code == 0:
		print("NEAR_FOG_DRIVERS_WRAPPER=PASS")
	else:
		push_error("NEAR_FOG_DRIVERS_WRAPPER=FAIL exit=%d" % code)
	quit(code)
