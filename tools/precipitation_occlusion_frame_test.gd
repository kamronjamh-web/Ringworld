extends SceneTree
# Run: Godot --headless --path . --script res://tools/precipitation_occlusion_frame_test.gd
const TEST := preload("res://tools/PrecipitationOcclusionFrameTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TEST.new()
	var code: int = runner.Run()
	runner = null
	if code == 0:
		print("PRECIP_OCCLUSION_FRAME_WRAPPER=PASS")
	else:
		push_error("PRECIP_OCCLUSION_FRAME_WRAPPER=FAIL exit=%d" % code)
	quit(code)
