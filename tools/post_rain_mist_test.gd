extends SceneTree
# Headless: post-rain mist factors of RingAtmosphereModel (S3 WO-E1). Run: Godot --headless --path . --script res://tools/post_rain_mist_test.gd [-- --verbose]
const TEST := preload("res://tools/PostRainMistTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TEST.new()
	var code: int = runner.Run(OS.get_cmdline_user_args().has("--verbose"))
	runner = null
	if code == 0:
		print("POST_RAIN_MIST_WRAPPER=PASS")
	else:
		push_error("POST_RAIN_MIST_WRAPPER=FAIL exit=%d" % code)
	quit(code)
