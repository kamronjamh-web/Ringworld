extends SceneTree

## T2R-3: barriers, conditioning, routing (see tools/DrainageGraphTest.cs). Headless. Run with tools/run_tests.ps1 -Tests drainage_graph_test.
const TEST := preload("res://tools/DrainageGraphTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TEST.new()
	var code: int = runner.Run()
	runner = null
	if code == 0:
		print("DRAINAGE_GRAPH_WRAPPER=PASS")
	else:
		push_error("DRAINAGE_GRAPH_WRAPPER=FAIL exit=%d" % code)
	quit(code)
