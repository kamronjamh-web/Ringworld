extends SceneTree
# Run: tools/run_tests.ps1 -Tests pda_answer_policy_test -Label pda_debug
const TEST := preload("res://tools/PdaAnswerPolicyTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TEST.new()
	var code: int = runner.Run()
	runner = null
	if code == 0:
		print("PDA_ANSWER_POLICY_WRAPPER=PASS")
	else:
		push_error("PDA_ANSWER_POLICY_WRAPPER=FAIL exit=%d" % code)
	quit(code)
