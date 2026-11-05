extends SceneTree
# Run: tools/run_tests.ps1 -Tests imported_tree_prepared_catalog_test -Label prepared_trees -Cache reuse
const TEST := preload("res://tools/ImportedTreePreparedCatalogTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	create_timer(60.0).timeout.connect(func():
		push_error("IMPORTED_TREE_PREPARED_CATALOG_WRAPPER=FAIL timeout")
		quit(1))
	var runner = TEST.new()
	var code: int = runner.Run("res://.godot/tree_prepared_full_%d.res" % Time.get_ticks_usec())
	runner = null
	if code == 0:
		print("IMPORTED_TREE_PREPARED_CATALOG_WRAPPER=PASS")
	else:
		push_error("IMPORTED_TREE_PREPARED_CATALOG_WRAPPER=FAIL exit=%d" % code)
	quit(code)
