extends SceneTree

const VALIDATION := preload("res://tools/GrassExclusionValidation.cs")

func _initialize() -> void:
	var validation: RefCounted = VALIDATION.new()
	if not bool(validation.call("Run")):
		push_error("GRASS_EXCLUSION=FAIL validation helper returned false")
		quit(1)
		return
	print("GRASS_EXCLUSION=PASS immutable canonical construction volumes")
	quit(0)
