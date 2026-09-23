extends SceneTree

func _initialize() -> void:
	call_deferred("_run")

func _run() -> void:
	var test = load("res://tools/TreeLightingShadowBehaviorTest.cs").new()
	var passed: bool = test.call("Run", root)
	await process_frame
	if not passed:
		push_error("TREE_LIGHTING_SHADOW_BEHAVIOR=FAIL")
		quit(1)
		return
	print("TREE_LIGHTING_SHADOW_BEHAVIOR=PASS")
	quit(0)
