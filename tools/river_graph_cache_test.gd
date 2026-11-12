extends SceneTree

## T2R-9: the river graph store and its lifecycle on strip graphs (see tools/RiverGraphCacheTest.cs). Headless. Run with tools/run_tests.ps1 -Tests river_graph_cache_test.
const TEST := preload("res://tools/RiverGraphCacheTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TEST.new()
	var code: int = runner.Run()
	runner = null
	if code == 0:
		print("RIVER_GRAPH_CACHE_WRAPPER=PASS")
	else:
		push_error("RIVER_GRAPH_CACHE_WRAPPER=FAIL exit=%d" % code)
	quit(code)
