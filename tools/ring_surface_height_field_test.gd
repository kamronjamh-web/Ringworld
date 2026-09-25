extends SceneTree

const SurfaceHeightFieldTest := preload("res://tools/RingSurfaceHeightFieldTest.cs")

func _init() -> void:
	var passed: bool = SurfaceHeightFieldTest.new().Run()
	quit(0 if passed else 1)
