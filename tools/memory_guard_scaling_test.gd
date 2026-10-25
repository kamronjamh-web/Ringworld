extends SceneTree
const SMOKE := preload("res://tools/MemoryGuardScalingTest.cs")
func _initialize() -> void:
	var result: Dictionary = SMOKE.new().Run()
	if not bool(result.get("passed", false)): push_error("MEMORY_GUARD_SCALING_TEST=FAIL " + str(result.get("detail", ""))); quit(1); return
	print("MEMORY_GUARD_SCALING_TEST=PASS " + str(result.get("detail", ""))); quit(0)
