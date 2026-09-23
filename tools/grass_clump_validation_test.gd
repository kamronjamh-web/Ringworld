extends SceneTree

const DRIVER := preload("res://tools/GrassClumpValidationDriver.cs")
var _driver: Node
var _frames := 0

func _initialize() -> void:
	_driver = DRIVER.new()
	root.add_child(_driver)

func _process(_delta: float) -> bool:
	_frames += 1
	var result: String = _driver.get("Result")
	if result != "RUNNING":
		if result == "PASS":
			quit(0)
		else:
			push_error("GRASS_CLUMP_VALIDATION=" + result)
			quit(1)
		return true
	if _frames > 300:
		push_error("GRASS_CLUMP_VALIDATION=FAIL timeout")
		quit(1)
		return true
	return false
