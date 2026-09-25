extends SceneTree

const RingSurfaceFrameTest := preload("res://tools/RingSurfaceFrameTest.cs")

func _init() -> void:
	var passed: bool = RingSurfaceFrameTest.new().Run()
	quit(0 if passed else 1)
