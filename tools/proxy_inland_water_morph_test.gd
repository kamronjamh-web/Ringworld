extends SceneTree

## PT2-3: inland water morph carries no underlay restore (see tools/ProxyInlandWaterMorphTest.cs). Headless. Run with tools/run_tests.ps1 -Tests proxy_inland_water_morph_test.
const TEST := preload("res://tools/ProxyInlandWaterMorphTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TEST.new()
	var code: int = runner.Run()
	runner = null
	if code == 0:
		print("PROXY_INLAND_WATER_MORPH_WRAPPER=PASS")
	else:
		push_error("PROXY_INLAND_WATER_MORPH_WRAPPER=FAIL exit=%d" % code)
	quit(code)
