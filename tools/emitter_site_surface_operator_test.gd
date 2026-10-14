extends SceneTree

const TEST := preload("res://tools/EmitterSiteSurfaceOperatorTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TEST.new()
	var code: int = runner.Run()
	runner = null
	if code == 0:
		print("EMITTER_SITE_OPERATOR_WRAPPER=PASS")
	else:
		push_error("EMITTER_SITE_OPERATOR_WRAPPER=FAIL exit=%d" % code)
	quit(code)
