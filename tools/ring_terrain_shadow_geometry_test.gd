extends SceneTree

const GeometryTest := preload("res://tools/RingTerrainShadowGeometryTest.cs")

func _init() -> void:
	var passed: bool = GeometryTest.new().Run()
	quit(0 if passed else 1)
