extends SceneTree

## T2R-12c: true-width rivers on the proxy levels (see tools/ProxyRiverTrueWidthTest.cs). Headless. Run with tools/run_tests.ps1 -Tests proxy_river_true_width_test.
const TEST := preload("res://tools/ProxyRiverTrueWidthTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TEST.new()
	var code: int = runner.Run()
	runner = null
	if code == 0:
		print("PROXY_RIVER_TRUE_WIDTH_WRAPPER=PASS")
	else:
		push_error("PROXY_RIVER_TRUE_WIDTH_WRAPPER=FAIL exit=%d" % code)
	quit(code)
