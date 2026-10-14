extends SceneTree

const TEST := preload("res://tools/EmitterSitePlacementTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TEST.new()
	var code: int = runner.Run(false)
	runner = null
	if code == 0:
		print("EMITTER_SITE_PLACEMENT_WRAPPER=PASS")
	else:
		push_error("EMITTER_SITE_PLACEMENT_WRAPPER=FAIL exit=%d" % code)
	quit(code)
