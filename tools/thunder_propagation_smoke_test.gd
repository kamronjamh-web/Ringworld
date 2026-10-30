extends SceneTree

const ThunderPropagationDiagnosticRunnerScript := preload("res://scripts/Weather/ThunderPropagationDiagnosticRunner.cs")

func _initialize() -> void:
	var runner := ThunderPropagationDiagnosticRunnerScript.new()
	var result := str(runner.RunAll())
	print(result)
	if result == "Thunder propagation diagnostics: PASS":
		print("THUNDER_PROPAGATION_SMOKE=PASS")
		quit(0)
	else:
		push_error("THUNDER_PROPAGATION_SMOKE=FAIL " + result)
		quit(1)
