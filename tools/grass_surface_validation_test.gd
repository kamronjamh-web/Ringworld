extends SceneTree

const VALIDATION := preload("res://tools/GrassSurfaceValidation.cs")

func _initialize() -> void:
	var validation: RefCounted = VALIDATION.new()
	if not bool(validation.call("Run")):
		push_error("GRASS_SURFACE_CONTRACT=FAIL validation helper returned false")
		quit(1)
		return
	quit(0)
