extends SceneTree
# Run: tools/run_tests.ps1 -Tests pda_context_test -Label pda_debug -Cache reuse
const TEST := preload("res://tools/PdaContextTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TEST.new()
	var code: int = runner.Run(root)
	runner = null
	if code == 0:
		print("PDA_CONTEXT_WRAPPER=PASS")
	else:
		push_error("PDA_CONTEXT_WRAPPER=FAIL exit=%d" % code)
	quit(code)
