extends SceneTree

## T6-4a: the understory sapling row (see tools/UnderstoryPlacementTest.cs). Headless.
## Run with tools/run_tests.ps1 -Tests understory_placement_test.
const TEST := preload("res://tools/UnderstoryPlacementTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TEST.new()
	var code: int = runner.Run()
	runner = null
	if code == 0:
		print("UNDERSTORY_PLACEMENT_WRAPPER=PASS")
	else:
		push_error("UNDERSTORY_PLACEMENT_WRAPPER=FAIL exit=%d" % code)
	quit(code)
