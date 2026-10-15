extends SceneTree

const TEST := preload("res://tools/EmitterStandInTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TEST.new()
	var code: int = runner.Run(false)
	runner = null
	if code == 0:
		print("EMITTER_FAR_STANDIN_WRAPPER=PASS")
	else:
		push_error("EMITTER_FAR_STANDIN_WRAPPER=FAIL exit=%d" % code)
	quit(code)
