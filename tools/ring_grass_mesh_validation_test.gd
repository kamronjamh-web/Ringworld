extends SceneTree

const VALIDATION := preload("res://tools/RingGrassMeshValidation.cs")

func _initialize() -> void:
	var validation: RefCounted = VALIDATION.new()
	if not bool(validation.call("Run")):
		push_error("RING_GRASS_MESH=FAIL validation helper returned false")
		quit(1)
		return
	print("RING_GRASS_MESH=PASS normalized roots/tips, UVs, winding, bounds")
	quit(0)
