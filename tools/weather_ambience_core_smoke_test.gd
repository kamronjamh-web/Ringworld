extends SceneTree

const RunnerScript := preload("res://scripts/Weather/WeatherAmbienceDiagnosticRunner.cs")

func _initialize() -> void:
	var result := str(RunnerScript.new().RunAll())
	print(result)
	if result == "Weather ambience diagnostics: PASS":
		print("WEATHER_AMBIENCE_CORE_SMOKE=PASS")
		quit(0)
	else:
		push_error("WEATHER_AMBIENCE_CORE_SMOKE=FAIL " + result)
		quit(1)
