extends SceneTree

## T2R-10: inland water kind and level in the sampler (see tools/InlandWaterSamplerTest.cs). Headless. Run with tools/run_tests.ps1 -Tests inland_water_sampler_test.
const TEST := preload("res://tools/InlandWaterSamplerTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TEST.new()
	var code: int = runner.Run()
	runner = null
	if code == 0:
		print("INLAND_WATER_SAMPLER_WRAPPER=PASS")
	else:
		push_error("INLAND_WATER_SAMPLER_WRAPPER=FAIL exit=%d" % code)
	quit(code)
