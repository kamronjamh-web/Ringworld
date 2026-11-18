extends SceneTree

## T5-6: the procedural rock mesh set and its prepared artifact (see tools/ScatterRockMeshValidationTest.cs). Headless; user arg --png writes the CPU contact sheet.
## Run with tools/run_tests.ps1 -Tests scatter_rock_mesh_validation_test.
const TEST := preload("res://tools/ScatterRockMeshValidationTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TEST.new()
	var code: int = runner.Run()
	runner = null
	if code == 0:
		print("SCATTER_ROCK_MESH_VALIDATION_WRAPPER=PASS")
	else:
		push_error("SCATTER_ROCK_MESH_VALIDATION_WRAPPER=FAIL exit=%d" % code)
	quit(code)
