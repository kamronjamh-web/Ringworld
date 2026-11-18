extends SceneTree

## T5-5: the production rock rows (see tools/ScatterRockPlacementTest.cs). Headless; user arg --png writes the debug plot.
## Run with tools/run_tests.ps1 -Tests scatter_rock_placement_test.
const TEST := preload("res://tools/ScatterRockPlacementTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TEST.new()
	var code: int = runner.Run()
	runner = null
	if code == 0:
		print("SCATTER_ROCK_PLACEMENT_WRAPPER=PASS")
	else:
		push_error("SCATTER_ROCK_PLACEMENT_WRAPPER=FAIL exit=%d" % code)
	quit(code)
