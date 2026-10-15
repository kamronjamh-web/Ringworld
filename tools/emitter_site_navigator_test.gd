extends SceneTree

const TEST := preload("res://tools/EmitterSiteNavigatorTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TEST.new()
	var code: int = runner.Run(false)
	runner = null
	if code == 0:
		print("EMITTER_SITE_NAVIGATOR_WRAPPER=PASS")
	else:
		push_error("EMITTER_SITE_NAVIGATOR_WRAPPER=FAIL exit=%d" % code)
	quit(code)
