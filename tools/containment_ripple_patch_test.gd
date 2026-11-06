extends SceneTree
# Headless: containment field ripple patches (placement, fade to zero, reduced flashes, pool empty, near selection, far quad rule, timing).
# Run: Godot --headless --path . --script res://tools/containment_ripple_patch_test.gd. Prints CONTAINMENT_RIPPLE_PATCH=PASS.
const TEST := preload("res://tools/ContainmentRipplePatchTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TEST.new()
	var code: int = runner.Run()
	runner = null
	if code == 0:
		print("CONTAINMENT_RIPPLE_PATCH_WRAPPER=PASS")
	else:
		push_error("CONTAINMENT_RIPPLE_PATCH_WRAPPER=FAIL exit=%d" % code)
	quit(code)
