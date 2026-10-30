extends SceneTree
# S6 WO1: pure CPU cloud layer column golden (RingCloudLayers). Run:
#   Godot --headless --path . --script res://tools/cloud_layer_column_test.gd [-- --capture]
const TEST := preload("res://tools/CloudLayerColumnTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TEST.new()
	var code: int = runner.Run(OS.get_cmdline_user_args().has("--capture"))
	runner = null
	if code == 0:
		print("CLOUD_LAYER_COLUMN_WRAPPER=PASS")
	else:
		push_error("CLOUD_LAYER_COLUMN_WRAPPER=FAIL exit=%d" % code)
	quit(code)
