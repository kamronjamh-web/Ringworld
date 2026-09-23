extends SceneTree

const SMOKE := preload("res://tools/VegetationSettingsSmoke.cs")
var test
var frames := 0

func _initialize() -> void:
	test = SMOKE.new()
	root.add_child(test)

func _process(_delta: float) -> bool:
	frames += 1
	var result: String = test.get("Result")
	if result != "RUNNING":
		print("VEGETATION_SETTINGS_SMOKE=" + result)
		quit(0 if result.begins_with("PASS") else 1)
		return true
	if frames > 60:
		push_error("VEGETATION_SETTINGS_SMOKE=FAIL timeout")
		quit(1)
	return false
