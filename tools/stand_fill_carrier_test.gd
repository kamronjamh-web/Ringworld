extends SceneTree

## T6 stand filler carriers (SF-1): see tools/StandFillCarrierTest.cs. Headless.
## Run with tools/run_tests.ps1 -Tests stand_fill_carrier_test.
const TEST := preload("res://tools/StandFillCarrierTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TEST.new()
	var code: int = runner.Run()
	runner = null
	if code == 0:
		print("STAND_FILL_CARRIER_WRAPPER=PASS")
	else:
		push_error("STAND_FILL_CARRIER_WRAPPER=FAIL exit=%d" % code)
	quit(code)
