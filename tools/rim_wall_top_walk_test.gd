extends SceneTree
# Headless: WO12 rim wall top walk (side resolution, landing, lift, walking, clamps, jump).
# Run: Godot --headless --path . --script res://tools/rim_wall_top_walk_test.gd. Prints RIM_WALL_TOP_WALK=PASS.
const TEST := preload("res://tools/RimWallTopWalkTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TEST.new()
	var code: int = runner.Run()
	runner = null
	if code == 0:
		print("RIM_WALL_TOP_WALK_WRAPPER=PASS")
	else:
		push_error("RIM_WALL_TOP_WALK_WRAPPER=FAIL exit=%d" % code)
	quit(code)
