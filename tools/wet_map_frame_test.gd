extends SceneTree
# Run: Godot --headless --path . --script res://tools/wet_map_frame_test.gd
const TEST := preload("res://tools/WetMapFrameTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TEST.new()
	var code: int = runner.Run()
	runner = null
	if code == 0:
		print("WET_MAP_FRAME_WRAPPER=PASS")
	else:
		push_error("WET_MAP_FRAME_WRAPPER=FAIL exit=%d" % code)
	quit(code)
