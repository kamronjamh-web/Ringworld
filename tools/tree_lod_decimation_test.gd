extends SceneTree
# Headless. Run: tools/run_tests.ps1 -Tests tree_lod_decimation_test -Label tr4
const TEST := preload("res://tools/TreeLodDecimationTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	create_timer(380.0).timeout.connect(func():
		push_error("TREE_LOD_DECIMATION_TEST=FAIL timeout")
		quit(1))
	var runner = TEST.new()
	var code: int = runner.Run()
	runner = null
	quit(code)
