extends SceneTree

func _init() -> void:
	var scene := load("res://scenes/ImportedTreeImpostorCatalogRegressionSmoke.tscn") as PackedScene
	if scene == null:
		push_error("IMPORTED_TREE_IMPOSTOR_CATALOG_SMOKE=FAIL: missing smoke scene")
		quit(1)
		return
	get_root().add_child(scene.instantiate())
