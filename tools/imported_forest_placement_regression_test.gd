extends SceneTree
const DRIVER := preload("res://tools/ImportedForestPlacementRegressionTest.cs")
func _initialize() -> void:
	var passed: bool = DRIVER.new().Run()
	print("IMPORTED_FOREST_PLACEMENT_REGRESSION=", "PASS" if passed else "FAIL")
	quit(0 if passed else 1)
