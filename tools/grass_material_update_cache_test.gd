extends SceneTree

const GrassMaterialUpdateCacheTests := preload("res://tools/GrassMaterialUpdateCacheTests.cs")

func _initialize() -> void:
	var test := GrassMaterialUpdateCacheTests.new()
	quit(0 if test.Run() else 1)
