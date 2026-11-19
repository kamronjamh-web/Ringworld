extends SceneTree

## T6-3: canopy structure in worlds with vegetation.forest_structure (see tools/ForestStructureTest.cs). Headless.
## Run with tools/run_tests.ps1 -Tests forest_structure_test.
const TEST := preload("res://tools/ForestStructureTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TEST.new()
	var code: int = runner.Run()
	runner = null
	if code == 0:
		print("FOREST_STRUCTURE_WRAPPER=PASS")
	else:
		push_error("FOREST_STRUCTURE_WRAPPER=FAIL exit=%d" % code)
	quit(code)
