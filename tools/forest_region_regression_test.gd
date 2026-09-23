extends SceneTree

const DRIVER := preload("res://tools/ForestRegionRegressionTest.cs")

func _initialize() -> void:
	call_deferred("_run")

func _run() -> void:
	var passed: bool = DRIVER.new().Run()
	if not passed:
		push_error("FOREST_REGION_REGRESSION=FAIL")
		quit(1)
		return
	print("FOREST_REGION_REGRESSION=PASS")
	quit(0)
