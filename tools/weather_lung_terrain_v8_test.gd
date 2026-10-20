extends SceneTree

const TEST := preload("res://tools/WeatherLungTerrainV8Test.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TEST.new()
	var code: int = runner.Run()
	runner = null
	if code == 0:
		print("WEATHER_LUNG_TERRAIN_V8_WRAPPER=PASS")
	else:
		push_error("WEATHER_LUNG_TERRAIN_V8_WRAPPER=FAIL exit=%d" % code)
	quit(code)
