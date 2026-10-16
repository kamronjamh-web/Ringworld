extends SceneTree

const TEST := preload("res://tools/EmitterGridLayerTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TEST.new()
	var code: int = runner.Run(false)
	runner = null
	if code == 0:
		print("EMITTER_GRID_LAYER_WRAPPER=PASS")
	else:
		push_error("EMITTER_GRID_LAYER_WRAPPER=FAIL exit=%d" % code)
	quit(code)
