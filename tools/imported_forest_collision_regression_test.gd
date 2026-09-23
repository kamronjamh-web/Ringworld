extends SceneTree

const DRIVER := preload("res://tools/ImportedForestCollisionRegressionTest.cs")

func _initialize() -> void:
	var passed: bool = DRIVER.new().Run()
	quit(0 if passed else 1)
