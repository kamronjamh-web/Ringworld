extends SceneTree
# Headless: storm lifecycle code (continuous, non-decreasing, deterministic, 1.5 with lifecycles off) and anvil settings. WO-A.

func _initialize() -> void:
	var result: String = load("res://tools/StormLifecycleCodeTest.cs").new().Run()
	print("STORM_LIFECYCLE_CODE=", result)
	quit(0 if result.begins_with("PASS") else 1)
