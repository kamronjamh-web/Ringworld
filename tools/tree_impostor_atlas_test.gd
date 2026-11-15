extends SceneTree
# Headless. Run: tools/run_tests.ps1 -Tests tree_impostor_atlas_test -Label tr7a
const TEST := preload("res://tools/TreeImpostorAtlasTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	create_timer(280.0).timeout.connect(func():
		push_error("TREE_IMPOSTOR_ATLAS_TEST=FAIL timeout")
		quit(1))
	var runner = TEST.new()
	var code: int = runner.Run()
	runner = null
	quit(code)
