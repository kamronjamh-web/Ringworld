extends SceneTree

func _initialize() -> void:
	call_deferred("run")

func run() -> void:
	root.add_child(load("res://scripts/Terrain/Editable/SmoothTerrainOverlapOwnershipTest.cs").new())
