extends SceneTree

## T5-3: the habitat field grid (see tools/HabitatFieldGridTest.cs). Headless.
## Run with tools/run_tests.ps1 -Tests habitat_field_grid_test; --png (after --) writes .godot/audit_baseline/common/t5/t5-3_habitat_steep_forest_edge.png.
const TEST := preload("res://tools/HabitatFieldGridTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TEST.new()
	var code: int = runner.Run()
	runner = null
	if code == 0:
		print("HABITAT_FIELD_GRID_WRAPPER=PASS")
	else:
		push_error("HABITAT_FIELD_GRID_WRAPPER=FAIL exit=%d" % code)
	quit(code)
