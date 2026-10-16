extends SceneTree

const TEST := preload("res://tools/GrassCardPlacementTest.cs")

func _initialize() -> void:
	var test: RefCounted = TEST.new()
	if not bool(test.call("Run")):
		push_error("GRASS_CARD_PLACEMENT=FAIL")
		quit(1)
		return
	quit(0)
