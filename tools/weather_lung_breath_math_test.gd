extends SceneTree

const TEST := preload("res://tools/WeatherLungBreathMathTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TEST.new()
	var code: int = runner.Run(false)
	runner = null
	if code == 0:
		print("WEATHER_LUNG_BREATH_MATH_WRAPPER=PASS")
	else:
		push_error("WEATHER_LUNG_BREATH_MATH_WRAPPER=FAIL exit=%d" % code)
	quit(code)
