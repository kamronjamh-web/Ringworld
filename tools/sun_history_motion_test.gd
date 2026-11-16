extends SceneTree
# Headless: solar cloud-history speed and suppression policy (perf pass E1). Run: Godot --headless --path . --script res://tools/sun_history_motion_test.gd [-- --verbose]
const TEST := preload("res://tools/SunHistoryMotionTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TEST.new()
	var code: int = runner.Run(OS.get_cmdline_user_args().has("--verbose"))
	runner = null
	if code == 0:
		print("SUN_HISTORY_MOTION_WRAPPER=PASS")
	else:
		push_error("SUN_HISTORY_MOTION_WRAPPER=FAIL exit=%d" % code)
	quit(code)
