extends SceneTree

const VALIDATION := preload("res://tools/GrassGpuGeometryTest.cs")

func _initialize() -> void:
	var validation: RefCounted = VALIDATION.new()
	if not bool(validation.call("Run")):
		quit(1)
		return
	print("GRASS_GPU_GEOMETRY=PASS triangle budgets, authored attributes, roots, bounds, winding, activation gates and gameplay defaults across quality profiles")
	quit(0)
