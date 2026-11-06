extends SceneTree
# Headless: containment field near patch (mesh build, window clamp, coverage, noise continuity, planner, timing).
# Run: Godot --headless --path . --script res://tools/containment_field_near_patch_test.gd. Prints CONTAINMENT_FIELD_NEAR_PATCH=PASS.
const TEST := preload("res://tools/ContainmentFieldNearPatchTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TEST.new()
	var code: int = runner.Run()
	runner = null
	if code == 0:
		print("CONTAINMENT_FIELD_NEAR_PATCH_WRAPPER=PASS")
	else:
		push_error("CONTAINMENT_FIELD_NEAR_PATCH_WRAPPER=FAIL exit=%d" % code)
	quit(code)
