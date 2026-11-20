extends SceneTree

## R1-0: main reflected source is Sky unconditionally; far pins stay Background (see tools/ReflectionEnvironmentContractTest.cs). Headless.
const TEST := preload("res://tools/ReflectionEnvironmentContractTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TEST.new()
	var code: int = runner.Run()
	runner = null
	if code == 0:
		print("REFLECTION_ENVIRONMENT_CONTRACT_WRAPPER=PASS")
	else:
		push_error("REFLECTION_ENVIRONMENT_CONTRACT_WRAPPER=FAIL exit=%d" % code)
	quit(code)
