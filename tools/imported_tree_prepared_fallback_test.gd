extends SceneTree
# Fresh process required: source fallback publishes the process-wide catalog.
const TEST := preload("res://tools/ImportedTreePreparedCatalogTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TEST.new()
	var code: int = runner.RunFallback("res://.godot/tree_prepared_fallback_%d.res" % Time.get_ticks_usec())
	runner = null
	if code == 0:
		print("IMPORTED_TREE_PREPARED_FALLBACK_WRAPPER=PASS")
	else:
		push_error("IMPORTED_TREE_PREPARED_FALLBACK_WRAPPER=FAIL exit=%d" % code)
	quit(code)
