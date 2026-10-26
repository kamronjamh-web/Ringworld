extends SceneTree
# Headless: pure storm arrival maths (S3 WO-A1). Run: Godot --headless --path . --script res://tools/storm_arrival_math_test.gd [-- --verbose]
const TEST := preload("res://tools/StormArrivalMathTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TEST.new()
	var code: int = runner.Run(OS.get_cmdline_user_args().has("--verbose"))
	runner = null
	if code == 0:
		print("STORM_ARRIVAL_MATH_WRAPPER=PASS")
	else:
		push_error("STORM_ARRIVAL_MATH_WRAPPER=FAIL exit=%d" % code)
	quit(code)
