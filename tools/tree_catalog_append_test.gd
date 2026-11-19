extends SceneTree

## T6-5: derived tree forms and the A6a append rule (see tools/TreeCatalogAppendTest.cs). Headless.
## Run with tools/run_tests.ps1 -Tests tree_catalog_append_test. `-- --write-base-golden` rewrites tools/fixtures/tree_catalog_base_golden.json from the CURRENT base files (do this only before a deliberate base change).
const TEST := preload("res://tools/TreeCatalogAppendTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TEST.new()
	var code: int = runner.Run()
	runner = null
	if code == 0:
		print("TREE_CATALOG_APPEND_WRAPPER=PASS")
	else:
		push_error("TREE_CATALOG_APPEND_WRAPPER=FAIL exit=%d" % code)
	quit(code)
