extends SceneTree
# Headless: containment field WO8 (even C / 126 cue spacing, single source, RimWallTopAnchors).
# Run: Godot --headless --path . --script res://tools/rim_wall_top_anchors_test.gd. Prints RIM_WALL_TOP_ANCHORS=PASS.
const TEST := preload("res://tools/RimWallTopAnchorsTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TEST.new()
	var code: int = runner.Run()
	runner = null
	if code == 0:
		print("RIM_WALL_TOP_ANCHORS_WRAPPER=PASS")
	else:
		push_error("RIM_WALL_TOP_ANCHORS_WRAPPER=FAIL exit=%d" % code)
	quit(code)
