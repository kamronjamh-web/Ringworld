extends SceneTree

const TEST := preload("res://tools/EmitterPoiStreamerTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TEST.new()
	var code: int = runner.Run(false)
	runner = null
	if code == 0:
		print("EMITTER_POI_STREAMER_WRAPPER=PASS")
	else:
		push_error("EMITTER_POI_STREAMER_WRAPPER=FAIL exit=%d" % code)
	quit(code)
