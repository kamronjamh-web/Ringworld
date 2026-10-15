extends SceneTree

const TEST := preload("res://tools/EmitterBoreApertureTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TEST.new()
	var code: int = runner.Run(false)
	runner = null
	if code == 0:
		print("EMITTER_BORE_APERTURE_WRAPPER=PASS")
	else:
		push_error("EMITTER_BORE_APERTURE_WRAPPER=FAIL exit=%d" % code)
	quit(code)
