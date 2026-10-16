extends SceneTree

const TEST := preload("res://tools/EclipseEventStreamTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TEST.new()
	var code: int = runner.Run(false)
	runner = null
	if code == 0:
		print("ECLIPSE_EVENT_STREAM_WRAPPER=PASS")
	else:
		push_error("ECLIPSE_EVENT_STREAM_WRAPPER=FAIL exit=%d" % code)
	quit(code)
