extends SceneTree
## Run with tools/run_tests.ps1 -Tests world_generation_climate_test.

const TEST := preload("res://tools/WorldGenerationClimateTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var test = TEST.new()
	var code: int = test.Run()
	if code == 0:
		print("WORLD_GENERATION_CLIMATE_WRAPPER=PASS")
	else:
		push_error("WORLD_GENERATION_CLIMATE_WRAPPER=FAIL")
	quit(code)
