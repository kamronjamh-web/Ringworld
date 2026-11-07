extends SceneTree

const TEST := preload("res://tools/PdaConversationStoreTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TEST.new()
	var code: int = runner.Run()
	runner = null
	if code == 0:
		print("PDA_CONVERSATION_STORE_WRAPPER=PASS")
	else:
		push_error("PDA_CONVERSATION_STORE_WRAPPER=FAIL exit=%d" % code)
	quit(code)
