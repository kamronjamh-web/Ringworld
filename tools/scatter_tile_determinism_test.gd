extends SceneTree

## T5-4a: the scatter framework (see tools/ScatterTileDeterminismTest.cs). Headless.
## Run with tools/run_tests.ps1 -Tests scatter_tile_determinism_test.
const TEST := preload("res://tools/ScatterTileDeterminismTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TEST.new()
	var code: int = runner.Run()
	runner = null
	if code == 0:
		print("SCATTER_TILE_DETERMINISM_WRAPPER=PASS")
	else:
		push_error("SCATTER_TILE_DETERMINISM_WRAPPER=FAIL exit=%d" % code)
	quit(code)
