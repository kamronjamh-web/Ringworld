extends SceneTree

const VALIDATION := preload("res://tools/MeadowGrassMeshTest.cs")

func _initialize() -> void:
	var validation: RefCounted = VALIDATION.new()
	if not bool(validation.call("Run")):
		quit(1)
		return
	print("MEADOW_GRASS_MESH=PASS authored tiers, cache, spread, source frames, deterministic variants and preserved placement budgets")
	quit(0)
