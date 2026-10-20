extends SceneTree

const TEST := preload("res://tools/WeatherLungPlacementTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TEST.new()
	var code: int = runner.Run()
	runner = null
	if code == 0:
		print("WEATHER_LUNG_PLACEMENT_WRAPPER=PASS")
	else:
		push_error("WEATHER_LUNG_PLACEMENT_WRAPPER=FAIL exit=%d" % code)
	quit(code)
