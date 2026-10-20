extends SceneTree

const TEST := preload("res://tools/PoiHazeRegistryTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TEST.new()
	var code: int = runner.Run(false)
	runner = null
	if code == 0:
		print("POI_HAZE_REGISTRY_WRAPPER=PASS")
	else:
		push_error("POI_HAZE_REGISTRY_WRAPPER=FAIL exit=%d" % code)
	quit(code)
