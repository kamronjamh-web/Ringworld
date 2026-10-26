extends SceneTree
# Headless: rainbow geometry and rain illumination maths (S3 WO-F1). Run: Godot --headless --path . --script res://tools/ring_rainbow_math_test.gd [-- --verbose]
const TEST := preload("res://tools/RingRainbowMathTest.cs")

func _init() -> void:
	call_deferred("_run")

func _run() -> void:
	var runner = TEST.new()
	var code: int = runner.Run(OS.get_cmdline_user_args().has("--verbose"))
	runner = null
	if code == 0:
		print("RING_RAINBOW_MATH_WRAPPER=PASS")
	else:
		push_error("RING_RAINBOW_MATH_WRAPPER=FAIL exit=%d" % code)
	quit(code)
