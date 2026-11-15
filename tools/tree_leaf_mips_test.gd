extends SceneTree
# Headless. Run: tools/run_tests.ps1 -Tests tree_leaf_mips_test -Label tr7c
const TEST := preload("res://tools/TreeLeafMipsTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	create_timer(580.0).timeout.connect(func():
		push_error("TREE_LEAF_MIPS_TEST=FAIL timeout")
		quit(1))
	var runner = TEST.new()
	var code: int = runner.Run()
	runner = null
	quit(code)
