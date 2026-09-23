extends SceneTree

const DRIVER := preload("res://tools/ImportedForestEditRegressionTest.cs")

func _initialize() -> void:
	var passed: bool = DRIVER.new().Run()
	print("IMPORTED_FOREST_EDIT_REGRESSION=", "PASS" if passed else "FAIL")
	quit(0 if passed else 1)
