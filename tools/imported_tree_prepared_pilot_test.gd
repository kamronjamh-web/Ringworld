extends SceneTree
# Run: tools/run_tests.ps1 -Tests imported_tree_prepared_pilot_test -Label prepared_trees -Cache reuse
# Direct authoring pilot: Godot --headless --path . --script res://tools/imported_tree_prepared_pilot_test.gd -- --keep
const TEST := preload("res://tools/ImportedTreePreparedPilotTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	create_timer(60.0).timeout.connect(func():
		push_error("IMPORTED_TREE_PREPARED_PILOT_WRAPPER=FAIL timeout")
		quit(1))
	var path := "res://.godot/tree_prepared_pilot_%d.res" % Time.get_ticks_usec()
	var runner = TEST.new()
	var code: int = runner.Run(path, OS.get_cmdline_user_args().has("--keep"))
	runner = null
	if code == 0:
		print("IMPORTED_TREE_PREPARED_PILOT_WRAPPER=PASS")
	else:
		push_error("IMPORTED_TREE_PREPARED_PILOT_WRAPPER=FAIL exit=%d" % code)
	quit(code)
