extends SceneTree

const RunnerScript := preload("res://scripts/Weather/WeatherAmbienceDiagnosticRunner.cs")

func _initialize() -> void:
	var result := str(RunnerScript.new().RunAll())
	print(result)
	if result == "Weather ambience diagnostics: PASS":
		quit(0)
	else:
		push_error(result)
		quit(1)
