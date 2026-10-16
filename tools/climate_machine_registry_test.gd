extends SceneTree

const TEST := preload("res://tools/ClimateMachineRegistryTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TEST.new()
	var code: int = runner.Run(OS.get_cmdline_user_args().has("--capture"))
	runner = null
	if code == 0:
		print("CLIMATE_MACHINE_REGISTRY_WRAPPER=PASS")
	else:
		push_error("CLIMATE_MACHINE_REGISTRY_WRAPPER=FAIL exit=%d" % code)
	quit(code)
