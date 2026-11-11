extends SceneTree

# Run: tools/run_tests.ps1 -Tests world_generation_contract_test
const TEST := preload("res://tools/WorldGenerationContractTest.cs")

func _initialize() -> void:
	call_deferred("_run")

func _run() -> void:
	var test: RefCounted = TEST.new()
	var code: int = test.Run()
	if code == 0:
		print("WORLD_GENERATION_CONTRACT_WRAPPER=PASS")
	else:
		push_error("WORLD_GENERATION_CONTRACT_WRAPPER=FAIL exit=%d" % code)
	quit(code)
